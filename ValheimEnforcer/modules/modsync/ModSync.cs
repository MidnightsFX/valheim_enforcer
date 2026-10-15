using BepInEx;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using ValheimEnforcer.common;
using ValheimEnforcer.modules.mods;

namespace ValheimEnforcer.modules.modsync {

    /// <summary>
    /// Client side: lets a server that refused this player for their mods fix them, with the player's say-so.
    ///
    /// Nothing here runs unless both sides asked for it. The server has to send OffersModDownloads, which only
    /// its admin's OfferModDownloads setting does; the player has to have AllowServerModInstalls on in their
    /// own, unsynced, config. Without the server's offer there is nothing to show; without the player's setting
    /// the window explains what would change and how to allow it, and installs nothing.
    ///
    /// The server never sends code. Its mod list - which every client already receives in the handshake - names
    /// Thunderstore packages and the file hashes it will accept, and this downloads those exact versions from
    /// thunderstore.io itself and refuses anything that does not hash to what the server recorded.
    ///
    /// Flow: the handshake hands over the server's list (<see cref="NoteServerList"/>); the connection error
    /// window gets a button (<see cref="ModSyncWindow"/>); the player reviews the plan and agrees; the packages
    /// are downloaded, checked and staged on a worker thread (<see cref="Start"/>); then the game quits and the
    /// native helper applies the staged changes and starts it again (<see cref="Restart"/>).
    /// </summary>
    internal static class ModSync {

        internal enum Phase { Idle, Working, Ready, Failed }

        // Old copies of replaced plugin folders are kept for the player to restore by hand, a few runs deep.
        private const int KeepBackups = 5;

        // The latest server list that offered downloads, for the connection attempt in progress. Cleared when the
        // next attempt starts, so a window from a different server can never act on this one's list.
        private static DataObjects.Mods offer;
        private static string rejoinAddress;

        private static volatile Phase phase = Phase.Idle;
        private static volatile string status = "";
        private static volatile string failure;
        private static ModSyncRestart.Ops readyOps;
        private static CancellationTokenSource cancellation;

        internal static string Root { get { return Path.Combine(Path.Combine(Paths.CachePath, "ValheimEnforcer"), "modsync"); } }
        internal static string StagingRoot { get { return Path.Combine(Root, "staged"); } }
        internal static string BackupRoot { get { return Path.Combine(Root, "replaced"); } }
        private static string HelperDir { get { return Path.Combine(Root, "bin"); } }
        private static string PlanPath { get { return Path.Combine(Root, "apply.plan"); } }
        private static string LogPath { get { return Path.Combine(Root, "apply.log"); } }
        /// <summary>Where the helper says why, when it could not read the plan and so has no log path.</summary>
        private static string PlanErrorPath { get { return Path.Combine(Root, "apply.error.log"); } }

        internal static bool HasOffer { get { return offer != null; } }
        internal static string RejoinAddress { get { return rejoinAddress; } }
        internal static Phase CurrentPhase { get { return phase; } }
        internal static string Status { get { return status; } }
        internal static string Failure { get { return failure; } }

        /// <summary>
        /// The changes are applied by the native helper, so installing needs a build of it for this machine:
        /// Windows and Proton (the Windows build, under Wine), Linux and macOS, all x86_64.
        /// </summary>
        internal static bool CanInstallHere { get { return ModSyncHelper.Available; } }

        internal static bool PlayerAllowsInstalls { get { return ValConfig.AllowServerModInstalls != null && ValConfig.AllowServerModInstalls.Value; } }

        internal static void ClearOffer() {
            offer = null;
            rejoinAddress = null;
        }

        /// <summary>Called with every server list a client receives. Keeps it only when the server offers downloads.</summary>
        internal static void NoteServerList(DataObjects.Mods serverMods) {
            if (serverMods == null || !serverMods.OffersModDownloads) {
                ClearOffer();
                return;
            }
            offer = serverMods;
            rejoinAddress = CurrentRejoinAddress();
            Logger.LogDebug($"This server offers mod downloads; rejoin address {(rejoinAddress ?? "not available")}.");
        }

        /// <summary>
        /// "host:port" for +connect, or null when this connection cannot be expressed that way. Only a direct
        /// connection to a dedicated server's address can; a crossplay join code or a Steam id join cannot, and
        /// the player is told to rejoin from the server list instead.
        /// </summary>
        private static string CurrentRejoinAddress() {
            if (ZNet.m_onlineBackend != OnlineBackendType.Steamworks) { return null; }
            if (string.IsNullOrEmpty(ZNet.m_serverHost) || ZNet.m_serverHostPort <= 0) { return null; }
            return $"{ZNet.m_serverHost}:{ZNet.m_serverHostPort}";
        }

        /// <summary>The plan for the server that offered, against what this game has loaded. Null without an offer.</summary>
        internal static ModSyncPlan BuildPlan() {
            if (offer == null || ModManager.ModSettings == null) { return null; }
            Dictionary<string, string> dllPaths = new Dictionary<string, string>();
            foreach (KeyValuePair<string, BaseUnityPlugin> plugin in ModManager.ActiveMods) {
                string location = plugin.Value?.Info?.Location;
                if (!string.IsNullOrEmpty(location)) { dllPaths[plugin.Key] = location; }
            }
            string[] protectedIds = { global::ValheimEnforcer.ValheimEnforcer.PluginGUID, Jotunn.Main.ModGuid };
            return ModSyncPlan.Build(ModManager.ModSettings, offer, dllPaths, Paths.PluginPath, protectedIds);
        }

        /// <summary>Downloads, checks and stages every package in the plan on a worker thread.</summary>
        internal static void Start(ModSyncPlan plan) {
            if (phase == Phase.Working || plan == null) { return; }
            cancellation = new CancellationTokenSource();
            CancellationToken token = cancellation.Token;
            long cap = (long)ValConfig.ThunderstoreMaxArchiveMB.Value * 1024 * 1024;
            readyOps = null;
            failure = null;
            status = "Starting";
            phase = Phase.Working;
            Logger.LogInfo($"Mod sync: fetching {plan.Installs.Count} package(s) and turning off {plan.Disables.Count} plugin(s) for this server.");
            Task.Run(() => Run(plan, cap, token));
        }

        /// <summary>Stops a fetch in progress, or drops a staged result the player backed out of.</summary>
        internal static void Cancel() {
            if (phase == Phase.Working) {
                try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
                return;
            }
            if (phase == Phase.Ready || phase == Phase.Failed) {
                Discard();
            }
            phase = Phase.Idle;
        }

        private static void Run(ModSyncPlan plan, long cap, CancellationToken token) {
            try {
                ModSyncPackage.DeleteDirectory(StagingRoot);
                Directory.CreateDirectory(StagingRoot);

                List<ModSyncPackage.Staged> staged = new List<ModSyncPackage.Staged>();
                int count = plan.Installs.Count;
                for (int i = 0; i < count; i++) {
                    token.ThrowIfCancellationRequested();
                    ModSyncPlan.PackageInstall package = plan.Installs[i];
                    status = $"Downloading {package.Spec} ({i + 1} of {count})";
                    string url = ThunderstoreHttp.DownloadUrlFor(package.Owner, package.Name, package.Version);
                    using (MemoryStream archive = ThunderstoreHttp.DownloadCapped(url, cap, token, out string why)) {
                        if (archive == null) {
                            Fail($"Could not download {package.Spec}: {why}.");
                            return;
                        }
                        status = $"Checking {package.Spec} ({i + 1} of {count})";
                        if (!ModSyncPackage.Stage(archive, package, StagingRoot, out ModSyncPackage.Staged result, out string bad)) {
                            Fail(bad + ".");
                            return;
                        }
                        staged.Add(result);
                        Logger.LogInfo($"Mod sync: {package.Spec} downloaded and matches the server's record.");
                    }
                }

                readyOps = BuildOps(plan, staged);
                status = "Ready";
                // Written last: the main thread reads readyOps once it sees Ready.
                phase = Phase.Ready;
            } catch (OperationCanceledException) {
                Discard();
                status = "Cancelled";
                phase = Phase.Idle;
                Logger.LogInfo("Mod sync: cancelled. Nothing was changed.");
            } catch (Exception e) {
                Logger.LogWarning($"Mod sync failed: {e}");
                Fail($"Something went wrong while preparing the files: {e.Message}.");
            }
        }

        private static void Fail(string why) {
            Discard();
            failure = why;
            phase = Phase.Failed;
            Logger.LogWarning($"Mod sync: {why} Nothing was changed.");
        }

        private static void Discard() {
            readyOps = null;
            try { ModSyncPackage.DeleteDirectory(StagingRoot); } catch (Exception e) { Logger.LogDebug($"Could not clear the mod sync staging folder: {e.Message}"); }
        }

        /// <summary>
        /// The file moves for the helper. A copy of a plugin that lives inside a folder about to be replaced is
        /// not renamed separately: the whole folder is moved aside, which already takes it out of BepInEx's sight.
        /// </summary>
        private static ModSyncRestart.Ops BuildOps(ModSyncPlan plan, List<ModSyncPackage.Staged> staged) {
            ModSyncRestart.Ops ops = new ModSyncRestart.Ops { BackupDir = Path.Combine(BackupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss")) };
            List<string> targets = staged.Select(s => Path.Combine(Paths.PluginPath, s.Package.FolderName)).ToList();
            HashSet<string> renamed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            IEnumerable<string> outgoing = staged.SelectMany(s => s.Package.ReplacedDlls).Concat(plan.Disables.Select(d => d.DllPath));
            foreach (string dll in outgoing) {
                if (targets.Any(target => ModSyncPlan.IsUnder(dll, target))) { continue; }
                if (renamed.Add(dll)) { ops.Disables.Add(dll); }
            }

            foreach (ModSyncPackage.Staged package in staged) {
                ops.Folders.Add(new KeyValuePair<string, string>(package.PluginsDir, Path.Combine(Paths.PluginPath, package.Package.FolderName)));
                if (package.ConfigDir == null) { continue; }
                foreach (string file in Directory.GetFiles(package.ConfigDir, "*", SearchOption.AllDirectories)) {
                    string relative = file.Substring(package.ConfigDir.TrimEnd('\\', '/').Length + 1);
                    ops.Configs.Add(new KeyValuePair<string, string>(file, Path.Combine(Paths.ConfigPath, relative)));
                }
            }
            return ops;
        }

        /// <summary>
        /// Starts the helper that applies the staged changes once the game is gone, then quits. Returns false with
        /// a reason, and leaves the game running, if the helper could not be started.
        /// </summary>
        internal static bool Restart(out string why) {
            why = null;
            if (phase != Phase.Ready || readyOps == null) {
                why = "nothing is staged";
                return false;
            }
            string helper = ModSyncHelper.Extract(HelperDir, out why);
            if (helper == null) {
                Logger.LogWarning($"Mod sync: could not prepare the helper that applies the changes: {why}");
                return false;
            }

            Process self = Process.GetCurrentProcess();
            IEnumerable<string> args = Environment.GetCommandLineArgs().Skip(1);
            ModSyncRestart.Relaunch relaunch;
            if (ModSyncHelper.IsLinux) {
                relaunch = ModSyncRestart.Relaunch.FromProc();
            } else if (ModSyncHelper.IsMac) {
                // The executable inside Valheim.app, which is what the BepInEx launch script starts as well.
                relaunch = ModSyncRestart.Relaunch.ExplicitWithEnvironment(self.MainModule.FileName, args, Environment.CurrentDirectory, Environment.GetEnvironmentVariables());
            } else {
                relaunch = ModSyncRestart.Relaunch.Explicit(self.MainModule.FileName, args, Environment.CurrentDirectory);
            }
            string plan = ModSyncRestart.BuildPlan(self.Id, readyOps, StagingRoot, LogPath, relaunch, rejoinAddress);
            if (!ModSyncRestart.Launch(helper, PlanPath, plan, out why)) {
                Logger.LogWarning($"Mod sync: could not start the helper that applies the changes: {why}");
                return false;
            }
            Logger.LogInfo($"Mod sync: closing Valheim to apply {readyOps.Folders.Count} install(s) and {readyOps.Disables.Count} rename(s); it starts again afterwards{(rejoinAddress != null ? " and rejoins " + rejoinAddress : "")}.");
            Application.Quit();
            return true;
        }

        /// <summary>
        /// At startup: writes what the last run's helper did into this session's log, and clears anything a run
        /// left staged but never applied.
        /// </summary>
        internal static void ReportLastRun() {
            try {
                foreach (string log in new[] { PlanErrorPath, LogPath }) {
                    if (!File.Exists(log)) { continue; }
                    foreach (string line in File.ReadAllLines(log)) {
                        if (line.StartsWith("FAILED", StringComparison.Ordinal) || line.StartsWith("Valheim did not exit", StringComparison.Ordinal)) {
                            Logger.LogWarning($"Mod sync: {line.Trim()}");
                        } else {
                            Logger.LogInfo($"Mod sync: {line.Trim()}");
                        }
                    }
                    File.Delete(log);
                }
                if (Directory.Exists(StagingRoot)) {
                    Logger.LogWarning("Mod sync: files staged by an earlier session were never applied; removing them.");
                    ModSyncPackage.DeleteDirectory(StagingRoot);
                }
                if (Directory.Exists(BackupRoot)) {
                    // Named by date, so name order is age order.
                    List<string> backups = Directory.GetDirectories(BackupRoot).OrderBy(d => d, StringComparer.Ordinal).ToList();
                    for (int i = 0; i < backups.Count - KeepBackups; i++) {
                        ModSyncPackage.DeleteDirectory(backups[i]);
                    }
                }
            } catch (Exception e) {
                Logger.LogWarning($"Mod sync: could not read what the last run did: {e.Message}");
            }
        }

        /// <summary>A new connection attempt starts with no offer; the next server has to make its own.</summary>
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Awake))]
        public static class ZNet_Awake_ClearOffer {
            [HarmonyPrefix]
            private static void Prefix() {
                ClearOffer();
            }
        }
    }
}
