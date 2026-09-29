using System;
using System.Collections.Generic;
using ValheimEnforcer.common;
using ValheimEnforcer.modules.notifications;
using ValheimEnforcer.modules.worldintegrity;

namespace ValheimEnforcer.modules.cheatmonitor {

    /// <summary>
    /// Markers known cheat tools write into data the server receives anyway: custom-data keys on items, and
    /// values on the objects they place (the latter is read by StructureValidator, which already watches the
    /// objects a client creates). This half reads the items in each character delta.
    ///
    /// Read on the server from what the client sends to keep its character in sync, so it does not depend on
    /// the client's own scan. An item carrying a marker is logged and posted, never acted on: items change
    /// hands, and the player holding one may not be the one who made it.
    /// </summary>
    internal static class CheatArtifacts {

        private static readonly TimeSpan NotifyCooldown = TimeSpan.FromSeconds(60);
        private static readonly Dictionary<string, DateTime> lastNotified = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private const int LogDetailCap = 5;

        internal static bool Enabled() {
            return ValConfig.DetectCheatToolArtifacts != null && ValConfig.DetectCheatToolArtifacts.Value
                && ZNet.instance != null && ZNet.instance.IsServer();
        }

        /// <summary>Checks the items one delta adds. Server main thread; never throws.</summary>
        internal static void InspectDelta(DataObjects.DeltaSummaryUpdate delta, string account, string characterName) {
            try {
                if (!Enabled() || delta?.ItemModifications == null) { return; }
                List<string> findings = null;
                string firstTool = null;
                string firstPrefab = null;
                foreach (DataObjects.ItemDelta change in delta.ItemModifications) {
                    if (change?.Item?.m_customdata == null || change.Op != DataObjects.ItemDeltaChangeType.Added) { continue; }
                    foreach (string key in change.Item.m_customdata.Keys) {
                        string tool = CheatToolCatalog.ToolForItemDataKey(key);
                        if (tool == null) { continue; }
                        if (findings == null) { findings = new List<string>(); }
                        findings.Add($"{change.Item.prefabName} x{change.Item.m_stack} carries '{key}' ({tool})");
                        if (firstTool == null) {
                            firstTool = tool;
                            firstPrefab = change.Item.prefabName;
                        }
                        break;
                    }
                }
                if (findings == null) { return; }
                Report(account, characterName, findings, firstTool, firstPrefab);
            } catch (Exception e) {
                Logger.LogWarning($"Cheat artifact inspection failed: {e.Message}");
            }
        }

        private static void Report(string account, string characterName, List<string> findings, string tool, string prefab) {
            string who = $"{characterName} ({account})";
            int shown = Math.Min(findings.Count, LogDetailCap);
            for (int i = 0; i < shown; i++) {
                Logger.LogWarning($"Cheat artifact: {who} gained an item written by a cheat tool - {findings[i]}. Logged only: the holder may not be who made it.");
            }
            if (findings.Count > shown) {
                Logger.LogWarning($"Cheat artifact: and {findings.Count - shown} more from {who} in the same update.");
            }

            if (ValConfig.DiscordNotifyItemOrigin == null || !ValConfig.DiscordNotifyItemOrigin.Value) { return; }
            string key = string.IsNullOrEmpty(account) ? characterName ?? "" : account;
            if (lastNotified.TryGetValue(key, out DateTime last) && DateTime.UtcNow - last < NotifyCooldown) { return; }
            lastNotified[key] = DateTime.UtcNow;
            DiscordNotifier.Notify(NotificationEvent.ItemOriginFlagged, new Dictionary<string, string> {
                { "player", characterName ?? "unknown" },
                { "playerId", account ?? "unknown" },
                { "prefab", prefab ?? "unknown" },
                { "quality", "-" },
                { "count", findings.Count.ToString() },
                { "reason", $"carries item data written by {tool}" },
            });
        }

        /// <summary>
        /// A player id as it reads in a report: the id, and the account it belongs to when this server has
        /// seen that player.
        /// </summary>
        internal static string DescribePlayerId(long playerId) {
            string account = KnownPlayerIds.AccountOf(playerId);
            return string.IsNullOrEmpty(account) ? $"player id {playerId}" : $"player id {playerId}, account {account}";
        }

        internal static void Forget(string account) {
            if (!string.IsNullOrEmpty(account)) { lastNotified.Remove(account); }
        }

        internal static void Reset() {
            lastNotified.Clear();
        }
    }
}
