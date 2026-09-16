using System;
using System.Collections.Generic;
using ValheimEnforcer.common;
using ValheimEnforcer.modules.cheatmonitor;
using ValheimEnforcer.modules.notifications;

namespace ValheimEnforcer.modules.worldintegrity {

    /// <summary>
    /// Watches the items appearing in players' inventories for gear that was never made by anybody.
    ///
    /// Two signals, both about the crafter recorded on an item:
    ///
    ///   No crafter at all - the state of anything conjured with devcommands, a mod menu or a give command,
    ///   because only the crafting path ever writes the field. Flagged only for equipment the world offers no
    ///   uncrafted route to, which <see cref="ItemOriginIndex"/> works out from the loaded prefabs; otherwise
    ///   every looted circlet and shop-bought fishing rod would report.
    ///
    ///   A crafter nobody here has ever been - the case where somebody thought to stamp an id on a spawned
    ///   item and had to invent one. See <see cref="KnownPlayerIds"/>.
    ///
    /// Only items that have just <i>appeared</i> are examined, never whole inventories. That is what keeps this
    /// quiet: a character carrying gear from before the feature existed is never re-litigated, so no migration,
    /// grace period or one-time baseline is needed.
    ///
    /// <b>The honest limit.</b> The crafter id arrives inside a PackedItem the client wrote, so a client that
    /// thinks to set the field to a plausible value defeats both checks. This catches items that were spawned,
    /// which is how the tools actually in circulation hand out gear - none of them bother with a crafter. Price
    /// it as a good detector of careless cheating and not as an item-integrity guarantee, which is also why it
    /// only ever warns.
    /// </summary>
    internal static class ItemOriginValidator {

        /// <summary>Individual items named in one report before the rest are summarised.</summary>
        private const int LogDetailCap = 10;

        /// <summary>One report per player per minute at most, however many items were involved.</summary>
        private static readonly TimeSpan ReportCooldown = TimeSpan.FromMinutes(1);
        private static readonly Dictionary<string, DateTime> lastReported = new Dictionary<string, DateTime>();

        private sealed class Finding {
            internal string Prefab;
            internal int Stack;
            internal int Quality;
            internal string Why;
            public override string ToString() {
                return $"{Prefab} x{Stack} (quality {Quality}) - {Why}";
            }
        }

        internal static bool Enabled() {
            return ValConfig.DetectItemOrigins != null && ValConfig.DetectItemOrigins.Value
                && ZNet.instance != null && ZNet.instance.IsServer();
        }

        /// <summary>
        /// Inspects the items one delta says have just been added, and reports anything that cannot have been
        /// obtained legitimately.
        /// </summary>
        /// <param name="account">The reporting connection's account id, for the report.</param>
        /// <param name="characterName">The character the delta is for.</param>
        /// <param name="isAdmin">Admins are exempt, matching every other detector here.</param>
        internal static void InspectDelta(DataObjects.DeltaSummaryUpdate delta, string account, string characterName, bool isAdmin) {
            try {
                if (!Enabled() || delta?.ItemModifications == null) { return; }

                // Recorded before the items are judged, so a player's own freshly-reported id is already on file
                // when their first delta is checked. Without this the very first delta of a session would judge
                // that player's own crafting against a registry that has never heard of them.
                KnownPlayerIds.Note(delta.PlayerID, account);

                if (isAdmin && ValConfig.ItemOriginExemptAdmins.Value) { return; }
                if (!ItemOriginIndex.EnsureBuilt()) { return; }

                List<Finding> findings = new List<Finding>();
                foreach (DataObjects.ItemDelta change in delta.ItemModifications) {
                    if (change == null || change.Op != DataObjects.ItemDeltaChangeType.Added) { continue; }
                    string why = Judge(change.Item);
                    if (why == null) { continue; }
                    findings.Add(new Finding {
                        Prefab = change.Item.prefabName,
                        Stack = change.Item.m_stack,
                        Quality = change.Item.m_quality,
                        Why = why,
                    });
                }

                if (findings.Count > 0) { Report(account, characterName, findings); }
            } catch (Exception e) {
                // A detector must never take the character sync down with it.
                Logger.LogWarning($"Item origin validation failed: {e}");
            }
        }

        /// <summary>Why this item should not exist, or null when there is nothing wrong with it.</summary>
        private static string Judge(DataObjects.PackedItem item) {
            if (item == null || string.IsNullOrEmpty(item.prefabName)) { return null; }
            if (IsIgnored(item.prefabName)) { return null; }
            // An item the index has never heard of is one this server's content does not describe. Nothing can
            // be concluded from that, so it passes.
            if (!ItemOriginIndex.Knows(item.prefabName)) { return null; }

            if (item.m_crafterID == 0L) {
                if (!ValConfig.DetectUncraftedEquipment.Value) { return null; }
                if (!ItemOriginIndex.IsEquipment(item.prefabName)) { return null; }
                if (ItemOriginIndex.IsObtainableUncrafted(item.prefabName)) { return null; }
                return "equipment with no crafter, and nothing in this world drops, sells or spawns it";
            }

            if (!ValConfig.DetectUnknownCrafterIds.Value) { return null; }
            if (KnownPlayerIds.IsKnown(item.m_crafterID)) { return null; }
            return $"crafted by player id {item.m_crafterID}, which no player on this server has ever reported";
        }

        private static bool IsIgnored(string prefabName) {
            foreach (string entry in CheatToolCatalog.SplitList(ValConfig.IgnoredItemOriginPrefabs.Value)) {
                if (prefabName.IndexOf(entry, StringComparison.OrdinalIgnoreCase) >= 0) { return true; }
            }
            return false;
        }

        private static void Report(string account, string characterName, List<Finding> findings) {
            string who = $"{characterName} ({account})";

            int shown = Math.Min(findings.Count, LogDetailCap);
            for (int i = 0; i < shown; i++) {
                Logger.LogWarning($"Item origin: {who} gained {findings[i]}.");
            }
            if (findings.Count > shown) {
                Logger.LogWarning($"Item origin: and {findings.Count - shown} more from {who} in the same update.");
            }

            Notify(account, characterName, findings);
        }

        private static void Notify(string account, string characterName, List<Finding> findings) {
            if (ValConfig.DiscordNotifyItemOrigin == null || !ValConfig.DiscordNotifyItemOrigin.Value) { return; }

            // A cheat tool hands over a whole loadout at once, and a webhook post per item would walk the
            // server straight into Discord's rate limiter. Same shape as the structure notifier.
            string key = string.IsNullOrEmpty(account) ? characterName ?? "" : account;
            if (lastReported.TryGetValue(key, out DateTime last) && DateTime.UtcNow - last < ReportCooldown) { return; }
            lastReported[key] = DateTime.UtcNow;

            Finding first = findings[0];
            DiscordNotifier.Notify(NotificationEvent.ItemOriginFlagged, new Dictionary<string, string> {
                { "player", characterName ?? "unknown" },
                { "playerId", account ?? "unknown" },
                { "prefab", first.Prefab },
                { "quality", first.Quality.ToString() },
                { "count", findings.Count.ToString() },
                { "reason", first.Why },
            });
        }

        internal static void Reset() {
            lastReported.Clear();
        }

        /// <summary>Drops a departing player's report cooldown. Called from the ZNet.Disconnect hook. Keys are
        /// the account spelling the client's delta carried, which matches the socket's only through
        /// PlatformIds, so this compares rather than looks up - the table is small and this runs once per
        /// disconnect.</summary>
        internal static void Forget(string account) {
            if (string.IsNullOrEmpty(account)) { return; }
            List<string> gone = null;
            foreach (string key in lastReported.Keys) {
                if (PlatformIds.Matches(key, account)) { (gone ??= new List<string>()).Add(key); }
            }
            if (gone == null) { return; }
            foreach (string key in gone) { lastReported.Remove(key); }
        }

        internal static int TrackedCount => lastReported.Count;
    }
}
