using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.modules.notifications {
    internal static class DiscordNotifier {
        private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

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

        private static bool IsActive(NotificationCategory category, out string url) {
            url = ResolveUrl(category);
            return ZNet.instance != null && ZNet.instance.IsServer() && IsValidWebhookUrl(url);
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
            if (!IsActive(category, out _)) { return; }
            SendAsync(category, Build(evt, tokens));
        }

        /// <summary>
        /// As <see cref="Notify"/>, but blocks until the post completes. Only for the shutdown path, where the
        /// process is about to go away and a fire-and-forget task would never get to run.
        /// </summary>
        internal static void NotifySync(NotificationEvent evt, IDictionary<string, string> tokens = null) {
            NotificationCategory category = NotificationTemplates.CategoryOf(evt);
            if (!IsActive(category, out _)) { return; }
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
            if (string.IsNullOrWhiteSpace(body) || !IsActive(category, out string url)) { return; }
            Task.Run(() => Post(url, body));
        }

        internal static void SendSync(NotificationCategory category, string body) {
            if (string.IsNullOrWhiteSpace(body) || !IsActive(category, out string url)) { return; }
            try {
                // Post() uses ConfigureAwait(false) throughout, so blocking here cannot deadlock on the Unity context.
                Post(url, body).Wait(TimeSpan.FromSeconds(8));
            } catch (Exception e) {
                Logger.LogWarning($"Discord notifications: synchronous send failed: {e.Message}");
            }
        }

        private static async Task Post(string url, string body) {
            try {
                using (StringContent content = new StringContent(body, Encoding.UTF8, "application/json")) {
                    HttpResponseMessage resp = await http.PostAsync(url, content).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) {
                        // The template passed the load-time check, so a rejection here is Discord objecting to
                        // the document's content rather than its syntax - an embed over length, or a field it
                        // does not recognise. Naming the status is the only clue an admin gets.
                        Logger.LogWarning($"Discord notifications: webhook returned HTTP {(int)resp.StatusCode}. Check the template for this event against Discord's webhook reference.");
                    }
                }
            } catch (Exception e) {
                Logger.LogWarning($"Discord notifications: send failed: {e.Message}");
            }
        }
    }
}
