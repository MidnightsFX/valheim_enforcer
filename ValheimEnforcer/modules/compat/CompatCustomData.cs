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
    /// same items sat in the tombstone. Gear came back on every death, duplicated. EquipmentAndQuickSlots
    /// (its 3.x rewrite shares the ExtraSlots architecture, and its 2.x legacy keys are worse - the
    /// migration that consumes them re-adds gear with no guard at all) and InventorySlots carry the same
    /// design, so their keys are listed alongside.
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

        // Keys are matched by name rather than through any mod's API so a stale key is still shed from
        // saves on installations where the owning mod is not currently loaded - a server that never runs
        // it, or one that removed it - instead of lying in wait to resurrect gear if the mod is ever
        // (re)installed. ExtraSlotsCustomSlots (shudnal's addon) was reviewed too: it registers slots
        // purely through the ExtraSlots API and persists nothing of its own, so the ExtraSlots keys
        // already cover it.
        private static readonly HashSet<string> PassthroughPlayerKeys = new HashSet<string>() {
            // ExtraSlots InventoryBackup.customKeyBackupID: the serialized extra-rows inventory
            // described above.
            "ExtraSlotsInventoryBackup",
            // EquipmentAndQuickSlots (the 3.x rewrite) InventoryBackup.customKeyBackupID - the same
            // design as ExtraSlots: rewritten on every Player.Save, restored on Player.Load whenever the
            // slot rows are empty, restore guard knows only ServerCharacters.
            "eaqs_backup",
            // InventorySlots (sighsorry) BackupKey - the same design a third time (SaveSlotBackup /
            // TryRestoreSlotBackup).
            "InventorySlotsBackup",
            // EquipmentAndQuickSlots 2.x stored its whole equipment and quickslot side inventories in
            // these two keys, and the 3.x rewrite's Player.Load migration ADDS AND EQUIPS their contents
            // whenever a key is present - no empty-slots guard at all - then deletes the keys. Re-applying
            // a tracked copy would re-run that migration, and duplicate the gear, on every single spawn.
            // On a server still running 2.x these keys are live storage and the live value is equally the
            // only correct one: replaying a tracked copy after a death restored the pre-death loadout.
            "QuickSlotInventory",
            "EquipmentSlotInventory",
            // The matching EAQS 2.x container key, deleted by the same migration.
            "ExtendedPlayerData",
        };

        // Per-item slot bookkeeping. The ExtraSlots and eaqs_* trios are stamped onto items at save time
        // and pruned again during play, so two honest captures of the same item routinely disagree about
        // them; the InventorySlots pair marks which custom slot an item currently sits in - positional
        // metadata of exactly the kind item identity already excludes (m_gridPos). They all stay ON the
        // items - slot memory survives confiscation and restore that way - but item identity comparisons
        // have to ignore them, or moving an item between slots (or the stamp/prune cycle itself) reads as
        // a modified item and gets confiscated.
        private static readonly HashSet<string> IgnoredItemKeys = new HashSet<string>() {
            // ExtraSlots Slots.customKeyPlayerID / customKeySlotID / customKeyWeaponShield.
            "ExtraSlotsEquippedBy",
            "ExtraSlotsEquippedSlot",
            "ExtraSlotsEquippedWeaponShield",
            // EquipmentAndQuickSlots 3.x: the same stamp/prune slot memory, plus the transient marker for
            // armor parked by a gravestone pickup, which the validation sweep removes.
            "eaqs_player",
            "eaqs_slot",
            "eaqs_weaponshield",
            "eaqs_parked",
            // EquipmentAndQuickSlots 2.x grave markers, written onto tombstone items and honored once on
            // pickup by the 3.x rewrite.
            "eaqs-e",
            "eaqs-qs",
            // InventorySlots slot residence markers (MarkItemSlot / ClearItemSlot), and its
            // favorite-upgrade bookmark - a client-side UI id with no gameplay power, whose
            // delete-and-recreate cycle would otherwise read as a changed item.
            "InventorySlotsSlotId",
            "InventorySlotsEquippedBy",
            "InventorySlotsUpgradeFavoriteId",
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
