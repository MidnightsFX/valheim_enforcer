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

            ModSettings = new DataObjects.Mods();
            Logger.LogDebug($"Detected {ActiveMods.Keys.Count} mods.");

            // Read the config file
            LoadConfig(File.ReadAllText(ValConfig.ModsConfigFilePath));

            PluginHasher.WaitForPass(ValConfig.HashComputeTimeoutSeconds.Value * 1000);
            RebuildActiveMods();

            foreach (KeyValuePair<string, BaseUnityPlugin> plugin in ActiveMods) {
                Logger.LogDebug($"Found active mod: {plugin.Key} v{plugin.Value.Info.Metadata.Version}");
                string currentVersion = plugin.Value.Info.Metadata.Version.ToString();
                string localHash = PluginHasher.Get(plugin.Key)?.Hash;

                if (ModSettings.RequiredMods.ContainsKey(plugin.Key)) {
                    UpdateModVersionIfChanged(ModSettings.RequiredMods, plugin.Key, currentVersion);
                    RecordLocalHashIfAllowed(ModSettings.RequiredMods, plugin.Key, localHash, currentVersion);
                    continue;
                }
                if (ModSettings.AdminOnlyMods.ContainsKey(plugin.Key)) {
                    UpdateModVersionIfChanged(ModSettings.AdminOnlyMods, plugin.Key, currentVersion);
                    RecordLocalHashIfAllowed(ModSettings.AdminOnlyMods, plugin.Key, localHash, currentVersion);
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

            // Write out updates to the loaded mods, if enabled
            if (ValConfig.UpdateLoadedModsOnStartup.Value) {
                Logger.LogDebug("Updated Mods.yaml.");
                PersistModSettings();
            }
        }

        /// <summary>
        /// The banner a new Mods.yaml is created with. It lives here rather than beside the file creation code
        /// because <see cref="PersistModSettings"/> also has to put it back on installs that lost it: before
        /// comments were preserved, the first rewrite after launch deleted it.
        /// </summary>
        internal static readonly string[] ModsFileHeaderLines = {
            "#################################################",
            "# Valheim Enforcer - Mod List",
            "#",
            "# Regenerated on startup, and re-read within ConfigPollIntervalSeconds of being edited.",
            "# Comments are kept: a note on its own line stays with the entry below it. A comment sharing",
            "# a line with a value is not kept, because that line gets rewritten.",
            "#",
            "# Every entry is keyed by its BepInEx plugin GUID.",
            "#",
            "#   activeMods      What this machine actually loaded. Rebuilt every start - editing it does nothing.",
            "#   requiredMods    Clients must have these. Mods the server loads land here by themselves.",
            "#   optionalMods    Clients may have these, and may connect without them.",
            "#   adminOnlyMods   Only admins may connect with these; everyone else is rejected.",
            "#   serverOnlyMods  Server side only. Not demanded of clients - but a client that installs one",
            "#                   is rejected for it, so this is not the list for client-side mods.",
            "#",
            "# Per entry: enforceVersion: true requires an exact version match (defaults to false).",
            "# File verification uses acceptedHashes / hashSource / thunderstorePackage / hashEnforcement.",
            "# The README covers all of it, including how to pin a mod the server does not run itself.",
            "#################################################",
        };

        /// <summary>
        /// Serializes the current mod settings to Mods.yaml. Shared by the startup rewrite and the Thunderstore
        /// resolver so both go through the same self-write suppression - otherwise our own write comes straight
        /// back in through the file watcher one poll later.
        /// </summary>
        internal static void PersistModSettings() {
            if (ModSettings == null) { return; }
            try {
                string yaml = DataObjects.yamlserializer.Serialize(ModSettings);
                File.WriteAllText(ValConfig.ModsConfigFilePath, WithPreservedComments(yaml));
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

            DataObjects.Mod entry = modList[key];
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

        private static void UpdateModVersionIfChanged(Dictionary<string, DataObjects.Mod> modList, string key, string currentVersion) {
            if (modList[key].Version != currentVersion) {
                Logger.LogInfo($"Updating version for {key}: {modList[key].Version} -> {currentVersion}");
                modList[key].Version = currentVersion;
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
        /// Applies an edited Mods.yaml to the in-memory settings.
        ///
        /// Only the four policy lists are taken from the file. ActiveMods is deliberately NOT adopted and is
        /// re-derived from the loaded plugins instead: ActiveMods is the list this peer *reports* about itself
        /// during the handshake, and this method runs from the config file watcher, whose admin gate
        /// (SynchronizationManager.PlayerIsAdmin) defaults to true before login. Adopting it from file text
        /// would let any player hand-edit their own Mods.yaml, wait one poll interval, and connect claiming to
        /// be running whatever set of mods - and, once file verification exists, whatever hashes - they liked.
        /// </summary>
        internal static void UpdateModSettingConfigs(string yamlstring) {
            try {
                DataObjects.Mods fromFile = DataObjects.yamldeserializer.Deserialize<DataObjects.Mods>(yamlstring);
                if (fromFile == null) {
                    Logger.LogWarning("Mod configuration file was empty, keeping the current settings.");
                    return;
                }
                ModSettings = fromFile;
                RebuildActiveMods();
            } catch (System.Exception e) {
                Logger.LogWarning($"Failed to deserialize mod configurations: {e.Message}");
            }
        }

        internal static bool ValidateModlist(Mods CheckingMods, Mods AuthoratativeMods, bool isAdmin, bool adminStatusKnown, out string summay, out string details) {
            summay = "";
            details = "";
            List<string> extraMods = new List<string>();
            List<string> versionMismatch = new List<string>();
            List<string> adminOnlyNotAllowed = new List<string>();
            List<string> adminOnlyInfo = new List<string>();   // client-side: admin status not yet synced, surfaced as a neutral note
            List<string> hashMismatch = new List<string>();     // the file does not match anything the server accepts
            List<string> hashUnverifiable = new List<string>(); // the server has a record but no usable hash was reported
            List<string> hashNotRecorded = new List<string>();  // Strict only: enforced mod the server never pinned
            List<string> requiredModsMissing = AuthoratativeMods.RequiredMods.Keys.Distinct().ToList();

            Logger.LogDebug($"Validating modlist of {CheckingMods.ActiveMods.Count} mods isAdmin? {isAdmin}");

            foreach (KeyValuePair<string, DataObjects.Mod> mod in CheckingMods.ActiveMods) {
                requiredModsMissing.Remove(mod.Key);

                // The authoritative record this client mod matched, and which list it came from. Captured
                // rather than continue'd out of, because file verification runs on top of whatever the
                // version/admin check decided and needs the same record.
                //
                // The else-if chain also establishes an explicit Required > AdminOnly > Optional priority.
                // Previously these were independent ifs and the version-mismatch branches did not continue, so
                // a required (or optional) mod with the wrong version was reported as BOTH a version mismatch
                // and a non-allowed mod.
                DataObjects.Mod authoritative = null;
                bool requiredOrAdmin = false;

                // Compare required mods
                if (AuthoratativeMods.RequiredMods.ContainsKey(mod.Key)) {
                    authoritative = AuthoratativeMods.RequiredMods[mod.Key];
                    requiredOrAdmin = true;
                    if (authoritative.EnforceVersion && authoritative.Version != mod.Value.Version) {
                        versionMismatch.Add(mod.Key);
                    }
                }
                // Compare admin mods - prevent non-admin clients from joining with admin only mods.
                // Non-admins carrying one are rejected; admins are version-enforced when EnforceVersion is set.
                else if (AuthoratativeMods.AdminOnlyMods.ContainsKey(mod.Key)) {
                    authoritative = AuthoratativeMods.AdminOnlyMods[mod.Key];
                    requiredOrAdmin = true;
                    if (!adminStatusKnown) {
                        // Client side: Jotunn only syncs admin status after login (post-RPC_PeerInfo),
                        // and PlayerIsAdmin defaults to true, so we cannot trust it here. Surface a
                        // neutral note instead of guessing.
                        adminOnlyInfo.Add(mod.Key);
                    } else if (isAdmin) {
                        if (authoritative.EnforceVersion && authoritative.Version != mod.Value.Version) {
                            versionMismatch.Add(mod.Key);
                        }
                    } else {
                        adminOnlyNotAllowed.Add(mod.Key);
                    }
                }
                // Compare optional mods
                else if (AuthoratativeMods.OptionalMods.ContainsKey(mod.Key)) {
                    authoritative = AuthoratativeMods.OptionalMods[mod.Key];
                    if (authoritative.EnforceVersion && authoritative.Version != mod.Value.Version) {
                        versionMismatch.Add(mod.Key);
                    }
                }
                // ServerOnlyMods stays the skip button: a client carrying one is still an extra mod, exactly as
                // before, so it is deliberately not matched here.

                if (authoritative == null) {
                    // Mod didn't match one of the existing mods, its an extra
                    extraMods.Add(mod.Key);
                    continue;
                }

                switch (HashPolicy.Evaluate(authoritative, mod.Value, requiredOrAdmin)) {
                    case HashVerdict.Mismatch:
                        hashMismatch.Add(mod.Key);
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


            if (versionMismatch.Count > 0) {
                Logger.LogWarning($"Mods version mismatch with the server found:");
                summay = "A Mod mismatch was detected. Ensure you have the correct versions and are only using allowed mods.";
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
            if (versionMismatch.Count > 0 || requiredModsMissing.Count > 0 || extraMods.Count > 0 || adminOnlyNotAllowed.Count > 0 || adminOnlyInfo.Count > 0
                || hashMismatch.Count > 0 || hashUnverifiable.Count > 0 || hashNotRecorded.Count > 0) {
                // Build detailed error message for display in Jotunn's CompatibilityWindow
                StringBuilder errorBuilder = new StringBuilder();
                errorBuilder.AppendLine("\n<b>ValheimEnforcer - Mod Validation Failed</b>");

                if (versionMismatch.Count > 0) {
                    errorBuilder.AppendLine("\n<b>Version Mismatches:</b>");
                    foreach (var modKey in versionMismatch) {
                        errorBuilder.AppendLine($"  • {modKey}");
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

                string fullError = errorBuilder.ToString();
                details = fullError;
                //Logger.LogWarning(LastValidationError);
                return false;
            }
            Logger.LogInfo("Client mod list validated successfully.");
            return true;
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
            try {
                ModSettings = DataObjects.yamldeserializer.Deserialize<DataObjects.Mods>(yaml) ?? new DataObjects.Mods();
            } catch (System.Exception e) {
                Logger.LogError($"Could not parse {ValConfig.ModsConfigFilePath}: {e.Message}. Continuing with empty mod settings; fix the file and restart, or delete it to have it regenerated.");
                ModSettings = new DataObjects.Mods();
            }
        }

        internal static string GetDefaultConfig() {
            if (ModSettings != null) {
                return DataObjects.yamlserializer.Serialize(ModSettings);
            }
            return DataObjects.yamlserializer.Serialize(new DataObjects.Mods());
        }


        internal static class ValidateMods {
            // Register new RPC
            [HarmonyPatch(typeof(ZNet), nameof(ZNet.OnNewConnection))]
            public static class ZNet_OnNewConnection_Patch {
                [HarmonyPrefix]
                [HarmonyPriority(Priority.First)]
                private static void Prefix(ZNet __instance, ZNetPeer peer) {
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
                    Logger.LogDebug("Server sending mod version data to client");
                    rpc.Invoke(nameof(RPC_ReceiveModVersionData), ModSettings.ToZPackage());
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
                Logger.LogDebug($"Client received server mod data: Required: {serverMods.RequiredMods.Count}, Optional: {serverMods.OptionalMods.Count}, AdminOnly: {serverMods.AdminOnlyMods.Count} mods");
                // Client cannot trust its admin status during the handshake: Jotunn syncs it only
                // after login (post-RPC_PeerInfo) and PlayerIsAdmin defaults to true. Pass it as
                // unknown so admin-only mods are surfaced as a neutral note rather than a false pass.
                bool modsvalid = ValidateModlist(ModSettings, serverMods, isAdmin: false, adminStatusKnown: false, out string summary, out string details);

                // Always update so a clean run clears any note left over from a previous attempt.
                DetailsUpdater?.UpdateErrorText(summary, details);
                if (modsvalid == false) {
                    // Client does not kick, but it does set the error message, the server ultimately does the actual validation-
                    // this client side comparison is just to provide feedback to the user
                    Logger.LogWarning($"Mod compatibility check failed for client.");
                }
            } else {
                // Server received data from client
                Mods clientMods = new Mods().FromZPackage(data);
                bool isadmin = ZNet.instance.IsAdmin(sender.m_socket.GetHostName());
                Logger.LogDebug($"Server received server mod data from {peerAddress} Admin?{isadmin}: Required: {clientMods.RequiredMods.Count}, Optional: {clientMods.OptionalMods.Count}, AdminOnly: {clientMods.AdminOnlyMods.Count} mods");;
                bool modsvalid = ValidateModlist(clientMods, ModSettings, isadmin, adminStatusKnown: true, out string summary, out string details);
                if (modsvalid == false) {
                    Logger.LogWarning($"Mod compatibility check failed for client at {peerAddress}\n{summary}");
                    if (ValConfig.DiscordNotifyWrongMods.Value) {
                        string playerName = ResolvePeerName(sender) ?? peerAddress;
                        DiscordEmbed embed = new DiscordEmbed("Connection Rejected: Mod Mismatch", summary.Trim(), Red).AddField("Player", playerName, true);
                        DiscordNotifier.SendAsync(embed.ToMessage());
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

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_PeerInfo))]
        public static class ZNet_RPC_PeerInfo_ModRejection {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(ZNet __instance, ZRpc rpc) {
                if (!__instance.IsServer()) { return true; }

                string hostId = rpc.GetSocket()?.GetHostName();
                if (string.IsNullOrEmpty(hostId) || !RejectedHosts.Contains(hostId)) { return true; }

                Logger.LogWarning($"Refusing peer info from {hostId}: rejected earlier for a mod validation failure.");
                rpc.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorVersion);
                return false; // skip vanilla peer-info handling, exactly as vanilla's own rejections do
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
        public static class ZNet_Disconnect_ClearRejection {
            [HarmonyPrefix]
            private static void Prefix(ZNet __instance, ZNetPeer peer) {
                if (!__instance.IsServer() || peer == null) { return; }
                string hostId = peer.m_socket?.GetHostName();
                if (!string.IsNullOrEmpty(hostId) && RejectedHosts.Remove(hostId)) {
                    Logger.LogDebug($"Cleared mod rejection for {hostId}; a corrected client may reconnect.");
                }
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
