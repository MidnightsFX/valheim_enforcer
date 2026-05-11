using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimEnforcer.common;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.modules.character {
    internal static class ItemDeltaTracker {
        internal static void Initialize() {
            if (ZNet.instance != null && ZNet.instance.IsDedicated()) return;
            GameObject host = new GameObject("VE_ItemDeltaTracker");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<ItemDeltaTrackerBehaviour>();
            Logger.LogDebug("ItemDeltaTracker initialized.");
        }

        internal static PackedItem BuildPackedItem(ItemDrop.ItemData item) {
            return new PackedItem {
                prefabName = item.m_dropPrefab.name,
                m_stack = item.m_stack,
                m_durability = UnityEngine.Mathf.Clamp(item.m_durability, 0,
                    item.m_shared.m_maxDurability + (item.m_shared.m_durabilityPerLevel * UnityEngine.Mathf.Max(item.m_quality, 1))),
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
    }

    internal class ItemDeltaTrackerBehaviour : MonoBehaviour {
        private float NextQueueSend;

        public void Update() {
            if (Time.unscaledTime < NextQueueSend) { return; } 

            NextQueueSend = Time.unscaledTime + ValConfig.DeltaFlushIntervalSeconds.Value;
            FlushAndSendQueuedChanges();
        }

        private static void FlushAndSendQueuedChanges() {
            if (CharacterManager.PlayerCharacter == null) return;
            if (Player.m_localPlayer == null) return;
            if (ZNet.instance == null || ZNet.instance.GetServerPeer() == null) return;

            // Take all of the deltas off the queue
            List<ItemDelta> itemDeltas = new List<ItemDelta>(CharacterManager.PendingItemDeltas);
            CharacterManager.PendingItemDeltas.Clear();

            Dictionary<string, string> currentCustomData = Player.m_localPlayer.m_customData;
            Dictionary<string, string> customDataModifications = new Dictionary<string, string>();
            List<string> customDataRemovedKeys = new List<string>();

            foreach (var kvp in currentCustomData) {
                if (!CharacterManager._lastSentCustomData.TryGetValue(kvp.Key, out string oldVal) || oldVal != kvp.Value) {
                    customDataModifications[kvp.Key] = kvp.Value;
                }
            }
            foreach (string key in CharacterManager._lastSentCustomData.Keys) {
                if (currentCustomData.ContainsKey(key) == false) {
                    customDataRemovedKeys.Add(key);
                }
            }

            // No delta changes need to be sent
            // Skills are a lower priority update and will get updated when the next item, or custom data change happens
            if (itemDeltas.Count == 0 && customDataModifications.Count == 0 && customDataRemovedKeys.Count == 0) return;

            DeltaSummaryUpdate payload = new DeltaSummaryUpdate {
                CharacterName = CharacterManager.PlayerCharacter.Name,
                HostName = CharacterManager.PlayerCharacter.HostID,
                ItemModifications = itemDeltas,
                SkillLevels = Player.m_localPlayer.GetSkills().GetSkillList().ToDictionary(s => s.m_info.m_skill, s => s.m_level),
                PlayerCustomDataModifications = customDataModifications,
                RemovedCustomDataKeys = customDataRemovedKeys,
            };

            ZPackage package = new ZPackage();
            package.Write(DataObjects.yamlserializer.Serialize(payload));
            ValConfig.ItemDeltaUpdateRPC.SendPackage(ZNet.instance.GetServerPeer().m_uid, package);

            CharacterManager._lastSentCustomData = new Dictionary<string, string>(currentCustomData);
            Logger.LogDebug($"Delta flush: {itemDeltas.Count} items, {customDataModifications.Count} ({customDataRemovedKeys.Count} removed) custom data changes. Skill levels updated.");
        }
    }
}
