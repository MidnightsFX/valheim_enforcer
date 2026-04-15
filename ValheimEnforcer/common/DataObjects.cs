using Jotunn;
using Jotunn.Managers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using UnityEngine;
using ValheimEnforcer.modules.compat;
using ValheimEnforcer.modules.compat.ExtraSlots;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ValheimEnforcer.common {
    internal static class DataObjects {

        public static IDeserializer yamldeserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
        public static ISerializer yamlserializer = new SerializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults).Build();

        public static readonly string CustomDataKey = "VE_CUSTOM_DATA";

        public class Mod {
            public string PluginID { get; set; }
            public string Version { get; set; }
            public string Name { get; set; }
            [DefaultValue(false)]
            public bool EnforceVersion { get; set; }
            [DefaultValue("Minor")]
            public string VersionStrictness { get; set; } = "Minor";
        }

        public class Mods {
            public Dictionary<string, Mod> ActiveMods { get; set; } = new Dictionary<string, Mod>();
            public Dictionary<string, Mod> RequiredMods { get; set; } = new Dictionary<string, Mod>();
            public Dictionary<string, Mod> OptionalMods { get; set; } = new Dictionary<string, Mod>();
            public Dictionary<string, Mod> AdminOnlyMods { get; set; } = new Dictionary<string, Mod>();
            public Dictionary<string, Mod> ServerOnlyMods { get; set; } = new Dictionary<string, Mod>();

            public ZPackage ToZPackage() {
                string stringified = DataObjects.yamlserializer.Serialize(this);
                ZPackage package = new ZPackage();
                package.Write(stringified);
                return package;
            }

            public Mods FromZPackage(ZPackage incoming) {
                Mods mods = DataObjects.yamldeserializer.Deserialize<Mods>(incoming.ReadString());
                ActiveMods = mods.ActiveMods;
                RequiredMods = mods.RequiredMods;
                OptionalMods = mods.OptionalMods;
                AdminOnlyMods = mods.AdminOnlyMods;
                ServerOnlyMods = mods.ServerOnlyMods;
                return mods;
            }
        }

        public class CheatReport {
            public string Signal { get; set; }
            public string Detail { get; set; }
            public string TimestampUtc { get; set; }
        }

        public class CheatEngineDetector {
            public bool CheatEngineModuleLoaded { get; set; }
            public bool CheatEngineProcessDetected { get; set; }
            public bool IsCheatEngineDetected() {
                return CheatEngineModuleLoaded || CheatEngineProcessDetected;
            }
        }

        public class CheatSummaryReport {
            public string PlayerName { get; set; }
            public string PlatformID { get; set; }
            public CheatEngineDetector CheatEngineStatus { get; set; }
            public bool ValheimToolerStatus { get; set; }

            public bool cheatsDetected() {
                return (CheatEngineStatus != null && CheatEngineStatus.IsCheatEngineDetected()) || ValheimToolerStatus;
            }
        }

        public class ItemValidatorResult {
            public PackedItem SavedItemRef { get; set; }
            public ItemDrop.ItemData CharacterItemRef { get; set; }
            [DefaultValue(false)]
            public bool Validated { get; set; }
            public string ValidationMessage { get; set; }
            public ValidationSummary ValidationResult { get; set; }
        }

        public class ValidationSummary {
            [DefaultValue(false)]
            public bool NameAndStackMatch { get; set; }
            [DefaultValue(false)]
            public bool QualityMatch { get; set; }
            [DefaultValue(false)]
            public bool CustomDataMatch { get; set; }
            [DefaultValue(false)]
            public bool DurabilityMatch { get; set; }

            public bool IsValid() {
                return NameAndStackMatch && QualityMatch && CustomDataMatch && DurabilityMatch;
            }
        }

        [Serializable]
        public class PackedItem {
            public string prefabName { get; set; }
            public int m_stack { get; set; }
            public float m_durability { get; set; }
            public int m_quality { get; set; }
            [DefaultValue(0)]
            public int m_variant { get; set; }
            [DefaultValue(0)]
            public int m_worldlevel { get; set; }
            [DefaultValue(0L)]
            public long m_crafterID { get; set; }
            [DefaultValue("")]
            public string m_crafterName { get; set; }
            public Dictionary<string, string> m_customdata { get; set; }
            [DefaultValue(false)]
            public bool m_equipped { get; set; }
            public Vector2i m_gridpos { get; set; }

            public void AddToInventory(Player player, bool use_position) {
                Inventory inv = player.GetInventory();
                ZNetView.m_forceDisableInit = true;
                GameObject refGo = PrefabManager.Instance.GetPrefab(prefabName);
                if (refGo == null) {
                    Logger.LogError($"Could not find prefab with name {prefabName} for item with crafter name {m_crafterName} and crafter ID {m_crafterID}. This item will not be added to the inventory.");
                    ZNetView.m_forceDisableInit = false;
                    return;
                }
                GameObject instancedGo = UnityEngine.GameObject.Instantiate(refGo);
                ZNetView.m_forceDisableInit = false;
                ItemDrop itemdrop = instancedGo.GetComponent<ItemDrop>();
                itemdrop.m_itemData.m_stack = m_stack;
                itemdrop.m_itemData.m_durability = m_durability;
                if (m_quality == 0) {
                    itemdrop.m_itemData.m_quality = 1;
                } else {
                    itemdrop.m_itemData.m_quality = m_quality;
                }
                itemdrop.m_itemData.m_variant = m_variant;
                itemdrop.m_itemData.m_worldLevel = m_worldlevel;
                itemdrop.m_itemData.m_crafterID = m_crafterID;
                if (m_crafterName == null) {
                    itemdrop.m_itemData.m_crafterName = "";
                } else {
                    itemdrop.m_itemData.m_crafterName = m_crafterName;
                }
                itemdrop.m_itemData.m_customData = m_customdata;
                itemdrop.m_itemData.m_pickedUp = true; // Its not the real object, but it gets picked up like a real object.
                if (use_position) {
                    itemdrop.m_itemData.m_gridPos = m_gridpos;
                    inv.AddItem(itemdrop.m_itemData, itemdrop.m_itemData.m_stack, m_gridpos.x, m_gridpos.y);
                } else if (ModCompatability.IsExtraSlotsEnabled && modules.compat.ExtraSlots.API.IsGridPositionASlot(m_gridpos)) {
                    Logger.LogDebug($"Item {prefabName} saved grid position {m_gridpos} maps to an ExtraSlots slot. Placing into that slot.");
                    itemdrop.m_itemData.m_gridPos = m_gridpos;
                    inv.AddItem(itemdrop.m_itemData, itemdrop.m_itemData.m_stack, m_gridpos.x, m_gridpos.y);
                } else {
                    inv.AddItem(itemdrop.m_itemData);
                }
                // Restore the equipped status of the item if it was equipped
                if (m_equipped) {
                    player.EquipItem(itemdrop.m_itemData);
                }
                UnityEngine.Object.Destroy(instancedGo);
            }
        }

        public class CharacterSaveData {
            public Dictionary<string, Character> SavedCharacters = new Dictionary<string, Character>();
        }

        public class AccountEntries {
            public Dictionary<string, List<string>> AccountCharacterEntries = new Dictionary<string, List<string>>();
        }

        public class Character {
            public string Name {
                get; set;
            }
            public string HostID {
                get; set;
            }
            public Dictionary<Skills.SkillType, float> SkillLevels { get; set; } = new Dictionary<Skills.SkillType, float>();
            public Dictionary<string, string> PlayerCustomData { get; set; } = new Dictionary<string, string>();
            public List<PackedItem> PlayerItems { get; set; } = new List<PackedItem>();
            public List<PackedItem> ConfiscatedItems { get; set; } = new List<PackedItem>();

            public void AddItemToPlayerItems(ItemDrop.ItemData item) {
                if (PlayerItems == null) { PlayerItems = new List<PackedItem>(); }

                Logger.LogDebug($"Adding saved item {item.m_dropPrefab.name} with quality - {item.m_quality}");

                PlayerItems.Add(new PackedItem() {
                    prefabName = item.m_dropPrefab.name,
                    m_stack = item.m_stack,
                    m_durability = Mathf.Clamp(item.m_durability, 0, item.m_shared.m_maxDurability + (item.m_shared.m_durabilityPerLevel * Mathf.Max(item.m_quality, 1))),
                    m_quality = item.m_quality,
                    m_variant = item.m_variant,
                    m_worldlevel = item.m_worldLevel,
                    m_crafterID = item.m_crafterID,
                    m_crafterName = item.m_crafterName,
                    m_customdata = item.m_customData,
                    m_equipped = item.m_equipped,
                    m_gridpos = item.m_gridPos
                });
            }

            public void AddConfiscatedItem(ItemDrop.ItemData item) {
                if (ConfiscatedItems == null) { ConfiscatedItems = new List<PackedItem>(); }

                ConfiscatedItems.Add(new PackedItem() {
                    prefabName = item.m_dropPrefab.name,
                    m_stack = item.m_stack,
                    m_durability = item.m_durability,
                    m_quality = item.m_quality,
                    m_variant = item.m_variant,
                    m_worldlevel = item.m_worldLevel,
                    m_crafterID = item.m_crafterID,
                    m_crafterName = item.m_crafterName,
                    m_customdata = item.m_customData,
                    m_equipped = item.m_equipped,
                    m_gridpos = item.m_gridPos
                });
            }
        }
    }
}
