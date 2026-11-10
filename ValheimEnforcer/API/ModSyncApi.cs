using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using BepInEx;
using ValheimEnforcer.common;
using ValheimEnforcer.modules.mods;
using ValheimEnforcer.modules.modsync;

namespace ValheimEnforcer.API {

    /// <summary>
    /// What a server that refused this client would need changed, for the separate mod sync add-on that does
    /// the installing (ValheimEnforcerModSync, GUID <see cref="AddonGuid"/>).
    ///
    /// Enforcer itself never downloads, stages or installs anything for a player. It keeps the server's mod list
    /// from the handshake when the server offers fixes, and works out the plan with the same rules it uses to
    /// check mods, because the server applies those rules again on the next connection. Everything returned here
    /// is a snapshot: changing it changes nothing in Enforcer.
    ///
    /// The contract: a member here is never changed or removed without raising <see cref="Version"/>. New
    /// members may be added without raising it. An add-on checks for the version it was written against, and
    /// keeps working across every Enforcer update that only adds.
    ///
    /// Main thread only, like the handshake that fills it.
    /// </summary>
    public static class ModSyncApi {

        public const string EnforcerGuid = global::ValheimEnforcer.ValheimEnforcer.PluginGUID;
        public const string AddonGuid = "MidnightsFX.ValheimEnforcer.ModSync";

        /// <summary>
        /// Raised only when a member of this API changes or goes away. A property rather than a const, so an
        /// add-on reads the value of the Enforcer it is running against, not the one it was built against.
        /// </summary>
        public static int Version { get { return 1; } }

        /// <summary>True while the latest server this client tried offers fixes. Cleared when the next attempt starts.</summary>
        public static bool HasOffer { get { return ModSyncOffer.HasOffer; } }

        /// <summary>"host:port" to rejoin the offering server with +connect, or null when the join cannot be expressed that way.</summary>
        public static string RejoinAddress { get { return ModSyncOffer.RejoinAddress; } }

        /// <summary>The plan for the offering server against what this game has loaded, or null without an offer.</summary>
        public static PlanInfo BuildPlan() {
            ModSyncPlan plan = ModSyncOffer.BuildPlan();
            return plan == null ? null : new PlanInfo(plan, Paths.PluginPath);
        }

        /// <summary>The largest package archive to download, in bytes (the ThunderstoreMaxArchiveMB setting).</summary>
        public static long MaxArchiveBytes {
            get { return (long)(ValConfig.ThunderstoreMaxArchiveMB?.Value ?? 128) * 1024 * 1024; }
        }

        /// <summary>Most entries an archive may have. The same limits the server uses when it hashes a package.</summary>
        public static int MaxZipEntries { get { return ThunderstoreResolver.MaxZipEntries; } }

        /// <summary>Largest single file in an archive once unpacked, in bytes.</summary>
        public static long MaxEntryBytes { get { return ThunderstoreResolver.MaxEntryBytes; } }

        /// <summary>Largest whole archive once unpacked, in bytes.</summary>
        public static long MaxTotalUncompressedBytes { get { return ThunderstoreResolver.MaxTotalUncompressedBytes; } }
    }

    /// <summary>What to change to get past one server's mod check, and what cannot be changed automatically.</summary>
    public sealed class PlanInfo {

        /// <summary>Thunderstore packages to install or update, at the exact versions the server pinned.</summary>
        public IReadOnlyList<PackageInfo> Installs { get; }

        /// <summary>The player's plugins to turn off.</summary>
        public IReadOnlyList<DisableInfo> Disables { get; }

        /// <summary>Problems the player has to fix by hand, each with its reason.</summary>
        public IReadOnlyList<string> Unfixable { get; }

        /// <summary>Mods deliberately left as they are, each with its reason.</summary>
        public IReadOnlyList<string> Notes { get; }

        /// <summary>BepInEx/plugins. Nothing outside it is ever part of a plan.</summary>
        public string PluginsRoot { get; }

        public bool HasWork { get { return Installs.Count > 0 || Disables.Count > 0; } }

        internal PlanInfo(ModSyncPlan plan, string pluginsRoot) {
            Installs = plan.Installs.Select(p => new PackageInfo(p)).ToList().AsReadOnly();
            Disables = plan.Disables.Select(d => new DisableInfo(d)).ToList().AsReadOnly();
            Unfixable = plan.Unfixable.ToList().AsReadOnly();
            Notes = plan.Notes.ToList().AsReadOnly();
            PluginsRoot = pluginsRoot;
        }
    }

    /// <summary>One Thunderstore package to download, at the exact version the server pinned.</summary>
    public sealed class PackageInfo {

        /// <summary>"Owner-Name-Version".</summary>
        public string Spec { get; }
        public string Owner { get; }
        public string Name { get; }
        public string Version { get; }

        /// <summary>"Owner-Name": the folder r2modman and Gale give a package under BepInEx/plugins.</summary>
        public string FolderName { get { return $"{Owner}-{Name}"; } }

        /// <summary>
        /// Plugin id -> the file hashes the server accepts for it. Every one of these plugins has to be in the
        /// download, in a DLL whose hash is on its list. A hash is the SHA-256 of the DLL file as lowercase hex
        /// with no separators; compare without regard to case, since an admin may type one in either.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyList<string>> AcceptedHashes { get; }

        /// <summary>Plugin id -> the version the server enforces, for the ids where it enforces one.</summary>
        public IReadOnlyDictionary<string, string> RequiredVersions { get; }

        /// <summary>Why the package is in the plan, one line per plugin it is there for.</summary>
        public IReadOnlyList<string> Reasons { get; }

        /// <summary>The player's current DLLs this package replaces, which have to be turned off.</summary>
        public IReadOnlyList<string> ReplacedDlls { get; }

        internal PackageInfo(ModSyncPlan.PackageInstall package) {
            Spec = package.Spec;
            Owner = package.Owner;
            Name = package.Name;
            Version = package.Version;
            Dictionary<string, IReadOnlyList<string>> hashes = new Dictionary<string, IReadOnlyList<string>>();
            foreach (KeyValuePair<string, List<string>> entry in package.AcceptedHashes) {
                hashes[entry.Key] = (entry.Value ?? new List<string>()).ToList().AsReadOnly();
            }
            AcceptedHashes = new ReadOnlyDictionary<string, IReadOnlyList<string>>(hashes);
            RequiredVersions = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(package.RequiredVersions));
            Reasons = package.Reasons.ToList().AsReadOnly();
            ReplacedDlls = package.ReplacedDlls.ToList().AsReadOnly();
        }
    }

    /// <summary>One of the player's plugins to turn off, by renaming its DLL.</summary>
    public sealed class DisableInfo {
        public string PluginId { get; }
        public string DllPath { get; }
        public string Reason { get; }

        internal DisableInfo(ModSyncPlan.PluginDisable disable) {
            PluginId = disable.PluginID;
            DllPath = disable.DllPath;
            Reason = disable.Reason;
        }
    }
}
