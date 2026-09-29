using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using ValheimEnforcer.modules.mods;

namespace ValheimEnforcer.modules.cheatmonitor {

    /// <summary>
    /// SHA256 over the executable sections of a native DLL, read from the file on disk.
    ///
    /// A whole-file hash changes whenever anything in the file does - a timestamp, a debug record, or data
    /// carried in its resources. The code sections change only when the code does, so this names a loader
    /// build that the file hash alone would not.
    ///
    /// Read from disk rather than from the loaded image: in memory the loader has applied relocations, so
    /// the bytes differ from one process to the next.
    /// </summary>
    internal static class PeCodeHash {

        // Far above any proxy DLL; the check exists so a pathological file cannot be read whole.
        private const long MaxFileBytes = 64L * 1024 * 1024;
        private const uint ScnCntCode = 0x00000020;

        private struct CacheEntry {
            internal long Length;
            internal DateTime MTimeUtc;
            internal string Hash;
        }

        // Same invalidation as PluginHasher: path, length and write time.
        private static readonly ConcurrentDictionary<string, CacheEntry> cache = new ConcurrentDictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The hash of the file's code sections, or null when it is not a readable PE image.</summary>
        internal static string HashFile(string path) {
            if (string.IsNullOrEmpty(path)) { return null; }
            try {
                FileInfo info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxFileBytes) { return null; }
                if (cache.TryGetValue(path, out CacheEntry cached)
                    && cached.Length == info.Length && cached.MTimeUtc == info.LastWriteTimeUtc) {
                    return cached.Hash;
                }

                byte[] bytes;
                // Shared access for the same reason as PluginHasher: the file is loaded, and the loader holds it.
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                    bytes = new byte[fs.Length];
                    int read = 0;
                    while (read < bytes.Length) {
                        int n = fs.Read(bytes, read, bytes.Length - read);
                        if (n <= 0) { break; }
                        read += n;
                    }
                    if (read != bytes.Length) { return null; }
                }

                string hash = HashBytes(bytes);
                cache[path] = new CacheEntry { Length = info.Length, MTimeUtc = info.LastWriteTimeUtc, Hash = hash };
                return hash;
            } catch (Exception e) {
                Logger.LogDebug($"Could not hash the code of {path}: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// The hash of every section marked as code, in section-table order. Null when the bytes are not a PE
        /// image or carry no code section.
        /// </summary>
        internal static string HashBytes(byte[] b) {
            if (b == null || b.Length < 0x40) { return null; }
            if (b[0] != (byte)'M' || b[1] != (byte)'Z') { return null; }

            int pe = BitConverter.ToInt32(b, 0x3C);
            if (pe <= 0 || pe > b.Length - 24) { return null; }
            if (b[pe] != (byte)'P' || b[pe + 1] != (byte)'E' || b[pe + 2] != 0 || b[pe + 3] != 0) { return null; }

            int sectionCount = BitConverter.ToUInt16(b, pe + 6);
            int optionalSize = BitConverter.ToUInt16(b, pe + 20);
            int table = pe + 24 + optionalSize;
            if (sectionCount <= 0 || sectionCount > 96 || table + sectionCount * 40 > b.Length) { return null; }

            using (SHA256 sha = SHA256.Create()) {
                bool any = false;
                for (int i = 0; i < sectionCount; i++) {
                    int entry = table + i * 40;
                    uint characteristics = BitConverter.ToUInt32(b, entry + 36);
                    if ((characteristics & ScnCntCode) == 0) { continue; }
                    long rawSize = BitConverter.ToUInt32(b, entry + 16);
                    long rawPtr = BitConverter.ToUInt32(b, entry + 20);
                    if (rawSize == 0) { continue; }
                    if (rawPtr < 0 || rawPtr + rawSize > b.Length) { return null; }
                    sha.TransformBlock(b, (int)rawPtr, (int)rawSize, null, 0);
                    any = true;
                }
                if (!any) { return null; }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return PluginHasher.ToHex(sha.Hash);
            }
        }
    }
}
