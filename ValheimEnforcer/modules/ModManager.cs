using BepInEx;
using HarmonyLib;
using Jotunn;
using Jotunn.Extensions;
using Jotunn.Managers;
using Jotunn.Utils;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using ValheimEnforcer.common;
using ValheimEnforcer.modules.mods;
using ValheimEnforcer.modules.notifications;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.modules {
    internal static class ModManager {

        /// <summary>Best-effort lookup of a connecting peer's player name by its handshake RPC. May be empty early in the handshake.</summary>
        private static string ResolvePeerName(ZRpc rpc) {
            ZNetPeer peer = ZNet.instance?.GetPeer(rpc);
            return string.IsNullOrEmpty(peer?.m_playerName) ? null : peer.m_playerName;
        }

        internal static DataObjects.Mods ModSettings { get; set; }
        internal static Dictionary<string, BaseUnityPlugin> ActiveMods = new Dictionary<string, BaseUnityPlugin>();
        internal static JotunnDetailDisconnectExpansion DetailsUpdater { get; set; }

        internal static void SetModsActive() {
            ActiveMods.Clear();
            ActiveMods = BepInExUtils.GetPlugins(true);
            // Started before the config read so the file hashing overlaps the YAML parse below. Idempotent, so
            // the second call on a listen host (both Jotunn prefab events fire) costs nothing.
            PluginHasher.BeginPass(ActiveMods);
            // Same reasoning, second directory: BepInEx/patchers is loaded before any plugin and was never
            // looked at, so a cheat shipped as a preloader patcher bypassed mod validation entirely.
            PatcherIndex.BeginPass();

            ModSettings = new DataObjects.Mods();
            Logger.LogDebug($"Detected {ActiveMods.Keys.Count} mods.");

            // Read the config file. Guarded rather than assumed: this runs from a Jotunn prefab event, so a
            // missing file threw out of an event handler. Missing is deliberately not the same answer as
            // unreadable - there is nothing here to lose, so empty settings are correct and the startup rewrite
            // recreates the file from the loaded plugins.
            if (File.Exists(ValConfig.ModsConfigFilePath)) {
                LoadConfig(File.ReadAllText(ValConfig.ModsConfigFilePath));
            } else {
                Logger.LogWarning($"{ValConfig.ModsFileName} is not there; starting from an empty mod list and writing a fresh one.");
                ModSettings = new DataObjects.Mods();
            }
            NoteModsOnAdminOnlyAndRequired();

            PluginHasher.WaitForPass(ValConfig.HashComputeTimeoutSeconds.Value * 1000);
            PatcherIndex.WaitForPass(ValConfig.HashComputeTimeoutSeconds.Value * 1000);
            RebuildActiveMods();
            RebuildActivePatchers();
            WriteActiveModsFile();

            foreach (KeyValuePair<string, BaseUnityPlugin> plugin in ActiveMods) {
                Logger.LogDebug($"Found active mod: {plugin.Key} v{plugin.Value.Info.Metadata.Version}");
                string currentVersion = plugin.Value.Info.Metadata.Version.ToString();
                string localHash = PluginHasher.Get(plugin.Key)?.Hash;

                // AdminOnly before Required, matching ValidateModlist: for a mod on both lists the admin-only
                // entry is the one clients are checked against, so it is the one kept current.
                if (ModSettings.AdminOnlyMods.ContainsKey(plugin.Key)) {
                    UpdateModVersionIfChanged(ModSettings.AdminOnlyMods, plugin.Key, currentVersion);
                    RecordLocalHashIfAllowed(ModSettings.AdminOnlyMods, plugin.Key, localHash, currentVersion);
                    continue;
                }
                if (ModSettings.RequiredMods.ContainsKey(plugin.Key)) {
                    UpdateModVersionIfChanged(ModSettings.RequiredMods, plugin.Key, currentVersion);
                    RecordLocalHashIfAllowed(ModSettings.RequiredMods, plugin.Key, localHash, currentVersion);
                    continue;
                }
                if (ModSettings.OptionalMods.ContainsKey(plugin.Key)) {
                    UpdateModVersionIfChanged(ModSettings.OptionalMods, plugin.Key, currentVersion);
                    RecordLocalHashIfAllowed(ModSettings.OptionalMods, plugin.Key, localHash, currentVersion);
                    continue;
                }
                if (ModSettings.ServerOnlyMods.ContainsKey(plugin.Key)) {
                    UpdateModVersionIfChanged(ModSettings.ServerOnlyMods, plugin.Key, currentVersion);
                    continue;
                } // Server only mods are basically the skip button for a mod

                if (ValConfig.AutoAddModsToRequired.Value == true) {
                    Logger.LogDebug($"Automatically adding {plugin.Key} as a required mod.");
                    ModSettings.RequiredMods.Add(plugin.Key, new DataObjects.Mod() { EnforceVersion = false, Version = currentVersion, PluginID = plugin.Value.Info.Metadata.GUID, Name = plugin.Value.Info.Metadata.Name });
                    RecordLocalHashIfAllowed(ModSettings.RequiredMods, plugin.Key, localHash, currentVersion);
                }
            }

            if (ValConfig.RemoveUnloadedModsFromRequired.Value) {
                RemoveUnloadedRequiredMods();
            }

            // Write out updates to the loaded mods, if enabled
            if (ValConfig.UpdateLoadedModsOnStartup.Value) {
                Logger.LogDebug("Updated Mods.yaml.");
                PersistModSettings();
            }
        }

        /// <summary>
        /// Every field an entry may carry, what it does and what it defaults to.
        ///
        /// Written once and spliced into both banners rather than maintained twice. The two files disagreeing
        /// about the schema is a question of when, not whether, and the copy in ServerActiveMods.yaml is the one
        /// an admin is looking at in the moment they are about to paste an entry into Mods.yaml.
        ///
        /// It is deliberately the whole schema and not a pointer at the README. This is the file people hand
        /// edit, frequently over SSH on a box with no browser on it, and "the README covers all of it" is not an
        /// answer to "what do I type here". An admin who does not want it can delete it: a banner is only ever
        /// regenerated for a file that has no leading comment at all, so one line of their own keeps it gone.
        /// </summary>
        private static readonly string[] EntryFieldReferenceLines = {
            "# Fields on a mod entry. Only the ones you want are needed - the rest are written for you, or left",
            "# out entirely when unused.",
            "#",
            "#   pluginID            The BepInEx plugin GUID. The same value as the entry's key. Written for you.",
            "#   version             The version this list expects. Kept current automatically for mods this",
            "#                       machine actually loads, so updating a mod needs no edit here.",
            "#   name                Human readable. Used in logs and in the text a rejected player is shown.",
            "#   enforceVersion      Require an exact version match. Default false, so a client a patch version",
            "#                       behind is not locked out of a server that never asked for exact versions.",
            "#   keepWhenUnloaded    Never drop this entry just because this machine does not load the mod.",
            "#                       Default false. Only requiredMods is ever pruned, and only when",
            "#                       RemoveUnloadedModsFromRequired is on - this is the per-mod opt out, for a",
            "#                       mod you require of clients but do not run on the server yourself.",
            "#",
            "# File verification. Catches a mod somebody recompiled with different numbers in it while leaving",
            "# the version string alone. All optional; an entry using none of these is checked by name and",
            "# version only.",
            "#",
            "#   acceptedHashes      List of SHA256 hashes this mod's DLL may have. Matching any one passes, so",
            "#                       several builds can be allowed at once. Case insensitive.",
            "#   hashSource          Where acceptedHashes came from: " + HashPolicy.SourceLocal + ", " + HashPolicy.SourceManual + " or " + HashPolicy.SourceThunderstore + ".",
            "#                       Only " + HashPolicy.SourceLocal + " entries are refreshed at startup, which is what lets a hash you",
            "#                       pinned by hand survive a restart instead of being overwritten by whatever",
            "#                       this machine happens to have on disk. Set it to " + HashPolicy.SourceManual + " to protect yours.",
            "#   hashedFrom          Bookkeeping for the above: 'local:<version>', or 'Owner-Name-Version' for a",
            "#                       resolved package. Re-resolution happens only when this stops matching.",
            "#   thunderstorePackage 'Owner-ModName' or 'Owner-ModName-Version' - the same dependency string a",
            "#                       Thunderstore manifest uses. The server downloads that package and records",
            "#                       its hashes. Needs ResolveThunderstoreHashes, which is off by default. This",
            "#                       is the only way this mod reaches the network; download URLs are not",
            "#                       supported on purpose.",
            "#   hashEnforcement     Per-mod override of the server's HashEnforcement setting: " + HashPolicy.Off + ", " + HashPolicy.WhenKnown + "",
            "#                       or " + HashPolicy.Strict + ".",
            "#",
            "# Two more fields turn up in these files but are NOT policy and setting them achieves nothing:",
            "# 'hash' and 'hashStatus' are what a client reports about its own copy of a mod.",
            "#",
            "# A patcher entry carries only 'name' and 'acceptedHashes'. A patcher has no GUID and no version, so",
            "# its file is the only thing that identifies it.",
            "#",
            "# An empty list is written as a bare key ('optionalMods:' with nothing under it). Add entries by",
            "# indenting them beneath it, two spaces in.",
        };

        /// <summary>
        /// The banner a new Mods.yaml is created with. It lives here rather than beside the file creation code
        /// because <see cref="PersistModSettings"/> also has to put it back on installs that lost it: before
        /// comments were preserved, the first rewrite after launch deleted it.
        ///
        /// Declared after <see cref="EntryFieldReferenceLines"/> on purpose: static field initializers run in
        /// declaration order, so a reference that has not been initialized yet is simply null.
        /// </summary>
        internal static readonly string[] ModsFileHeaderLines = new[] {
            "#################################################",
            "# Valheim Enforcer - Mod List",
            "#",
            "# Regenerated on startup, and re-read within ConfigPollIntervalSeconds of being edited.",
            "# Comments are kept: a note on its own line stays with the entry below it. A comment sharing",
            "# a line with a value is not kept, because that line gets rewritten.",
            "#",
            "# Every entry is keyed by its BepInEx plugin GUID.",
            "#",
            ActiveModsHeaderLine,
            "#   requiredMods    Clients must have these. Mods the server loads land here by themselves.",
            "#   optionalMods    Clients may have these, and may connect without them.",
            "#   adminOnlyMods   Admins may have these, and may connect without them. Anyone else is rejected.",
            "#                   Wins over requiredMods, so a mod on both is required of nobody.",
            "#   serverOnlyMods  Server side only. Not demanded of clients - but a client that installs one",
            "#                   is rejected for it, so this is not the list for client-side mods.",
            "#",
            "# The two patcher lists are keyed by file path instead, relative to BepInEx/patchers. Patchers are",
            "# not plugins and carry no GUID or version, so the file hash is all there is to hold them to.",
            "#",
            "#   activePatchers  What this machine has in BepInEx/patchers. Rebuilt every start - editing it does nothing.",
            "#   allowedPatchers Patchers a client may carry. An allowlist: a client with none always passes.",
            "#                   The server's own are added here automatically. Needs Mods.ValidatePatchers to enforce.",
            "#",
        }
        .Concat(EntryFieldReferenceLines)
        .Concat(new[] { "#################################################" })
        .ToArray();


        /// <summary>The banner line pointing at ServerActiveMods.yaml, which took the place of <see cref="LegacyActiveModsHeaderLine"/>.</summary>
        private const string ActiveModsHeaderLine = "# ServerActiveMods.yaml, beside this file, lists every plugin this machine loaded. Copy entries from it into:";

        /// <summary>
        /// The line every banner carried while activeMods was still written into Mods.yaml. Swapped in place for
        /// <see cref="ActiveModsHeaderLine"/> on the next rewrite, since a preserved banner is never regenerated and
        /// would otherwise go on describing a list the file no longer holds. Matched exactly, so an admin who
        /// rewrote the line keeps their version.
        /// </summary>
        private const string LegacyActiveModsHeaderLine = "#   activeMods      What this machine actually loaded. Rebuilt every start - editing it does nothing.";

        private static readonly string[] ActiveModsFileHeaderLines = new[] {
            "#################################################",
            "# Valheim Enforcer - Active Mods",
            "#",
            "# Every plugin this machine loaded, for copying into the lists in Mods.yaml. Each entry is already",
            "# indented to sit under requiredMods, optionalMods, adminOnlyMods or serverOnlyMods.",
            "#",
            "# Reference only: deleted and rewritten every start, and never read. Editing it does nothing.",
            "#",
            "# The entries below carry only what is installed. Everything that expresses policy is yours to add",
            "# once the entry is in Mods.yaml:",
            "#",
        }
        .Concat(EntryFieldReferenceLines)
        .Concat(new[] { "#################################################" })
        .ToArray();

        /// <summary>
        /// Serializes the current mod settings to Mods.yaml. Shared by the startup rewrite and the Thunderstore
        /// resolver so both go through the same self-write suppression - otherwise our own write comes straight
        /// back in through the file watcher one poll later.
        /// </summary>
        internal static void PersistModSettings() {
            if (ModSettings == null) { return; }
            try {
                string yaml = SerializeModsFile(ModSettings);
                // Taken before the write, so the copy is by definition the file as the admin last left it, and
                // a session that never rewrites leaves no litter behind.
                ConfigFileBackup.TryBackupOnce(ValConfig.ModsConfigFilePath);
                // Atomic rather than File.WriteAllText: that truncates the destination before it writes, and
                // this document serializes requiredMods first and the three admin-authored lists last, so a
                // process killed mid-write left behind a file that had lost exactly the lists nothing rebuilds.
                AtomicFile.WriteText(ValConfig.ModsConfigFilePath, WithPreservedComments(yaml));
                // Qualified: Jotunn.Utils has a ConfigFileWatcher of its own and this file imports that namespace.
                common.ConfigFileWatcher.NoteSelfWrite(ValConfig.ModsConfigFilePath);
            } catch (System.Exception e) {
                Logger.LogWarning($"Could not write {ValConfig.ModsConfigFilePath}: {e.Message}");
            }
        }

        /// <summary>
        /// Carries the comments in the file on disk over onto freshly serialized YAML, and restores the header
        /// banner when the file no longer has one.
        ///
        /// This method is why an admin can annotate Mods.yaml at all: the serializer builds its output from the
        /// object graph, which has never held a comment, so without this every rewrite published a file with
        /// the admin's notes stripped out of it.
        ///
        /// A failure here degrades to the plain serialized text rather than propagating. Losing a comment is
        /// the lesser of the two outcomes; refusing to write is how a mod list goes stale without anyone
        /// noticing.
        /// </summary>
        private static string WithPreservedComments(string yaml) {
            try {
                string existing = File.Exists(ValConfig.ModsConfigFilePath) ? File.ReadAllText(ValConfig.ModsConfigFilePath) : null;
                YamlComments.Captured captured = YamlComments.Capture(existing);
                int legacyLine = captured.Leading.IndexOf(LegacyActiveModsHeaderLine);
                if (legacyLine >= 0) { captured.Leading[legacyLine] = ActiveModsHeaderLine; }
                RefreshUntouchedBanner(captured);
                string preserved = YamlComments.Reapply(yaml, captured);
                if (captured.HasLeadingBlock) { return preserved; }

                // No comment block at the top, so either this is a fresh file or an older build ate the banner.
                // One comment line of their own is enough for an admin who wants it gone to keep it gone.
                string newline = YamlComments.DetectNewline(yaml);
                return string.Join(newline, ModsFileHeaderLines) + newline + newline + preserved;
            } catch (System.Exception e) {
                Logger.LogWarning($"Could not preserve the comments in {ValConfig.ModsConfigFilePath}: {e.Message}. Writing it without them.");
                return yaml;
            }
        }

        /// <summary>
        /// Replaces the banner at the top of the file with the current one, but only when the one on disk is
        /// still entirely ours.
        ///
        /// Without this the guide would only ever reach brand new files. A captured banner is preserved
        /// verbatim, by design - it is the same machinery that keeps an admin's own notes - so every install
        /// that already has a Mods.yaml would keep whatever guide it was created with, forever, while the
        /// fields it describes moved on underneath it.
        ///
        /// "Still entirely ours" is decided line by line against every banner this mod has ever shipped. If an
        /// admin has written so much as one line of their own up there, or deleted one of ours, the block is
        /// theirs and is left exactly as it is. That keeps the two properties the existing code is careful
        /// about: nobody's text is ever deleted, and one comment line of their own is enough to keep the banner
        /// gone for good.
        ///
        /// It generalises the single line swap above it (LegacyActiveModsHeaderLine), which is kept because it
        /// still fixes that one line on a banner an admin HAS edited - a case this method deliberately skips.
        /// </summary>
        private static void RefreshUntouchedBanner(YamlComments.Captured captured) {
            if (!captured.HasLeadingBlock) { return; } // nothing there; the caller writes a fresh banner

            foreach (string line in captured.Leading) {
                if (line.Trim().Length == 0) { continue; } // blank spacing lines are ours either way
                if (!KnownBannerLines.Contains(line)) { return; }
            }

            // Capture keeps the blank line that separates the banner from the first key, so the block is the
            // banner plus some trailing whitespace. Both halves have to be handled separately: comparing against
            // the whole block would never match and this would rewrite - and log - on every single restart, and
            // replacing the whole block would eat the separator and butt the banner against requiredMods.
            int end = captured.Leading.Count;
            while (end > 0 && captured.Leading[end - 1].Trim().Length == 0) { end--; }

            List<string> banner = captured.Leading.GetRange(0, end);
            if (banner.Count == ModsFileHeaderLines.Length) {
                bool identical = true;
                for (int i = 0; i < ModsFileHeaderLines.Length && identical; i++) {
                    identical = banner[i] == ModsFileHeaderLines[i];
                }
                if (identical) { return; } // already current, so say nothing
            }

            List<string> spacing = captured.Leading.GetRange(end, captured.Leading.Count - end);
            if (spacing.Count == 0) { spacing.Add(""); } // a banner written without one still gets its separator

            captured.Leading.Clear();
            captured.Leading.AddRange(ModsFileHeaderLines);
            captured.Leading.AddRange(spacing);
            Logger.LogInfo($"Updated the guide at the top of {ValConfig.ModsFileName}. Any notes of your own elsewhere in the file are untouched.");
        }

        /// <summary>
        /// Every line that has appeared in a banner this mod generated, current and historical.
        ///
        /// A set rather than a list of whole banners: it has to recognise a file created by any past version,
        /// including one whose banner has since had the legacy line swapped in place, and matching per line
        /// costs nothing and handles the reordering between versions for free. Lines that fall out of the
        /// banner over time must stay in here - that is the entire point, since a file still carrying them is
        /// exactly the file that needs upgrading.
        /// </summary>
        private static readonly HashSet<string> KnownBannerLines = new HashSet<string>(
            ModsFileHeaderLines.Concat(new[] {
                LegacyActiveModsHeaderLine,
                // The 0.26.0 banner, before the per-entry field reference was added to it.
                "# Per entry: enforceVersion: true requires an exact version match (defaults to false).",
                "# File verification uses acceptedHashes / hashSource / thunderstorePackage / hashEnforcement.",
                "# The README covers all of it, including how to pin a mod the server does not run itself.",
                // The adminOnlyMods line as it was worded before it gained its second line. Wrong, as it
                // happens - admins may connect WITHOUT these too - which is its own reason to get the banner on
                // an existing install replaced rather than preserved forever.
                "#   adminOnlyMods   Only admins may connect with these; everyone else is rejected.",
            }));

        /// <summary>
        /// Records the hash computed for a locally loaded plugin onto its authoritative entry, so the mods this
        /// machine runs pin themselves with no manual work.
        ///
        /// Only touches entries with no provenance or a provenance of "Local". A hash an admin pinned by hand,
        /// or one resolved from a Thunderstore package, is authoritative over whatever this machine happens to
        /// have on disk and must survive a restart - otherwise a server whose own copy of a mod had been
        /// tampered with would quietly adopt the tampered hash as the new truth.
        /// </summary>
        private static void RecordLocalHashIfAllowed(Dictionary<string, DataObjects.Mod> modList, string key, string hash, string version) {
            if (!ValConfig.RecordHashesForLoadedMods.Value || string.IsNullOrEmpty(hash)) { return; }
            if (!modList.TryGetValue(key, out DataObjects.Mod entry) || entry == null) { return; }

            if (!string.IsNullOrEmpty(entry.HashSource)
                && !string.Equals(entry.HashSource, HashPolicy.SourceLocal, System.StringComparison.OrdinalIgnoreCase)) {
                return;
            }
            // Already exactly what we would write - stay quiet so a restart does not log a line per mod.
            if (entry.AcceptsHash(hash) && entry.AcceptedHashes.Count == 1) { return; }

            Logger.LogInfo($"Recording local file hash for {key} ({version}).");
            entry.AcceptedHashes = new List<string> { hash };
            entry.HashSource = HashPolicy.SourceLocal;
            entry.HashedFrom = $"local:{version}";
        }

        /// <summary>
        /// Logs every mod that is on both adminOnlyMods and requiredMods. Not an error - adminOnlyMods wins, so the
        /// mod is refused to non-admins and demanded of nobody - but the requiredMods entry is dead and the file
        /// reads as though the mod were required, so the admin is told which one counts.
        /// </summary>
        private static void NoteModsOnAdminOnlyAndRequired() {
            if (ModSettings?.AdminOnlyMods == null || ModSettings.RequiredMods == null) { return; }
            List<string> both = ModSettings.AdminOnlyMods.Keys.Where(key => ModSettings.RequiredMods.ContainsKey(key)).ToList();
            if (both.Count == 0) { return; }
            Logger.LogInfo($"On both adminOnlyMods and requiredMods, treated as admin-only (optional for admins, refused to everyone else): {string.Join(", ", both)}. The requiredMods entries do nothing and can be deleted.");
        }

        // Fetched once rather than indexed three times, and tolerant of an entry that is not there. Mods.Normalized
        // already guarantees no null values reach here, but this method and RecordLocalHashIfAllowed are the two
        // that WRITE to an entry, and neither should depend on an invariant established in another file to avoid
        // dereferencing null.
        private static void UpdateModVersionIfChanged(Dictionary<string, DataObjects.Mod> modList, string key, string currentVersion) {
            if (!modList.TryGetValue(key, out DataObjects.Mod entry) || entry == null) { return; }
            if (entry.Version == currentVersion) { return; }

            Logger.LogInfo($"Updating version for {key}: {entry.Version} -> {currentVersion}");
            entry.Version = currentVersion;
        }

        /// <summary>
        /// Drops every requiredMods entry for a plugin this machine did not load, so a mod taken off the server
        /// stops being demanded of clients. Required only: the other lists routinely hold mods the server never
        /// runs, which is what they are for.
        /// </summary>
        private static void RemoveUnloadedRequiredMods() {
            // Enforcer is itself a loaded plugin, so an empty list means the plugin scan failed - not that every
            // mod was uninstalled - and emptying requiredMods on the strength of it would wipe the admin's list.
            if (ActiveMods.Count == 0) { return; }

            int kept = 0;
            foreach (string key in ModSettings.RequiredMods.Keys.Where(guid => !ActiveMods.ContainsKey(guid)).ToList()) {
                // The opt-out for the case this setting's own description warns about: a mod required of clients
                // that the server does not run itself, typically pinned with a thunderstorePackage. Without it
                // the only way to keep such an entry was to turn the cleanup off for every mod.
                if (ModSettings.RequiredMods[key].KeepWhenUnloaded) { kept++; continue; }

                Logger.LogInfo($"Removing {key} from requiredMods: it is not loaded on this server (RemoveUnloadedModsFromRequired).");
                ModSettings.RequiredMods.Remove(key);
            }
            if (kept > 0) {
                Logger.LogInfo($"Kept {kept} unloaded requiredMods entry/entries that are marked keepWhenUnloaded.");
            }
        }

        /// <summary>
        /// Rebuilds <see cref="DataObjects.Mods.ActiveMods"/> from what BepInEx actually loaded into this
        /// process. Always derived, never read from disk - see <see cref="UpdateModSettingConfigs"/> for why
        /// that distinction is load bearing.
        /// </summary>
        private static void RebuildActiveMods() {
            if (ModSettings == null) { ModSettings = new DataObjects.Mods(); }
            if (ModSettings.ActiveMods == null) { ModSettings.ActiveMods = new Dictionary<string, DataObjects.Mod>(); }
            ModSettings.ActiveMods.Clear();

            foreach (KeyValuePair<string, BaseUnityPlugin> plugin in ActiveMods) {
                DataObjects.Mod entry = new DataObjects.Mod() {
                    EnforceVersion = true,
                    Version = plugin.Value.Info.Metadata.Version.ToString(),
                    PluginID = plugin.Value.Info.Metadata.GUID,
                    Name = plugin.Value.Info.Metadata.Name,
                };
                // Derived from the file on disk, never from the config file, for the same reason the entry
                // itself is.
                PluginHasher.Apply(plugin.Key, entry);
                ModSettings.ActiveMods[plugin.Key] = entry;
            }
        }

        /// <summary>
        /// Writes ServerActiveMods.yaml: every loaded plugin as a bare Mods.yaml entry, sorted, for an admin to
        /// copy into whichever list it belongs in.
        ///
        /// Only the file moved. <see cref="DataObjects.Mods.ActiveMods"/> is still what the handshake reports,
        /// and it is still derived from the loaded plugins rather than read from here. The entries carry no hash
        /// and no enforceVersion: those describe policy rather than what is installed, and a mod pasted into a
        /// list while the server runs it gets its hash recorded on the next start anyway.
        /// </summary>
        private static void WriteActiveModsFile() {
            Dictionary<string, DataObjects.Mod> entries = new Dictionary<string, DataObjects.Mod>();
            foreach (KeyValuePair<string, BaseUnityPlugin> plugin in ActiveMods.OrderBy(loaded => loaded.Key, System.StringComparer.OrdinalIgnoreCase)) {
                entries[plugin.Key] = new DataObjects.Mod() {
                    PluginID = plugin.Value.Info.Metadata.GUID,
                    Version = plugin.Value.Info.Metadata.Version.ToString(),
                    Name = plugin.Value.Info.Metadata.Name,
                };
            }

            try {
                // Under a top-level key, so each entry comes out indented exactly as it has to be in Mods.yaml.
                string yaml = DataObjects.yamlserializer.Serialize(new Dictionary<string, Dictionary<string, DataObjects.Mod>> { { "activeMods", entries } });
                string newline = YamlComments.DetectNewline(yaml);
                File.WriteAllText(ValConfig.ServerActiveModsFilePath, string.Join(newline, ActiveModsFileHeaderLines) + newline + newline + yaml);
            } catch (System.Exception e) {
                Logger.LogWarning($"Could not write {ValConfig.ServerActiveModsFilePath}: {e.Message}");
            }
        }

        /// <summary>
        /// Clears ServerActiveMods.yaml at startup, before any plugin has been looked at, so a start that never
        /// reaches <see cref="WriteActiveModsFile"/> does not leave the previous run's mod set on disk looking current.
        /// </summary>
        internal static void DeleteActiveModsFile() {
            try {
                if (File.Exists(ValConfig.ServerActiveModsFilePath)) { File.Delete(ValConfig.ServerActiveModsFilePath); }
            } catch (System.Exception e) {
                Logger.LogWarning($"Could not delete {ValConfig.ServerActiveModsFilePath}: {e.Message}");
            }
        }

        /// <summary>Patcher count the last "Detected N patcher(s)" line reported. -1 until the first rebuild.</summary>
        private static int lastAnnouncedPatcherCount = -1;

        /// <summary>
        /// Rebuilds <see cref="DataObjects.Mods.ActivePatchers"/> from the patcher directory, and on the server
        /// adopts anything new into <see cref="DataObjects.Mods.AllowedPatchers"/>.
        ///
        /// Derived, never read back from the file, for exactly the reason ActiveMods is: it is the list this
        /// peer *reports* about itself, and a list taken from a text file is a list a player can type anything
        /// into.
        ///
        /// The adopt step is what makes this feature need no manual work on a normal server. A patcher already
        /// installed when the feature arrives is allowlisted on the next start, and only a patcher that appears
        /// on a client without being on the server is ever refused.
        ///
        /// Also runs on every Mods.yaml re-read, which is why the "detected" line is only announced once per
        /// change: a config poll that rewrites the same list must not put a line in the log every time.
        /// </summary>
        private static void RebuildActivePatchers() {
            if (ModSettings == null) { ModSettings = new DataObjects.Mods(); }
            ModSettings.ActivePatchers = PatcherIndex.Snapshot();
            if (ModSettings.AllowedPatchers == null) { ModSettings.AllowedPatchers = new Dictionary<string, DataObjects.PatcherEntry>(); }

            if (ModSettings.ActivePatchers.Count > 0 && ModSettings.ActivePatchers.Count != lastAnnouncedPatcherCount) {
                Logger.LogInfo($"Detected {ModSettings.ActivePatchers.Count} BepInEx patcher(s).");
            }
            lastAnnouncedPatcherCount = ModSettings.ActivePatchers.Count;

            // Server-side adoption only. A client doing this would be allowlisting its own patchers, which is
            // not a thing a client gets to do - and on a client the list is never consulted anyway.
            if (ValConfig.AutoAddPatchersToAllowed == null || !ValConfig.AutoAddPatchersToAllowed.Value) { return; }
            if (ZNet.instance != null && !ZNet.instance.IsServer()) { return; }

            foreach (KeyValuePair<string, DataObjects.PatcherEntry> patcher in ModSettings.ActivePatchers) {
                // A null hit counts as absent: an allowedPatchers entry written with nothing under it comes back
                // as a present key with a null value, which TryGetValue reports as found and the hash recording
                // below would then dereference.
                if (!ModSettings.AllowedPatchers.TryGetValue(patcher.Key, out DataObjects.PatcherEntry allowed) || allowed == null) {
                    allowed = new DataObjects.PatcherEntry { Name = patcher.Value.Name };
                    ModSettings.AllowedPatchers[patcher.Key] = allowed;
                    Logger.LogDebug($"Automatically allowing the patcher {patcher.Key}.");
                }
                // Record the local hash the same way RecordLocalHashIfAllowed does for plugins: only ever add,
                // so an admin who pinned a hash by hand keeps it and a second build does not silently replace
                // the first.
                if (patcher.Value.Hash == null) { continue; }
                if (allowed.AcceptedHashes == null) { allowed.AcceptedHashes = new List<string>(); }
                if (!allowed.AcceptsHash(patcher.Value.Hash)) { allowed.AcceptedHashes.Add(patcher.Value.Hash); }
            }
        }

        /// <summary>
        /// Compares a client's patchers against the server's allowlist.
        ///
        /// Allowlist, not required-list: a client carrying no patchers passes unconditionally, and only what it
        /// actually has is checked. The reverse rule - "match the server" - would reject essentially every
        /// client, since a server may run patchers that no player has any reason to install.
        ///
        /// Hash comparison is unconditional rather than routed through HashPolicy. A patcher has no version
        /// string to fall back on, so its file is the only thing that identifies it; "allowed by name, any
        /// contents" would let a hostile DLL inherit an allowlisted name and defeat the whole check.
        /// </summary>
        private static void ValidatePatchers(Mods checking, Mods authoritative,
                                             List<string> extra, List<string> hashMismatch, List<string> unverifiable) {
            if (checking?.ActivePatchers == null || checking.ActivePatchers.Count == 0) { return; }
            Dictionary<string, DataObjects.PatcherEntry> allowed = authoritative?.AllowedPatchers;

            foreach (KeyValuePair<string, DataObjects.PatcherEntry> reported in checking.ActivePatchers) {
                DataObjects.PatcherEntry record = null;
                if (allowed != null) { allowed.TryGetValue(reported.Key, out record); }

                if (record == null) {
                    extra.Add(reported.Key);
                    continue;
                }
                if (!record.HasRecordedHash()) { continue; } // allowed by name; the server pinned nothing to compare

                if (string.IsNullOrEmpty(reported.Value?.Hash)) {
                    unverifiable.Add($"{reported.Key} ({reported.Value?.HashStatus ?? "no hash reported"})");
                } else if (!record.AcceptsHash(reported.Value.Hash)) {
                    hashMismatch.Add(reported.Key);
                }
            }
        }

        /// <summary>
        /// Applies an edited Mods.yaml to the in-memory settings.
        ///
        /// Only the policy lists are taken from the file. ActiveMods is deliberately NOT adopted and is
        /// re-derived from the loaded plugins instead: ActiveMods is the list this peer *reports* about itself
        /// during the handshake, and this method runs from the config file watcher, whose admin gate
        /// (SynchronizationManager.PlayerIsAdmin) defaults to true before login. Adopting it from file text
        /// would let any player hand-edit their own Mods.yaml, wait one poll interval, and connect claiming to
        /// be running whatever set of mods - and, once file verification exists, whatever hashes - they liked.
        ///
        /// ActivePatchers is re-derived for the same reason, and that also puts the server's own patchers back
        /// into AllowedPatchers: without it, a re-read left the allowlist as whatever the file happened to say,
        /// so a patcher this server actually runs stopped being allowed the moment an admin touched Mods.yaml.
        /// </summary>
        internal static void UpdateModSettingConfigs(string yamlstring) {
            try {
                // Strict here too, and for the same reason as the startup read: this is an admin's hand edit
                // arriving, and a duplicate key they have just introduced is worth naming while they still have
                // the file open. A failure keeps the settings we already had, so the cost of being strict is one
                // poll's delay rather than anything lost.
                DataObjects.Mods fromFile = DataObjects.yamlconfigdeserializer.Deserialize<DataObjects.Mods>(yamlstring);
                if (fromFile == null) {
                    // An empty read is not an empty mod list. Adopting it would hand the next rewrite a blank
                    // object to publish over a file that is most likely mid-save rather than genuinely empty.
                    Logger.LogWarning($"{ValConfig.ModsFileName} read back empty, keeping the current settings.");
                    return;
                }
                ModSettings = DataObjects.Mods.Normalized(fromFile, ValConfig.ModsFileName);
                NoteModsOnAdminOnlyAndRequired();
                RebuildActiveMods();
                RebuildActivePatchers();
                Logger.LogInfo($"Re-read {ValConfig.ModsFileName}: now enforcing {DescribeListCounts(ModSettings)}.");
            } catch (System.Exception e) {
                Logger.LogWarning($"Could not read the edited {ValConfig.ModsFileName} ({ConfigFileBackup.DescribeParseFailure(e)}). Keeping the settings already loaded; the file is re-read on every change, so fixing it is enough.");
            }
        }

        /// <summary>
        /// Everything the validator worked out, kept apart instead of flattened into the summary string.
        ///
        /// The summary is written for a player staring at a connection-failed panel, so it reads as prose. A
        /// Discord template wants to address the categories individually - ping the mod team only for a hash
        /// mismatch, list the missing mods and nothing else - which needs the lists before they are joined up.
        /// </summary>
        internal class ModMismatchDetail {
            internal List<string> MissingMods = new List<string>();
            internal List<string> ExtraMods = new List<string>();
            internal List<string> VersionMismatches = new List<string>();
            internal List<string> AdminOnlyMods = new List<string>();
            internal List<string> HashMismatches = new List<string>();
            internal List<string> UnverifiedMods = new List<string>();
            internal List<string> Patchers = new List<string>();

            /// <summary>Comma-separated, or an empty string so the field carrying it drops out of the message.</summary>
            internal static string Join(List<string> entries) {
                return entries == null || entries.Count == 0 ? "" : string.Join(", ", entries);
            }
        }

        /// <summary>
        /// True when <paramref name="key"/> is listed in <paramref name="list"/>, with <paramref name="entry"/>
        /// set to the record to check against - never null when it returns true.
        ///
        /// An entry written with no settings under it ("com.example.Mod:" and nothing indented below) is a
        /// listing with no options set, not an absent listing, so it must not fall through to the next list or
        /// be re-reported as an unlisted mod. Keeping the membership test and the null handling in one place is
        /// what lets the caller's if/else-if chain read as a plain priority order while remaining safe: the
        /// chain's ordering is load-bearing, because letting an admin-only entry fall through to requiredMods
        /// would quietly demote the mod from "refused to non-admins" to "required of everyone".
        /// </summary>
        private static bool TryAuthoritative(Dictionary<string, DataObjects.Mod> list, string key, out DataObjects.Mod entry) {
            entry = null;
            if (list == null || !list.TryGetValue(key, out entry)) { return false; }
            if (entry == null) { entry = new DataObjects.Mod(); }
            return true;
        }

        internal static bool ValidateModlist(Mods CheckingMods, Mods AuthoratativeMods, bool isAdmin, bool adminStatusKnown, out string summay, out string details, out ModMismatchDetail detail) {
            summay = "";
            details = "";
            detail = new ModMismatchDetail();
            List<string> extraMods = new List<string>();
            List<string> versionMismatch = new List<string>();
            List<string> adminOnlyNotAllowed = new List<string>();
            List<string> adminOnlyInfo = new List<string>();   // client-side: admin status not yet synced, surfaced as a neutral note
            List<string> hashMismatch = new List<string>();     // the file does not match anything the server accepts
            List<string> hashUnverifiable = new List<string>(); // the server has a record but no usable hash was reported
            List<string> hashNotRecorded = new List<string>();  // Strict only: enforced mod the server never pinned
            List<string> extraPatchers = new List<string>();       // a BepInEx patcher the server does not allow
            List<string> patcherHashMismatch = new List<string>(); // allowed by name, but not this build of it
            List<string> patcherUnverifiable = new List<string>(); // the server pinned it, the client reported no hash
            // Both sides normalized before anything reads them. Their producers already do this, so it is belt
            // and braces - but this method is reached from the handshake on every connection, in both
            // directions, and a null list here is a throw inside an RPC handler that ZRpc swallows, which would
            // skip mod validation altogether rather than fail it. Also lets every list below be read plainly:
            // AdminOnlyMods used to carry a lone "?? new Dictionary<>" that made the other three look guaranteed
            // when they were not.
            CheckingMods = DataObjects.Mods.Normalized(CheckingMods);
            AuthoratativeMods = DataObjects.Mods.Normalized(AuthoratativeMods);
            Dictionary<string, DataObjects.Mod> adminOnlyMods = AuthoratativeMods.AdminOnlyMods;
            // Admin-only mods are optional for admins: permitted, never demanded - of admins or anyone else. A mod
            // on both lists is the normal way to get here rather than a typo: the server loads a mod, it lands in
            // requiredMods by itself, and the admin then adds it to adminOnlyMods without deleting the original.
            //
            // Worked out in one pass over the required list rather than by building it whole and striking a
            // name off it for every mod the peer declared. List.Remove is a scan, so that was declared-mods
            // times required-mods - on the server, before the password check, on a list whose length the
            // connecting peer chooses.
            List<string> requiredModsMissing = new List<string>();
            foreach (string required in AuthoratativeMods.RequiredMods.Keys) {
                if (adminOnlyMods.ContainsKey(required)) { continue; }
                if (CheckingMods.ActiveMods.ContainsKey(required)) { continue; }
                requiredModsMissing.Add(required);
            }
            // At least one version mismatch was found by the file check rather than by enforceVersion, so the
            // list gets the extra line saying why a version the server never marked as enforced still matters.
            bool versionPinnedByHash = false;

            if (Logger.DebugEnabled) { Logger.LogDebug($"Validating modlist of {CheckingMods.ActiveMods.Count} mods isAdmin? {isAdmin}"); }

            foreach (KeyValuePair<string, DataObjects.Mod> mod in CheckingMods.ActiveMods) {
                // The authoritative record this client mod matched, and which list it came from. Captured
                // rather than continue'd out of, because file verification runs on top of whatever the
                // version/admin check decided and needs the same record.
                //
                // The else-if chain also establishes an explicit AdminOnly > Required > Optional priority.
                // Previously these were independent ifs and the version-mismatch branches did not continue, so
                // a required (or optional) mod with the wrong version was reported as BOTH a version mismatch
                // and a non-allowed mod. AdminOnly is first so a mod on both lists stays refused to non-admins;
                // with Required first, putting a mod in adminOnlyMods did nothing until it was also deleted
                // from requiredMods.
                DataObjects.Mod authoritative = null;
                bool requiredOrAdmin = false;
                // Whether this client's copy of this mod is actually version-checked. Not simply
                // EnforceVersion: a non-admin carrying an admin-only mod is rejected for that and never gets
                // as far as its version, and neither does a client whose admin status has not synced yet.
                bool versionEnforced = false;

                // Compare admin mods - prevent non-admin clients from joining with admin only mods.
                // Non-admins carrying one are rejected; admins are version-enforced when EnforceVersion is set.
                if (TryAuthoritative(adminOnlyMods, mod.Key, out authoritative)) {
                    requiredOrAdmin = true;
                    if (!adminStatusKnown) {
                        // Client side: Jotunn only syncs admin status after login (post-RPC_PeerInfo),
                        // and PlayerIsAdmin defaults to true, so we cannot trust it here. Surface a
                        // neutral note instead of guessing.
                        adminOnlyInfo.Add(mod.Key);
                    } else if (isAdmin) {
                        versionEnforced = authoritative.EnforceVersion;
                    } else {
                        adminOnlyNotAllowed.Add(mod.Key);
                    }
                }
                // Compare required mods
                else if (TryAuthoritative(AuthoratativeMods.RequiredMods, mod.Key, out authoritative)) {
                    requiredOrAdmin = true;
                    versionEnforced = authoritative.EnforceVersion;
                }
                // Compare optional mods
                else if (TryAuthoritative(AuthoratativeMods.OptionalMods, mod.Key, out authoritative)) {
                    versionEnforced = authoritative.EnforceVersion;
                }
                // ServerOnlyMods stays the skip button: a client carrying one is still an extra mod, exactly as
                // before, so it is deliberately not matched here.

                if (authoritative == null) {
                    // No list claimed it, so it is an extra. This is purely the "unlisted" verdict now - it used
                    // to double as a null check, and sat AFTER three reads of authoritative.EnforceVersion, so an
                    // entry written with no body under it both threw here on every single connecting client and,
                    // in the admin-only case, got the mod named twice in the rejection text: once as admin-only
                    // and again as unlisted. TryAuthoritative settles both.
                    extraMods.Add(mod.Key);
                    continue;
                }

                bool versionDiffers = authoritative.Version != mod.Value.Version;

                if (versionEnforced && versionDiffers) {
                    versionMismatch.Add(DescribeVersions(mod.Key, authoritative.Version, mod.Value.Version));
                    // A different build of a mod is a different file, so its hash will not match either. The
                    // version difference already rejects this client and it is the one thing they can act on,
                    // so the file check is skipped rather than telling them, on the same screen, to reinstall
                    // the version they are being rejected for having.
                    continue;
                }

                switch (HashPolicy.Evaluate(authoritative, mod.Value, requiredOrAdmin)) {
                    case HashVerdict.Mismatch:
                        // Same reasoning as above for the case where enforceVersion is off: the versions
                        // differ, which is why the files do, so the player is told to match the version
                        // rather than accused of running a modified DLL. The rejection itself stands - a
                        // client does not get to talk its way out of a hash check by declaring a version it
                        // is not running - because a recorded hash pins the version whether or not
                        // enforceVersion says so, which is what the note under the list explains.
                        if (versionDiffers && !string.IsNullOrEmpty(authoritative.Version)) {
                            versionMismatch.Add(DescribeVersions(mod.Key, authoritative.Version, mod.Value.Version));
                            versionPinnedByHash = true;
                        } else {
                            hashMismatch.Add(mod.Key);
                        }
                        break;
                    case HashVerdict.Unverifiable:
                        hashUnverifiable.Add($"{mod.Key} ({mod.Value.HashStatus ?? "no hash reported"})");
                        break;
                    case HashVerdict.NotRecorded:
                        hashNotRecorded.Add(mod.Key);
                        break;
                    case HashVerdict.Pass:
                    default:
                        break;
                }
            }


            // Patchers are checked separately from mods and against their own list. They are not plugins:
            // BepInEx loads them from BepInEx/patchers before any plugin exists and lets them rewrite the
            // game's assemblies on the way in, so they need covering - but they carry no GUID or version to
            // hold them to, and an allowlist keyed on the file is the only policy that applies.
            if (ValConfig.ValidatePatchers != null && ValConfig.ValidatePatchers.Value) {
                ValidatePatchers(CheckingMods, AuthoratativeMods, extraPatchers, patcherHashMismatch, patcherUnverifiable);
            } else if (CheckingMods?.ActivePatchers != null && CheckingMods.ActivePatchers.Count > 0) {
                // Reported even when enforcement is off, so an admin can see what their players actually carry
                // and build the list before switching ValidatePatchers on.
                Logger.LogInfo($"Patcher validation is off; the peer being checked carries {CheckingMods.ActivePatchers.Count} patcher(s): {string.Join(", ", CheckingMods.ActivePatchers.Keys.ToArray())}");
            }

            // The same lists the summary is built from, kept addressable for the Discord template. hashUnverifiable
            // and hashNotRecorded are merged: both mean "the server could not confirm this file", and the split
            // between them is a detail of HashEnforcement rather than something a channel message acts on.
            detail.MissingMods = requiredModsMissing;
            detail.ExtraMods = extraMods;
            detail.VersionMismatches = versionMismatch;
            detail.AdminOnlyMods = adminOnlyNotAllowed;
            detail.HashMismatches = hashMismatch;
            detail.UnverifiedMods = new List<string>(hashUnverifiable);
            detail.UnverifiedMods.AddRange(hashNotRecorded);
            // One field for every patcher problem. The split between "not allowed" and "wrong build" matters in
            // the disconnect text, where the player is told what to do about it, and not in a channel message
            // whose job is to say that a patcher was involved at all.
            detail.Patchers = new List<string>(extraPatchers);
            detail.Patchers.AddRange(patcherHashMismatch);
            detail.Patchers.AddRange(patcherUnverifiable);

            if (versionMismatch.Count > 0) {
                string wrongVersions = $"\nMod versions that do not match the server: {string.Join(", ", versionMismatch)}";
                summay += wrongVersions;
                Logger.LogWarning(wrongVersions);
            }
            if (requiredModsMissing.Count > 0) {
                string requiredMissing = $"\nMissing required mods: {string.Join(", ", requiredModsMissing)}";
                summay += requiredMissing;
                Logger.LogWarning(requiredMissing);
            }
            if (extraMods.Count > 0) {
                string unallowedMods = $"\nNon-allowed mods found: {string.Join(", ", extraMods)}";
                summay += unallowedMods;
                Logger.LogWarning(unallowedMods);
            }
            if (adminOnlyNotAllowed.Count > 0) {
                string adminMods = $"\nAdmin-only mods not permitted for non-admins: {string.Join(", ", adminOnlyNotAllowed)}";
                summay += adminMods;
                Logger.LogWarning(adminMods);
            }
            if (adminOnlyInfo.Count > 0) {
                string adminInfo = $"\nThis server restricts some mods to admins; if you are not an admin you will be disconnected: {string.Join(", ", adminOnlyInfo)}";
                summay += adminInfo;
                Logger.LogInfo(adminInfo);
            }
            if (hashMismatch.Count > 0) {
                string modified = $"\nModified mod files detected: {string.Join(", ", hashMismatch)}";
                summay += modified;
                Logger.LogWarning(modified);
            }
            if (hashUnverifiable.Count > 0) {
                string unverified = $"\nMod files that could not be verified: {string.Join(", ", hashUnverifiable)}";
                summay += unverified;
                Logger.LogWarning(unverified);
            }
            if (hashNotRecorded.Count > 0) {
                string unpinned = $"\nThe server has no recorded file hash for: {string.Join(", ", hashNotRecorded)}";
                summay += unpinned;
                Logger.LogWarning(unpinned);
            }
            if (extraPatchers.Count > 0) {
                string badPatchers = $"\nBepInEx patchers not allowed by this server: {string.Join(", ", extraPatchers)}";
                summay += badPatchers;
                Logger.LogWarning(badPatchers);
            }
            if (patcherHashMismatch.Count > 0) {
                string modifiedPatchers = $"\nModified BepInEx patcher files detected: {string.Join(", ", patcherHashMismatch)}";
                summay += modifiedPatchers;
                Logger.LogWarning(modifiedPatchers);
            }
            if (patcherUnverifiable.Count > 0) {
                string unverifiedPatchers = $"\nBepInEx patchers that could not be verified: {string.Join(", ", patcherUnverifiable)}";
                summay += unverifiedPatchers;
                Logger.LogWarning(unverifiedPatchers);
            }
            if (versionMismatch.Count > 0 || requiredModsMissing.Count > 0 || extraMods.Count > 0 || adminOnlyNotAllowed.Count > 0 || adminOnlyInfo.Count > 0
                || hashMismatch.Count > 0 || hashUnverifiable.Count > 0 || hashNotRecorded.Count > 0
                || extraPatchers.Count > 0 || patcherHashMismatch.Count > 0 || patcherUnverifiable.Count > 0) {
                // Build detailed error message for display in Jotunn's CompatibilityWindow
                StringBuilder errorBuilder = new StringBuilder();
                errorBuilder.AppendLine("\n<b>ValheimEnforcer - Mod Validation Failed</b>");

                if (versionMismatch.Count > 0) {
                    errorBuilder.AppendLine("\n<b>Version Mismatches:</b>");
                    AppendBullets(errorBuilder, versionMismatch);
                    errorBuilder.AppendLine("  Install the version listed for each of these - not a newer one.");
                    if (versionPinnedByHash) {
                        errorBuilder.AppendLine("  This server verifies mod files, so only the exact build it has on record is accepted.");
                    }
                }

                if (requiredModsMissing.Count > 0) {
                    errorBuilder.AppendLine("\n<b>Missing Required Mods:</b>");
                    foreach (var modKey in requiredModsMissing) {
                        errorBuilder.AppendLine($"  • {modKey}");
                    }
                }

                if (extraMods.Count > 0) {
                    errorBuilder.AppendLine("\n<b>Non-Allowed Mods:</b>");
                    foreach (var modKey in extraMods) {
                        errorBuilder.AppendLine($"  • {modKey}");
                    }
                }

                if (adminOnlyNotAllowed.Count > 0) {
                    errorBuilder.AppendLine("\n<b>Admin-Only Mods (not permitted):</b>");
                    foreach (var modKey in adminOnlyNotAllowed) {
                        errorBuilder.AppendLine($"  • {modKey}");
                    }
                }

                if (adminOnlyInfo.Count > 0) {
                    errorBuilder.AppendLine("\n<b>Admin-Only Mods (require admin):</b>");
                    foreach (var modKey in adminOnlyInfo) {
                        errorBuilder.AppendLine($"  • {modKey}");
                    }
                }

                if (hashMismatch.Count > 0) {
                    errorBuilder.AppendLine("\n<b>Modified Mod Files:</b>");
                    AppendBullets(errorBuilder, hashMismatch);
                    errorBuilder.AppendLine("  Reinstall these from their original download - a recompiled or edited DLL will not match.");
                }

                if (hashUnverifiable.Count > 0) {
                    errorBuilder.AppendLine("\n<b>Unverifiable Mod Files:</b>");
                    AppendBullets(errorBuilder, hashUnverifiable);
                    errorBuilder.AppendLine("  These could not be checked against a file on disk. Plugins loaded from memory cannot be verified.");
                }

                if (hashNotRecorded.Count > 0) {
                    errorBuilder.AppendLine("\n<b>Mods The Server Has Not Pinned (server misconfiguration):</b>");
                    AppendBullets(errorBuilder, hashNotRecorded);
                    errorBuilder.AppendLine("  Ask the server admin to record a hash for these, or to lower HashEnforcement.");
                }

                if (extraPatchers.Count > 0) {
                    errorBuilder.AppendLine("\n<b>Non-Allowed BepInEx Patchers:</b>");
                    AppendBullets(errorBuilder, extraPatchers);
                    errorBuilder.AppendLine("  These are files in your BepInEx/patchers folder, not plugins. Remove them, or ask the admin to allow them.");
                }

                if (patcherHashMismatch.Count > 0) {
                    errorBuilder.AppendLine("\n<b>Modified BepInEx Patcher Files:</b>");
                    AppendBullets(errorBuilder, patcherHashMismatch);
                    errorBuilder.AppendLine("  Reinstall these from their original download - a recompiled or edited DLL will not match.");
                }

                if (patcherUnverifiable.Count > 0) {
                    errorBuilder.AppendLine("\n<b>Unverifiable BepInEx Patcher Files:</b>");
                    AppendBullets(errorBuilder, patcherUnverifiable);
                }

                string fullError = errorBuilder.ToString();
                details = fullError;
                //Logger.LogWarning(LastValidationError);
                return false;
            }
            Logger.LogInfo("Client mod list validated successfully.");
            return true;
        }

        /// <summary>
        /// One version-mismatch entry: the mod plus both versions, so the player is told which build to install
        /// rather than only which mod is unhappy. Degrades to the bare key when the server's record carries no
        /// version - a hand-written entry can leave it out - because "needs (blank)" helps nobody.
        /// </summary>
        private static string DescribeVersions(string key, string expected, string reported) {
            if (string.IsNullOrEmpty(expected)) { return key; }
            return $"{key} (needs {expected}, has {(string.IsNullOrEmpty(reported) ? "unknown" : reported)})";
        }

        /// <summary>
        /// Appends a bulleted list, truncated so a strict server facing a client with a 200 mod pack produces a
        /// readable message rather than a wall the player has to scroll past.
        /// </summary>
        private static void AppendBullets(StringBuilder builder, List<string> entries, int limit = 25) {
            for (int i = 0; i < entries.Count && i < limit; i++) {
                builder.AppendLine($"  • {entries[i]}");
            }
            if (entries.Count > limit) {
                builder.AppendLine($"  ... and {entries.Count - limit} more");
            }
        }

        /// <summary>
        /// Loads the policy lists from Mods.yaml text. Never throws: a malformed file used to propagate out of
        /// SetModsActive, which left ModSettings null and NREd the handshake send in
        /// <see cref="ZNet_RPC_ClientHandshake_Patch"/>. An unusable file degrades to empty settings, which the
        /// startup rewrite then repopulates from the loaded plugins.
        /// </summary>
        internal static void LoadConfig(string yaml) {
            // Two readers, two different questions. The strict one decides whether we understood the file well
            // enough to be allowed to rewrite it; the lenient one decides what to enforce for this session.
            // Strict-only would be a regression: a file with a duplicate key loads today under last-wins rules,
            // and refusing it outright would drop a working server to no mod policy at all over a formatting
            // fault. Lenient-only is what let a broken file be quietly replaced by an empty one.
            try {
                ModSettings = DataObjects.Mods.Normalized(
                    DataObjects.yamlconfigdeserializer.Deserialize<DataObjects.Mods>(yaml), ValConfig.ModsFileName);
                return;
            } catch (System.Exception strict) {
                RecoverUnreadableModsFile(yaml, strict);
            }
        }

        /// <summary>
        /// Salvages what can be salvaged from a Mods.yaml the strict reader rejected: the previous file is copied
        /// aside, a second pass tries to read it under the old permissive rules, and the admin is told in one
        /// message what broke, where their data went and how to get it back.
        ///
        /// The file IS still rewritten afterwards - the server heals itself rather than running on a file nobody
        /// can parse - which is only defensible because the copy is taken first. Before it was, the rewrite
        /// published the empty fallback over the admin's lists and the old log line told them to "fix the file
        /// and restart" after the code had already made that impossible.
        /// </summary>
        private static void RecoverUnreadableModsFile(string yaml, System.Exception strict) {
            string backup = ConfigFileBackup.BackupUnreadable(ValConfig.ModsConfigFilePath);

            try {
                ModSettings = DataObjects.Mods.Normalized(
                    DataObjects.yamldeserializer.Deserialize<DataObjects.Mods>(yaml), ValConfig.ModsFileName);
            } catch (System.Exception lenient) {
                // Both readers refused it, so this is a real syntax error rather than a duplicate key.
                Logger.LogDebug($"The permissive re-read of {ValConfig.ModsFileName} failed too: {ConfigFileBackup.DescribeParseFailure(lenient)}");
                ModSettings = new DataObjects.Mods();
            }

            Logger.LogError(
                $"{ValConfig.ModsFileName} could not be read ({ConfigFileBackup.DescribeParseFailure(strict)})."
                + (backup == null ? "" : $" The file as you left it has been saved as {backup}; copy your optionalMods / adminOnlyMods / serverOnlyMods entries back out of it.")
                + $" Mod enforcement this session is running on {DescribeListCounts(ModSettings)}."
                + " A common cause is entries added underneath a list written as 'optionalMods: {}' - the '{}' has to be deleted first."
                + $" Fix the file and save it: it is re-read within ConfigPollIntervalSeconds, with no restart needed.");
        }

        /// <summary>The per-list counts, for telling an admin what survived a bad file without making them guess.</summary>
        private static string DescribeListCounts(DataObjects.Mods mods) {
            return $"{mods.RequiredMods.Count} required, {mods.OptionalMods.Count} optional, "
                 + $"{mods.AdminOnlyMods.Count} admin-only, {mods.ServerOnlyMods.Count} server-only";
        }

        internal static string GetDefaultConfig() {
            return SerializeModsFile(ModSettings ?? new DataObjects.Mods());
        }

        /// <summary>
        /// Mods.yaml as written: the settings without activeMods, which goes to ServerActiveMods.yaml instead.
        /// A copy rather than clearing the list on the live object, because the handshake still sends it. Nonce
        /// and Attestation stay behind too - they belong to one connection, never to the file.
        /// </summary>
        private static string SerializeModsFile(DataObjects.Mods mods) {
            DataObjects.Mods written = new DataObjects.Mods {
                ActiveMods = null, // null rather than empty, so OmitDefaults leaves the key out entirely
                RequiredMods = mods.RequiredMods,
                OptionalMods = mods.OptionalMods,
                AdminOnlyMods = mods.AdminOnlyMods,
                ServerOnlyMods = mods.ServerOnlyMods,
                ActivePatchers = mods.ActivePatchers,
                AllowedPatchers = mods.AllowedPatchers,
            };
            return OpenEmptyLists(DataObjects.yamlserializer.Serialize(written));
        }

        /// <summary>
        /// Rewrites "optionalMods: {}" and its siblings to a bare "optionalMods:".
        ///
        /// This is the single edit that stops admins destroying this file. YAML has no block form for an empty
        /// mapping, so the emitter's only options are "{}" or leaving the key out - and "{}" is a trap, because
        /// the three lists nobody fills in sit at the very bottom of a six hundred line file, which is exactly
        /// where somebody scrolls to when they want to add one. Indenting an entry underneath "optionalMods: {}"
        /// is not valid YAML, the whole file then failed to parse, and the recovery path published an empty one
        /// over the top of it. A bare key takes an indented entry underneath perfectly happily, and it is what
        /// an admin writes by hand anyway, so the generated file and a hand-written one stop disagreeing.
        ///
        /// Textual, on output we generated ourselves one statement earlier, matching whole lines against a fixed
        /// set of key names - and this file already accepts a far larger textual pass over the same document in
        /// WithPreservedComments, for the same reason: it is machine generated, block style and uniformly
        /// indented. The round trip is stable: bare key reads back as null, Mods.Normalized turns that into an
        /// empty dictionary, and it serializes as a bare key again.
        ///
        /// Depends on Mods.Normalized. Without it a bare key deserializes to null and NREs through startup,
        /// which would turn a trap that costs the admin their lists into one that costs them the server.
        /// </summary>
        private static string OpenEmptyLists(string yaml) {
            if (string.IsNullOrEmpty(yaml)) { return yaml; }
            return EmptyListLine.Replace(yaml, "${key}:");
        }

        /// <summary>
        /// Anchored to whole lines and to the top-level keys by name, so it can never touch a value inside an
        /// entry - a mod whose name really is "{}" stays untouched, as does any future key not named here.
        ///
        /// The trailing lookahead is not consumed on purpose. In multiline mode "$" matches immediately before
        /// the "\n", which on the CRLF output the serializer produces on Windows leaves the "\r" sitting to its
        /// left: consuming it would strip the carriage return from the one line this touches and mix line
        /// endings within the file.
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex EmptyListLine = new System.Text.RegularExpressions.Regex(
            @"^(?<key>requiredMods|optionalMods|adminOnlyMods|serverOnlyMods|activePatchers|allowedPatchers): \{\}(?=[ \t]*\r?$)",
            System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.Compiled);


        internal static class ValidateMods {
            // Register new RPC
            [HarmonyPatch(typeof(ZNet), nameof(ZNet.OnNewConnection))]
            public static class ZNet_OnNewConnection_Patch {
                [HarmonyPrefix]
                [HarmonyPriority(Priority.First)]
                private static void Prefix(ZNet __instance, ZNetPeer peer) {
                    // This is Priority.First, so a throw here would take out the other OnNewConnection patches
                    // and vanilla's own per-peer registrations. The three sibling OnNewConnection patches all
                    // null-check the peer; this one did not.
                    if (peer?.m_rpc == null) { return; }
                    Logger.LogDebug($"New Connection, register VE Mod Sync RPC.");
                    // Register our RPC handler
                    peer.m_rpc.Register<ZPackage>(nameof(RPC_ReceiveModVersionData), RPC_ReceiveModVersionData);
                }
            }
        }


        // Send Client list during handshake
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_ClientHandshake))]
        public static class ZNet_RPC_ClientHandshake_Patch {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static void Prefix(ZNet __instance, ZRpc rpc) {
                if (__instance.IsClientInstance()) {
                    if (ModSettings == null) {
                        Logger.LogWarning("Mod settings are not initialized yet; sending no mod data. The server will see an empty mod list and is likely to reject this connection.");
                        return;
                    }
                    // Normally long finished - this fires when the player clicks Join, well after SetModsActive -
                    // but a listen host or an immediate reconnect can race it, and a half-filled hash set would
                    // read to the server as a tampered client.
                    PluginHasher.WaitForPass(2000);
                    PluginHasher.ApplyTo(ModSettings.ActiveMods);
                    // Same race, same fix: the patcher scan normally finished long ago, but a listen host or an
                    // immediate reconnect can reach the handshake while it is still running, and a half-filled
                    // set reads to the server as a client hiding patchers.
                    PatcherIndex.WaitForPass(2000);
                    ModSettings.ActivePatchers = PatcherIndex.Snapshot();
                    // Computed last, over the mod and patcher lists exactly as they are about to be sent.
                    // Null when the server issued no nonce, which leaves the field out of the payload.
                    ModSettings.Attestation = Attestation.Respond(ModSettings);
                    Logger.LogDebug("Client sending mod version data to server");
                    rpc.Invoke(nameof(RPC_ReceiveModVersionData), ModSettings.ActiveModsToZPackage());
                }
            }
        }

        // Send server list during handshake
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_ServerHandshake))]
        public static class ZNet_RPC_ServerHandshake_Patch {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static void Prefix(ZNet __instance, ZRpc rpc) {
                if (__instance.IsServer()) {
                    if (ModSettings == null) {
                        Logger.LogWarning("Mod settings are not initialized yet; not sending the server mod list to this client.");
                        return;
                    }
                    // Minted here, which is the earliest point the server speaks to this connection - and,
                    // because vanilla only invokes ClientHandshake after this prefix returns, always before the
                    // client builds the report it has to fold the nonce into.
                    string nonce = Attestation.Issue(rpc.GetSocket()?.GetHostName());
                    Logger.LogDebug("Server sending mod version data to client");
                    rpc.Invoke(nameof(RPC_ReceiveModVersionData), ModSettings.ToZPackage(nonce));
                }
            }
        }

        /// <summary>
        /// RPC handler to receive and store mod version data
        /// </summary>
        private static void RPC_ReceiveModVersionData(ZRpc sender, ZPackage data) {
            Logger.LogDebug($"Received mod version data from {sender.m_socket.GetEndPointString()}");
            string peerAddress = sender.m_socket.GetEndPointString();
            if (!ZNet.instance.IsServer()) {
                // Client received data from server
                Mods serverMods = new Mods().FromZPackage(data);
                // Held before validation runs: the handshake prefix that uses it fires on the very next
                // message from this server, so there is no later opportunity.
                Attestation.HoldNonce(serverMods.Nonce);
                Logger.LogDebug($"Client received server mod data: Required: {serverMods.RequiredMods.Count}, Optional: {serverMods.OptionalMods.Count}, AdminOnly: {serverMods.AdminOnlyMods.Count} mods");
                // Client cannot trust its admin status during the handshake: Jotunn syncs it only
                // after login (post-RPC_PeerInfo) and PlayerIsAdmin defaults to true. Pass it as
                // unknown so admin-only mods are surfaced as a neutral note rather than a false pass.
                // The structured detail is server-side notification material; the client only needs the text it
                // puts in the connection-failed panel.
                bool modsvalid = ValidateModlist(ModSettings, serverMods, isAdmin: false, adminStatusKnown: false, out string summary, out string details, out _);

                // Always update so a clean run clears any note left over from a previous attempt.
                DetailsUpdater?.UpdateErrorText(summary, details);
                if (modsvalid == false) {
                    // Client does not kick, but it does set the error message, the server ultimately does the actual validation-
                    // this client side comparison is just to provide feedback to the user
                    Logger.LogWarning($"Mod compatibility check failed for client.");
                }
            } else {
                // One declaration per connection, and this is it. A client sends its mod list exactly once - from
                // the ClientHandshake prefix above, which vanilla fires once per connection - so the handler is
                // taken off this connection before anything else happens. Left registered it was a pre-password,
                // pre-ban-check YAML parse that any peer could invoke as often as it liked for as long as it held
                // the socket open, which RejectPeer does not close: ZRpc drains every queued message in one frame,
                // so a loop of these was a main-thread stall on demand. A second one now reaches no handler.
                // Removing an entry from inside its own invocation is safe; ZRpc looked it up before calling us.
                sender.Unregister(nameof(RPC_ReceiveModVersionData));

                // Server received data from client. This is a pre-authentication parse (it rides the handshake),
                // so bound it before deserializing a hostile or malformed blob.
                if ((data?.Size() ?? 0) > ValConfig.MaxModListBytes) {
                    Logger.LogWarning($"Rejecting an oversize mod list from {peerAddress} ({data.Size()} bytes).");
                    RejectPeer(sender);
                    return;
                }
                Mods clientMods = new Mods().FromZPackage(data);
                // Bounded again once it is an object, before anything walks it. The byte ceiling alone still
                // admits tens of thousands of five-byte entries, each of which is validated, named in the
                // rejection text and logged.
                int declared = (clientMods.ActiveMods?.Count ?? 0) + (clientMods.ActivePatchers?.Count ?? 0);
                if (declared > ValConfig.MaxDeclaredModEntries) {
                    Logger.LogWarning($"Rejecting a mod list from {peerAddress} declaring {declared} entries; the ceiling is {ValConfig.MaxDeclaredModEntries}.");
                    RejectPeer(sender);
                    return;
                }
                string validatingHost = sender.m_socket?.GetHostName();
                bool isadmin = ZNet.instance.IsAdmin(validatingHost);
                // Whether anything found below is allowed to end this connection. The checks themselves still
                // run either way: an operator who turned this on wants to read what their admin actually
                // carried, and skipping the work as well would leave them nothing to read.
                bool exemptAdmin = ExemptFromModValidation(validatingHost);
                Logger.LogDebug($"Server received server mod data from {peerAddress} Admin?{isadmin}: Required: {clientMods.RequiredMods.Count}, Optional: {clientMods.OptionalMods.Count}, AdminOnly: {clientMods.AdminOnlyMods.Count} mods");;

                // Checked before the mod list itself. A failure here says the declaration below was not
                // generated for this connection, so it is not worth reasoning about its contents in detail -
                // and under Require it is a rejection on its own.
                if (!CheckAttestation(sender, validatingHost, clientMods, peerAddress, exemptAdmin)) { return; }

                bool modsvalid = ValidateModlist(clientMods, ModSettings, isadmin, adminStatusKnown: true, out string summary, out string details, out ModMismatchDetail detail);
                if (modsvalid || exemptAdmin) {
                    // Positive record: this host actually sent a mod list and it passed. ZNet_RPC_PeerInfo_ModRejection
                    // refuses any host that reaches PeerInfo in neither the validated nor the rejected set, which is
                    // what stops a client that simply never runs the handshake (rather than failing it) from joining.
                    // An exempt admin is recorded too, or that same gate would go on to refuse the connection this
                    // check just decided to allow.
                    if (!string.IsNullOrEmpty(validatingHost)) { ValidatedHosts.Add(validatingHost); }
                }
                if (modsvalid) {
                    // Recorded here, where we know both what this client declared and that we accepted it. If a
                    // server-side guard later refuses something this peer sends, that declaration is the other
                    // half of the contradiction.
                    //
                    // Deliberately not recorded for an exempt admin whose list FAILED. PeerTrust's whole inference
                    // rests on the peer running only mods the server approved, which is the premise the exemption
                    // just set aside; no record is the honest answer, and PeerTrust already reports that case in
                    // words rather than guessing.
                    network.PeerTrust.Declare(validatingHost, clientMods, ModSettings);
                }
                if (modsvalid == false) {
                    Logger.LogWarning($"Mod compatibility check failed for client at {peerAddress}\n{summary}");
                    if (exemptAdmin) {
                        // Logged, never notified. A Discord mod-mismatch message exists to tell staff somebody was
                        // turned away; nobody was. Paging the channel every time an admin joins on their test build
                        // is how a useful alert becomes one people stop reading.
                        Logger.LogInfo($"Letting the client at {peerAddress} in regardless: ModValidationExemptAdmins is on and this connection is on the server's admin list.");
                        return;
                    }
                    if (ValConfig.DiscordNotifyWrongMods.Value) {
                        string playerName = ResolvePeerName(sender) ?? peerAddress;
                        DiscordNotifier.Notify(NotificationEvent.ModMismatch, new Dictionary<string, string> {
                            { "player", playerName },
                            { "playerId", sender.m_socket?.GetHostName() ?? "" },
                            { "summary", summary.Trim() },
                            { "missingMods", ModMismatchDetail.Join(detail.MissingMods) },
                            { "extraMods", ModMismatchDetail.Join(detail.ExtraMods) },
                            { "versionMismatches", ModMismatchDetail.Join(detail.VersionMismatches) },
                            { "adminOnlyMods", ModMismatchDetail.Join(detail.AdminOnlyMods) },
                            { "hashMismatches", ModMismatchDetail.Join(detail.HashMismatches) },
                            { "unverifiedMods", ModMismatchDetail.Join(detail.UnverifiedMods) },
                            { "patchers", ModMismatchDetail.Join(detail.Patchers) },
                        });
                    }
                    RejectPeer(sender);
                }
            }
        }

        /// <summary>
        /// Host ids the server has refused for a mod validation failure, cleared when the peer disconnects so a
        /// player who fixes their mods can rejoin immediately. Server side only.
        /// </summary>
        private static readonly HashSet<string> RejectedHosts = new HashSet<string>();

        /// <summary>
        /// Host ids that sent a mod list which PASSED validation this connection. The positive half of the gate:
        /// a client that never sends its mod list at all - patching out the handshake rather than fabricating a
        /// passing list - would otherwise slip past a check that only ever looks for a recorded FAILURE. Cleared
        /// on disconnect alongside RejectedHosts. Server side only.
        /// </summary>
        private static readonly HashSet<string> ValidatedHosts = new HashSet<string>();

        /// <summary>
        /// Whether this connection is held to the mod gate at all.
        ///
        /// Admin status is decided by <c>ZNet.IsAdmin</c> against the host name of the socket the handshake
        /// arrived on - the game's own check, against the server's own adminlist.txt - so a client cannot
        /// claim it. Off by default, and worth being deliberate about: with it on, a line in that file is the
        /// only thing between an account and every check in this file.
        /// </summary>
        private static bool ExemptFromModValidation(string hostId) {
            if (ValConfig.ModValidationExemptAdmins == null || !ValConfig.ModValidationExemptAdmins.Value) { return false; }
            return !string.IsNullOrEmpty(hostId) && ZNet.instance != null && ZNet.instance.IsAdmin(hostId);
        }

        /// <summary>
        /// Server side: refuse a peer that failed mod validation.
        ///
        /// The "Error" RPC on its own is only advisory. Vanilla ZNet.RPC_Error assigns m_connectionStatus and
        /// does nothing else - it does not disconnect - so the connection is only actually torn down because an
        /// honest client notices the status and logs itself out. Vanilla's own rejections do not rely on that:
        /// they return early out of RPC_PeerInfo, so the server never completes the login regardless of what the
        /// client does. Recording the host here lets ZNet_RPC_PeerInfo_ModRejection do the same for us, which
        /// matters because a client capable of stubbing out RPC_Error is exactly the client this check exists
        /// to stop.
        /// </summary>
        private static void RejectPeer(ZRpc sender) {
            string hostId = sender.GetSocket()?.GetHostName();
            if (!string.IsNullOrEmpty(hostId)) { RejectedHosts.Add(hostId); }
            sender.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorVersion);
            // Push the error out before anything tears the connection down - same reason FinalSaveRpc flushes.
            sender.GetSocket()?.Flush();
        }

        /// <summary>
        /// Verifies that this client's declaration was built for this connection, and applies AttestationPolicy.
        ///
        /// Returns false when the connection has been refused and the caller must stop. A pass here is a narrow
        /// claim - see <see cref="Attestation"/> - so it is deliberately not treated as evidence about the
        /// contents of the declaration, only about its freshness.
        /// </summary>
        private static bool CheckAttestation(ZRpc sender, string hostId, Mods clientMods, string peerAddress, bool exemptAdmin) {
            Attestation.Verdict verdict = Attestation.Check(hostId, clientMods);
            if (verdict == Attestation.Verdict.Pass) { return true; }

            string why;
            switch (verdict) {
                case Attestation.Verdict.Missing:
                    why = "it carries no attestation (the client is running a ValheimEnforcer build that predates the feature, or one that has had it removed)";
                    break;
                case Attestation.Verdict.Mismatch:
                    why = "its attestation does not match the mod list it sent (the report was not generated for this connection)";
                    break;
                case Attestation.Verdict.NotIssued:
                    // Our own bookkeeping, not the client's fault: the nonce went missing between handshake and
                    // report. Reconnecting re-issues one, so this must never reject however strict the policy.
                    Logger.LogWarning($"No attestation nonce is on record for {peerAddress}; skipping the check for this connection.");
                    return true;
                default:
                    why = "its attestation could not be verified";
                    break;
            }

            if (Attestation.Policy() != Attestation.Require) {
                Logger.LogWarning($"Attestation check failed for the client at {peerAddress}: {why}. Allowed, because AttestationPolicy is {Attestation.Policy()}.");
                return true;
            }

            // Covered by the exemption on purpose. The commonest reason an admin has no usable attestation is
            // that they are testing an older or hand-built ValheimEnforcer, which is precisely the case
            // ModValidationExemptAdmins exists for; rejecting them here would leave the setting unable to
            // deliver what it promises on any server running Require.
            if (exemptAdmin) {
                Logger.LogInfo($"Attestation check failed for the client at {peerAddress}: {why}. Allowed anyway: ModValidationExemptAdmins is on and this connection is on the server's admin list.");
                return true;
            }

            Logger.LogWarning($"Rejecting the client at {peerAddress}: {why}.");
            // No detail panel is set from here. DetailsUpdater drives the local client's connection-error
            // window, so on a server it reaches nobody; the rejected player sees vanilla's version-error
            // screen, which is the same thing every other pre-PeerInfo rejection in this file produces.
            RejectPeer(sender);
            return false;
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_PeerInfo))]
        public static class ZNet_RPC_PeerInfo_ModRejection {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(ZNet __instance, ZRpc rpc) {
                if (!__instance.IsServer()) { return true; }

                string hostId = rpc.GetSocket()?.GetHostName();
                if (string.IsNullOrEmpty(hostId)) { return true; }

                if (RejectedHosts.Contains(hostId)) {
                    Logger.LogWarning($"Refusing peer info from {hostId}: rejected earlier for a mod validation failure.");
                    rpc.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorVersion);
                    return false; // skip vanilla peer-info handling, exactly as vanilla's own rejections do
                }

                // Positive gate: an honest client's mod list arrives during RPC_ClientHandshake, before PeerInfo,
                // on the same ordered ZRpc stream - so by here it is in ValidatedHosts. Reaching PeerInfo in
                // neither set means the mod handshake never ran, which is what a client that stubbed it out looks
                // like. Only the ModSync RPC (registered when ModSettings is ready) can populate the set, so do
                // not refuse before this server is itself ready to validate.
                if (ModSettings != null && !ValidatedHosts.Contains(hostId)) {
                    // The one gate an exempt admin has to be let through here rather than earlier: a client with
                    // no ValheimEnforcer on it never reaches the mod RPC at all, so nothing there ever ran to
                    // record it. Without this, "admins may connect with any mods" would stop short of the case
                    // an operator is most likely to want it for - joining on a plain, unmodded client.
                    if (ExemptFromModValidation(hostId)) {
                        Logger.LogInfo($"No mod list was received from {hostId} before PeerInfo, but ModValidationExemptAdmins is on and this connection is on the server's admin list; allowing it.");
                        return true;
                    }
                    Logger.LogWarning($"Refusing peer info from {hostId}: no mod list was received before PeerInfo (the ValheimEnforcer handshake did not run).");
                    rpc.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorVersion);
                    return false;
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
        public static class ZNet_Disconnect_ClearRejection {
            [HarmonyPrefix]
            private static void Prefix(ZNet __instance, ZNetPeer peer) {
                if (!__instance.IsServer() || peer == null) { return; }
                string hostId = peer.m_socket?.GetHostName();
                if (string.IsNullOrEmpty(hostId)) { return; }
                if (RejectedHosts.Remove(hostId)) {
                    Logger.LogDebug($"Cleared mod rejection for {hostId}; a corrected client may reconnect.");
                }
                ValidatedHosts.Remove(hostId);
                Attestation.Clear(hostId);
                network.PeerTrust.Clear(hostId);
                // The per-player cooldown tables. Each is tiny, but each would otherwise hold an entry for
                // every player who has ever tripped it since startup.
                network.RpcGuardPolicy.Forget(hostId);
                worldintegrity.StructureValidator.Forget(hostId);
                worldintegrity.ItemOriginValidator.Forget(hostId);
                worldintegrity.InventoryBoundsValidator.Forget(hostId);
            }
        }

        internal static void AddErrorMessageDetailsForMenu() {
            // We only want to monitor the start scene for the disconnect dialogue box
            if (SceneManager.GetActiveScene().name.Equals("start") == false) { return; }

            DetailsUpdater = GUIManager.CustomGUIFront.AddComponent<JotunnDetailDisconnectExpansion>();
        }

        public class JotunnDetailDisconnectExpansion : MonoBehaviour {
            GameObject ContentView;
            Text HeaderText;
            Text FooterText;
            static string HeaderMessage = "";
            static string FooterMessage = "";
            bool textset = false;

            public void UpdateErrorText(string header, string footer) {
                Logger.LogDebug($"Set Error results {header} {footer}");
                HeaderMessage = header;
                FooterMessage = footer;
                textset = false;
            }

            public void Update() {
                if (GUIManager.CustomGUIFront == null) { return; }
                Transform contentTForm = GUIManager.CustomGUIFront.transform.Find("CompatibilityWindow(Clone)/Scroll View/Viewport/Content");
                if (contentTForm == null) { 
                    textset = false;
                    return;
                }

                //List<string> children = new List<string>();
                //int count = contentTForm.childCount;
                //for (int i = 0; i < count; i++) {
                //    Transform child = contentTForm.GetChild(i);
                //    children.Add(child.name);
                //}
                //Logger.LogDebug($"Object Children: {string.Join(",", children) }");
                //return;

                if (textset == true) { return; }

                // Fix the scrollbars sensitivity
                GUIManager.CustomGUIFront.transform.Find("CompatibilityWindow(Clone)/Scroll View").GetComponent<ScrollRect>().scrollSensitivity = 1000f;


                ContentView = contentTForm.gameObject;
                // Assign references
                Transform headerTform = ContentView.transform.Find("Failed Connection Text");
                if (headerTform != null) { HeaderText = headerTform.GetComponent<Text>(); } else { Logger.LogDebug("Could not find HeaderText"); }
                Transform footerTForm = ContentView.transform.Find("Error Messages Text");
                if (footerTForm != null) { FooterText = footerTForm.GetComponent<Text>(); } else { Logger.LogDebug("Could not find FooterText"); }

                // Only override when VE has an actual message to add.
                // Otherwise preserve whatever Jotunn (or another mod) already wrote to the compatibility window.
                if (HeaderText != null && !string.IsNullOrEmpty(HeaderMessage)) {
                    HeaderText.text = $"<color=#FFA13C>Failed Connection:</color>\n{HeaderMessage}";
                }
                if (FooterText != null && !string.IsNullOrEmpty(FooterMessage)) {
                    FooterText.text = $"<color=#FFA13C>Further Steps:</color>\n{FooterMessage}";
                }
                Logger.LogDebug($"Set error results. H:{HeaderMessage} F:{FooterMessage}");
                textset = true;
            }
        }
    }
}
