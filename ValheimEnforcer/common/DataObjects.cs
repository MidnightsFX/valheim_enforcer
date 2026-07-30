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
        // DisableAliases is required, not cosmetic. YamlDotNet's anchor assigner keys objects by Equals/GetHashCode
        // rather than by reference, so once PackedItem gained value equality two *distinct* items that compare
        // equal would be emitted as one anchor plus an alias - and because durability, grid position, equipped
        // state and the confiscation fields sit outside that equality, the aliased entry would silently inherit
        // the other's values for all of them (two identical stacks collapsing onto one grid slot, a confiscated
        // item losing its reason and timestamp). Writing every item out in full costs a little disk and wire size
        // and keeps each entry independent. The deserializer still understands aliases, so saves written by
        // earlier versions load unchanged.
        public static ISerializer yamlserializer = new SerializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults).DisableAliases().Build();

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

        // Equality is value based and deliberately partial - it answers "is this the same item?", not "is every
        // field identical?". Excluded from Equals/GetHashCode:
        //   m_durability  - drains continuously (Attack, Humanoid.DrainEquipedItemDurability) without ever firing
        //                   Inventory.Changed, so including it would make every delta a full inventory replace the
        //                   moment any unrelated change happened to flush. Durability is reconciled by the full
        //                   save pushes instead, and enforced separately by CharacterManager.ValidateItems.
        //   m_gridpos,
        //   m_equipped    - identity is what the player possesses, not where it sits or whether it is worn.
        //   confiscated*  - confiscation bookkeeping, not part of the item.
        // The excluded fields all still reach the server: SavePlayerCharacter rebuilds PlayerItems from the live
        // inventory on join, respawn, clean logout and every FullSyncScheduler pull.
        //
        // NOTE for ConfiscatedItems: two confiscated entries that differ only in reason/timestamp compare equal.
        // Nothing calls Remove/Contains on that list today (CommandHelpers filters by prefab), but a future caller
        // needs to know.
        [Serializable]
        public class PackedItem : IEquatable<PackedItem> {
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

            // The live ItemDrop.ItemData dictionary keeps being mutated by the game, so a PackedItem that merely
            // referenced it would compare equal to every later snapshot of the same item no matter what changed.
            // Every capture site copies instead. Null is preserved rather than normalised to empty so the
            // OmitDefaults serialization shape does not change.
            internal static Dictionary<string, string> CopyCustomData(Dictionary<string, string> source) {
                return source == null ? null : new Dictionary<string, string>(source);
            }

            // Quality 0 means "unset" in older saves and is treated as 1 everywhere else (see AddToInventory and
            // CharacterManager.ValidateItems), so normalise here too - otherwise a legacy save would churn one
            // spurious remove/add pair for every item on the first flush after a join.
            private static int NormalizedQuality(int quality) {
                return quality == 0 ? 1 : quality;
            }

            private static bool CustomDataEquals(Dictionary<string, string> a, Dictionary<string, string> b) {
                int acount = a == null ? 0 : a.Count;
                int bcount = b == null ? 0 : b.Count;
                if (acount != bcount) { return false; }
                if (acount == 0) { return true; } // null and empty are the same thing here
                foreach (KeyValuePair<string, string> kvp in a) {
                    if (!b.TryGetValue(kvp.Key, out string other)) { return false; }
                    if (kvp.Value != other) { return false; }
                }
                return true;
            }

            private static int CustomDataHash(Dictionary<string, string> data) {
                if (data == null || data.Count == 0) { return 0; } // must agree with CustomDataEquals
                int acc = 0;
                foreach (KeyValuePair<string, string> kvp in data) {
                    // XOR the per-pair hashes so the result does not depend on enumeration order - a yaml round
                    // trip is free to reorder the map.
                    unchecked {
                        acc ^= ((kvp.Key?.GetHashCode() ?? 0) * 31) ^ (kvp.Value?.GetHashCode() ?? 0);
                    }
                }
                return acc;
            }

            public bool Equals(PackedItem other) {
                if (ReferenceEquals(this, other)) { return true; }
                if (other is null) { return false; }
                return prefabName == other.prefabName
                    && m_stack == other.m_stack
                    && NormalizedQuality(m_quality) == NormalizedQuality(other.m_quality)
                    && m_variant == other.m_variant
                    && m_worldlevel == other.m_worldlevel
                    && m_crafterID == other.m_crafterID
                    && m_crafterName == other.m_crafterName
                    && CustomDataEquals(m_customdata, other.m_customdata);
            }

            public override bool Equals(object obj) {
                return Equals(obj as PackedItem);
            }

            public override int GetHashCode() {
                unchecked {
                    int hash = 17;
                    hash = (hash * 31) + (prefabName?.GetHashCode() ?? 0);
                    hash = (hash * 31) + m_stack;
                    hash = (hash * 31) + NormalizedQuality(m_quality);
                    hash = (hash * 31) + m_variant;
                    hash = (hash * 31) + m_worldlevel;
                    hash = (hash * 31) + m_crafterID.GetHashCode();
                    hash = (hash * 31) + (m_crafterName?.GetHashCode() ?? 0);
                    hash = (hash * 31) + CustomDataHash(m_customdata);
                    return hash;
                }
            }

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
                // Copy rather than hand over the dictionary: the join-time restore feeds items straight from the
                // tracked baseline (CharacterManager.LoadAndValidatePlayer), so sharing it would let the live item
                // mutate the baseline it was restored from. The empty fallback keeps a legacy save that carries no
                // custom data from handing vanilla a null dictionary.
                itemdrop.m_itemData.m_customData = CopyCustomData(m_customdata) ?? new Dictionary<string, string>();
                itemdrop.m_itemData.m_pickedUp = true; // Its not the real object, but it gets picked up like a real object.

                bool placed = false;

                // Restore into the exact saved slot when we have one. ExtraSlots equipment slots sit outside the
                // normal grid flow, so they have to be tried before the generic add - AddItem(item) would reflow
                // the item into an ordinary bag slot instead. The positional overload returns false without adding
                // anything when the target slot is occupied or out of grid range, so the result must be checked.
                bool wantSavedSlot = use_position
                    || (ModCompatability.IsExtraSlotsEnabled && modules.compat.ExtraSlots.API.IsGridPositionASlot(m_gridpos));
                if (wantSavedSlot) {
                    itemdrop.m_itemData.m_gridPos = m_gridpos;
                    placed = inv.AddItem(itemdrop.m_itemData, itemdrop.m_itemData.m_stack, m_gridpos.x, m_gridpos.y);
                    if (!placed) {
                        Logger.LogDebug($"Saved grid position {m_gridpos} for {prefabName} is occupied or out of range, falling back to the first free slot.");
                    }
                }

                // Inventory.CanAddItem returns true when there IS room for the item.
                if (!placed && inv.CanAddItem(itemdrop.m_itemData)) {
                    placed = inv.AddItem(itemdrop.m_itemData);
                }

                if (!placed) {
                    Logger.LogDebug($"Dropping item {prefabName} at player position because it cannot be added to the inventory.");
                    ItemDrop.DropItem(itemdrop.m_itemData, itemdrop.m_itemData.m_stack, player.gameObject.transform.position, player.gameObject.transform.rotation);
                } else if (m_equipped) {
                    // Restore the equipped status, but only for an item that actually made it into the inventory -
                    // equipping a dropped item leaves the player in a desynced "equipped but not carried" state.
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
                if (packedItem == null) { return false; }

                // Primary path: PackedItem.Equals is value based, so this matches the client's delta against our
                // copy on everything that identifies an item (including custom data), while tolerating the
                // durability/slot/equip drift that deltas deliberately do not report.
                if (PlayerItems != null && PlayerItems.Contains(packedItem)) {
                    removed = PlayerItems.Remove(packedItem);
                }
                if (removed == true ) { return true; }

                // Drift recovery only. Strictly weaker than Equals - it additionally ignores quality and custom
                // data - so it fires when our copy has diverged in a way the delta stream cannot express (a save
                // that predates a change, or a dropped delta). Keeping it means the server still converges rather
                // than accumulating phantom items.
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
                    m_customdata = PackedItem.CopyCustomData(item.m_customData),
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
                    m_customdata = PackedItem.CopyCustomData(item.m_customData),
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
