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
        // The same reader, plus duplicate key checking, for files this machine's admin wrote by hand.
        //
        // A repeated key is silently last-wins by default. Mods.yaml writes its empty lists at the very bottom
        // of the file, so an admin who adds an "optionalMods:" block higher up has it overridden by the
        // generated one below with no diagnostic of any kind - and then written back out empty, which is the
        // data loss this is here to surface. Deliberately NOT used on the handshake path: a hostile peer must
        // not gain a new way to make Deserialize throw, where ZRpc would swallow it and skip mod validation.
        public static IDeserializer yamlconfigdeserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties().WithDuplicateKeyChecking().Build();
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

            /// <summary>
            /// Admin authored: keeps this entry out of the reach of RemoveUnloadedModsFromRequired, so a mod
            /// required of clients but not run by the server itself survives a restart.
            ///
            /// The escape hatch for the case that setting's own description warns about - a client-side mod you
            /// require by hand, typically one pinned with a thunderstorePackage - without having to turn the
            /// whole cleanup off for every other mod. Defaults false, so it does nothing until an admin sets it.
            ///
            /// Only requiredMods is ever pruned, so this field is meaningless on the other three lists. It is on
            /// Mod rather than on a wrapper because the lists all hold the same type, and a field that reads as
            /// "do not delete my line" is worth having available wherever someone thinks to type it.
            /// </summary>
            [DefaultValue(false)]
            public bool KeepWhenUnloaded { get; set; }

            // NOTE: a VersionStrictness field lived here and was read by nothing at all - not one call site in
            // the codebase. An admin who set it got it serialized straight back and silently ignored, which is
            // worse than not offering it. Removed rather than documented; IgnoreUnmatchedProperties means a
            // "versionStrictness:" line left in an existing file is ignored rather than throwing, and it
            // disappears on the next rewrite. If per-part version comparison is wanted, it wants implementing
            // against EnforceVersion, not resurrecting as another inert property.

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

            /// <summary>
            /// The single place a Mods object arriving from outside this process is made safe to use.
            ///
            /// Every list here can come back null, and not only from a malformed file. An absent key
            /// deserializes to null rather than throwing (IgnoreUnmatchedProperties, and a payload from a build
            /// that predates a list simply does not carry it), and - the one that actually bit - a key written
            /// with no value at all does too. YamlDotNet's NullNodeDeserializer runs before the object one, so
            /// "optionalMods:" on a line by itself does not leave the property at its field initializer, it
            /// OVERWRITES it with null. An admin who deleted the "{}" intending to type entries underneath got
            /// a NullReferenceException out of the middle of startup and silently lost mod enforcement for the
            /// session.
            ///
            /// Null entry VALUES are filled in rather than dropped. A bare "com.example.Mod:" under a list is an
            /// admin listing a mod they have not given any options to yet; an empty Mod reads as "this mod, any
            /// version, no file check", which is what they meant. Dropping the key instead would delete their
            /// line from the file on the next rewrite, which is the whole bug class this work exists to end.
            /// </summary>
            /// <param name="origin">
            /// Names the source in the log, e.g. "Mods.yaml". Null on the wire path, which reports at Debug
            /// instead: a peer must not be able to write lines into the server log by sending a payload full of
            /// empty entries.
            /// </param>
            internal static Mods Normalized(Mods mods, string origin = null) {
                // Not merely defensive: an empty or whitespace-only handshake string deserializes to null
                // WITHOUT throwing, and FromZPackage used to dereference the result immediately.
                if (mods == null) { return new Mods(); }

                int filled = 0;
                mods.ActiveMods = NormalizeList(mods.ActiveMods, ref filled, () => new Mod());
                mods.RequiredMods = NormalizeList(mods.RequiredMods, ref filled, () => new Mod());
                mods.OptionalMods = NormalizeList(mods.OptionalMods, ref filled, () => new Mod());
                mods.AdminOnlyMods = NormalizeList(mods.AdminOnlyMods, ref filled, () => new Mod());
                mods.ServerOnlyMods = NormalizeList(mods.ServerOnlyMods, ref filled, () => new Mod());
                mods.ActivePatchers = NormalizeList(mods.ActivePatchers, ref filled, () => new PatcherEntry());
                mods.AllowedPatchers = NormalizeList(mods.AllowedPatchers, ref filled, () => new PatcherEntry());

                if (filled > 0) {
                    string message = $"Filled in {filled} entry/entries written with no settings under them. They are treated as listing the mod with no options set.";
                    if (string.IsNullOrEmpty(origin)) { Logger.LogDebug(message); } else { Logger.LogInfo($"{origin}: {message}"); }
                }
                return mods;
            }

            /// <summary>One list: an empty dictionary in place of null, and an empty entry in place of a null value.</summary>
            private static Dictionary<string, T> NormalizeList<T>(Dictionary<string, T> list, ref int filled, Func<T> empty) where T : class {
                if (list == null) { return new Dictionary<string, T>(); }

                // Collected before mutating: assigning into a dictionary while enumerating it throws.
                List<string> blank = null;
                foreach (KeyValuePair<string, T> entry in list) {
                    if (entry.Value != null) { continue; }
                    if (blank == null) { blank = new List<string>(); }
                    blank.Add(entry.Key);
                }
                if (blank == null) { return list; }

                foreach (string key in blank) {
                    list[key] = empty();
                    filled++;
                }
                return list;
            }

            public Mods FromZPackage(ZPackage incoming) {
                // Normalised on `mods` and not on `this`, because `mods` is what every caller uses - they all
                // go through `new Mods().FromZPackage(pkg)` and keep the return value.
                Mods mods = Normalized(DataObjects.yamldeserializer.Deserialize<Mods>(incoming.ReadString()));

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

        /// <summary>
        /// One entry in the legacy KnownCheaters.yaml, and in the embedded seed that still ships with the mod.
        ///
        /// Read-only history as of the Bans.yaml collapse: BanStore imports these once on first run and the
        /// file is never read again. Kept because the embedded seed is still written in this shape and because
        /// an existing server's file has to be migratable.
        /// </summary>
        public class KnownCheaterEntry {
            public string Id { get; set; }
            public string Reason { get; set; }
        }

        /// <summary>
        /// One ban in Bans.yaml.
        ///
        /// Categories are strings rather than an enum on purpose. This file is hand-edited, and YamlDotNet
        /// throws on an enum value it cannot parse - so a single typo would take the whole ban list down with
        /// it. As strings, an unrecognised category is dropped with a warning and the rest of the entry still
        /// enforces. BanCategories owns the parsing.
        ///
        /// Timestamps are ISO-8601 strings, not DateTime, for the same reason AuditEvent.T is: a DateTime
        /// round-trips through YamlDotNet in whatever the host's calendar and offset happen to be, and these
        /// values also have to survive a trip through JSON to the ban network unchanged.
        /// </summary>
        public class BanEntry {
            public string Id { get; set; }
            [DefaultValue(null)]
            public string Name { get; set; }
            [DefaultValue(null)]
            public List<string> Categories { get; set; }
            [DefaultValue(null)]
            public string Reason { get; set; }
            [DefaultValue(null)]
            public string AddedUtc { get; set; }
            /// <summary>Admin hostId, or one of: auto, builtin, legacy, command.</summary>
            [DefaultValue(null)]
            public string AddedBy { get; set; }
            /// <summary>local | auto | builtin | legacy. Never "network" - see BanOutbox.</summary>
            [DefaultValue(null)]
            public string Source { get; set; }
            /// <summary>null means permanent.</summary>
            [DefaultValue(null)]
            public string ExpiresUtc { get; set; }
            /// <summary>Whether this ban may be reported to the ban network. Defaults on.</summary>
            [DefaultValue(true)]
            public bool Share { get; set; } = true;
        }

        /// <summary>The whole of Bans.yaml. A mapping rather than a bare list so it can carry a version.</summary>
        public class BansFile {
            public int Version { get; set; }
            [DefaultValue(null)]
            public List<BanEntry> Bans { get; set; }
        }

        /// <summary>
        /// One owner decision that overrides what the ban network says. Identified by either a raw platform id
        /// or the network subject hash, because a pulled entry the owner has never seen locally has no id.
        /// </summary>
        public class BanOverride {
            [DefaultValue(null)]
            public string Id { get; set; }
            [DefaultValue(null)]
            public string Hash { get; set; }
            /// <summary>allow | deny.</summary>
            public string Decision { get; set; }
            /// <summary>Limit the override to these categories. Null or empty means all of them.</summary>
            [DefaultValue(null)]
            public List<string> Categories { get; set; }
            [DefaultValue(null)]
            public string Reason { get; set; }
            [DefaultValue(null)]
            public string AddedUtc { get; set; }
            [DefaultValue(null)]
            public string AddedBy { get; set; }
        }

        /// <summary>The whole of BanNetwork/Overrides.yaml.</summary>
        public class OverridesFile {
            public int Version { get; set; }
            [DefaultValue(null)]
            public List<BanOverride> Overrides { get; set; }
        }

        /// <summary>
        /// One entry of the ban network feed, and one line of BanNetwork/NetworkBans.yaml.
        ///
        /// This is a wire type: it is deserialized from whatever the endpoint sends, so every field must
        /// survive being absent, and nothing here may be an enum or a DateTime - a value the parser cannot
        /// make sense of has to degrade to a default rather than throw out of the middle of a batch.
        /// Categories and timestamps are resolved afterwards, by NetworkBanRecord.
        /// </summary>
        public class NetworkBanEntry {
            /// <summary>The feed's change counter. Also the local cursor - the highest seq applied.</summary>
            public long Seq { get; set; }
            /// <summary>32 hex characters. Opaque here; BanSubjectCache resolves it to an id when it can.</summary>
            public string Subject { get; set; }
            [DefaultValue(null)]
            public List<string> Categories { get; set; }
            [DefaultValue(null)]
            public string Reason { get; set; }
            /// <summary>Last seen character name, advisory. Written by another server, so display only.</summary>
            [DefaultValue(null)]
            public string Name { get; set; }
            /// <summary>Distinct servers with a live report. The knob BanNetworkMinReporters reads.</summary>
            public int Reporters { get; set; }
            [DefaultValue(null)]
            public string FirstSeenUtc { get; set; }
            [DefaultValue(null)]
            public string UpdatedUtc { get; set; }
            /// <summary>Null means permanent. The most permissive expiry across the reporting servers.</summary>
            [DefaultValue(null)]
            public string ExpiresUtc { get; set; }
            /// <summary>A tombstone: the network retracted this entry and it must stop being enforced.</summary>
            [DefaultValue(false)]
            public bool Revoked { get; set; }
        }

        /// <summary>
        /// One queued report, and one line of BanNetwork/outbox.yaml.
        ///
        /// Carries the RAW platform id: HTTPS is the encryption and the API key the authorisation, and the
        /// network hashes on ingest. Nothing here is an enum or a DateTime - this is serialized as JSON for
        /// the wire, and both would emit something a strict JSON parser rejects.
        /// </summary>
        public class BanReportLine {
            /// <summary>Client-generated UUID. The idempotency key: a replayed report is not a second one.</summary>
            public string ReportId { get; set; }
            public string PlayerId { get; set; }
            /// <summary>"ban" or "unban".</summary>
            public string Op { get; set; }
            [DefaultValue(null)]
            public List<string> Categories { get; set; }
            [DefaultValue(null)]
            public string Reason { get; set; }
            /// <summary>Last known character name. Omitted entirely when BanNetworkSharePlayerName is off.</summary>
            [DefaultValue(null)]
            public string Name { get; set; }
            [DefaultValue(null)]
            public string OccurredUtc { get; set; }
            /// <summary>Null means permanent. Kept so a temporary ban stays temporary across the network.</summary>
            [DefaultValue(null)]
            public string ExpiresUtc { get; set; }
        }

        /// <summary>One line of the POST /v1/reports response: what became of one submitted line.</summary>
        public class BanReportResult {
            public string ReportId { get; set; }
            public bool Accepted { get; set; }
            [DefaultValue(null)]
            public string Error { get; set; }
        }

        /// <summary>
        /// BanNetwork/state.yaml: everything the scheduler needs to pick up where it left off.
        ///
        /// Machine-owned. The backoff is persisted deliberately - a server in a crash loop would otherwise
        /// hammer a shared endpoint once per boot.
        /// </summary>
        public class BanNetworkStateFile {
            public int Version { get; set; }
            /// <summary>Highest seq applied. Pulls ask for everything above it.</summary>
            public long Cursor { get; set; }
            [DefaultValue(null)]
            public string LastPullUtc { get; set; }
            [DefaultValue(null)]
            public string LastPushUtc { get; set; }
            /// <summary>Last known verdict on the API key, so a terminal one survives a restart.</summary>
            [DefaultValue(null)]
            public string KeyState { get; set; }
            [DefaultValue(null)]
            public string KeyStateNote { get; set; }
            [DefaultValue(0)]
            public int ConsecutiveFailures { get; set; }
            [DefaultValue(null)]
            public string NextAttemptUtc { get; set; }
            /// <summary>The id half of the key. Not secret, and the only half ever logged.</summary>
            [DefaultValue(null)]
            public string ServerId { get; set; }
            [DefaultValue(null)]
            public string LastError { get; set; }
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

            /// <summary>
            /// The Forsaken Power the sender has selected, or null when it is not reporting one - tracking is off,
            /// or the sender predates it. Null leaves the stored value alone; the empty string is a real value
            /// meaning "no power selected". See <see cref="Character.GuardianPower"/>.
            /// </summary>
            [DefaultValue(null)]
            public string GuardianPower { get; set; }

            /// <summary>
            /// The foods the sender currently has eaten, or null when it is not reporting them - tracking is off, or
            /// the sender predates it. Null leaves the stored value alone; an empty list is a real value meaning "no
            /// food". See <see cref="Character.Foods"/>.
            /// </summary>
            [DefaultValue(null)]
            public List<PackedFood> Foods { get; set; }
        }

        /// <summary>
        /// One eaten food, recorded the way vanilla's own player profile records it: the item's prefab name and the
        /// seconds of burn time left. Health, stamina and eitr are not stored because vanilla derives them from the
        /// burn time on every food tick.
        /// </summary>
        [Serializable]
        public class PackedFood {
            public string Name { get; set; }
            public float Time { get; set; }

            // Two captures of the same food moments apart differ only by drain, and a float that has been through
            // YAML may not round trip exactly. Anything within this is the same serving.
            internal const float TimeToleranceSeconds = 1f;

            public PackedFood() {
            }

            internal static List<PackedFood> Copy(List<PackedFood> foods) {
                if (foods == null) { return null; }
                List<PackedFood> copy = new List<PackedFood>(foods.Count);
                foreach (PackedFood food in foods) {
                    if (food == null) { continue; }
                    copy.Add(new PackedFood { Name = food.Name, Time = food.Time });
                }
                return copy;
            }

            /// <summary>
            /// True when <paramref name="candidate"/> holds anything <paramref name="baseline"/> does not account for:
            /// a food that is not in it, or more burn time on one than it had. Burn time only ever drains, so the
            /// only ways to get either are eating something or bringing it in from elsewhere. Having less than the
            /// baseline - a food that drained or ran out - is never an excess. A null candidate is not reporting
            /// its foods at all and is counted as an excess of any tracked baseline, so that not reporting cannot
            /// be used to escape the check.
            /// </summary>
            internal static bool Exceeds(List<PackedFood> candidate, List<PackedFood> baseline) {
                if (baseline == null) { return false; }
                if (candidate == null) { return true; }
                foreach (PackedFood food in candidate) {
                    if (food == null) { continue; }
                    PackedFood match = Find(baseline, food.Name);
                    if (match == null || food.Time > match.Time + TimeToleranceSeconds) { return true; }
                }
                return false;
            }

            /// <summary>True when both lists hold the same foods, in any order, with burn times within the tolerance.</summary>
            internal static bool Same(List<PackedFood> a, List<PackedFood> b) {
                if (a == null || b == null) { return a == b; }
                if (Count(a) != Count(b)) { return false; }
                foreach (PackedFood food in a) {
                    if (food == null) { continue; }
                    PackedFood match = Find(b, food.Name);
                    if (match == null || Math.Abs(food.Time - match.Time) > TimeToleranceSeconds) { return false; }
                }
                return true;
            }

            private static PackedFood Find(List<PackedFood> foods, string name) {
                foreach (PackedFood food in foods) {
                    if (food != null && food.Name == name) { return food; }
                }
                return null;
            }

            private static int Count(List<PackedFood> foods) {
                int count = 0;
                foreach (PackedFood food in foods) {
                    if (food != null) { count++; }
                }
                return count;
            }
        }

        public class CharacterSaveData {
            public Dictionary<string, Character> SavedCharacters = new Dictionary<string, Character>();
        }

        public class AccountEntries {
            public Dictionary<string, List<string>> AccountCharacterEntries = new Dictionary<string, List<string>>();
        }

        /// <summary>
        /// One forced lowering of a skill by this mod: a returning character clamped back to the level the server
        /// holds for them, or a new character's skills set to zero. Recorded so an admin can undo it
        /// (enforcer-skills-restore) the way a confiscated item can be handed back. The game's own skill loss on
        /// death is not one of these, and neither is the correction of an impossible value (above 100, negative,
        /// NaN) - there is nothing valid to put such a value back to.
        /// </summary>
        [Serializable]
        public class SkillReduction {
            public Skills.SkillType Skill { get; set; }
            /// <summary>The level the skill was lowered from. Kept as reported, even when it is a value the game
            /// could never produce, because "reduced from 150" tells an admin what actually happened; a restore
            /// clamps it to the valid range.</summary>
            public float From { get; set; }
            public float To { get; set; }
            public string Reason { get; set; }
            public DateTime Time { get; set; }
            /// <summary>Stable per-record identity, assigned once in Character.AddSkillReduction, for the same
            /// reason PackedItem.confiscationId exists: the client reports what it lowered this session on every
            /// full push, and the server appends by id so a repeated push neither duplicates nor loses one.</summary>
            [DefaultValue(null)]
            public string Id { get; set; }
        }

        /// <summary>
        /// One difficulty bucket of vanilla's player statistics - the numbers the post-1.0 achievement system
        /// is built on (PlayerProfile.PlayerStats).
        ///
        /// Everything is keyed by NAME and stored sparsely, never by index and never densely. Vanilla writes
        /// these into the .fch as a positional float[205] in PlayerStatType order, so an index here would be
        /// silently wrong the first time Iron Gate inserts an enum member - and the stat that moved would be
        /// restored onto the wrong counter rather than failing loudly. A name that no longer exists is simply
        /// skipped on the way back in. Sparse because 205 counters are mostly zero for any real character, and
        /// this rides every full character save.
        /// </summary>
        /// <summary>One item a starter loadout grants.</summary>
        public class LoadoutItem {
            /// <summary>The prefab name, as it appears in the game's object database - the same spelling
            /// NewCharacterStartingItems takes.</summary>
            public string prefabName { get; set; }
            [DefaultValue(1)]
            public int stack { get; set; } = 1;
            /// <summary>Upgrade level. 1 is an unupgraded item; a loadout may grant more, unlike the
            /// starting-item allowlist, because this is the server handing the item over rather than a
            /// character turning up with it.</summary>
            [DefaultValue(1)]
            public int quality { get; set; } = 1;
            [DefaultValue(0)]
            public int variant { get; set; }
            /// <summary>Whether the item arrives equipped. Ignored for anything that cannot be.</summary>
            [DefaultValue(false)]
            public bool equipped { get; set; }
        }

        /// <summary>
        /// A starting kit for a character this server has never seen.
        ///
        /// Granted AFTER the new-character rules have stripped what the character arrived with, which is what
        /// makes the two coexist without a special case: the strip decides what a character may keep, and this
        /// decides what the server hands them, so an item in a loadout never has to be in
        /// NewCharacterStartingItems as well.
        /// </summary>
        public class Loadout {
            /// <summary>Free text, for the admin reading the file and for enforcer-loadout-list.</summary>
            [DefaultValue(null)]
            public string description { get; set; }
            [DefaultValue(null)]
            public List<LoadoutItem> items { get; set; }
            /// <summary>Skill levels to start at. Only ever raises: a level already above what the loadout
            /// names is left alone, so a kit cannot be used to take something away.</summary>
            [DefaultValue(null)]
            public Dictionary<Skills.SkillType, float> skills { get; set; }
            /// <summary>Item shared-names ("$item_...") to count as already discovered. Materials rather than
            /// recipes is usually what is wanted - vanilla derives most recipes from the materials and
            /// stations a character knows. Only used when SyncKnownItems is on, because that is what gives
            /// the server somewhere to record them.</summary>
            [DefaultValue(null)]
            public List<string> knownMaterials { get; set; }
            [DefaultValue(null)]
            public List<string> knownRecipes { get; set; }
            /// <summary>Where the character first wakes up. Only used when SyncSpawnPoint is on. Set
            /// haveSpawnPoint to false - the default - to leave the world's own start location alone.</summary>
            [DefaultValue(false)]
            public bool haveSpawnPoint { get; set; }
            [DefaultValue(0f)]
            public float spawnX { get; set; }
            [DefaultValue(0f)]
            public float spawnY { get; set; }
            [DefaultValue(0f)]
            public float spawnZ { get; set; }
        }

        /// <summary>The whole of Loadouts.yaml.</summary>
        public class Loadouts {
            public Dictionary<string, Loadout> loadouts { get; set; } = new Dictionary<string, Loadout>();
        }

        public class StatBucket {
            /// <summary>PlayerStatType name -> value. Zeros omitted.</summary>
            [DefaultValue(null)]
            public Dictionary<string, float> Stats { get; set; }
            /// <summary>World name -> seconds played there.</summary>
            [DefaultValue(null)]
            public Dictionary<string, float> KnownWorlds { get; set; }
            /// <summary>"globalkey value" -> seconds since it was set.</summary>
            [DefaultValue(null)]
            public Dictionary<string, float> KnownWorldKeys { get; set; }
            [DefaultValue(null)]
            public Dictionary<string, float> KnownCommands { get; set; }
            /// <summary>KillModifiers name -> (creature name -> count). Vanilla holds five of these in an array
            /// indexed by the enum; keyed by name here for the same reason the stats are.</summary>
            [DefaultValue(null)]
            public Dictionary<string, Dictionary<string, float>> EnemyStats { get; set; }
            [DefaultValue(null)]
            public Dictionary<string, float> ItemPickup { get; set; }
            [DefaultValue(null)]
            public Dictionary<string, float> ItemCraft { get; set; }
            [DefaultValue(null)]
            public Dictionary<string, float> Pickable { get; set; }
            [DefaultValue(null)]
            public Dictionary<string, float> FoodEaten { get; set; }
            [DefaultValue(null)]
            public Dictionary<string, float> PiecesPlaced { get; set; }
        }

        /// <summary>
        /// Where a character respawns on this world. Vanilla keeps these per world UID inside the player
        /// profile, which is why they are lost with a corrupted local save.
        ///
        /// Stored as flat floats rather than a Vector3: the shared serializer has no converter for Unity types,
        /// and a Vector3 round-trips through it as an object carrying every derived property Unity puts on the
        /// struct (normalized, magnitude, sqrMagnitude...), which is both enormous and lossy.
        ///
        /// The logout point and death point are deliberately NOT here. Restoring a stale logout point on join
        /// would silently relocate a player who has since moved, which is the opposite of what a progression
        /// restore is for; they belong to crash recovery, where putting somebody back is the whole point.
        /// </summary>
        public class SpawnPoints {
            /// <summary>False means "no bed claimed here", which is a real answer and not the same as untracked -
            /// untracked is the whole <see cref="Progression.Spawn"/> being null.</summary>
            public bool HaveCustomSpawn { get; set; }
            public float SpawnX { get; set; }
            public float SpawnY { get; set; }
            public float SpawnZ { get; set; }
            public float HomeX { get; set; }
            public float HomeY { get; set; }
            public float HomeZ { get; set; }
        }

        /// <summary>
        /// The half of a character's progress that lives in the player profile rather than in the character
        /// itself, and that the server had no copy of until now: what they have explored, what they know how to
        /// make, what they have killed, and where they wake up.
        ///
        /// Every field is null when untracked, on the same reasoning as <see cref="Character.GuardianPower"/>
        /// and <see cref="Character.Foods"/>: a save written while a sync setting was off must not read back as
        /// "this character knows nothing", or turning the setting on would wipe everyone once. Empty and null
        /// are different answers throughout.
        ///
        /// The map is not here. It is 8.4 MB uncompressed per world and tens of kilobytes to a megabyte
        /// compressed, so it lives beside the save as an opaque .map file and only its hash is recorded here -
        /// see modules.character.MapSync.
        /// </summary>
        public class Progression {
            /// <summary>Item shared-names and Piece.m_name values. Largely derived by vanilla from materials and
            /// stations on every inventory change, so it is restored alongside them rather than instead.</summary>
            [DefaultValue(null)]
            public List<string> KnownRecipes { get; set; }
            /// <summary>Item shared-names ("$item_..."), a different key space from <see cref="Trophies"/>.</summary>
            [DefaultValue(null)]
            public List<string> KnownMaterials { get; set; }
            /// <summary>Crafting station name -> highest level seen.</summary>
            [DefaultValue(null)]
            public Dictionary<string, int> KnownStations { get; set; }
            /// <summary>PREFAB names. Vanilla stores trophies by prefab while recipes and materials are
            /// localization tokens; do not merge the two.</summary>
            [DefaultValue(null)]
            public List<string> Trophies { get; set; }
            /// <summary>BiomeSector.GetBiomeName() strings.</summary>
            [DefaultValue(null)]
            public List<string> KnownBiomes { get; set; }
            /// <summary>Runestone label -> text. The largest of the string sets on a well-travelled character.</summary>
            [DefaultValue(null)]
            public Dictionary<string, string> KnownTexts { get; set; }
            /// <summary>"key value" pairs, e.g. "invrows 4" - vanilla uses these for permanent unlocks.</summary>
            [DefaultValue(null)]
            public List<string> Uniques { get; set; }
            /// <summary>Tutorials seen, which is also where vanilla files discovered location names.</summary>
            [DefaultValue(null)]
            public List<string> ShownTutorials { get; set; }
            /// <summary>DifficultyRequirement name -> that bucket's counters.</summary>
            [DefaultValue(null)]
            public Dictionary<string, StatBucket> Stats { get; set; }
            [DefaultValue(null)]
            public SpawnPoints Spawn { get; set; }
            /// <summary>Lowercase hex SHA256 of the map blob in the .map file beside this save, or null when
            /// none is stored. The client keeps the last hash it sent and skips rebuilding an 8.4 MB package
            /// when nothing has been explored since.</summary>
            [DefaultValue(null)]
            public string MapHash { get; set; }
        }

        public class Character {
            public string Name { get; set; }
            public string HostID { get; set; }
            public DisconnectionState LastDisconnect { get; set; } = DisconnectionState.Clean;
            public Dictionary<Skills.SkillType, float> SkillLevels { get; set; } = new Dictionary<Skills.SkillType, float>();
            /// <summary>
            /// The Forsaken Power this character last had selected here - the status effect name vanilla hands to
            /// Player.SetGuardianPower, such as GP_Eikthyr. Empty and null are different answers: empty means
            /// tracked with no power selected, null means never tracked (the save predates
            /// PreventExternalForsakenPowerChanges, or was written with it off), and a join adopts the live power
            /// rather than stripping it. See modules.character.ForsakenPower.
            /// </summary>
            [DefaultValue(null)]
            public string GuardianPower { get; set; }
            /// <summary>
            /// The foods this character last had eaten here. Empty and null are different answers, the same way as
            /// <see cref="GuardianPower"/>: empty means tracked with no food eaten, null means never tracked (the save
            /// predates PreventExternalFoodChanges, or was written with it off), and a join adopts the live foods
            /// rather than stripping them. See modules.character.FoodSync.
            /// </summary>
            [DefaultValue(null)]
            public List<PackedFood> Foods { get; set; }
            public Dictionary<string, string> PlayerCustomData { get; set; } = new Dictionary<string, string>();
            public Dictionary<string, PackedStatusEffect> ActiveCharacterEffects { get; set; } = new Dictionary<string, PackedStatusEffect>();
            public List<PackedItem> PlayerItems { get; set; } = new List<PackedItem>();
            public List<PackedItem> ConfiscatedItems { get; set; } = new List<PackedItem>();
            /// <summary>
            /// Every forced skill reduction this character has had here that an admin has not yet restored or
            /// cleared. Server-owned in exactly the way <see cref="ConfiscatedItems"/> is: the client only reports
            /// what it lowered this session, the server appends by id, and it is withheld from every payload sent to
            /// a client. Null rather than empty when there are none, so a save with no records is written unchanged.
            /// See modules.character.SkillReductions.
            /// </summary>
            [DefaultValue(null)]
            public List<SkillReduction> SkillReductions { get; set; }
            /// <summary>
            /// Skill levels an admin has restored (enforcer-skills-restore) that the player's client has not yet been
            /// seen to hold. Server-owned and kept across the client's saves, but - unlike the lists above - sent to
            /// the client, because the client is what applies it: on join, or straight away when online. An entry
            /// is dropped by the server the moment a save or delta reports the skill at or above the level, so a
            /// restore that never reached the client (they disconnected as it was sent, or run a build that
            /// predates this) is applied on a later join instead of being lost. Also raises the ceiling the
            /// returning-character skill clamp holds the first save of a session to, so the restored level is not
            /// read as an external gain and taken straight back.
            /// </summary>
            [DefaultValue(null)]
            public Dictionary<Skills.SkillType, float> PendingSkillRestores { get; set; }
            /// <summary>
            /// Map, known items, statistics and spawn point - the progress that lives in the player profile.
            /// Null when nothing about it is being synced, which is the default. See <see cref="Progression"/>.
            /// </summary>
            [DefaultValue(null)]
            public Progression Progress { get; set; }
            /// <summary>
            /// Bumped by the server on every save it accepts for this character, and never by a client.
            ///
            /// It is the only thing that can order two copies of one character, which is what crash recovery
            /// needs: a snapshot a client hands back is adopted only when its sequence is strictly greater than
            /// the one the server holds, so a replayed old snapshot is refused rather than rolling somebody
            /// backwards. Populated from the moment progression sync ships so that recovery, when it arrives,
            /// finds a number already counting rather than every character sitting at zero.
            /// </summary>
            public long SaveSequence { get; set; }

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

            /// <summary>Records that this mod lowered a skill. Pure data - safe on the CharacterStore worker.</summary>
            public void AddSkillReduction(Skills.SkillType skill, float from, float to, string reason) {
                if (SkillReductions == null) { SkillReductions = new List<SkillReduction>(); }
                SkillReductions.Add(new SkillReduction {
                    Skill = skill,
                    From = from,
                    To = to,
                    Reason = reason,
                    Time = DateTime.UtcNow,
                    Id = Guid.NewGuid().ToString("N"),
                });
            }

            /// <summary>
            /// Server side: fold a client's reported skill reductions into this (authoritative) character's list.
            /// Append only, keyed on id, for exactly the reasons <see cref="MergeConfiscatedItems"/> gives: the
            /// client's copy is a report of this session, never a replacement for ours, and a repeated push must be
            /// idempotent. Entries with no id are legacy mirrors of something already held and are ignored.
            /// </summary>
            /// <returns>How many new entries were appended.</returns>
            public int MergeSkillReductions(List<SkillReduction> incoming) {
                if (incoming == null || incoming.Count == 0) { return 0; }

                HashSet<string> known = new HashSet<string>();
                if (SkillReductions != null) {
                    foreach (SkillReduction existing in SkillReductions) {
                        if (existing != null && !string.IsNullOrEmpty(existing.Id)) { known.Add(existing.Id); }
                    }
                }

                int added = 0;
                foreach (SkillReduction candidate in incoming) {
                    if (candidate == null || string.IsNullOrEmpty(candidate.Id)) { continue; }
                    if (!known.Add(candidate.Id)) { continue; } // already recorded
                    if (SkillReductions == null) { SkillReductions = new List<SkillReduction>(); }
                    SkillReductions.Add(candidate);
                    added++;
                }
                return added;
            }

            /// <summary>Notes that an admin wants this skill put back to <paramref name="level"/>. A higher level
            /// already pending for the same skill is kept.</summary>
            public void AddPendingSkillRestore(Skills.SkillType skill, float level) {
                if (PendingSkillRestores == null) { PendingSkillRestores = new Dictionary<Skills.SkillType, float>(); }
                if (PendingSkillRestores.TryGetValue(skill, out float current) && current >= level) { return; }
                PendingSkillRestores[skill] = level;
            }

            /// <summary>
            /// Server side, after <see cref="SkillLevels"/> has been replaced by what a client reported: drop every
            /// pending restore the report shows has landed. The client says nothing about having applied one; it
            /// simply reports the raised level, and that is the only confirmation worth having. Pure data.
            /// </summary>
            /// <returns>How many pending restores were confirmed.</returns>
            public int ConsumePendingSkillRestores() {
                if (PendingSkillRestores == null || PendingSkillRestores.Count == 0 || SkillLevels == null) { return 0; }
                List<Skills.SkillType> landed = new List<Skills.SkillType>();
                foreach (KeyValuePair<Skills.SkillType, float> pending in PendingSkillRestores) {
                    // A whisker of tolerance: the level has been through YAML at least once on its way here.
                    if (SkillLevels.TryGetValue(pending.Key, out float reported) && reported + 0.01f >= pending.Value) {
                        landed.Add(pending.Key);
                    }
                }
                foreach (Skills.SkillType skill in landed) {
                    Logger.LogInfo($"Skill restore for {Name} confirmed: {skill} is back at {SkillLevels[skill]}.");
                    PendingSkillRestores.Remove(skill);
                }
                if (PendingSkillRestores.Count == 0) { PendingSkillRestores = null; }
                return landed.Count;
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
            public string BanEnforced { get; set; }
            [YamlMember(ScalarStyle = ScalarStyle.Literal)]
            public string BanNetworkUnavailable { get; set; }
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
