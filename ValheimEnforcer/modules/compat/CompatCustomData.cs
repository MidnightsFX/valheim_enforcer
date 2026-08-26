using System.Collections.Generic;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.modules.compat {
    /// <summary>
    /// Custom-data keys owned by compatible mods that merely DESCRIBE the inventory, and so must never be
    /// tracked or enforced as though they were state of their own.
    ///
    /// The motivating case is ExtraSlots. It keeps a full serialized copy of every item in its extra slot
    /// rows under one player custom-data key, rewrites that copy on every vanilla Player.Save, and on
    /// Player.Load RESTORES it - clones the items back into the inventory and re-equips them - whenever the
    /// extra slots are empty. Tracking that key made the enforcer's copy authoritative and stale at once:
    /// vanilla refreshes the live copy in the save that runs right before a death respawn, but the tracked
    /// copy still described the pre-death inventory, and re-applying it to the freshly spawned player (whose
    /// extra slots are empty by definition) handed ExtraSlots a pre-death inventory to resurrect while the
    /// same items sat in the tombstone. Gear came back on every death, duplicated.
    ///
    /// The rule that follows: a key listed here is left entirely to the mod that owns it. It is never
    /// captured into the tracked character, never written into a server save, never streamed in a delta, and
    /// - the part that matters - never applied back onto a live player, whose own value (kept current by the
    /// owning mod through the vanilla profile) always wins.
    ///
    /// Everything here must be safe off the main thread: the CharacterStore worker runs these checks through
    /// full-save ingestion, PackedItem equality and ReturningCharacterRules. That is why the config flag is
    /// mirrored into a volatile bool refreshed from the main thread instead of the ConfigEntry being read
    /// directly.
    /// </summary>
    internal static class CompatCustomData {

        // ExtraSlots InventoryBackup.customKeyBackupID: the serialized extra-rows inventory described above.
        // Matched by name rather than through the ExtraSlots API so a stale key is still shed from saves on
        // installations where ExtraSlots is not currently loaded - a server that never runs it, or one that
        // removed it - instead of lying in wait to resurrect gear if the mod is ever (re)installed.
        private static readonly HashSet<string> PassthroughPlayerKeys = new HashSet<string>() {
            "ExtraSlotsInventoryBackup",
        };

        // ExtraSlots' per-item slot memory (Slots.customKeyPlayerID / customKeySlotID /
        // customKeyWeaponShield). ExtraSlots stamps these onto items at save time and prunes them again
        // during play, so two honest captures of the same item routinely disagree about them. They stay ON
        // the items - slot memory survives confiscation and restore that way - but item identity comparisons
        // have to ignore them, or the stamp/prune cycle reads as a modified item and gets confiscated.
        private static readonly HashSet<string> IgnoredItemKeys = new HashSet<string>() {
            "ExtraSlotsEquippedBy",
            "ExtraSlotsEquippedSlot",
            "ExtraSlotsEquippedWeaponShield",
        };

        // Snapshot of PassthroughCompatModCustomData, written only from the main thread (RefreshEnabled) and
        // read from worker threads. Defaults true so anything that runs before config binding behaves like
        // the shipped default.
        private static volatile bool enabled = true;

        /// <summary>Main thread only. Re-reads the config flag; wired to its SettingChanged so a config
        /// reload or a server sync takes effect immediately.</summary>
        internal static void RefreshEnabled() {
            enabled = ValConfig.PassthroughCompatModCustomData == null || ValConfig.PassthroughCompatModCustomData.Value;
        }

        /// <summary>Whether item identity comparisons should disregard this item custom-data key.</summary>
        internal static bool IsIgnoredItemKey(string key) {
            return enabled && key != null && IgnoredItemKeys.Contains(key);
        }

        /// <summary>Whether the delta stream should not carry this player custom-data key.</summary>
        internal static bool IsPassthroughPlayerKey(string key) {
            return enabled && key != null && PassthroughPlayerKeys.Contains(key);
        }

        /// <summary>Removes pass-through keys from a tracked or stored custom-data dictionary, in place.
        /// Null-safe, and a no-op when the escape hatch is off.</summary>
        internal static void StripPassthroughKeys(IDictionary<string, string> customData) {
            if (!enabled || customData == null || customData.Count == 0) { return; }
            foreach (string key in PassthroughPlayerKeys) {
                customData.Remove(key);
            }
        }

        /// <summary>
        /// A detached snapshot of live player custom data with pass-through keys removed - the capture-side
        /// counterpart of <see cref="ApplyToPlayer"/>. Every site that records live custom data into the
        /// tracked character goes through this.
        /// </summary>
        internal static Dictionary<string, string> SnapshotForTracking(Dictionary<string, string> live) {
            Dictionary<string, string> snapshot = PackedItem.SnapshotCustomData(live);
            StripPassthroughKeys(snapshot);
            return snapshot;
        }

        /// <summary>
        /// The dictionary to install onto a live player from a tracked snapshot. Pass-through keys are never
        /// taken from the tracked copy; whatever value the player currently holds for them - the freshest
        /// truth, since the owning mod rewrites it on every vanilla save, including the one right before a
        /// death respawn - is preserved instead.
        /// </summary>
        internal static Dictionary<string, string> ApplyToPlayer(Dictionary<string, string> tracked, Dictionary<string, string> live) {
            Dictionary<string, string> result = PackedItem.SnapshotCustomData(tracked);
            if (!enabled) { return result; }
            foreach (string key in PassthroughPlayerKeys) {
                result.Remove(key);
                if (live != null && live.TryGetValue(key, out string liveValue)) {
                    result[key] = liveValue;
                }
            }
            return result;
        }
    }
}
