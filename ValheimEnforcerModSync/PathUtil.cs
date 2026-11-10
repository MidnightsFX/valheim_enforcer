using System;
using System.IO;
using System.Text;

namespace ValheimEnforcerModSync {

    internal static class PathUtil {

        /// <summary>True when <paramref name="path"/> is inside <paramref name="root"/>, after both are made absolute.</summary>
        internal static bool IsUnder(string path, string root) {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) { return false; }
            try {
                string full = Path.GetFullPath(path);
                string dir = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return full.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
            } catch (Exception) {
                return false;
            }
        }

        /// <summary>
        /// Lowercase hex, no separators: the form ModSyncApi documents for the hashes a server accepts, which is
        /// also how ValheimEnforcer records them.
        /// </summary>
        internal static string ToHex(byte[] bytes) {
            if (bytes == null) { return null; }
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) { sb.Append(b.ToString("x2")); }
            return sb.ToString();
        }
    }
}
