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

        /// <summary>
        /// One BepInEx preloader patcher, keyed by its path relative to BepInEx/patchers with forward slashes.
        ///
        /// Deliberately shaped like <see cref="Mod"/>: <see cref="Hash"/>/<see cref="HashStatus"/> are what a
        /// client reports about itself, <see cref="AcceptedHashes"/> is what a server will allow. A patcher
        /// carries no BepInEx metadata at all - no plugin id, no version - so unlike a mod there is nothing
        /// else to identify it by, which is why the file hash is the only policy this list can express.
        /// </summary>
        public class PatcherEntry {
            public string Name { get; set; }

            /// <summary>Client -> server: the SHA256 of the file, or null with a reason in HashStatus.</summary>
            [DefaultValue(null)]
            public string Hash { get; set; }

            /// <summary>Client -> server: why Hash is null. One of PluginHasher's fixed status tokens.</summary>
            [DefaultValue(null)]
            public string HashStatus { get; set; }

            /// <summary>Server side: the hashes this patcher is allowed to have. Admin authored or recorded.</summary>
            [DefaultValue(null)]
            public List<string> AcceptedHashes { get; set; }

            public bool AcceptsHash(string candidate) {
                if (AcceptedHashes == null || AcceptedHashes.Count == 0) { return false; }
                if (string.IsNullOrEmpty(candidate)) { return false; }
                foreach (string accepted in AcceptedHashes) {
                    if (string.Equals(accepted, candidate, StringComparison.OrdinalIgnoreCase)) { return true; }
                }
                return false;
            }

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

            /// <summary>What this machine actually has in BepInEx/patchers. Rebuilt every start, like ActiveMods.</summary>
            public Dictionary<string, PatcherEntry> ActivePatchers { get; set; } = new Dictionary<string, PatcherEntry>();

            /// <summary>
            /// Server policy: the patchers a client may carry. Allowlist semantics, not required-mod semantics -
            /// a client with none is always fine, which matters because almost no client has any while a server
            /// may well have several.
            /// </summary>
            public Dictionary<string, PatcherEntry> AllowedPatchers { get; set; } = new Dictionary<string, PatcherEntry>();

            /// <summary>
            /// Server -> client: a random value this connection must fold into its report, so the report cannot
            /// be a canned answer replayed from a previous session. Absent when the feature is off, and absent
            /// from a server that predates it - both of which a client handles by simply not answering.
            /// </summary>
            [DefaultValue(null)]
            public string Nonce { get; set; }

            /// <summary>
            /// Client -> server: SHA256 over the nonce and the declaration being sent. See
            /// <see cref="modules.mods.Attestation"/> for exactly what a match does and does not prove.
            /// </summary>
            [DefaultValue(null)]
            public string Attestation { get; set; }

            /// <summary>
            /// The server's payload for one specific connection: everything <see cref="ToZPackage"/> sends,
            /// plus that connection's nonce.
            ///
            /// A copy rather than a mutation of the shared settings object, because two clients can be
            /// mid-handshake at once and stamping the live object would hand the second one the first one's
            /// nonce. The copy is shallow on purpose - only the scalar differs, and nothing here writes to the
            /// dictionaries.
            /// </summary>
            public ZPackage ToZPackage(string nonce) {
                if (string.IsNullOrEmpty(nonce)) { return ToZPackage(); }
                Mods forPeer = new Mods {
                    ActiveMods = ActiveMods,
                    RequiredMods = RequiredMods,
                    OptionalMods = OptionalMods,
                    AdminOnlyMods = AdminOnlyMods,
                    ServerOnlyMods = ServerOnlyMods,
                    ActivePatchers = ActivePatchers,
                    AllowedPatchers = AllowedPatchers,
                    Nonce = nonce,
                };
                return forPeer.ToZPackage();
            }

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
                Mods trimmed = new Mods { ActiveMods = ActiveMods, ActivePatchers = ActivePatchers, Attestation = Attestation };
                ZPackage package = new ZPackage();
                package.Write(DataObjects.yamlserializer.Serialize(trimmed));
                return package;
            }

            public Mods FromZPackage(ZPackage incoming) {
                Mods mods = DataObjects.yamldeserializer.Deserialize<Mods>(incoming.ReadString());
                // Normalised on `mods` and not on `this`, because `mods` is what every caller uses - they all
                // go through `new Mods().FromZPackage(pkg)` and keep the return value. A payload from a build
                // that predates patcher reporting carries neither key, and IgnoreUnmatchedProperties means an
                // absent key deserializes to null rather than throwing. Empty reads correctly here: a client
                // that says nothing about patchers is a client reporting that it has none.
                mods.ActivePatchers = mods.ActivePatchers ?? new Dictionary<string, PatcherEntry>();
                mods.AllowedPatchers = mods.AllowedPatchers ?? new Dictionary<string, PatcherEntry>();

                ActiveMods = mods.ActiveMods;
                RequiredMods = mods.RequiredMods;
                OptionalMods = mods.OptionalMods;
                AdminOnlyMods = mods.AdminOnlyMods;
                ServerOnlyMods = mods.ServerOnlyMods;
                ActivePatchers = mods.ActivePatchers;
                AllowedPatchers = mods.AllowedPatchers;
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
            /// <summary>Low-confidence sighting (generic window class); the server logs it but never enforces on it.</summary>
            [DefaultValue(false)]
            public bool Weak { get; set; }
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
        // Nothing calls Remove/Contains on that list today (ConfiscatedItems partitions it by prefab in one pass
        // for exactly this reason, rather than removing by value), and the server-side
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
            // ReconcilePlayerToCharacter confiscates in a tight loop and DateTime.UtcNow has ~15ms resolution on
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

            /// <summary>
            /// A detached copy of a player's custom data, for the paths that must end up holding a dictionary
            /// rather than possibly-null.
            ///
            /// Player custom data used to be passed around by reference in both directions, which quietly made
            /// the tracked character and the live player share one dictionary - and a shared dictionary is why
            /// DeltaChangeTracker could never detect a custom data change: it was diffing a dictionary against
            /// itself. Always copy.
            /// </summary>
            internal static Dictionary<string, string> SnapshotCustomData(Dictionary<string, string> source) {
                return source == null ? new Dictionary<string, string>() : new Dictionary<string, string>(source);
            }

            /// <summary>
            /// An item's identity, as everything on both sides of the wire understands it: the name of its
            /// ItemDrop prefab.
            ///
            /// m_dropPrefab is null for any item whose prefab did not resolve on this machine - a modded item the
            /// ObjectDB lookup missed, or an inventory entry another mod synthesised. Every site that used to read
            /// m_dropPrefab.name straight threw a NullReferenceException on such an item and took the whole join
            /// validation pass down with it. Returning false instead lets each caller decide, and there is
            /// deliberately no fallback to m_shared.m_name: that is a localization token ("$item_bow"), not a
            /// prefab name, so writing it into a save would produce an entry AddToInventory can never resolve.
            /// </summary>
            internal static bool TryPrefabName(ItemDrop.ItemData item, out string prefabName) {
                prefabName = item?.m_dropPrefab?.name;
                return !string.IsNullOrEmpty(prefabName);
            }

            /// <summary>Human-readable identification for logs and confiscation reasons. Never throws and never
            /// returns null, so it is safe on the paths that exist to report a problem with the item.</summary>
            internal static string Describe(ItemDrop.ItemData item) {
                if (item == null) { return "<null item>"; }
                if (item.m_dropPrefab != null && !string.IsNullOrEmpty(item.m_dropPrefab.name)) { return item.m_dropPrefab.name; }
                string shared = item.m_shared?.m_name;
                return string.IsNullOrEmpty(shared) ? "<unidentifiable item>" : $"<unidentifiable item {shared}>";
            }

            /// <summary>
            /// Packs a live inventory item, or returns null when the item has no resolvable prefab name and
            /// therefore cannot be tracked, matched or restored.
            ///
            /// clampDurability mirrors the difference between the existing capture sites: the tracked-item paths
            /// clamp to the item's real maximum (a save must never claim more durability than the item can hold),
            /// while a confiscation record keeps the raw value it was taken with.
            /// </summary>
            internal static PackedItem From(ItemDrop.ItemData item, bool clampDurability = true) {
                if (!TryPrefabName(item, out string prefabName)) { return null; }
                float durability = item.m_durability;
                if (clampDurability) {
                    durability = Mathf.Clamp(item.m_durability, 0,
                        item.m_shared.m_maxDurability + (item.m_shared.m_durabilityPerLevel * Mathf.Max(item.m_quality, 1)));
                }
                return new PackedItem() {
                    prefabName = prefabName,
                    m_stack = item.m_stack,
                    m_durability = durability,
                    m_quality = item.m_quality,
                    m_variant = item.m_variant,
                    m_worldlevel = item.m_worldLevel,
                    m_crafterID = item.m_crafterID,
                    m_crafterName = item.m_crafterName,
                    m_customdata = CopyCustomData(item.m_customData),
                    m_equipped = item.m_equipped,
                    m_gridpos = item.m_gridPos
                };
            }

            // Quality 0 means "unset" in older saves and is treated as 1 everywhere else (see AddToInventory and
            // CharacterManager.ValidateItems), so normalise here too - otherwise a legacy save would churn one
            // spurious remove/add pair for every item on the first flush after a join.
            private static int NormalizedQuality(int quality) {
                return quality == 0 ? 1 : quality;
            }

            private static bool CustomDataEquals(Dictionary<string, string> a, Dictionary<string, string> b) {
                // Keys a compat mod stamps onto items at save time and prunes again during play
                // (CompatCustomData.IsIgnoredItemKey) are not part of the item's identity: two honest
                // captures of the same item routinely disagree about them, and counting them made a
                // stamped copy and a pruned copy compare as different items - which is a confiscation.
                // Compared entry-wise in both directions rather than by count so those keys can be
                // skipped; null and empty are the same thing here.
                if (a != null) {
                    foreach (KeyValuePair<string, string> kvp in a) {
                        if (CompatCustomData.IsIgnoredItemKey(kvp.Key)) { continue; }
                        if (b == null || !b.TryGetValue(kvp.Key, out string other) || kvp.Value != other) { return false; }
                    }
                }
                if (b != null) {
                    foreach (KeyValuePair<string, string> kvp in b) {
                        if (CompatCustomData.IsIgnoredItemKey(kvp.Key)) { continue; }
                        // Values already compared above; only a key missing from a can still differ.
                        if (a == null || !a.ContainsKey(kvp.Key)) { return false; }
                    }
                }
                return true;
            }

            private static int CustomDataHash(Dictionary<string, string> data) {
                if (data == null || data.Count == 0) { return 0; } // must agree with CustomDataEquals
                int acc = 0;
                foreach (KeyValuePair<string, string> kvp in data) {
                    // Skipped keys must stay out of the hash too, or equal items could hash differently.
                    if (CompatCustomData.IsIgnoredItemKey(kvp.Key)) { continue; }
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

            /// <summary>
            /// The sender's own PlayerProfile id - the value stamped onto anything they craft. Not the account
            /// id: it lives in the player's profile and the server has no other way to learn it, which is why
            /// it is reported here and collected into the known-id registry.
            /// </summary>
            [DefaultValue(0L)]
            public long PlayerID { get; set; }
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

            /// <summary>Tracks a live item. Returns false when the item has no resolvable prefab name: such an
            /// entry could never be matched against a later snapshot or handed back by AddToInventory, and a list
            /// of them would fuzzy-match each other in RemoveFromPlayerItems, so it is skipped rather than
            /// recorded.</summary>
            public bool AddItemToPlayerItems(ItemDrop.ItemData item) {
                if (PlayerItems == null) { PlayerItems = new List<PackedItem>(); }

                PackedItem packed = PackedItem.From(item);
                if (packed == null) {
                    Logger.LogWarning($"Not tracking {PackedItem.Describe(item)} for {Name}: it has no ItemDrop prefab, so it cannot be saved or restored.");
                    return false;
                }

                Logger.LogDebug($"Adding saved item {packed.prefabName} with quality - {packed.m_quality}");
                PlayerItems.Add(packed);
                return true;
            }

            public bool AddConfiscatedItem(ItemDrop.ItemData item, string reason = "") {
                PackedItem packedItem = PackedItem.From(item, clampDurability: false);
                if (packedItem == null) {
                    Logger.LogWarning($"Cannot record a confiscation of {PackedItem.Describe(item)} for {Name}: it has no ItemDrop prefab, so it could never be returned.");
                    return false;
                }
                AddConfiscatedItem(packedItem, reason);
                return true;
            }

            /// <summary>Records an already-packed item as confiscated. This is the overload the server-side paths
            /// use: they hold a Character parsed from YAML and no live Player at all.</summary>
            public void AddConfiscatedItem(PackedItem packedItem, string reason = "") {
                if (packedItem == null) { return; }
                if (ConfiscatedItems == null) { ConfiscatedItems = new List<PackedItem>(); }

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
            /// (CharacterManager.ReconcilePlayerToCharacter at join) but the list is owned by the server, because
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
            [YamlMember(ScalarStyle = ScalarStyle.Literal)]
            public string StructureFlagged { get; set; }
            [YamlMember(ScalarStyle = ScalarStyle.Literal)]
            public string ClientContradiction { get; set; }
            [YamlMember(ScalarStyle = ScalarStyle.Literal)]
            public string ItemOriginFlagged { get; set; }
        }
    }
}
