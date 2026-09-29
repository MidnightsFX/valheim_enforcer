using System;
using System.Collections.Generic;
using ValheimEnforcer.modules.character;
using ValheimEnforcer.modules.notifications;

namespace ValheimEnforcer.modules.cheatmonitor {

    /// <summary>
    /// The medium-confidence tier: something a client did or reported that says its anti-cheat may have been
    /// interfered with, but not strongly enough to act on alone. Written to the server log and posted to the
    /// moderation channel; nobody is kicked or banned from here.
    /// </summary>
    internal static class CheatSuspicion {

        /// <summary>
        /// Minimum gap between Discord posts about one player. The log line is always written; only the post
        /// is held back, so several findings from one client arriving together make one message.
        /// </summary>
        private static readonly TimeSpan NotifyCooldown = TimeSpan.FromSeconds(60);
        private static readonly Dictionary<string, DateTime> lastNotified = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        /// <summary>Server, main thread.</summary>
        internal static void Report(ZNetPeer peer, string tool, string detail, string reason) {
            string playerName = peer?.m_playerName;
            string hostId = peer != null ? PeerIdentity.AccountFor(peer) : null;
            string who = string.IsNullOrEmpty(hostId) ? (playerName ?? "unknown") : $"{playerName} ({hostId})";
            Logger.LogWarning($"Medium-confidence sighting from {who}: {tool} - {reason}{(string.IsNullOrEmpty(detail) ? "" : $" [{detail}]")}");

            if (ValConfig.DiscordNotifyCheatSuspected == null || !ValConfig.DiscordNotifyCheatSuspected.Value) { return; }
            string key = string.IsNullOrEmpty(hostId) ? playerName ?? "" : hostId;
            if (lastNotified.TryGetValue(key, out DateTime last) && DateTime.UtcNow - last < NotifyCooldown) { return; }
            lastNotified[key] = DateTime.UtcNow;

            DiscordNotifier.Notify(NotificationEvent.CheatSuspected, new Dictionary<string, string> {
                { "player", playerName ?? "unknown" },
                { "playerId", hostId ?? "unknown" },
                { "tool", tool ?? "" },
                { "detail", string.IsNullOrEmpty(detail) ? "-" : detail },
                { "reason", reason ?? "" },
            });
        }

        internal static void Forget(string hostId) {
            if (!string.IsNullOrEmpty(hostId)) { lastNotified.Remove(hostId); }
        }

        internal static void Reset() {
            lastNotified.Clear();
        }
    }
}
