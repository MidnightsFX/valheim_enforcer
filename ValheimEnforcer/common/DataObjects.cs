using Jotunn;
using Jotunn.Managers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using UnityEngine;
using ValheimEnforcer.modules.compat;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ValheimEnforcer.common {
    internal static class DataObjects {

        public static IDeserializer yamldeserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
        public static ISerializer yamlserializer = new SerializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults).Build();

        public static readonly string CustomDataKey = "VE_CUSTOM_DATA";

        private static readonly int Poison = "Poison".GetStableHashCode();
        private static readonly int Burning = "Burning".GetStableHashCode();
        private static readonly int Spirit = "Spirit".GetStableHashCode();

        // Color status markers
        internal const int Green = 0x57F287;
        internal const int Grey = 0x95A5A6;
        internal const int Amber = 0xFEE75C;
        internal const int Red = 0xED4245;

        public enum ItemDeltaChangeType {
            Added,
            Removed
        }

        public enum DisconnectionState {
            Clean,
            DirtyDisconnect
        }

        public class RPCServerUpdateData {
            public string PlatformID { get; set; }
            public string PlayerName { get; set; }
            public string ItemPrefabFilter { get; set; } = "All";
        }

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

        public class KnownCheaterEntry {
            public string Id { get; set; }
            public string Reason { get; set; }
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
        public class PackedStatusEffect {
            // This is effectively the remaining TTL
            public float TimeRemaining { get; set; }
            public float Time { get; set; }
            public int NameHash { get; set; }
            [DefaultValue(0f)]
            public float DamageLeft { get; set; } = 0f;
            [DefaultValue(0f)]
            public float DamagePerHit { get; set; } = 0f;
            [DefaultValue(0f)]
            public float FireDamageLeft { get; set; } = 0f;
            [DefaultValue(0f)]
            public float FireDamagePerHit { get; set; } = 0f;
            [DefaultValue(0f)]
            public float SpiritDamageLeft { get; set; } = 0f;
            [DefaultValue(0f)]
            public float SpiritDamagePerHit { get; set; } = 0f;

            // Default constructor is used by unity
            public PackedStatusEffect() {
            }

            public PackedStatusEffect(StatusEffect status) {
                NameHash = status.NameHash();
                TimeRemaining = status.m_ttl;
                Time = status.m_time;

                if (NameHash == Poison) {
                    SE_Poison sePosion = (SE_Poison)status;
                    DamageLeft = sePosion.m_damageLeft;
                    DamagePerHit = sePosion.m_damagePerHit;
                } else if (NameHash == Burning || NameHash == Spirit) {
                    SE_Burning seBurining = (SE_Burning)status;
                    FireDamageLeft = seBurining.m_fireDamageLeft;
                    FireDamagePerHit = seBurining.m_fireDamagePerHit;
                    SpiritDamageLeft = seBurining.m_spiritDamageLeft;
                    SpiritDamagePerHit = seBurining.m_spiritDamagePerHit;
                }

            }

            public StatusEffect ToStatusEffect() {
                StatusEffect original = ObjectDB.instance.GetStatusEffect(NameHash);

                if (original == null) {
                    Logger.LogWarning($"Tried to get a status effect which does not exist ID:{NameHash}");
                    return null;
                }

                StatusEffect se = original.Clone();

                if (NameHash == Poison) {
                    var sePoison = (SE_Poison)se;
                    sePoison.m_ttl = TimeRemaining;
                    sePoison.m_time = Time;
                    sePoison.m_damageLeft = DamageLeft;
                    sePoison.m_damagePerHit = DamagePerHit;
                    return sePoison;
                }

                if (NameHash == Burning || NameHash == Spirit) {
                    SE_Burning seBurning = (SE_Burning)se;
                    seBurning.m_ttl = TimeRemaining;
                    seBurning.m_time = Time;
                    seBurning.m_fireDamageLeft = FireDamageLeft;
                    seBurning.m_fireDamagePerHit = FireDamagePerHit;
                    seBurning.m_spiritDamageLeft = SpiritDamageLeft;
                    seBurning.m_spiritDamagePerHit = SpiritDamagePerHit;
                    return seBurning;
                }

                return se;
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
            public string confiscatedReason { get; set; }
            public DateTime confiscatedTime { get; set; }

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

                if (inv.CanAddItem(itemdrop.m_itemData) == false) {
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
                } else {
                    Logger.LogDebug($"Dropping item {prefabName} at player position because it cannot be added to the inventory.");
                    ItemDrop.DropItem(itemdrop.m_itemData, itemdrop.m_itemData.m_stack, player.gameObject.transform.position, player.gameObject.transform.rotation);
                }


                // Restore the equipped status of the item if it was equipped
                if (m_equipped) {
                    player.EquipItem(itemdrop.m_itemData);
                }
                UnityEngine.Object.Destroy(instancedGo);
            }
        }

        public class ItemDelta {

            public PackedItem Item { get; set; }
            public ItemDeltaChangeType Op { get; set; }
        }

        public class DeltaSummaryUpdate {
            public string Name { get; set; }
            public string HostID { get; set; }
            public DisconnectionState DisconnectionState { get; set; } = DisconnectionState.DirtyDisconnect;
            public List<ItemDelta> ItemModifications { get; set; } = new List<ItemDelta>();
            public Dictionary<string, string> PlayerCustomDataModifications { get; set; } = new Dictionary<string, string>();
            public List<string> RemovedCustomDataKeys { get; set; } = new List<string>();
            public Dictionary<Skills.SkillType, float> SkillLevels { get; set; } = new Dictionary<Skills.SkillType, float>();
            public Dictionary<string, PackedStatusEffect> ActiveCharacterEffects { get; set; } = new Dictionary<string, PackedStatusEffect>();
        }

        public class CharacterSaveData {
            public Dictionary<string, Character> SavedCharacters = new Dictionary<string, Character>();
        }

        public class AccountEntries {
            public Dictionary<string, List<string>> AccountCharacterEntries = new Dictionary<string, List<string>>();
        }

        public class Character {
            public string Name { get; set; }
            public string HostID { get; set; }
            public DisconnectionState LastDisconnect { get; set; } = DisconnectionState.Clean;
            public Dictionary<Skills.SkillType, float> SkillLevels { get; set; } = new Dictionary<Skills.SkillType, float>();
            public Dictionary<string, string> PlayerCustomData { get; set; } = new Dictionary<string, string>();
            public Dictionary<string, PackedStatusEffect> ActiveCharacterEffects { get; set; } = new Dictionary<string, PackedStatusEffect>();
            public List<PackedItem> PlayerItems { get; set; } = new List<PackedItem>();
            public List<PackedItem> ConfiscatedItems { get; set; } = new List<PackedItem>();

            public bool RemoveFromPlayerItems(PackedItem packedItem) {
                bool removed = false;

                // exact match
                if (PlayerItems != null && PlayerItems.Contains(packedItem)) {
                    removed = PlayerItems.Remove(packedItem);
                }
                if (removed == true ) { return true; }

                // Fuzzy match, ignore durability and quality as those can be changed by the player and still be the same item for the most part.
                // TODO: Add custom data as a comparison factor for the future
                if (PlayerItems != null) {
                    foreach (var item in PlayerItems) {
                        if (packedItem.prefabName == item.prefabName &&
                            packedItem.m_stack == item.m_stack &&
                            packedItem.m_variant == item.m_variant &&
                            packedItem.m_worldlevel == item.m_worldlevel &&
                            packedItem.m_crafterID == item.m_crafterID &&
                            packedItem.m_crafterName == item.m_crafterName) {
                            removed = PlayerItems.Remove(item);
                            if (removed) {
                                Logger.LogDebug($"Removed item {item.prefabName} from player items based on a fuzzy match.");
                                break;
                            }
                        }
                    }
                }

                return removed;
            }

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

            public void AddConfiscatedItem(ItemDrop.ItemData item, string reason = "") {
                if (ConfiscatedItems == null) { ConfiscatedItems = new List<PackedItem>(); }

                PackedItem packedItem = new PackedItem() {
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
                };

                if (string.IsNullOrEmpty(reason) == false) {
                    packedItem.confiscatedReason = reason;
                }
                packedItem.confiscatedTime = DateTime.UtcNow;
                ConfiscatedItems.Add(packedItem);
            }
        }

        internal class DiscordMessage {
            public List<DiscordEmbed> Embeds { get; } = new List<DiscordEmbed>();

            public DiscordMessage AddEmbed(DiscordEmbed embed) {
                Embeds.Add(embed);
                return this;
            }

            public string ToJson() {
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"embeds\":[");
                for (int i = 0; i < Embeds.Count; i++) {
                    if (i > 0) { sb.Append(','); }
                    Embeds[i].AppendJson(sb);
                }
                sb.Append("]}");
                return sb.ToString();
            }

            // Try to avoid embedding anything problematic in the messages
            internal static string EscapeJson(string value) {
                if (string.IsNullOrEmpty(value)) { return ""; }
                StringBuilder sb = new StringBuilder(value.Length + 8);
                foreach (char c in value) {
                    switch (c) {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20) { sb.Append("\\u").Append(((int)c).ToString("x4")); } else { sb.Append(c); }
                            break;
                    }
                }
                return sb.ToString();
            }
        }

        internal class DiscordEmbed {
            // Discord embed limits we defensively clamp to: title 256, description 4096, field value 1024.
            private const int TitleLimit = 256;
            private const int DescriptionLimit = 4096;
            private const int FieldValueLimit = 1024;

            public string Title {
                get; set;
            }
            public string Description {
                get; set;
            }
            public int Color {
                get; set;
            }
            public string Timestamp {
                get; set;
            }
            public List<DiscordEmbedField> Fields { get; } = new List<DiscordEmbedField>();

            public DiscordEmbed(string title, string description, int color, string timestamp = null) {
                this.Title = title;
                this.Description = description;
                this.Color = color;
                if (timestamp != null) {
                    this.Timestamp = timestamp;
                } else {
                    this.Timestamp = DateTime.UtcNow.ToString("o");
                }
            }

            public DiscordMessage ToMessage() {
                return new DiscordMessage().AddEmbed(this);
            }

            public DiscordEmbed AddField(string name, string value, bool inline = false) {
                string addvalue = string.IsNullOrEmpty(value) ? "unknown" : value;
                Fields.Add(new DiscordEmbedField { Name = name, Value = Clamp(addvalue, FieldValueLimit), Inline = inline });
                return this;
            }

            public void AppendJson(StringBuilder sb) {
                sb.Append('{');
                bool first = true;
                AppendStringProp(sb, "title", Clamp(Title, TitleLimit), ref first);
                AppendStringProp(sb, "description", Clamp(Description, DescriptionLimit), ref first);
                if (!first) { sb.Append(','); }
                sb.Append("\"color\":").Append(Color);
                first = false;
                AppendStringProp(sb, "timestamp", Timestamp, ref first);
                if (Fields.Count > 0) {
                    sb.Append(",\"fields\":[");
                    for (int i = 0; i < Fields.Count; i++) {
                        if (i > 0) { sb.Append(','); }
                        Fields[i].AppendJson(sb);
                    }
                    sb.Append(']');
                }
                sb.Append('}');
            }

            private static void AppendStringProp(StringBuilder sb, string name, string value, ref bool first) {
                if (string.IsNullOrEmpty(value)) { return; }
                if (!first) { sb.Append(','); }
                sb.Append('"').Append(name).Append("\":\"").Append(DiscordMessage.EscapeJson(value)).Append('"');
                first = false;
            }

            private static string Clamp(string value, int max) {
                if (string.IsNullOrEmpty(value) || value.Length <= max) { return value; }
                return value.Substring(0, max);
            }
        }

        internal class DiscordEmbedField {
            public string Name { get; set; }
            public string Value { get; set; }
            public bool Inline { get; set; }

            public void AppendJson(StringBuilder sb) {
                sb.Append("{\"name\":\"").Append(DiscordMessage.EscapeJson(Name))
                  .Append("\",\"value\":\"").Append(DiscordMessage.EscapeJson(Value))
                  .Append("\",\"inline\":").Append(Inline ? "true" : "false")
                  .Append('}');
            }
        }
    }
}
