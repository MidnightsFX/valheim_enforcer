using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimEnforcer;
using ValheimEnforcer.common;
using ValheimEnforcer.modules.character;
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
        private static void MarkBaselineDirty() {
            BaselineDirty = true;
            DirtySince = Time.unscaledTime;
        }

        internal static void ClearDirty() {
            BaselineDirty = false;
        }

        internal static PackedItem BuildPackedItem(ItemDrop.ItemData item) {
            return new PackedItem {
                prefabName = item.m_dropPrefab.name,
                m_stack = item.m_stack,
                m_durability = UnityEngine.Mathf.Clamp(item.m_durability, 0, item.m_shared.m_maxDurability + (item.m_shared.m_durabilityPerLevel * UnityEngine.Mathf.Max(item.m_quality, 1))),
                m_quality = item.m_quality,
                m_variant = item.m_variant,
                m_worldlevel = item.m_worldLevel,
                m_crafterID = item.m_crafterID,
                m_crafterName = item.m_crafterName,
                m_customdata = PackedItem.CopyCustomData(item.m_customData),
                m_equipped = item.m_equipped,
                m_gridpos = item.m_gridPos,
            };
        }

        internal static List<ItemDelta> BuildCharacterItemDeltas() {
            List<ItemDelta> itemDeltas = new List<ItemDelta>();
            if (CharacterManager.PlayerCharacter == null) return itemDeltas;

            List<PackedItem> unmatched = new List<PackedItem>();
            foreach(ItemDrop.ItemData item in Player.m_localPlayer.GetInventory().GetAllItems()) {
                unmatched.Add(BuildPackedItem(item));
            }

            // Multiset diff: pair every baseline entry off against at most one entry in the current snapshot.
            // Both lists routinely hold several equal PackedItems (two identical wood stacks), so matching has to
            // consume its match - a whole-list Contains would let one surviving stack cancel every baseline copy,
            // or let one lost stack report every copy as removed.
            foreach (PackedItem baselineItem in CharacterManager.PlayerCharacter.PlayerItems) {
                if (baselineItem == null) { continue; } // corrupt save; never put a null on the wire
                int match = unmatched.IndexOf(baselineItem); // value equality via IEquatable<PackedItem>
                if (match >= 0) {
                    unmatched.RemoveAt(match);
                    continue;
                }
                itemDeltas.Add(new ItemDelta {
                    Item = baselineItem,
                    Op = ItemDeltaChangeType.Removed
                });
            }

            // Whatever the baseline could not account for is new.
            foreach (PackedItem newItem in unmatched) {
                itemDeltas.Add(new ItemDelta {
                    Item = newItem,
                    Op = ItemDeltaChangeType.Added
                });
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
        SyncChangesToServer();
    }

    private static void SyncChangesToServer() {
        Logger.LogDebug("Checking for character changes to sync to server...");
        // Take all of the deltas off the queue
        List<ItemDelta> itemDeltas = CharacterDeltaTracker.BuildCharacterItemDeltas();

        Dictionary<string, string> currentCustomData = Player.m_localPlayer.m_customData;
        Dictionary<string, string> customDataModifications = new Dictionary<string, string>();
        List<string> customDataRemovedKeys = new List<string>();

        foreach (KeyValuePair<string, string> kvp in currentCustomData) {
            // has the key already 
            if (CharacterManager.PlayerCharacter.PlayerCustomData.ContainsKey(kvp.Key)) {
                // Data update
                if (CharacterManager.PlayerCharacter.PlayerCustomData[kvp.Key] != kvp.Value) {
                    customDataModifications.Add(kvp.Key, kvp.Value);
                }
            } else {
                // new key, add to modifications
                customDataModifications.Add(kvp.Key, kvp.Value);
                continue;
            }
        }
        foreach(KeyValuePair<string, string> kvp in CharacterManager.PlayerCharacter.PlayerCustomData) {
            if (!currentCustomData.ContainsKey(kvp.Key)) {
                customDataRemovedKeys.Add(kvp.Key);
            }
        }

        // No delta changes need to be sent
        // Skills are a lower priority update and will get updated when the next item, or custom data change happens
        if (itemDeltas.Count == 0 && customDataModifications.Count == 0 && customDataRemovedKeys.Count == 0) { return; }
        Logger.LogDebug("Changes found, syncing deltas.");

        // Refresh the in-memory baseline first, and unconditionally. This used to sit behind a server-peer check,
        // which meant a singleplayer or listen-host session never refreshed it at all: the baseline stayed frozen
        // at whatever the player had when they logged in, which is what made the post-death item restore hand
        // back a full duplicate of a pre-death inventory that was already sitting in the tombstone.
        List<PackedItem> currentPlayerItems = new List<PackedItem>();
        foreach (ItemDrop.ItemData item in Player.m_localPlayer.GetInventory().GetAllItems()) {
            currentPlayerItems.Add(CharacterDeltaTracker.BuildPackedItem(item));
        }
        CharacterManager.PlayerCharacter.PlayerItems = currentPlayerItems;
        CharacterManager.PlayerCharacter.PlayerCustomData = currentCustomData;
        CharacterManager.PlayerCharacter.SkillLevels = Player.m_localPlayer.GetSkills().GetSkillList().ToDictionary(s => s.m_info.m_skill, s => s.m_level);

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
            // A routine delta is emitted mid-session, so the server save is only current as of this update:
            // if the player disappears without a clean logout it is a dirty (stale) disconnect. A clean logout
            // sends a full save with LastDisconnect = Clean, which is the last write and wins.
            DisconnectionState = DisconnectionState.DirtyDisconnect,
            ItemModifications = itemDeltas,
            SkillLevels = Player.m_localPlayer.GetSkills().GetSkillList().ToDictionary(s => s.m_info.m_skill, s => s.m_level),
            PlayerCustomDataModifications = customDataModifications,
            RemovedCustomDataKeys = customDataRemovedKeys,
            ActiveCharacterEffects = currentActiveEffects,
        };

        ZPackage package = new ZPackage();
        package.Write(DataObjects.yamlserializer.Serialize(payload));
        ValConfig.ItemDeltaUpdateRPC.SendPackage(serverPeer.m_uid, package);

        Logger.LogDebug($"Delta flush: {itemDeltas.Count} items, {customDataModifications.Count} ({customDataRemovedKeys.Count} removed) custom data changes. Skill levels updated.");
    }
}

