using System;
using System.Collections.Generic;
using ValheimEnforcer.common;
using ValheimEnforcer.modules.notifications;

namespace ValheimEnforcer.modules.bannetwork {

    /// <summary>
    /// Discord posts for the ban network.
    ///
    /// Two events, and the restraint is deliberate. A moderation channel that reports every routine refusal
    /// stops being read, and once it stops being read the one message that mattered is missed - which is the
    /// failure mode the per-player cooldowns elsewhere in this mod exist to avoid. So only refusals made on
    /// another server's word are posted, and connection trouble is posted once per change of state rather
    /// than once per failed attempt.
    /// </summary>
    internal static class BanNotifications {

        /// <summary>The last state posted about, so a service that is down all night posts once, not hourly.</summary>
        private static KeyState lastReportedState = KeyState.Ok;

        internal static void Enforced(string playerName, string hostId, BanVerdict verdict, string action) {
            if (ValConfig.BanNetworkNotifyEnforced == null || !ValConfig.BanNetworkNotifyEnforced.Value) { return; }
            if (verdict == null) { return; }

            NetworkBanRecord record = null;
            string subject = BanSubjectCache.Hash(hostId);
            if (!string.IsNullOrEmpty(subject)) { record = BanNetworkStore.Find(subject); }

            DiscordNotifier.Notify(NotificationEvent.BanEnforced, new Dictionary<string, string> {
                { "player", string.IsNullOrEmpty(playerName) ? "(not connected)" : playerName },
                { "playerId", hostId ?? "" },
                { "categories", BanCategories.Canonical(verdict.Categories) },
                { "reason", record != null && !string.IsNullOrEmpty(record.Reason) ? record.Reason : (verdict.Reason ?? "") },
                { "reporters", record != null ? record.Reporters.ToString() : "unknown" },
                { "source", verdict.Source ?? "" },
                { "action", action ?? "" },
            });
        }

        /// <summary>
        /// Posts when the ban network's state changes to something an admin should act on.
        ///
        /// Fires on the transition, not the condition: an endpoint that is unreachable for six hours is one
        /// message, not six. Recovery is posted too, because "it is working again" is the other half of the
        /// information and without it nobody knows whether to go and look.
        /// </summary>
        internal static void StateChanged(KeyState state, string note, string error) {
            if (ValConfig.BanNetworkNotifyEnforced == null || !ValConfig.BanNetworkNotifyEnforced.Value) { return; }
            if (state == lastReportedState) { return; }

            bool wasBad = lastReportedState != KeyState.Ok;
            bool isBad = state != KeyState.Ok;
            lastReportedState = state;
            if (!isBad && !wasBad) { return; }

            DiscordNotifier.Notify(NotificationEvent.BanNetworkUnavailable, new Dictionary<string, string> {
                { "state", isBad ? KeyStates.Describe(state, note) : "connected again" },
                { "detail", error ?? note ?? "" },
                { "queued", BanOutbox.Count.ToString() },
                { "held", BanNetworkStore.Count.ToString() },
            });
        }

        internal static void Reset() {
            lastReportedState = KeyState.Ok;
        }
    }
}
