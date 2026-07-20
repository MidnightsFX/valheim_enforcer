using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.modules.notifications {
    internal static class DiscordNotifier {
        private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        internal static void Initialize() {
            string url = ValConfig.DiscordWebhookUrl.Value;
            if (string.IsNullOrWhiteSpace(url)) {
                Logger.LogInfo("Discord notifications: no webhook URL configured, disabled.");
                return;
            }
            if (!IsValidWebhookUrl(url)) {
                Logger.LogWarning("Discord notifications: configured webhook URL is invalid, disabled. Expected https://discord.com/api/webhooks/...");
                return;
            }
            Logger.LogInfo("Discord notifications enabled.");
        }

        internal static bool IsValidWebhookUrl(string url) {
            if (string.IsNullOrWhiteSpace(url)) { return false; }
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri u)) { return false; }
            if (u.Scheme != "https") { return false; }
            bool hostOk = u.Host == "discord.com" || u.Host == "discordapp.com"
                || u.Host == "ptb.discord.com" || u.Host == "canary.discord.com";
            return hostOk && u.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal);
        }

        private static bool IsActive(out string url) {
            url = ValConfig.DiscordWebhookUrl.Value;
            return ZNet.instance != null && ZNet.instance.IsServer() && IsValidWebhookUrl(url);
        }

        internal static void SendAsync(DiscordMessage message) {
            if (message == null || !IsActive(out string url)) { return; }
            Task.Run(() => Post(url, message));
        }

        internal static void SendSync(DiscordMessage message) {
            if (message == null || !IsActive(out string url)) { return; }
            try {
                // Post() uses ConfigureAwait(false) throughout, so blocking here cannot deadlock on the Unity context.
                Post(url, message).Wait(TimeSpan.FromSeconds(8));
            } catch (Exception e) {
                Logger.LogWarning($"Discord notifications: synchronous send failed: {e.Message}");
            }
        }

        private static async Task Post(string url, DiscordMessage message) {
            try {
                using (StringContent content = new StringContent(message.ToJson(), Encoding.UTF8, "application/json")) {
                    HttpResponseMessage resp = await http.PostAsync(url, content).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) {
                        Logger.LogWarning($"Discord notifications: webhook returned HTTP {(int)resp.StatusCode}.");
                    }
                }
            } catch (Exception e) {
                Logger.LogWarning($"Discord notifications: send failed: {e.Message}");
            }
        }
    }
}
