using System.IO;
using System.IO.Compression;
using System.Text;

namespace ValheimEnforcer.common {

    /// <summary>
    /// GZip with a ceiling on what a payload is allowed to expand into.
    ///
    /// The ceiling is the whole point, and it is why this is a helper rather than four lines at each call
    /// site. A compressed payload says nothing about its own size until it has been expanded, so an
    /// unbounded decompress lets a few kilobytes on the wire turn into hundreds of megabytes of allocation
    /// on whichever machine receives it - the server for a character save, the admin's own client for an
    /// audit download. Expansion stops and returns null at the ceiling rather than throwing, so "too big" is
    /// an ordinary result every caller checks for; a payload that is not gzip at all still throws, because
    /// "corrupt" and "too big" are different things to put in front of an admin.
    ///
    /// The ceiling belongs to the caller because the right value is a property of the payload, not of the
    /// codec: a character save and a week of audit history are bounded by very different numbers.
    ///
    /// Deliberately free of any dependency on ValConfig or BepInEx, so it can be exercised headless.
    /// </summary>
    internal static class Gzip {

        /// <summary>Read buffer for the bounded expansion. Under the 85 KB large-object threshold.</summary>
        private const int ChunkBytes = 8192;

        /// <summary>UTF-8 without a BOM, matching <see cref="AtomicFile"/>: a BOM in the middle of a
        /// decompressed document is a parse error nobody enjoys tracking down.</summary>
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>Compresses UTF-8 text. Returns an empty array for null or empty input.</summary>
        internal static byte[] CompressText(string text) {
            if (string.IsNullOrEmpty(text)) { return new byte[0]; }
            return Compress(Utf8NoBom.GetBytes(text));
        }

        /// <summary>Compresses opaque bytes. Returns an empty array for null or empty input.</summary>
        internal static byte[] Compress(byte[] raw) {
            if (raw == null || raw.Length == 0) { return new byte[0]; }
            using (MemoryStream output = new MemoryStream()) {
                using (GZipStream gz = new GZipStream(output, CompressionMode.Compress)) {
                    gz.Write(raw, 0, raw.Length);
                }
                return output.ToArray();
            }
        }

        /// <summary>
        /// Expands to UTF-8 text, or null when the input is empty or would expand past
        /// <paramref name="ceilingBytes"/>. Throws for a payload that is not gzip - see <see cref="Decompress"/>.
        /// </summary>
        /// <exception cref="InvalidDataException">The payload is not valid gzip, or is truncated.</exception>
        internal static string DecompressText(byte[] data, int ceilingBytes) {
            byte[] raw = Decompress(data, ceilingBytes);
            return raw == null ? null : Utf8NoBom.GetString(raw);
        }

        /// <summary>
        /// Expands to bytes, or null when the input is empty or would expand past
        /// <paramref name="ceilingBytes"/>.
        ///
        /// The running total is counted from bytes actually read out of the stream, never from anything the
        /// payload claims about itself - the same reasoning as the zip-entry guard in ThunderstoreResolver.
        ///
        /// A payload that is not gzip at all throws rather than returning null, and that difference is
        /// deliberate: "too big" and "corrupt" are different things to tell an admin, and every caller
        /// already has a try/catch around this for exactly that reason. Collapsing them would have a
        /// truncated download reported as "ask for fewer days".
        /// </summary>
        /// <exception cref="InvalidDataException">The payload is not valid gzip, or is truncated.</exception>
        internal static byte[] Decompress(byte[] data, int ceilingBytes) {
            if (data == null || data.Length == 0) { return null; }
            if (ceilingBytes <= 0) { return null; }
            using (MemoryStream input = new MemoryStream(data))
            using (GZipStream gz = new GZipStream(input, CompressionMode.Decompress))
            using (MemoryStream output = new MemoryStream()) {
                byte[] buffer = new byte[ChunkBytes];
                int total = 0;
                int read;
                while ((read = gz.Read(buffer, 0, buffer.Length)) > 0) {
                    total += read;
                    if (total > ceilingBytes) { return null; }
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
        }
    }
}
