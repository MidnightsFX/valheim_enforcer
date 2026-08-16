using Jotunn;
using Jotunn.Managers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using UnityEngine;
using ValheimEnforcer.modules.compat;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ValheimEnforcer.common {
    internal static class DataObjects {

        // IgnoreUnmatchedProperties is a compatibility guard, not laziness. YamlDotNet throws on any key it
        // cannot map to a property, so a document written by a *newer* build - a Mods.yaml carrying fields an
        // older VE does not know, or a handshake payload from a peer one version ahead - would throw out of
        // Deserialize rather than simply ignoring what it does not understand. On the handshake path ZRpc
        // swallows that exception, which would silently skip mod validation entirely. Ignoring unknown keys
        // degrades to "validate what I understand" instead.
        public static IDeserializer yamldeserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build();
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

            // ---- File verification ------------------------------------------------------------------------
            // Every field below defaults to null, so with OmitDefaults a mod that uses none of them serializes
            // exactly as it did before this feature existed. Existing Mods.yaml files are unchanged on rewrite
            // apart from the entries that actually gain hashes.

            /// <summary>
            /// Client -> server: lowercase hex SHA256 of the DLL BepInEx loaded this plugin from. Null when the
            /// plugin could not be hashed, in which case <see cref="HashStatus"/> says why. A null hash is never
            /// treated as a pass - omitting it would otherwise be the cheapest possible bypass.
            /// </summary>
            [DefaultValue(null)]
            public string Hash { get; set; }

            /// <summary>
            /// Server side: every hash considered valid for this mod; matching any one of them passes. A list
            /// rather than a single value because one Thunderstore archive can ship several DLLs and, when the
            /// plugin GUID cannot be read back out of them, any is a candidate. Accepting N hashes for one GUID
            /// does not weaken the check: producing a DLL whose SHA256 equals one of the others is a preimage
            /// attack, and the comparison is per-GUID.
            /// Deliberately not initialized inline - an empty list would defeat OmitDefaults and write
            /// "acceptedHashes: []" onto every entry in the file.
            /// </summary>
            [DefaultValue(null)]
            public List<string> AcceptedHashes { get; set; }

            /// <summary>
            /// Provenance of <see cref="AcceptedHashes"/>: "Local", "Manual" or "Thunderstore". Only "Local"
            /// entries are refreshed by the startup pass, so a hash an admin pinned by hand or one resolved
            /// from Thunderstore survives a restart instead of being overwritten by whatever this machine
            /// happens to have on disk.
            /// </summary>
            [DefaultValue(null)]
            public string HashSource { get; set; }

            /// <summary>
            /// What produced <see cref="AcceptedHashes"/>: "local:&lt;version&gt;" for a DLL this machine loads
            /// itself, or "Owner-Name-Version" for a resolved Thunderstore package. Re-resolution happens only
            /// when this stops matching what we would fetch today, so a restart re-downloads nothing.
            /// </summary>
            [DefaultValue(null)]
            public string HashedFrom { get; set; }

            /// <summary>
            /// Admin authored: the Thunderstore package to resolve hashes from, as "Owner-ModName" or
            /// "Owner-ModName-Version" - the same dependency string format used in a Thunderstore manifest.
            /// This is the only way the server reaches the network; arbitrary download URLs are deliberately
            /// not supported.
            /// </summary>
            [DefaultValue(null)]
            public string ThunderstorePackage { get; set; }

            /// <summary>
            /// Admin authored: per-mod override of the server's HashEnforcement setting. "Off", "WhenKnown" or
            /// "Strict".
            /// </summary>
            [DefaultValue(null)]
            public string HashEnforcement { get; set; }

            /// <summary>
            /// Client -> server: why <see cref="Hash"/> is null. One of a fixed set of tokens ("dynamic",
            /// "missing", "unreadable", "timeout") - never a filesystem path. Surfaced in the rejection text so
            /// a player is told what actually happened rather than getting a bare failure.
            /// </summary>
            [DefaultValue(null)]
            public string HashStatus { get; set; }

            /// <summary>
            /// True when <paramref name="candidate"/> is one of the accepted hashes. Case insensitive so an
            /// admin who pastes uppercase hex (as Get-FileHash produces) is not silently rejected.
            /// </summary>
            public bool AcceptsHash(string candidate) {
                if (AcceptedHashes == null || AcceptedHashes.Count == 0) { return false; }
                if (string.IsNullOrEmpty(candidate)) { return false; }
                foreach (string accepted in AcceptedHashes) {
                    if (string.Equals(accepted, candidate, StringComparison.OrdinalIgnoreCase)) { return true; }
                }
                return false;
            }

            /// <summary>True when the server holds at least one hash to compare a client against.</summary>
            public bool HasRecordedHash() {
                return AcceptedHashes != null && AcceptedHashes.Count > 0;
            }
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

            /// <summary>
            /// Client -> server handshake payload: ActiveMods only.
            ///
            /// The server reads nothing but ActiveMods out of a client's payload (see
            /// ModManager.ValidateModlist), so shipping the client's own Required/Optional/AdminOnly/ServerOnly
            /// lists was always dead weight - and it got substantially worse once every entry could carry a
            /// 64 character hash. The four empty dictionaries still serialize and deserialize, so the receiving
            /// side's count logging is unaffected.
            /// </summary>
            public ZPackage ActiveModsToZPackage() {
                Mods trimmed = new Mods { ActiveMods = ActiveMods };
                ZPackage package = new ZPackage();
                package.Write(DataObjects.yamlserializer.Serialize(trimmed));
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

        /// <summary>
        /// A single cheat tool sighting on a client. The client reports what it saw and nothing more;
        /// the server decides what that means by resolving the label against its own CheatToolCatalog.
        /// </summary>
        public class CheatToolDetection {
            /// <summary>Canonical tool label from CheatToolCatalog.</summary>
            public string Tool { get; set; }
            /// <summary>Which scan found it: "process", "module" or "window".</summary>
            public string Vector { get; set; }
            /// <summary>The matched process name, module name, or window class/title.</summary>
            public string Detail { get; set; }
        }

        public class CheatSummaryReport {
            public string PlayerName { get; set; }
            public string PlatformID { get; set; }
            // Only matched entries are ever sent - never the player's full process list.
            public List<CheatToolDetection> DetectedTools { get; set; }
            public bool ValheimToolerStatus { get; set; }

            public bool cheatsDetected() {
                return ValheimToolerStatus || (DetectedTools != null && DetectedTools.Count > 0);
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
        //   confiscated*,
        //   confiscationId - confiscation bookkeeping, not part of the item.
        // The excluded fields all still reach the server: SavePlayerCharacter rebuilds PlayerItems from the live
        // inventory on join, respawn, clean logout and every FullSyncScheduler pull.
        //
        // NOTE for ConfiscatedItems: two confiscated entries that differ only in reason/timestamp/id compare equal.
        // Nothing calls Remove/Contains on that list today (CommandHelpers filters by prefab), and the server-side
        // append merge keys on confiscationId rather than Equals precisely because of this.
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
            // Stable per-confiscation identity, assigned once in Character.AddConfiscatedItem. The server uses it to
            // append a client's newly confiscated items idempotently (MergeConfiscatedItems) - re-sending the same
            // entry on a later full push must not duplicate it. confiscatedTime cannot serve this purpose:
            // ConfiscateUntrackedItems confiscates in a tight loop and DateTime.UtcNow has ~15ms resolution on
            // Windows, so a batch routinely shares one timestamp. Null on every non-confiscated item, and on
            // confiscated entries written before this field existed.
            [DefaultValue(null)]
            public string confiscationId { get; set; }

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
                packedItem.confiscationId = Guid.NewGuid().ToString("N");
                ConfiscatedItems.Add(packedItem);
            }

            /// <summary>
            /// Server side: fold a client's reported confiscations into this (authoritative) character's list.
            ///
            /// Append only - the client's copy is never allowed to replace ours. Confiscation happens client side
            /// (CharacterManager.ConfiscateUntrackedItems at join) but the list is owned by the server, because
            /// admin commands (/clear, /return) mutate it while the player is connected. A wholesale overwrite
            /// from a client's later full push would resurrect entries an admin had just cleared or handed back.
            ///
            /// Incoming entries with no confiscationId are ignored: the field is assigned at the moment of
            /// confiscation, so a missing one means the entry is legacy data mirrored back from a save we already
            /// hold. Matching on the id also makes a repeated push idempotent, so a full save that gets sent twice
            /// (or one that gets dropped and re-sent) neither duplicates nor loses a confiscation.
            /// </summary>
            /// <returns>How many new entries were appended.</returns>
            public int MergeConfiscatedItems(List<PackedItem> incoming) {
                if (incoming == null || incoming.Count == 0) { return 0; }
                if (ConfiscatedItems == null) { ConfiscatedItems = new List<PackedItem>(); }

                HashSet<string> known = new HashSet<string>();
                foreach (PackedItem existing in ConfiscatedItems) {
                    if (existing != null && !string.IsNullOrEmpty(existing.confiscationId)) {
                        known.Add(existing.confiscationId);
                    }
                }

                int added = 0;
                foreach (PackedItem candidate in incoming) {
                    if (candidate == null || string.IsNullOrEmpty(candidate.confiscationId)) { continue; }
                    if (!known.Add(candidate.confiscationId)) { continue; } // already recorded
                    ConfiscatedItems.Add(candidate);
                    added++;
                }
                return added;
            }
        }

        /// <summary>
        /// The shape of Notifications.yaml: one template per notification event, keyed by the event name in
        /// camelCase. Each value is the literal Discord webhook payload for that event, placeholders and all -
        /// this mod does not model the message, it substitutes and posts. A null entry means "use the built-in
        /// default", which is how a file an admin has trimmed to the two events they care about still works.
        ///
        /// ScalarStyle.Literal is not cosmetic. Left to itself the serializer picks folded style ('>') for these
        /// multi-line values, and while that happens to round trip for the shipped templates, folding is defined
        /// to collapse newlines - so a payload laid out differently would come back reflowed. Pinning the style
        /// means the file an admin reads after a rewrite is byte for byte the payload that gets sent.
        /// </summary>
        internal class NotificationTemplateSet {
            [YamlMember(ScalarStyle = ScalarStyle.Literal)]
            public string ServerStartup { get; set; }
            [YamlMember(ScalarStyle = ScalarStyle.Literal)]
            public string ServerShutdown { get; set; }
            [YamlMember(ScalarStyle = ScalarStyle.Literal)]
            public string WorldSaved { get; set; }
            [YamlMember(ScalarStyle = ScalarStyle.Literal)]
            public string PlayerJoined { get; set; }
            [YamlMember(ScalarStyle = ScalarStyle.Literal)]
            public string PlayerLeft { get; set; }
            [YamlMember(ScalarStyle = ScalarStyle.Literal)]
            public string CheaterBanned { get; set; }
            [YamlMember(ScalarStyle = ScalarStyle.Literal)]
            public string CharacterRejected { get; set; }
            [YamlMember(ScalarStyle = ScalarStyle.Literal)]
            public string ModMismatch { get; set; }
        }
    }
}
