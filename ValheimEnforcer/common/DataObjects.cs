using Jotunn.Managers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ValheimEnforcer.common {
    internal static class DataObjects {

        public static IDeserializer yamldeserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
        public static ISerializer yamlserializer = new SerializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults).Build();

        public class Mod {
            public string PluginID { get; set; }
            public string Version { get; set; }
            public string Name { get; set; }
            [DefaultValue(false)]
            public bool EnforceVersion { get; set; }
        }

        public class Mods {
            public Dictionary<string, Mod> ActiveMods { get; set; } = new Dictionary<string, Mod>();
            public Dictionary<string, Mod> RequiredMods { get; set; } = new Dictionary<string, Mod>();
            public Dictionary<string, Mod> OptionalMods { get; set; } = new Dictionary<string, Mod>();
            public Dictionary<string, Mod> AdminOnlyMods { get; set; } = new Dictionary<string, Mod>();
        }

        [Serializable]
        public class PackedItem {
            public string prefabName { get; set; }
            public int m_stack { get; set; }
            public float m_durability { get; set; }
            [DefaultValue(1)]
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

            public void AddToInventory(Inventory inv, bool use_position) {
                ZNetView.m_forceDisableInit = true;
                GameObject refGo = PrefabManager.Cache.GetPrefab<GameObject>(prefabName);
                GameObject instancedGo = UnityEngine.GameObject.Instantiate(refGo);
                ZNetView.m_forceDisableInit = false;
                ItemDrop itemdrop = instancedGo.GetComponent<ItemDrop>();
                itemdrop.m_itemData.m_stack = m_stack;
                itemdrop.m_itemData.m_durability = m_durability;
                itemdrop.m_itemData.m_quality = m_quality;
                itemdrop.m_itemData.m_variant = m_variant;
                itemdrop.m_itemData.m_worldLevel = m_worldlevel;
                itemdrop.m_itemData.m_crafterID = m_crafterID;
                itemdrop.m_itemData.m_crafterName = m_crafterName;
                itemdrop.m_itemData.m_customData = m_customdata;
                itemdrop.m_itemData.m_pickedUp = true; // Its not the real object, but it gets picked up like a real object.
                if (use_position) {
                    itemdrop.m_itemData.m_gridPos = m_gridpos;
                    inv.AddItem(itemdrop.m_itemData, itemdrop.m_itemData.m_stack, m_gridpos.x, m_gridpos.y);
                } else {
                    inv.AddItem(itemdrop.m_itemData);
                }
                UnityEngine.Object.Destroy(instancedGo);
            }
        }

        public class Character {
            public string Name {
                get; set;
            }
            public string HostID {
                get; set;
            }
            public Dictionary<Skills.SkillType, float> SkillLevels { get; set; } = new Dictionary<Skills.SkillType, float>();
            public List<PackedItem> PlayerItems { get; set; } = new List<PackedItem>();
            public List<PackedItem> ConfiscatedItems { get; set; } = new List<PackedItem>();

            public void AddItemToPlayerItems(ItemDrop.ItemData item) {
                PlayerItems.Add(new PackedItem() {
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

            public void AddConfiscatedItem(ItemDrop.ItemData item) {
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
