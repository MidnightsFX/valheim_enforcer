using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.modules.notifications {
    internal static class DiscordNotifier {
        private static readonly TimeSpan PostTimeout = TimeSpan.FromSeconds(10);

        // Rebuilt whenever Discord.ProxyUrl changes, so a config reload takes effect on the next post the way the
        // webhook URLs do. Keyed on the raw value it was built from; a null client under a set key is a ProxyUrl
        // that could not be used. A replaced client is left to the collector rather than disposed, because
        // disposing it would cancel a post still in flight on it.
        private static readonly object clientLock = new object();
        private static string clientBuiltFor;
        private static HttpClient http;
        private static string httpVia = "";

        internal static void Initialize() {
            bool any = false;
            foreach (NotificationCategory category in (NotificationCategory[])Enum.GetValues(typeof(NotificationCategory))) {
                string url = ResolveUrl(category);
                if (string.IsNullOrWhiteSpace(url)) { continue; }
                if (!IsValidWebhookUrl(url)) {
                    // The URL itself is never logged, here or anywhere else - it is the whole secret, and a
                    // BepInEx log gets pasted into support threads.
                    Logger.LogWarning($"Discord notifications: the webhook URL for {category} is invalid, so that category is disabled. Expected https://discord.com/api/webhooks/...");
                    continue;
                }
                any = true;
                bool own = !string.IsNullOrWhiteSpace(CategoryUrl(category));
                Logger.LogDebug($"Discord notifications: {category} -> {(own ? "its own webhook" : "the shared WebhookUrl")}.");
            }

            if (any) {
                Logger.LogInfo("Discord notifications enabled.");
                // Built now rather than on the first post, so a proxy problem is in the log at startup instead of
                // whenever somebody first joins.
                Client(out _);
            } else {
                Logger.LogInfo("Discord notifications: no webhook URL configured, disabled.");
            }
        }

        internal static bool IsValidWebhookUrl(string url) {
            if (string.IsNullOrWhiteSpace(url)) { return false; }
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri u)) { return false; }
            if (u.Scheme != "https") { return false; }
            bool hostOk = u.Host == "discord.com" || u.Host == "discordapp.com"
                || u.Host == "ptb.discord.com" || u.Host == "canary.discord.com";
            return hostOk && u.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal);
        }

        /// <summary>The URL configured for this category alone, before the shared fallback is considered.</summary>
        private static string CategoryUrl(NotificationCategory category) {
            switch (category) {
                case NotificationCategory.PlayerActivity: return ValConfig.DiscordWebhookUrlPlayerActivity.Value;
                case NotificationCategory.ServerStatus: return ValConfig.DiscordWebhookUrlServerStatus.Value;
                case NotificationCategory.Moderation: return ValConfig.DiscordWebhookUrlModeration.Value;
                case NotificationCategory.ModMismatch: return ValConfig.DiscordWebhookUrlModMismatch.Value;
                default: return null;
            }
        }

        /// <summary>
        /// Where a category's messages go: its own webhook if it has one, otherwise the shared WebhookUrl.
        /// Read fresh on every send rather than cached at startup, so an edit to the config file takes effect
        /// as soon as the file watcher reloads it.
        /// </summary>
        internal static string ResolveUrl(NotificationCategory category) {
            string url = CategoryUrl(category);
            if (string.IsNullOrWhiteSpace(url)) { url = ValConfig.DiscordWebhookUrl.Value; }
            return url;
        }

        /// <summary>
        /// Reads a ProxyUrl value. True with a null proxy for an empty value, which means connect directly; false
        /// with <paramref name="problem"/> saying what is wrong. The problem never quotes the value - it can carry
        /// the proxy's password, and a BepInEx log gets pasted into support threads.
        /// </summary>
        internal static bool TryParseProxy(string value, out WebProxy proxy, out string problem) {
            proxy = null;
            problem = null;
            if (string.IsNullOrWhiteSpace(value)) { return true; }

            string text = value.Trim();
            // A bare host:port is how most proxy clients show their address, so it is taken to mean http.
            if (text.IndexOf("://", StringComparison.Ordinal) < 0) { text = "http://" + text; }
            if (!Uri.TryCreate(text, UriKind.Absolute, out Uri u) || string.IsNullOrEmpty(u.Host)) {
                problem = "it is not a valid address";
                return false;
            }
            if (u.Scheme.StartsWith("socks", StringComparison.OrdinalIgnoreCase)) {
                problem = "SOCKS proxies are not supported, use the proxy's HTTP port instead";
                return false;
            }
            if (u.Scheme != Uri.UriSchemeHttp) {
                problem = $"only http:// proxies are supported, not {u.Scheme}://";
                return false;
            }
            // A path or query means this is somewhere to post to rather than something to post through - most
            // likely a webhook relay pasted into the wrong setting.
            if (u.AbsolutePath != "/" || u.Query.Length > 0) {
                problem = "a proxy address is a host and port only, with no path";
                return false;
            }

            proxy = new WebProxy(new Uri("http://" + u.Authority + "/"));
            if (u.UserInfo.Length > 0) {
                int colon = u.UserInfo.IndexOf(':');
                string user = Uri.UnescapeDataString(colon < 0 ? u.UserInfo : u.UserInfo.Substring(0, colon));
                string password = colon < 0 ? "" : Uri.UnescapeDataString(u.UserInfo.Substring(colon + 1));
                proxy.Credentials = new NetworkCredential(user, password);
            }
            return true;
        }

        /// <summary>False with the reason when ProxyUrl is set but cannot be used.</summary>
        internal static bool IsProxyUsable(out string problem) {
            return TryParseProxy(ValConfig.DiscordProxyUrl.Value, out _, out problem);
        }

        /// <summary>
        /// The client posts go out on, built for the current ProxyUrl, and how it reaches Discord for the failure
        /// log. Null when ProxyUrl is set but unusable: an admin who set a proxy did so because the direct route
        /// is blocked or unwanted, so quietly falling back to it would be the wrong kind of helpful.
        /// </summary>
        private static HttpClient Client(out string via) {
            string setting = ValConfig.DiscordProxyUrl.Value ?? "";
            lock (clientLock) {
                if (clientBuiltFor != setting) {
                    string previous = clientBuiltFor;
                    clientBuiltFor = setting;
                    http = CreateClient(setting, previous, out httpVia);
                }
                via = httpVia;
                return http;
            }
        }

        private static HttpClient CreateClient(string setting, string previous, out string via) {
            via = "";
            if (!TryParseProxy(setting, out WebProxy proxy, out string problem)) {
                Logger.LogWarning($"Discord notifications: ProxyUrl is unusable ({problem}), so nothing will be posted until it is fixed or cleared. Expected http://host:port.");
                return null;
            }
            if (proxy == null) {
                if (!string.IsNullOrWhiteSpace(previous)) {
                    Logger.LogInfo("Discord notifications: ProxyUrl cleared, posting directly.");
                }
                return new HttpClient { Timeout = PostTimeout };
            }

            via = $" through the proxy at {proxy.Address.Authority}";
            Logger.LogInfo($"Discord notifications: posting{via}{(proxy.Credentials != null ? " with a login" : "")}.");
            return new HttpClient(new HttpClientHandler { Proxy = proxy, UseProxy = true }) { Timeout = PostTimeout };
        }

        private static bool IsActive(NotificationCategory category, out string url, out HttpClient client, out string via) {
            client = null;
            via = "";
            url = ResolveUrl(category);
            if (ZNet.instance == null || !ZNet.instance.IsServer() || !IsValidWebhookUrl(url)) { return false; }
            client = Client(out via);
            return client != null;
        }

        /// <summary>
        /// Renders an event's template and posts it. This is what call sites use; they build the tokens their
        /// event has and never touch embeds, URLs or categories.
        /// </summary>
        internal static void Notify(NotificationEvent evt, IDictionary<string, string> tokens = null) {
            NotificationCategory category = NotificationTemplates.CategoryOf(evt);
            // Checked before rendering rather than inside the send: on a server with no webhook configured -
            // which is the default - this is the difference between one URL check per join and building a
            // message that was always going to be thrown away.
            if (!IsActive(category, out _, out _, out _)) { return; }
            SendAsync(category, Build(evt, tokens));
        }

        /// <summary>
        /// As <see cref="Notify"/>, but blocks until the post completes. Only for the shutdown path, where the
        /// process is about to go away and a fire-and-forget task would never get to run.
        /// </summary>
        internal static void NotifySync(NotificationEvent evt, IDictionary<string, string> tokens = null) {
            NotificationCategory category = NotificationTemplates.CategoryOf(evt);
            if (!IsActive(category, out _, out _, out _)) { return; }
            SendSync(category, Build(evt, tokens));
        }

        private static string Build(NotificationEvent evt, IDictionary<string, string> tokens) {
            try {
                Dictionary<string, string> merged = CommonTokens();
                if (tokens != null) {
                    // Event tokens win: an event that has a more specific idea of {world} than the global one
                    // should get to use it.
                    foreach (KeyValuePair<string, string> token in tokens) { merged[token.Key] = token.Value; }
                }
                return NotificationTemplates.Render(evt, merged);
            } catch (Exception e) {
                // Every caller is inside a Harmony patch on the server's main thread. A malformed template is
                // not worth taking a connect handshake or a shutdown down over.
                Logger.LogWarning($"Discord notifications: could not build the {evt} message: {e.Message}");
                return null;
            }
        }

        /// <summary>Placeholders every event carries, so no call site has to repeat them.</summary>
        private static Dictionary<string, string> CommonTokens() {
            Dictionary<string, string> tokens = new Dictionary<string, string>(StringComparer.Ordinal) {
                { "server", ValConfig.DiscordServerLabel.Value ?? "" },
                // Round-trip ("o") format, which is the ISO-8601 Discord's embed "timestamp" field requires.
                // The embed builder this replaced supplied the timestamp itself and got the format right; now
                // the template writes the field, so the token has to carry it.
                { "timestamp", DateTime.UtcNow.ToString("o") },
                // Discord wants a number for "color". Offered as tokens so an admin picking the shipped palette
                // does not have to convert hex by hand - any other colour is a plain decimal in the template.
                { "colorGreen", Green.ToString() },
                { "colorAmber", Amber.ToString() },
                { "colorRed", Red.ToString() },
                { "colorGrey", Grey.ToString() },
                { "world", "" },
                { "onlinePlayers", "" },
            };
            try {
                if (ZNet.instance != null) {
                    tokens["world"] = ZNet.instance.GetWorldName() ?? "";
                    tokens["onlinePlayers"] = ZNet.instance.GetNrOfPlayers().ToString();
                }
            } catch (Exception e) {
                // ZNet is mid-teardown on the shutdown path; an empty {world} beats no message.
                Logger.LogDebug($"Discord notifications: could not read the world state for placeholders: {e.Message}");
            }
            return tokens;
        }

        internal static void SendAsync(NotificationCategory category, string body) {
            if (string.IsNullOrWhiteSpace(body) || !IsActive(category, out string url, out HttpClient client, out string via)) { return; }
            Task.Run(() => Post(client, via, url, body));
        }

        internal static void SendSync(NotificationCategory category, string body) {
            if (string.IsNullOrWhiteSpace(body) || !IsActive(category, out string url, out HttpClient client, out string via)) { return; }
            try {
                // Post() uses ConfigureAwait(false) throughout, so blocking here cannot deadlock on the Unity context.
                Post(client, via, url, body).Wait(TimeSpan.FromSeconds(8));
            } catch (Exception e) {
                Logger.LogWarning($"Discord notifications: synchronous send failed: {e.Message}");
            }
        }

        private static async Task Post(HttpClient client, string via, string url, string body) {
            try {
                using (StringContent content = new StringContent(body, Encoding.UTF8, "application/json")) {
                    HttpResponseMessage resp = await client.PostAsync(url, content).ConfigureAwait(false);
                    if (resp.StatusCode == HttpStatusCode.ProxyAuthenticationRequired) {
                        // Only a proxy answers 407, so the template is not the thing to check.
                        Logger.LogWarning("Discord notifications: the proxy refused the post (HTTP 407). Check the user name and password in ProxyUrl.");
                    } else if (!resp.IsSuccessStatusCode) {
                        // The template passed the load-time check, so a rejection here is Discord objecting to
                        // the document's content rather than its syntax - an embed over length, or a field it
                        // does not recognise. Naming the status is the only clue an admin gets.
                        Logger.LogWarning($"Discord notifications: webhook returned HTTP {(int)resp.StatusCode}. Check the template for this event against Discord's webhook reference.");
                    }
                }
            } catch (Exception e) {
                Logger.LogWarning($"Discord notifications: send failed{via}: {Describe(e)}");
            }
        }

        // HttpClient wraps the actual failure - a refused connection, a proxy that could not reach Discord - in an
        // exception whose own message says only that sending failed.
        private static string Describe(Exception e) {
            Exception inner = e.GetBaseException();
            return inner == e ? Tidy(e.Message) : $"{Tidy(e.Message)} ({Tidy(inner.Message)})";
        }

        // On Windows, Mono's socket error text ends in a line break and a run of NULs - the unused end of the buffer
        // the system message was read into - which would split the log line.
        private static string Tidy(string message) {
            return (message ?? "").TrimEnd('\0').Trim();
        }
    }
}
