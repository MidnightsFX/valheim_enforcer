using System;
using System.Collections.Generic;
using ValheimEnforcer.common;
using ValheimEnforcer.modules.compat;
// Aliased rather than imported: the type is called API, which says nothing at a call site.
using ExtraSlotsAPI = ValheimEnforcer.modules.compat.ExtraSlots.API;

namespace ValheimEnforcer.modules.worldintegrity {

    /// <summary>
    /// Watches where items sit in a player's inventory for slots that inventory does not have.
    ///
    /// Every item carries the grid cell it occupies, and the client writes that field. An item at column 11 of
    /// an eight-column inventory is not suspicious, it is impossible: the only way to produce one is to have
    /// resized the grid, which is a feature of the cheat menus currently in circulation and of nothing else.
    /// Valheaven reports its own grid in the client log as it changes it - "Inv 12x8", then "10x8", then
    /// "8x8" as the player walked it back.
    ///
    /// <b>Width is the check; height is not.</b> Vanilla builds the player inventory eight columns wide
    /// (<c>Humanoid.m_inventory</c>) and offers no way to change that - <c>Player.SetInventorySize</c> takes
    /// rows only. Height is genuinely mod territory: vanilla itself sells rows up to nine at the trader and
    /// records them in the <c>invrows</c> key, ExtraSlots and AzuExtendedPlayerInventory both add rows, and
    /// EquipmentAndQuickSlots writes <c>m_height</c> directly to fit its visible and hidden slot rows. Every
    /// one of those leaves the width at eight. So the column bound ships enforceable with a default that is
    /// correct for vanilla and for all three of those mods, and the row bound ships off, for an admin who
    /// knows what their own stack produces.
    ///
    /// Negative coordinates are never flagged. <c>(-1,-1)</c> is the "no position" sentinel - ExtraSlots uses
    /// it for an empty slot and unplaced items carry it - so it means the opposite of an impossible position.
    ///
    /// <b>The honest limit.</b> The grid position arrives inside a PackedItem the client wrote, so a client
    /// that clamps the field before sending defeats this. What it cannot do is keep the extra slots: the
    /// server stores what it was told, so an item clamped on the way out is an item in a different place when
    /// the character comes back. That is the walk-back the log above caught in the act.
    /// </summary>
    internal static class InventoryBoundsValidator {

        /// <summary>Individual items named in one report before the rest are summarised.</summary>
        private const int LogDetailCap = 10;

        /// <summary>One report per player per minute at most, however many items were involved.</summary>
        private static readonly TimeSpan ReportCooldown = TimeSpan.FromMinutes(1);
        private static readonly Dictionary<string, DateTime> lastReported = new Dictionary<string, DateTime>();

        private sealed class Finding {
            internal string Prefab;
            internal int X;
            internal int Y;
            internal string Why;
            public override string ToString() {
                return $"{Prefab} at grid ({X}, {Y}) - {Why}";
            }
        }

        internal static bool Enabled() {
            return ValConfig.DetectInventoryGrid != null && ValConfig.DetectInventoryGrid.Value
                && ZNet.instance != null && ZNet.instance.IsServer();
        }

        /// <summary>
        /// Inspects the items one delta says have just been added, and reports any sitting in a cell the
        /// inventory does not have.
        /// </summary>
        /// <param name="peer">The reporting connection, for the contradiction record. May be null.</param>
        /// <param name="account">The reporting connection's account id, for the report.</param>
        /// <param name="characterName">The character the delta is for.</param>
        /// <param name="isAdmin">Admins are exempt by default, matching every other detector here.</param>
        internal static void InspectDelta(DataObjects.DeltaSummaryUpdate delta, ZNetPeer peer, string account, string characterName, bool isAdmin) {
            try {
                if (!Enabled() || delta?.ItemModifications == null) { return; }
                if (isAdmin && ValConfig.InventoryGridExemptAdmins.Value) { return; }

                int maxWidth = ValConfig.MaxInventoryWidth.Value;
                int maxHeight = ValConfig.MaxInventoryHeight.Value;

                List<Finding> findings = null;
                foreach (DataObjects.ItemDelta change in delta.ItemModifications) {
                    if (change?.Item == null || change.Op != DataObjects.ItemDeltaChangeType.Added) { continue; }
                    string why = Judge(change.Item, maxWidth, maxHeight);
                    if (why == null) { continue; }
                    (findings ??= new List<Finding>()).Add(new Finding {
                        Prefab = change.Item.prefabName,
                        X = change.Item.m_gridpos.x,
                        Y = change.Item.m_gridpos.y,
                        Why = why,
                    });
                }

                if (findings != null) { Report(peer, account, characterName, findings); }
            } catch (Exception e) {
                // A detector must never take the character sync down with it.
                Logger.LogWarning($"Inventory grid validation failed: {e}");
            }
        }

        /// <summary>Why this item cannot be where it says it is, or null when the position is fine.</summary>
        private static string Judge(DataObjects.PackedItem item, int maxWidth, int maxHeight) {
            if (string.IsNullOrEmpty(item.prefabName)) { return null; }

            int x = item.m_gridpos.x;
            int y = item.m_gridpos.y;
            // The "no position" sentinel, and anything else negative. An unplaced item is not a cheated one.
            if (x < 0 || y < 0) { return null; }

            bool tooWide = maxWidth > 0 && x >= maxWidth;
            bool tooTall = maxHeight > 0 && y >= maxHeight;
            if (!tooWide && !tooTall) { return null; }

            // Asked only of a position that has already failed, because the answer costs a reflected call into
            // another mod. ExtraSlots places its equipment slots outside the ordinary grid flow and is the one
            // mod here that can legitimately produce a cell the plain bounds reject - the same carve-out
            // PackedItem.ToItemData makes when it restores a saved slot.
            if (ModCompatability.IsExtraSlotsEnabled && IsExtraSlotsPosition(item.m_gridpos)) { return null; }

            if (tooWide) {
                return $"column {x} in an inventory {maxWidth} columns wide; nothing widens the player grid, so this cell does not exist";
            }
            return $"row {y} in an inventory {maxHeight} rows tall; this cell does not exist";
        }

        private static bool IsExtraSlotsPosition(Vector2i pos) {
            try {
                return ExtraSlotsAPI.IsGridPositionASlot(pos);
            } catch (Exception e) {
                // A compat shim that throws must not turn into a detection.
                Logger.LogDebug($"ExtraSlots slot lookup failed for {pos}: {e.Message}");
                return true;
            }
        }

        private static void Report(ZNetPeer peer, string account, string characterName, List<Finding> findings) {
            string who = $"{characterName} ({account})";

            int shown = Math.Min(findings.Count, LogDetailCap);
            for (int i = 0; i < shown; i++) {
                Logger.LogWarning($"Inventory grid: {who} has {findings[i]}.");
            }
            if (findings.Count > shown) {
                Logger.LogWarning($"Inventory grid: and {findings.Count - shown} more from {who} in the same update.");
            }

            // The same signal an RPC guard refusal carries, and it belongs there for the same reason: this is
            // not a correction the server made quietly, it is a statement about the payload that the server
            // checked against bounds it holds itself. A client whose declared mods do not include anything
            // that resizes an inventory, sending an item from a cell no inventory has, is the shape PeerTrust
            // exists to correlate. Rate-limited alongside the notification below so one cheated loadout is one
            // entry rather than fifty.
            if (!ShouldReportAgain(account, characterName)) { return; }
            network.PeerTrust.NoteGuardTrip(peer, "inventory", findings[0].ToString());
        }

        /// <summary>
        /// One report per player per minute. A menu hands over a whole loadout at once, and without this the
        /// per-item log above would be the only thing keeping the rest of the pipeline in proportion.
        /// </summary>
        private static bool ShouldReportAgain(string account, string characterName) {
            string key = string.IsNullOrEmpty(account) ? characterName ?? "" : account;
            if (lastReported.TryGetValue(key, out DateTime last) && DateTime.UtcNow - last < ReportCooldown) { return false; }
            lastReported[key] = DateTime.UtcNow;
            return true;
        }

        internal static void Reset() {
            lastReported.Clear();
        }

        /// <summary>Drops a departing player's report cooldown. Keys are the account spelling the client's
        /// delta carried, which matches the socket's only through PlatformIds, so this compares rather than
        /// looks up - see ItemOriginValidator.Forget, which this mirrors.</summary>
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
