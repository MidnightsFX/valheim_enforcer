using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.bannetwork {

    /// <summary>What a publish came back with.</summary>
    internal sealed class PushResult {
        internal bool Ok;
        internal KeyState State = KeyState.Error;
        internal string Note;
        internal string Error;
        internal bool RateLimited;
        internal int RetryAfterMinutes;
        internal List<DataObjects.BanReportResult> Results = new List<DataObjects.BanReportResult>();
    }

    /// <summary>What a call to the ban network came back with.</summary>
    internal sealed class PullResult {
        internal bool Ok;
        internal KeyState State = KeyState.Error;
        internal string Note;
        internal string Error;
        internal List<NetworkBanRecord> Entries = new List<NetworkBanRecord>();
        internal long NextCursor;
        internal bool More;
        internal int Skipped;
        /// <summary>The service asked everyone to slow down; honoured when longer than our own interval.</summary>
        internal int MinIntervalMinutes;
        /// <summary>The cursor is behind the retention horizon: drop everything and pull from zero.</summary>
        internal bool ResyncRequired;
    }

    /// <summary>
    /// Talks to the ban network over HTTPS.
    ///
    /// <para>House style is set by DiscordNotifier and ThunderstoreResolver: one static HttpClient, https
    /// only, an explicit check of the URL's shape, ConfigureAwait(false) throughout, and the credential never
    /// logged. This is stricter than ThunderstoreResolver in one respect - it does not follow redirects at
    /// all, where that one follows them and re-checks each hop. Following a redirect while carrying an
    /// Authorization header is how a credential ends up somewhere it was never meant to go, and our own API
    /// never redirects, so a redirect here means something is wrong and the right answer is to stop.</para>
    ///
    /// <para>Everything here runs on a background thread and touches no Unity or ZNet state. Config values
    /// are passed in, captured on the main thread by the caller.</para>
    /// </summary>
    internal static class BanNetworkClient {

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient() {
            HttpClientHandler handler = new HttpClientHandler {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            };
            HttpClient client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Add("User-Agent", $"ValheimEnforcer/{ValheimEnforcer.PluginVersion}");
            client.DefaultRequestHeaders.Add("X-VEBN-Protocol", "1");
            return client;
        }

        /// <summary>
        /// Validates the configured endpoint before anything is sent to it.
        ///
        /// Refusing userinfo, a query and a fragment is not pedantry: an endpoint carrying credentials or a
        /// query is a sign the value was pasted from somewhere it should not have been, and this is a setting
        /// an admin can type freely into.
        /// </summary>
        internal static bool TryBuildBase(string configured, out Uri baseUri, out string error) {
            baseUri = null;
            error = null;
            if (string.IsNullOrWhiteSpace(configured)) { error = "no endpoint is configured"; return false; }
            if (!Uri.TryCreate(configured.Trim(), UriKind.Absolute, out Uri parsed)) {
                error = "not a valid URL";
                return false;
            }
            if (parsed.Scheme != Uri.UriSchemeHttps) { error = "must be https"; return false; }
            if (!string.IsNullOrEmpty(parsed.UserInfo)) { error = "must not contain a username or password"; return false; }
            if (!string.IsNullOrEmpty(parsed.Query)) { error = "must not contain a query string"; return false; }
            if (!string.IsNullOrEmpty(parsed.Fragment)) { error = "must not contain a fragment"; return false; }
            baseUri = parsed;
            return true;
        }

        internal static async Task<PullResult> Status(Uri baseUri, string apiKey) {
            return await Call(baseUri, apiKey, "v1/status", parseEntries: false).ConfigureAwait(false);
        }

        /// <summary>
        /// Publishes a batch of reports. The response says what became of each line individually, so a
        /// partially accepted batch does not make this server resend the half that landed.
        /// </summary>
        internal static async Task<PushResult> Push(Uri baseUri, string apiKey, string ndjsonBody) {
            PushResult result = new PushResult();
            Uri target;
            try {
                target = new Uri(baseUri, "v1/reports");
            } catch (Exception e) {
                result.Error = $"could not build a request URL: {e.Message}";
                return result;
            }

            try {
                using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, target)) {
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
                    request.Content = new StringContent(ndjsonBody, new UTF8Encoding(false), "application/x-ndjson");
                    using (HttpResponseMessage response = await Http.SendAsync(request).ConfigureAwait(false)) {
                        result.State = StateFor(response);
                        result.Note = Header(response, "X-VEBN-Server-Status");
                        result.RetryAfterMinutes = RetryAfterMinutes(response);

                        if (!response.IsSuccessStatusCode) {
                            result.Error = await DescribeFailure(response).ConfigureAwait(false);
                            // 429 is not a failure of the key, and must not be recorded as one - it is the
                            // service asking this server to slow down, which it should simply do.
                            if ((int)response.StatusCode == 429) { result.RateLimited = true; }
                            return result;
                        }

                        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        result.Results = BanNetworkJson.ParseResults(body);
                        result.Ok = true;
                        result.State = KeyState.Ok;
                        return result;
                    }
                }
            } catch (TaskCanceledException) {
                result.Error = "the request timed out";
                return result;
            } catch (Exception e) {
                result.Error = e.Message;
                return result;
            }
        }

        internal static async Task<PullResult> Pull(Uri baseUri, string apiKey, long since, int limit) {
            return await Call(baseUri, apiKey, $"v1/bans?since={since}&limit={limit}", parseEntries: true)
                .ConfigureAwait(false);
        }

        private static async Task<PullResult> Call(Uri baseUri, string apiKey, string relative, bool parseEntries) {
            PullResult result = new PullResult();
            Uri target;
            try {
                target = new Uri(baseUri, relative);
            } catch (Exception e) {
                result.Error = $"could not build a request URL: {e.Message}";
                return result;
            }

            try {
                using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, target)) {
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
                    using (HttpResponseMessage response = await Http.SendAsync(request).ConfigureAwait(false)) {
                        return await Interpret(response, result, parseEntries).ConfigureAwait(false);
                    }
                }
            } catch (TaskCanceledException) {
                result.Error = "the request timed out";
                return result;
            } catch (Exception e) {
                // Never include the request URL in a message that could carry the key. It cannot here - the
                // key is a header - but the habit is worth keeping.
                result.Error = e.Message;
                return result;
            }
        }

        private static async Task<PullResult> Interpret(HttpResponseMessage response, PullResult result, bool parseEntries) {
            result.State = StateFor(response);
            result.Note = Header(response, "X-VEBN-Server-Status");
            result.MinIntervalMinutes = IntHeader(response, "X-VEBN-Min-Interval");

            if ((int)response.StatusCode == 409) {
                // The cursor predates a purge, so continuing from it would leave this server enforcing bans
                // the network has already retracted. Start over rather than carry on with a partial picture.
                result.ResyncRequired = string.Equals(Header(response, "X-VEBN-Resync"), "full", StringComparison.OrdinalIgnoreCase);
                result.Error = "the local cursor is older than the network's retention window";
                result.State = KeyState.Ok;
                return result;
            }

            if (!response.IsSuccessStatusCode) {
                result.Error = await DescribeFailure(response).ConfigureAwait(false);
                return result;
            }

            result.Ok = true;
            result.State = KeyState.Ok;
            result.NextCursor = LongHeader(response, "X-VEBN-Next-Cursor");
            result.More = Header(response, "X-VEBN-More") == "1";

            if (!parseEntries) { return result; }

            long length = response.Content.Headers.ContentLength ?? 0;
            if (length > BanNetworkJson.MaxResponseBytes) {
                result.Ok = false;
                result.Error = $"the response is {length} bytes, over the {BanNetworkJson.MaxResponseBytes} byte limit";
                return result;
            }

            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (body.Length > BanNetworkJson.MaxResponseBytes) {
                result.Ok = false;
                result.Error = "the response was larger than the limit";
                return result;
            }

            result.Entries = BanNetworkJson.ParseBody(body, out int skipped);
            result.Skipped = skipped;
            return result;
        }

        /// <summary>
        /// Maps a status code onto what the scheduler should do about it. 401 and 403-with-a-terminal-status
        /// stop the scheduler calling at all; 403 'pending' does not, because it is the one that resolves
        /// without anybody touching this server.
        /// </summary>
        private static KeyState StateFor(HttpResponseMessage response) {
            int code = (int)response.StatusCode;
            if (code == 401) { return KeyState.Rejected; }
            if (code == 403) {
                switch ((Header(response, "X-VEBN-Server-Status") ?? "").ToLowerInvariant()) {
                    case "pending":   return KeyState.Pending;
                    case "suspended": return KeyState.Suspended;
                    case "revoked":   return KeyState.Revoked;
                    default:          return KeyState.Revoked;
                }
            }
            return response.IsSuccessStatusCode ? KeyState.Ok : KeyState.Error;
        }

        private static async Task<string> DescribeFailure(HttpResponseMessage response) {
            string detail = "";
            try {
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!string.IsNullOrEmpty(body)) {
                    if (body.Length > 300) { body = body.Substring(0, 300); }
                    detail = ": " + body.Replace("\n", " ").Trim();
                }
            } catch (Exception) {
                // The status code alone is enough to act on.
            }
            return $"HTTP {(int)response.StatusCode}{detail}";
        }

        private static string Header(HttpResponseMessage response, string name) {
            if (response.Headers.TryGetValues(name, out IEnumerable<string> values)) {
                foreach (string value in values) { return value; }
            }
            return null;
        }

        private static long LongHeader(HttpResponseMessage response, string name) {
            return long.TryParse(Header(response, name), out long parsed) ? parsed : 0;
        }

        private static int IntHeader(HttpResponseMessage response, string name) {
            return int.TryParse(Header(response, name), out int parsed) ? parsed : 0;
        }

        /// <summary>Retry-After, in minutes, rounded up. Zero when absent.</summary>
        internal static int RetryAfterMinutes(HttpResponseMessage response) {
            if (response?.Headers?.RetryAfter == null) { return 0; }
            TimeSpan? delta = response.Headers.RetryAfter.Delta;
            if (delta.HasValue) { return (int)Math.Ceiling(delta.Value.TotalMinutes); }
            return 0;
        }
    }
}
