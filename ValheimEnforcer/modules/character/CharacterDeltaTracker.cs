using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimEnforcer;
using ValheimEnforcer.common;
using ValheimEnforcer.modules.character;
using ValheimEnforcer.modules.compat;
using static ValheimEnforcer.common.DataObjects;
using Logger = ValheimEnforcer.Logger;

namespace ValheimEnforcer.modules.character {
    internal static class CharacterDeltaTracker {
        internal static float LastDeltaSyncTime = 0;
        internal static DeltaChangeTracker DeltaTracker;

        // Coalesce a burst of changes into one update - a single stack move fires Inventory.Changed repeatedly.
        internal const float SettleSeconds = 2f;

        private static Inventory watched;
        internal static bool BaselineDirty { get; private set; }
        internal static float DirtySince { get; private set; }

        internal static void Initialize() {
            if (ZNet.instance != null && ZNet.instance.IsDedicated() || DeltaTracker != null) { return; }
            GameObject host = new GameObject("VE_ItemDeltaTracker");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            DeltaTracker = host.AddComponent<DeltaChangeTracker>();
            Logger.LogDebug("ItemDeltaTracker initialized.");
        }

        // Watch the local player's inventory for changes instead of polling on a fixed timer. This is what keeps
        // the tracked baseline correct without the enforcer having to know when anything else has finished
        // changing it: a death mod that hands items back when the player confirms a UI panel, tombstone loot,
        // crafting and chest transfers all land here through the same Inventory.Changed callback.
        // Called from the Game.SpawnPlayer postfix, because each spawn builds a new Player with a new Inventory.
        internal static void WatchInventory(Player player) {
            if (player == null) { return; }
            Inventory inv = player.GetInventory();
            if (inv == null || ReferenceEquals(inv, watched)) { return; }
            StopWatching();
            watched = inv;
            watched.m_onChanged += MarkBaselineDirty;
            Logger.LogDebug("Watching local player inventory for changes.");
        }

        internal static void StopWatching() {
            if (watched != null) { watched.m_onChanged -= MarkBaselineDirty; }
            watched = null;
            BaselineDirty = false;
        }

        // Runs inside inventory mutation, so it stays trivial - the real work happens on the next Update tick.
        // Internal so a change that never touches the inventory (ForsakenPower, FoodSync) can wake the flush the same way.
        internal static void MarkBaselineDirty() {
            BaselineDirty = true;
            DirtySince = Time.unscaledTime;
        }

        internal static void ClearDirty() {
            BaselineDirty = false;
        }

        // Null when the item has no resolvable ItemDrop prefab; callers skip those, exactly as
        // Character.AddItemToPlayerItems does, so the delta stream and the tracked baseline stay describing the
        // same set of items.
        internal static PackedItem BuildPackedItem(ItemDrop.ItemData item) {
            return PackedItem.From(item);
        }

        /// <summary>
        /// One walk of the live inventory, packed. Kept separate so a flush can reuse the same snapshot
        /// for the diff and for the baseline refresh instead of packing every item twice - packing
        /// copies each item's custom data dictionary, so the second walk was pure waste.
        /// </summary>
        internal static List<PackedItem> PackCurrentInventory() {
            List<PackedItem> packedItems = new List<PackedItem>();
            foreach (ItemDrop.ItemData item in Player.m_localPlayer.GetInventory().GetAllItems()) {
                PackedItem packed = BuildPackedItem(item);
                if (packed == null) { continue; } // untrackable (no ItemDrop prefab) - never entered the baseline either
                packedItems.Add(packed);
            }
            return packedItems;
        }

        internal static List<ItemDelta> BuildCharacterItemDeltas(List<PackedItem> currentItems) {
            List<ItemDelta> itemDeltas = new List<ItemDelta>();
            if (CharacterManager.PlayerCharacter == null) return itemDeltas;

            // Multiset diff: pair every baseline entry off against at most one entry in the current snapshot.
            // Both sides routinely hold several equal PackedItems (two identical wood stacks), so matching has to
            // consume its match - a whole-list Contains would let one surviving stack cancel every baseline copy,
            // or let one lost stack report every copy as removed.
            //
            // Bucketed by value rather than scanned with IndexOf. The scan was O(n*m) and every probe ran
            // PackedItem.Equals, which walks both items' custom data in both directions - on a full inventory of
            // modded items that comparison was the bulk of a flush. GetHashCode is built from exactly the fields
            // Equals compares, with the same quality normalisation and the same ignored custom-data keys, so
            // hashing agrees with equality here. A queue per bucket keeps the old first-match-wins behaviour:
            // equal items can still differ in the fields identity ignores (durability, grid position, equipped),
            // and the delta must carry the same instance IndexOf would have picked.
            Dictionary<PackedItem, Queue<PackedItem>> unmatched = new Dictionary<PackedItem, Queue<PackedItem>>();
            foreach (PackedItem packed in currentItems) {
                if (!unmatched.TryGetValue(packed, out Queue<PackedItem> bucket)) {
                    bucket = new Queue<PackedItem>();
                    unmatched[packed] = bucket;
                }
                bucket.Enqueue(packed);
            }

            foreach (PackedItem baselineItem in CharacterManager.PlayerCharacter.PlayerItems) {
                if (baselineItem == null) { continue; } // corrupt save; never put a null on the wire
                if (unmatched.TryGetValue(baselineItem, out Queue<PackedItem> bucket) && bucket.Count > 0) {
                    bucket.Dequeue();
                    continue;
                }
                itemDeltas.Add(new ItemDelta {
                    Item = baselineItem,
                    Op = ItemDeltaChangeType.Removed
                });
            }

            // Whatever the baseline could not account for is new.
            foreach (Queue<PackedItem> bucket in unmatched.Values) {
                foreach (PackedItem newItem in bucket) {
                    itemDeltas.Add(new ItemDelta {
                        Item = newItem,
                        Op = ItemDeltaChangeType.Added
                    });
                }
            }

            return itemDeltas;
        }
    }
}

internal class DeltaChangeTracker : MonoBehaviour {

    public void Update() {

        // Change driven rather than polled: an idle player produces no work and sends nothing at all. Full
        // character saves are still not driven from here; the server pulls them on its own schedule (see
        // FullSyncScheduler) and the client responds to that request via OnClientReceiveFullSyncRequest.
        if (!CharacterDeltaTracker.BaselineDirty) { return; }
        if (Time.unscaledTime < CharacterDeltaTracker.DirtySince + CharacterDeltaTracker.SettleSeconds) { return; }
        // Rate limit, so a player who keeps rearranging their inventory cannot spam the server.
        if (Time.unscaledTime < CharacterDeltaTracker.LastDeltaSyncTime) { return; }
        // Not in a state where the change can be recorded yet. Leave the dirty flag set rather than clearing it,
        // so the pending change is picked up on a later tick instead of being silently dropped.
        if (CharacterManager.PlayerCharacter == null || Player.m_localPlayer == null || ZNet.instance == null) { return; }

        CharacterDeltaTracker.LastDeltaSyncTime = Time.unscaledTime + ValConfig.DeltaSynchronizationFrequencyInSeconds.Value;
        CharacterDeltaTracker.ClearDirty();

        // Every one of these runs on the main thread, and it fires after any inventory change - which
        // includes taking a hit, so it lands in the middle of combat. Timed so a slow flush shows up
        // as itself rather than as an unexplained hitch.
        StallWatch timer = StallWatch.Start("Character delta flush");
        try {
            SyncChangesToServer();
        } finally {
            timer.Stop();
        }
    }

    private static void SyncChangesToServer() {
        Logger.LogDebug("Checking for character changes to sync to server...");
        // One inventory walk, reused below for the baseline refresh.
        List<PackedItem> currentPlayerItems = CharacterDeltaTracker.PackCurrentInventory();
        List<ItemDelta> itemDeltas = CharacterDeltaTracker.BuildCharacterItemDeltas(currentPlayerItems);

        // This comparison only started producing results once the two dictionaries stopped being the same
        // object (they used to be aliased, so it diffed a dictionary against itself and never saw a change).
        // Both are defaulted here because they are now actually walked.
        Dictionary<string, string> currentCustomData = Player.m_localPlayer.m_customData ?? new Dictionary<string, string>();
        Dictionary<string, string> trackedCustomData = CharacterManager.PlayerCharacter.PlayerCustomData ?? new Dictionary<string, string>();
        Dictionary<string, string> customDataModifications = new Dictionary<string, string>();
        List<string> customDataRemovedKeys = new List<string>();

        foreach (KeyValuePair<string, string> kvp in currentCustomData) {
            // Pass-through compat keys live only on the player; the tracked baseline never holds them, so
            // without this skip the (large) ExtraSlots backup would stream as a "new key" on every flush.
            if (CompatCustomData.IsPassthroughPlayerKey(kvp.Key)) { continue; }
            // has the key already
            if (trackedCustomData.ContainsKey(kvp.Key)) {
                // Data update
                if (trackedCustomData[kvp.Key] != kvp.Value) {
                    customDataModifications.Add(kvp.Key, kvp.Value);
                }
            } else {
                // new key, add to modifications
                customDataModifications.Add(kvp.Key, kvp.Value);
                continue;
            }
        }
        foreach(KeyValuePair<string, string> kvp in trackedCustomData) {
            if (CompatCustomData.IsPassthroughPlayerKey(kvp.Key)) { continue; }
            if (!currentCustomData.ContainsKey(kvp.Key)) {
                customDataRemovedKeys.Add(kvp.Key);
            }
        }

        // Selecting a power at a boss stone never touches the inventory; ForsakenPower's SetGuardianPower postfix is
        // what marks the tracker dirty for it. Null when the power is not being tracked.
        string currentGuardianPower = ForsakenPower.Capture(Player.m_localPlayer);
        bool guardianPowerChanged = currentGuardianPower != null && currentGuardianPower != CharacterManager.PlayerCharacter.GuardianPower;

        // Eating usually changes the inventory as well, but a feast does not; FoodSync's EatFood postfix marks the
        // tracker dirty for that. Null when foods are not being tracked.
        List<PackedFood> currentFoods = FoodSync.Capture(Player.m_localPlayer);
        bool foodsChanged = FoodSync.ChangedSince(currentFoods, CharacterManager.PlayerCharacter.Foods);

        // No delta changes need to be sent
        // Skills (and food burn time) are a lower priority update and will get updated when the next item, or custom data change happens
        if (itemDeltas.Count == 0 && customDataModifications.Count == 0 && customDataRemovedKeys.Count == 0 && !guardianPowerChanged && !foodsChanged) { return; }
        Logger.LogDebug("Changes found, syncing deltas.");

        // Refresh the in-memory baseline first, and unconditionally. This used to sit behind a server-peer check,
        // which meant a singleplayer or listen-host session never refreshed it at all: the baseline stayed frozen
        // at whatever the player had when they logged in, which is what made the post-death item restore hand
        // back a full duplicate of a pre-death inventory that was already sitting in the tombstone.
        CharacterManager.PlayerCharacter.PlayerItems = currentPlayerItems;
        // Copy, never alias. currentCustomData IS Player.m_localPlayer.m_customData; storing the reference
        // would make the baseline and the live dictionary the same object, and the comparison above would
        // then be comparing a dictionary with itself - which is why custom data changes were never detected.
        CharacterManager.PlayerCharacter.PlayerCustomData = CompatCustomData.SnapshotForTracking(currentCustomData);
        // Built once and used for both the baseline and the payload below. The payload is serialized and
        // dropped inside this method, so the two sharing one dictionary cannot outlive the flush.
        var skillLevels = Player.m_localPlayer.GetSkills().GetSkillList().ToDictionary(s => s.m_info.m_skill, s => s.m_level);
        CharacterManager.PlayerCharacter.SkillLevels = skillLevels;
        CharacterManager.PlayerCharacter.GuardianPower = currentGuardianPower;
        CharacterManager.PlayerCharacter.Foods = currentFoods;

        Dictionary<string, PackedStatusEffect> currentActiveEffects = new Dictionary<string, PackedStatusEffect>();
        foreach (StatusEffect se in Player.m_localPlayer.GetSEMan().GetStatusEffects()) {
            currentActiveEffects.Add(se.name, new PackedStatusEffect(se));
        }

        ZNetPeer serverPeer = ZNet.instance.GetServerPeer();
        if (serverPeer == null) {
            // Singleplayer / listen host: there is no server peer because we ARE the server, so the local write
            // is the authoritative save. The rate limit above bounds how often this touches disk.
            // Mid-session, so it records the session as still active - a clean logout overwrites this with Clean.
            CharacterManager.PlayerCharacter.LastDisconnect = DisconnectionState.DirtyDisconnect;
            ValConfig.WritePlayerCharacterToSave(CharacterManager.PlayerCharacter.HostID, CharacterManager.PlayerCharacter, routine: true);
            Logger.LogDebug($"Baseline refresh written locally: {currentPlayerItems.Count} items.");
            return;
        }

        DeltaSummaryUpdate payload = new DeltaSummaryUpdate {
            Name = CharacterManager.PlayerCharacter.Name,
            HostID = CharacterManager.PlayerCharacter.HostID,
            // The id this character stamps on everything it crafts. Sent so the server can tell a crafter it
            // has seen from one somebody made up; see worldintegrity/KnownPlayerIds.
            PlayerID = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerID() : 0L,
            // A routine delta is emitted mid-session, so the server save is only current as of this update:
            // if the player disappears without a clean logout it is a dirty (stale) disconnect. A clean logout
            // sends a full save with LastDisconnect = Clean, which is the last write and wins.
            DisconnectionState = DisconnectionState.DirtyDisconnect,
            ItemModifications = itemDeltas,
            SkillLevels = skillLevels,
            PlayerCustomDataModifications = customDataModifications,
            RemovedCustomDataKeys = customDataRemovedKeys,
            ActiveCharacterEffects = currentActiveEffects,
            GuardianPower = currentGuardianPower,
            Foods = currentFoods,
        };

        ZPackage package = new ZPackage();
        package.Write(DataObjects.yamlserializer.Serialize(payload));
        ValConfig.ItemDeltaUpdateRPC.SendPackage(serverPeer.m_uid, package);

        Logger.LogDebug($"Delta flush: {itemDeltas.Count} items, {customDataModifications.Count} ({customDataRemovedKeys.Count} removed) custom data changes. Skill levels updated.");
    }
}

