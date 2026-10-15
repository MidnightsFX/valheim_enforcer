using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ValheimEnforcer.common;
using ValheimEnforcer.modules.mods;

namespace ValheimEnforcer.modules.modsync {

    /// <summary>
    /// What the client mod sync would change to get past one server's mod check: which Thunderstore packages to
    /// install or update, which of the player's mods to turn off, and what it cannot fix.
    ///
    /// The verdicts follow <see cref="ModManager.ValidateModlist"/> rule for rule - the same list priority, the
    /// same version and file checks - because the server runs that method on the next connection, and a fix
    /// worked out by any other rule is a fix the server can still refuse. Two deliberate differences: admin-only
    /// mods are left alone (the client cannot know whether the player is an admin), and patchers are not
    /// handled at all (a patcher runs before every plugin, so installing or removing one stays a decision for the
    /// player and their mod manager).
    ///
    /// Pure: everything it needs is passed in, so it can be exercised without the game.
    /// </summary>
    internal sealed class ModSyncPlan {

        /// <summary>One Thunderstore package to download, at the exact version the server pinned.</summary>
        internal sealed class PackageInstall {
            internal string Spec;
            internal string Owner;
            internal string Name;
            internal string Version;

            /// <summary>Plugin id -> the file hashes the server accepts for it. The download must match.</summary>
            internal readonly Dictionary<string, List<string>> AcceptedHashes = new Dictionary<string, List<string>>();

            /// <summary>Plugin id -> the version the server enforces, for the ids where it enforces one.</summary>
            internal readonly Dictionary<string, string> RequiredVersions = new Dictionary<string, string>();

            internal readonly List<string> Reasons = new List<string>();

            /// <summary>The player's current copies this package replaces, which have to be turned off.</summary>
            internal readonly List<string> ReplacedDlls = new List<string>();

            /// <summary>The folder name r2modman and Gale give a package, which is where it is installed.</summary>
            internal string FolderName { get { return $"{Owner}-{Name}"; } }
        }

        /// <summary>One of the player's plugins to turn off.</summary>
        internal sealed class PluginDisable {
            internal string PluginID;
            internal string DllPath;
            internal string Reason;
        }

        internal readonly List<PackageInstall> Installs = new List<PackageInstall>();
        internal readonly List<PluginDisable> Disables = new List<PluginDisable>();
        internal readonly List<string> Unfixable = new List<string>();
        internal readonly List<string> Notes = new List<string>();

        internal bool HasWork { get { return Installs.Count > 0 || Disables.Count > 0; } }

        private readonly IDictionary<string, string> dllPaths;
        private readonly string pluginsRoot;

        private ModSyncPlan(IDictionary<string, string> dllPaths, string pluginsRoot) {
            this.dllPaths = dllPaths ?? new Dictionary<string, string>();
            this.pluginsRoot = pluginsRoot;
        }

        /// <param name="client">This machine's report: ActiveMods with the hashes it would send.</param>
        /// <param name="server">The server's mod list, as it arrived in the handshake.</param>
        /// <param name="dllPaths">Plugin id -> the full path of the DLL it was loaded from.</param>
        /// <param name="pluginsRoot">BepInEx/plugins. Nothing outside it is ever renamed.</param>
        /// <param name="protectedIds">Plugins never turned off, whatever the server says - this mod and Jotunn.</param>
        internal static ModSyncPlan Build(DataObjects.Mods client, DataObjects.Mods server, IDictionary<string, string> dllPaths, string pluginsRoot, ICollection<string> protectedIds) {
            ModSyncPlan plan = new ModSyncPlan(dllPaths, pluginsRoot);
            client = DataObjects.Mods.Normalized(client);
            server = DataObjects.Mods.Normalized(server);
            string hashMode = server.HashEnforcementMode;

            // Missing first, in the server's own order, so the list reads the way the rejection text does.
            foreach (KeyValuePair<string, DataObjects.Mod> required in server.RequiredMods) {
                if (server.AdminOnlyMods.ContainsKey(required.Key)) { continue; }
                if (client.ActiveMods.ContainsKey(required.Key)) { continue; }
                plan.AddInstall(required.Key, required.Value ?? new DataObjects.Mod(), $"{required.Key} is missing", replacingId: null);
            }

            foreach (KeyValuePair<string, DataObjects.Mod> mod in client.ActiveMods) {
                string id = mod.Key;
                DataObjects.Mod reported = mod.Value ?? new DataObjects.Mod();
                DataObjects.Mod authoritative;
                bool requiredOrAdmin;

                if (TryGet(server.AdminOnlyMods, id, out authoritative)) {
                    plan.Notes.Add($"{id}: this server keeps it for admins, so it is left as it is");
                    continue;
                }
                if (TryGet(server.RequiredMods, id, out authoritative)) {
                    requiredOrAdmin = true;
                } else if (TryGet(server.OptionalMods, id, out authoritative)) {
                    requiredOrAdmin = false;
                } else {
                    plan.AddDisable(id, "not allowed on this server", protectedIds);
                    continue;
                }

                bool versionDiffers = authoritative.Version != reported.Version;
                string versionReason = $"{id} needs {authoritative.Version}, you have {(string.IsNullOrEmpty(reported.Version) ? "an unknown version" : reported.Version)}";

                if (authoritative.EnforceVersion && versionDiffers) {
                    plan.AddInstall(id, authoritative, versionReason, replacingId: id);
                    continue;
                }

                switch (HashPolicy.Evaluate(authoritative, reported, requiredOrAdmin, hashMode)) {
                    case HashVerdict.Mismatch:
                        plan.AddInstall(id, authoritative,
                            versionDiffers && !string.IsNullOrEmpty(authoritative.Version) ? versionReason : $"{id}: your copy is not the build the server checks for",
                            replacingId: id);
                        break;
                    case HashVerdict.Unverifiable:
                        plan.Unfixable.Add($"{id}: your copy could not be checked ({reported.HashStatus ?? "no hash"})");
                        break;
                    case HashVerdict.NotRecorded:
                        plan.Unfixable.Add($"{id}: the server has no file hash for it, which the server's admin has to fix");
                        break;
                    default:
                        break;
                }
            }

            plan.DropSharedDisables(client);
            return plan;
        }

        /// <summary>
        /// The exact "Owner-Name-Version" to download for an entry, or false when the server has not pinned one.
        ///
        /// What the server actually hashed wins over what it was asked to hash. When an admin moves a pin to a new
        /// version and the resolve has not happened yet, the recorded hashes still belong to the old one - and
        /// the old one is the build the server will accept, so it is the one worth downloading.
        /// </summary>
        internal static bool TryPinnedSpec(DataObjects.Mod entry, out string spec) {
            spec = null;
            if (entry == null) { return false; }
            string owner;
            string name;
            string version;

            if (string.Equals(entry.HashSource, HashPolicy.SourceThunderstore, StringComparison.OrdinalIgnoreCase)
                && ThunderstoreResolver.TryParseSpec(entry.HashedFrom, out owner, out name, out version) && version != null) {
                spec = $"{owner}-{name}-{version}";
                return true;
            }

            if (!ThunderstoreResolver.TryParseSpec(entry.ThunderstorePackage, out owner, out name, out version)) { return false; }
            if (version == null) {
                // The resolver's own fallback, kept identical so the client reaches for the build the server
                // would have hashed. A wrong guess is caught by the hash check, not installed.
                if (entry.Version == null || !ThunderstoreResolver.VersionPart.IsMatch(entry.Version)) { return false; }
                version = entry.Version;
            }
            spec = $"{owner}-{name}-{version}";
            return true;
        }

        private void AddInstall(string id, DataObjects.Mod authoritative, string reason, string replacingId) {
            // Same rule as HashPolicy.Evaluate: no version and no hash typed in by hand means any build will do,
            // which leaves nothing exact to download and check.
            if (!authoritative.IsVersioned() && (!authoritative.HasRecordedHash() || authoritative.HashesAreAutomatic())) {
                Unfixable.Add($"{id}: the server lists it without a version, so there is no particular build to install");
                return;
            }
            if (!TryPinnedSpec(authoritative, out string spec)) {
                Unfixable.Add($"{id}: the server names no Thunderstore package for it");
                return;
            }
            if (!authoritative.HasRecordedHash()) {
                Unfixable.Add($"{id}: the server has no recorded file hash for it");
                return;
            }

            string replaced = null;
            if (replacingId != null) {
                // The old copy has to go. BepInEx keeps the newer of two plugins with the same id, so leaving it
                // in place would quietly undo a downgrade to the version the server pinned.
                if (!dllPaths.TryGetValue(replacingId, out replaced) || !IsUnder(replaced, pluginsRoot)) {
                    Unfixable.Add($"{id}: your copy is not in BepInEx/plugins, so it has to be replaced by hand");
                    return;
                }
            }

            ThunderstoreResolver.TryParseSpec(spec, out string owner, out string name, out string version);
            PackageInstall package = Installs.FirstOrDefault(p => string.Equals(p.Spec, spec, StringComparison.OrdinalIgnoreCase));
            if (package == null) {
                package = new PackageInstall { Spec = spec, Owner = owner, Name = name, Version = version };
                Installs.Add(package);
            }
            package.AcceptedHashes[id] = new List<string>(authoritative.AcceptedHashes);
            if (authoritative.EnforceVersion && !string.IsNullOrEmpty(authoritative.Version)) {
                package.RequiredVersions[id] = authoritative.Version;
            }
            package.Reasons.Add(reason);
            if (replaced != null && !package.ReplacedDlls.Contains(replaced, StringComparer.OrdinalIgnoreCase)) {
                package.ReplacedDlls.Add(replaced);
            }
        }

        private void AddDisable(string id, string reason, ICollection<string> protectedIds) {
            if (protectedIds != null && protectedIds.Contains(id)) {
                Unfixable.Add($"{id}: ValheimEnforcer needs it, so it is not turned off");
                return;
            }
            if (!dllPaths.TryGetValue(id, out string path) || !IsUnder(path, pluginsRoot)) {
                Unfixable.Add($"{id}: its file is not in BepInEx/plugins, so it has to be removed by hand");
                return;
            }
            Disables.Add(new PluginDisable { PluginID = id, DllPath = path, Reason = reason });
        }

        /// <summary>
        /// A DLL can host more than one plugin. Renaming it turns off every one of them, so a disable is only kept
        /// when every plugin loaded from that file is being turned off too.
        /// </summary>
        private void DropSharedDisables(DataObjects.Mods client) {
            HashSet<string> leaving = new HashSet<string>(Disables.Select(d => d.PluginID));
            List<PluginDisable> shared = new List<PluginDisable>();
            foreach (PluginDisable disable in Disables) {
                foreach (string other in client.ActiveMods.Keys) {
                    if (leaving.Contains(other)) { continue; }
                    if (!dllPaths.TryGetValue(other, out string otherPath)) { continue; }
                    if (!string.Equals(otherPath, disable.DllPath, StringComparison.OrdinalIgnoreCase)) { continue; }
                    Unfixable.Add($"{disable.PluginID}: it is in the same file as {other}, which the server allows");
                    shared.Add(disable);
                    break;
                }
            }
            foreach (PluginDisable disable in shared) { Disables.Remove(disable); }
        }

        private static bool TryGet(Dictionary<string, DataObjects.Mod> list, string key, out DataObjects.Mod entry) {
            entry = null;
            if (list == null || !list.TryGetValue(key, out entry)) { return false; }
            if (entry == null) { entry = new DataObjects.Mod(); }
            return true;
        }

        internal static bool IsUnder(string path, string root) {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) { return false; }
            try {
                string full = Path.GetFullPath(path);
                string dir = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return full.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
            } catch (Exception) {
                return false;
            }
        }

        /// <summary>A path shown to the player relative to BepInEx/plugins, which is the folder they recognise.</summary>
        internal string Shown(string path) {
            if (IsUnder(path, pluginsRoot)) {
                return "plugins/" + Path.GetFullPath(path).Substring(Path.GetFullPath(pluginsRoot).TrimEnd('\\', '/').Length + 1).Replace('\\', '/');
            }
            return Path.GetFileName(path);
        }

        /// <summary>The plan as the player reads it before agreeing to it. Unity rich text.</summary>
        internal string Describe() {
            StringBuilder text = new StringBuilder();
            if (Installs.Count > 0) {
                text.AppendLine("<color=#FFA13C>Install or update from Thunderstore:</color>");
                foreach (PackageInstall package in Installs) {
                    text.AppendLine($"  • {package.Owner}-{package.Name} {package.Version}");
                    foreach (string reason in package.Reasons) { text.AppendLine($"      {reason}"); }
                    foreach (string replaced in package.ReplacedDlls) { text.AppendLine($"      replaces {Shown(replaced)}"); }
                }
                text.AppendLine();
            }
            if (Disables.Count > 0) {
                text.AppendLine("<color=#FFA13C>Turn off (renamed to .old, not deleted):</color>");
                foreach (PluginDisable disable in Disables) {
                    text.AppendLine($"  • {disable.PluginID} - {Shown(disable.DllPath)}, {disable.Reason}");
                }
                text.AppendLine();
            }
            if (Unfixable.Count > 0) {
                text.AppendLine("<color=#FFA13C>Not fixed automatically:</color>");
                foreach (string line in Unfixable) { text.AppendLine($"  • {line}"); }
                text.AppendLine();
            }
            if (Notes.Count > 0) {
                text.AppendLine("<color=#FFA13C>Left alone:</color>");
                foreach (string line in Notes) { text.AppendLine($"  • {line}"); }
                text.AppendLine();
            }
            return text.ToString();
        }
    }
}
