using System;
using System.IO;
using System.Text;
using System.Threading;
using YamlDotNet.Serialization;

namespace ValheimEnforcer.common {

    /// <summary>
    /// Writes a YAML document to disk without ever building it as one string, and without ever leaving a
    /// half-written file where the old one was.
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
        /// Appended to the destination name while writing. Chosen so it never matches the "*.yaml" globs the
        /// character folder is enumerated with: a save mid-write must not show up as a character.
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

            string tmp = path + TempSuffix;
            if (File.Exists(tmp)) { File.Delete(tmp); } // a previous attempt that never got to publish

            using (FileStream stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, StreamBufferBytes))
            using (StreamWriter writer = new StreamWriter(stream, Utf8NoBom, WriterBufferBytes)) {
                serializer.Serialize(writer, graph);
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
