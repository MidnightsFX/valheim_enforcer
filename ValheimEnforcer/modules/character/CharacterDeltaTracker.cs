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

        internal static void Initialize() {
            if (ZNet.instance != null && ZNet.instance.IsDedicated() || DeltaTracker != null) { return; }
            GameObject host = new GameObject("VE_ItemDeltaTracker");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            DeltaTracker = host.AddComponent<DeltaChangeTracker>();
            Logger.LogDebug("ItemDeltaTracker initialized.");
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
                m_customdata = item.m_customData,
                m_equipped = item.m_equipped,
                m_gridpos = item.m_gridPos,
            };
        }

        internal static List<ItemDelta> BuildCharacterItemDeltas() {
            List<ItemDelta> itemDeltas = new List<ItemDelta>();
            if (CharacterManager.PlayerCharacter == null) return itemDeltas;

            List<PackedItem> currentPlayerItems = new List<PackedItem>();
            foreach(ItemDrop.ItemData item in Player.m_localPlayer.GetInventory().GetAllItems()) {
                currentPlayerItems.Add(BuildPackedItem(item));
            }

            // Remove all of the previous items that still exist, these are no change
            foreach (PackedItem playerPrevItem in CharacterManager.PlayerCharacter.PlayerItems) {
                currentPlayerItems.Remove(playerPrevItem);
            }

            // Set removeItem entries for items which are no longer found in the new save data
            foreach(PackedItem removedItem in CharacterManager.PlayerCharacter.PlayerItems) {
                if (currentPlayerItems.Contains(removedItem)) { continue; }
                itemDeltas.Add(new ItemDelta {
                    Item = removedItem,
                    Op = ItemDeltaChangeType.Removed
                });
            }

            // Set addItem entries for all new items found in the new save data
            foreach (PackedItem newItem in currentPlayerItems) {
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
        if (Time.unscaledTime < CharacterDeltaTracker.LastDeltaSyncTime) { return; }

        CharacterDeltaTracker.LastDeltaSyncTime = Time.unscaledTime + ValheimEnforcer.ValConfig.DeltaSynchronizationFrequencyInSeconds.Value;
        SyncChangesToServer();
    }

    private static void SyncChangesToServer() {
        // This only runs for dedicated <-> client setups
        if (CharacterManager.PlayerCharacter == null || Player.m_localPlayer == null || ZNet.instance == null || ZNet.instance.GetServerPeer() == null) { return; } 

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

        List<PackedItem> currentPlayerItems = new List<PackedItem>();
        foreach (ItemDrop.ItemData item in Player.m_localPlayer.GetInventory().GetAllItems()) {
            currentPlayerItems.Add(CharacterDeltaTracker.BuildPackedItem(item));
        }
        CharacterManager.PlayerCharacter.PlayerItems = currentPlayerItems;
        CharacterManager.PlayerCharacter.PlayerCustomData = currentCustomData;

        Dictionary<string, PackedStatusEffect> currentActiveEffects = new Dictionary<string, PackedStatusEffect>();
        foreach (StatusEffect se in Player.m_localPlayer.GetSEMan().GetStatusEffects()) {
            currentActiveEffects.Add(se.name, new PackedStatusEffect(se));
        }

        DeltaSummaryUpdate payload = new DeltaSummaryUpdate {
            Name = CharacterManager.PlayerCharacter.Name,
            HostID = CharacterManager.PlayerCharacter.HostID,
            ItemModifications = itemDeltas,
            SkillLevels = Player.m_localPlayer.GetSkills().GetSkillList().ToDictionary(s => s.m_info.m_skill, s => s.m_level),
            PlayerCustomDataModifications = customDataModifications,
            RemovedCustomDataKeys = customDataRemovedKeys,
            ActiveCharacterEffects = currentActiveEffects,
        };

        ZPackage package = new ZPackage();
        package.Write(DataObjects.yamlserializer.Serialize(payload));
        ValConfig.ItemDeltaUpdateRPC.SendPackage(ZNet.instance.GetServerPeer().m_uid, package);

        Logger.LogDebug($"Delta flush: {itemDeltas.Count} items, {customDataModifications.Count} ({customDataRemovedKeys.Count} removed) custom data changes. Skill levels updated.");
    }
}

