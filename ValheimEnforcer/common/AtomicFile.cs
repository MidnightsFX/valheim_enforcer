using System;
using System.IO;
using System.Text;
using System.Threading;
using YamlDotNet.Serialization;

namespace ValheimEnforcer.common {

    /// <summary>
    /// Writes a file to disk without ever leaving a half-written one where the old one was - and, for YAML,
    /// without ever building the document as one string.
    ///
    /// Two things this replaces, both from the character store's old write path. Serializing to a string and
    /// then File.WriteAllText allocated the whole document twice - once as UTF-16, once as UTF-8 - and a
    /// character save is hundreds of kilobytes, so both copies landed in the large-object space of a
    /// collector that never compacts it. Streaming through a small buffer keeps every allocation below that
    /// threshold. And WriteAllText truncates the destination before it writes, so a crash mid-write left an
    /// empty or partial save behind; writing beside the file and renaming over it means the destination is
    /// always either the previous complete document or the new one.
    ///
    /// Deliberately free of any dependency on ValConfig or BepInEx, so it can be exercised headless.
    /// </summary>
    internal static class AtomicFile {

        /// <summary>
        /// Appended to the destination name while writing. Chosen so it never matches the "*.yaml" or "*.map"
        /// globs the character folder is enumerated with: a save mid-write must not show up as a character.
        /// </summary>
        internal const string TempSuffix = ".tmp";

        // Both comfortably under the 85 KB large-object threshold, and large enough that a save is written in
        // a handful of syscalls rather than one per line.
        private const int StreamBufferBytes = 32 * 1024;
        private const int WriterBufferBytes = 16 * 1024;

        /// <summary>Wait before the one retry of the rename, for an antivirus or editor that has the file open.</summary>
        private const int RetryDelayMs = 50;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>
        /// Serializes <paramref name="graph"/> to <paramref name="path"/> through a temporary file beside it,
        /// and returns the last-write time the file system reports for the published file.
        /// </summary>
        /// <exception cref="IOException">When even the copy fallback fails; the caller decides what a failed write means.</exception>
        internal static DateTime WriteYaml(string path, object graph, ISerializer serializer) {
            if (string.IsNullOrEmpty(path)) { throw new ArgumentException("A path is required.", nameof(path)); }
            if (graph == null) { throw new ArgumentNullException(nameof(graph)); }
            if (serializer == null) { throw new ArgumentNullException(nameof(serializer)); }

            return WriteThrough(path, stream => {
                // The writer is disposed here rather than by WriteThrough, because flushing it has to happen
                // before the stream is closed and only this branch has one.
                using (StreamWriter writer = new StreamWriter(stream, Utf8NoBom, WriterBufferBytes)) {
                    serializer.Serialize(writer, graph);
                }
            });
        }

        /// <summary>
        /// Writes opaque bytes to <paramref name="path"/> with the same crash safety, and returns the
        /// last-write time of the published file.
        ///
        /// For payloads that are already a byte array and already compressed - the minimap blob, a sealed
        /// recovery snapshot - where there is nothing to stream through a serializer and nothing to gain by
        /// pretending there is. The LOH reasoning in the class doc does not apply to the caller's own array,
        /// which exists either way; what this avoids is the truncate-then-write window of File.WriteAllBytes.
        /// </summary>
        /// <exception cref="IOException">When even the copy fallback fails; the caller decides what a failed write means.</exception>
        internal static DateTime WriteBytes(string path, byte[] data) {
            if (string.IsNullOrEmpty(path)) { throw new ArgumentException("A path is required.", nameof(path)); }
            if (data == null) { throw new ArgumentNullException(nameof(data)); }

            return WriteThrough(path, stream => stream.Write(data, 0, data.Length));
        }

        /// <summary>
        /// Writes already-built text to <paramref name="path"/> with the same crash safety, and returns the
        /// last-write time of the published file.
        ///
        /// For documents that are assembled as a string before they can be written - Mods.yaml, whose serialized
        /// form has the admin's comments spliced back into it by ModManager.WithPreservedComments, so there is no
        /// object graph left to stream. The LOH reasoning in the class doc therefore does not apply here; what
        /// this buys is the other half of the class: File.WriteAllText truncates the destination before it writes,
        /// so a process death mid-write left the file cut off partway through. Mods.yaml serializes requiredMods
        /// first and the admin-authored lists last, which made a truncated write look exactly like the data-loss
        /// bug this replaced.
        ///
        /// Encoding matches File.WriteAllText: both are UTF-8 with no BOM, so switching a caller over does not
        /// change a single byte of a file that writes successfully.
        /// </summary>
        /// <exception cref="IOException">When even the copy fallback fails; the caller decides what a failed write means.</exception>
        internal static DateTime WriteText(string path, string contents) {
            if (string.IsNullOrEmpty(path)) { throw new ArgumentException("A path is required.", nameof(path)); }
            if (contents == null) { throw new ArgumentNullException(nameof(contents)); }

            return WriteThrough(path, stream => {
                // Disposed here rather than by WriteThrough for the same reason WriteYaml disposes its own:
                // the writer has to flush before the stream closes, and only the text branches have one.
                using (StreamWriter writer = new StreamWriter(stream, Utf8NoBom, WriterBufferBytes)) {
                    writer.Write(contents);
                }
            });
        }

        /// <summary>
        /// The shared body: clear any stale temporary file, open one beside the destination, let the caller
        /// fill it, then rename it over the destination.
        ///
        /// One closure allocation per call, which is the cost of not having this written out twice. It is a
        /// single delegate on a path that already does file I/O, so it is nowhere near the allocation problem
        /// the class exists to solve.
        /// </summary>
        private static DateTime WriteThrough(string path, Action<FileStream> emit) {
            string tmp = path + TempSuffix;
            if (File.Exists(tmp)) { File.Delete(tmp); } // a previous attempt that never got to publish

            using (FileStream stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, StreamBufferBytes)) {
                emit(stream);
            }

            Publish(tmp, path);
            return File.GetLastWriteTimeUtc(path);
        }

        /// <summary>
        /// Moves the finished temporary file over the destination. Rename is atomic on every file system the
        /// game runs on, and the temporary file is in the same directory so it is never a cross-volume move.
        /// The retry and the copy fallback exist for Windows, where something holding the destination open
        /// makes the rename fail; the copy is not atomic, but by then the temporary file is complete, so the
        /// worst case is the same partial-write window the old code had on every save.
        /// </summary>
        private static void Publish(string tmp, string path) {
            Exception first;
            try {
                Rename(tmp, path);
                return;
            } catch (IOException e) {
                first = e;
            } catch (UnauthorizedAccessException e) {
                first = e;
            }

            Thread.Sleep(RetryDelayMs);
            try {
                Rename(tmp, path);
                return;
            } catch (IOException) {
            } catch (UnauthorizedAccessException) {
            }

            Logger.LogWarning($"Could not rename {Path.GetFileName(tmp)} over {Path.GetFileName(path)} ({first.GetType().Name}: {first.Message}); copying it into place instead.");
            File.Copy(tmp, path, true);
            File.Delete(tmp);
        }

        // Replace and Move are each the wrong call in the other's case on .NET Framework and Mono: Replace throws
        // when the destination is absent, Move throws when it is present.
        private static void Rename(string tmp, string path) {
            if (File.Exists(path)) {
                File.Replace(tmp, path, null, true);
            } else {
                File.Move(tmp, path);
            }
        }
    }
}
