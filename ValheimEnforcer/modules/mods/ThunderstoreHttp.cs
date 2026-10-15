using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;

namespace ValheimEnforcer.modules.mods {

    /// <summary>
    /// The one way this mod reaches the network for a mod archive: thunderstore.io and its CDN, nothing else.
    ///
    /// Shared by the server's hash resolver and the client's mod sync, so both are held to the same allowlist,
    /// the same per-hop redirect check and the same byte cap. Keeping a second copy for the client would be the
    /// copy that drifts, and the client is the side that writes what it downloads to disk.
    /// </summary>
    internal static class ThunderstoreHttp {

        // thunderstore.io and anything under it. Its CDN host has changed before - downloads now redirect to
        // ccdn.thunderstore.io, which a fixed list of the older gcdn/hcdn hosts refused, so every resolve failed.
        // A subdomain is still Thunderstore's own, so the suffix keeps the same guarantee without the breakage.
        private const string AllowedDomain = "thunderstore.io";

        private const int MaxRedirects = 5;

        // AllowAutoRedirect is off so every hop can be re-checked against the allowlist; following redirects
        // automatically would let a redirect walk the request off it. thunderstore.io/package/download/...
        // always 302s to the CDN, so this path runs on every fetch.
        private static readonly HttpClient http = new HttpClient(
            new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None }) {
            Timeout = TimeSpan.FromSeconds(90)
        };

        internal static bool IsAllowedUrl(string url) {
            if (string.IsNullOrWhiteSpace(url)) { return false; }
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri u)) { return false; }
            if (u.Scheme != "https") { return false; }
            if (!u.IsDefaultPort) { return false; }
            return string.Equals(u.Host, AllowedDomain, StringComparison.OrdinalIgnoreCase)
                || u.Host.EndsWith("." + AllowedDomain, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The download address of one exact package version. The parts are expected to have come through
        /// <see cref="ThunderstoreResolver.TryParseSpec"/>, which is what keeps them from carrying a '/', '.' or
        /// '%' that could move the request off this path shape.
        /// </summary>
        internal static string DownloadUrlFor(string owner, string name, string version) {
            return $"https://thunderstore.io/package/download/{owner}/{name}/{version}/";
        }

        /// <summary>
        /// Downloads to memory with a hard byte cap, following redirects manually so each hop is re-validated.
        /// Content-Length is checked when present but never trusted - the cap is enforced while streaming too,
        /// so an absent or lying header cannot make this allocate without bound.
        ///
        /// Returns null with <paramref name="failure"/> saying why; the caller decides what a failure means for
        /// it (the resolver backs off, the client sync stops and tells the player).
        /// </summary>
        internal static MemoryStream DownloadCapped(string url, long capBytes, CancellationToken token, out string failure) {
            failure = null;
            string current = url;

            for (int hop = 0; hop <= MaxRedirects; hop++) {
                if (!IsAllowedUrl(current)) {
                    failure = $"refused to follow '{current}': not a permitted Thunderstore host";
                    return null;
                }

                using (HttpResponseMessage response = http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, token).GetAwaiter().GetResult()) {
                    if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400) {
                        Uri location = response.Headers.Location;
                        if (location == null) {
                            failure = $"HTTP {(int)response.StatusCode} with no redirect target";
                            return null;
                        }
                        current = location.IsAbsoluteUri ? location.ToString() : new Uri(new Uri(current), location).ToString();
                        continue;
                    }

                    if (!response.IsSuccessStatusCode) {
                        failure = $"HTTP {(int)response.StatusCode}";
                        return null;
                    }

                    long? declared = response.Content.Headers.ContentLength;
                    if (declared.HasValue && declared.Value > capBytes) {
                        failure = $"archive is {declared.Value / (1024 * 1024)}MB, over the {capBytes / (1024 * 1024)}MB limit";
                        return null;
                    }

                    MemoryStream buffer = new MemoryStream(declared.HasValue ? (int)Math.Min(declared.Value, capBytes) : 1 << 20);
                    try {
                        using (Stream body = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()) {
                            byte[] chunk = new byte[64 * 1024];
                            long total = 0;
                            int read;
                            while ((read = body.Read(chunk, 0, chunk.Length)) > 0) {
                                token.ThrowIfCancellationRequested();
                                total += read;
                                if (total > capBytes) {
                                    failure = $"archive exceeded the {capBytes / (1024 * 1024)}MB limit while downloading";
                                    buffer.Dispose();
                                    return null;
                                }
                                buffer.Write(chunk, 0, read);
                            }
                        }
                        buffer.Position = 0;
                        return buffer;
                    } catch {
                        buffer.Dispose();
                        throw;
                    }
                }
            }

            failure = $"more than {MaxRedirects} redirects";
            return null;
        }
    }
}
