using System;
using System.IO;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.recovery {

    /// <summary>
    /// How the server knows it crashed: a marker written when it stops cleanly, and found missing when it
    /// did not.
    ///
    /// This is the whole basis for automatic restoration. A snapshot handed back by a client is only adopted
    /// while the recovery window is open, and the window only opens when the last shutdown left no marker -
    /// so on an ordinary restart nothing a client offers is ever taken, however well it verifies. That is
    /// what keeps "the server can accept a newer copy of your character" from being a permanent facility
    /// somebody could learn to aim.
    ///
    /// Written after the character store has flushed, never before. A marker claiming a clean stop while a
    /// save is still queued would close the window over exactly the data it exists to recover.
    /// </summary>
    internal static class CrashMarker {

        internal const string FileName = "recovery-state.yaml";

        /// <summary>The on-disk shape. Its own type rather than the shared DataObjects because nothing else
        /// reads it and it never crosses the wire.</summary>
        internal class State {
            public bool CleanShutdown { get; set; }
            public string LastShutdownUtc { get; set; }
            public long WorldUid { get; set; }
            public string WorldName { get; set; }
        }

        private static string Path() {
            return System.IO.Path.Combine(ValConfig.GetSecondaryConfigDirectoryPath(), FileName);
        }

        // ---- the window ------------------------------------------------------------------------------

        private static bool windowOpen;
        private static DateTime windowOpenedUtc;
        private static string windowReason;
        private static bool checkedThisSession;

        /// <summary>True while verified, newer snapshots may be adopted without an admin saying so.</summary>
        internal static bool WindowOpen {
            get {
                if (!windowOpen) { return false; }
                int minutes = ValConfig.CrashRecoveryWindowMinutes != null ? ValConfig.CrashRecoveryWindowMinutes.Value : 30;
                if (DateTime.UtcNow - windowOpenedUtc >= TimeSpan.FromMinutes(Math.Max(1, minutes))) {
                    Close("the recovery window ran out");
                    return false;
                }
                return true;
            }
        }

        internal static string WindowReason {
            get { return windowReason; }
        }

        /// <summary>How long the window has left, for the status command. Zero when it is shut.</summary>
        internal static TimeSpan WindowRemaining {
            get {
                if (!windowOpen) { return TimeSpan.Zero; }
                int minutes = ValConfig.CrashRecoveryWindowMinutes != null ? ValConfig.CrashRecoveryWindowMinutes.Value : 30;
                TimeSpan left = TimeSpan.FromMinutes(Math.Max(1, minutes)) - (DateTime.UtcNow - windowOpenedUtc);
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }

        internal static void Close(string reason) {
            if (!windowOpen) { return; }
            windowOpen = false;
            windowReason = null;
            Logger.LogInfo($"Crash-recovery window closed: {reason}. Snapshots offered from now on need an admin to accept them.");
        }

        // ---- server lifecycle ------------------------------------------------------------------------

        /// <summary>
        /// Server start. Reads the marker the previous run left, decides whether this is a restart after a
        /// crash, and immediately writes the marker back as "not clean" so a hard kill during THIS session is
        /// caught by the next one.
        ///
        /// Gated on the feature's own setting, so a server that never enables crash recovery never grows a
        /// marker file. The cost of that is one session: the first start after an admin switches this on
        /// finds no marker, reads it as a first run rather than a crash, and opens no window. From the
        /// second start onwards it answers properly.
        /// </summary>
        internal static void OnServerStart() {
            if (checkedThisSession) { return; }
            if (ValConfig.EnableCrashRecovery == null || !ValConfig.EnableCrashRecovery.Value) { return; }
            checkedThisSession = true;

            State previous = Read();
            long worldUid = ZNet.instance != null ? ZNet.instance.GetWorldUID() : 0L;
            string worldName = ZNet.instance != null ? ZNet.instance.GetWorldName() : "";

            if (previous == null) {
                // No marker at all: a first run with this version, or a deleted config folder. Not treated as
                // a crash - opening a recovery window for a server that has simply never written one would
                // make the very first start after an upgrade the one session where snapshots are adopted
                // automatically, which is nobody's intent.
                Logger.LogInfo("No previous shutdown marker; assuming a first run rather than a crash. Crash recovery stays closed this session.");
            } else if (previous.CleanShutdown) {
                Logger.LogDebug($"The previous shutdown was clean ({previous.LastShutdownUtc}).");
            } else if (previous.WorldUid != 0L && worldUid != 0L && previous.WorldUid != worldUid) {
                // Same install, different world. Whatever went wrong last time belongs to that one.
                Logger.LogInfo($"The previous run did not shut down cleanly, but it was hosting a different world ('{previous.WorldName}'). Crash recovery stays closed.");
            } else {
                Open($"the previous run of this server did not shut down cleanly");
            }

            Write(new State {
                CleanShutdown = false,
                LastShutdownUtc = null,
                WorldUid = worldUid,
                WorldName = worldName,
            });
        }

        /// <summary>Opens the window. Also reachable from the admin command, for a rollback the marker could
        /// not have noticed - a restore from a backup, or a host that was reverted underneath the game.</summary>
        internal static void Open(string reason) {
            windowOpen = true;
            windowOpenedUtc = DateTime.UtcNow;
            windowReason = reason;
            int minutes = ValConfig.CrashRecoveryWindowMinutes != null ? ValConfig.CrashRecoveryWindowMinutes.Value : 30;
            Logger.LogWarning($"Crash-recovery window OPEN for up to {minutes} minute(s): {reason}. A snapshot a client hands back is adopted when it verifies and is newer than the save held here. Every one is logged.");
        }

        /// <summary>
        /// Server shutdown, after the character store has flushed. Anything queued at this point is on disk,
        /// so "clean" is true rather than hopeful.
        /// </summary>
        internal static void OnServerShutdown() {
            if (!checkedThisSession) { return; } // never started as a server; nothing of ours to record
            checkedThisSession = false;
            windowOpen = false;
            windowReason = null;
            try {
                Write(new State {
                    CleanShutdown = true,
                    LastShutdownUtc = DateTime.UtcNow.ToString("u"),
                    WorldUid = ZNet.instance != null ? ZNet.instance.GetWorldUID() : 0L,
                    WorldName = ZNet.instance != null ? ZNet.instance.GetWorldName() : "",
                });
            } catch (Exception e) {
                // A failure here means the next start believes this one crashed. That is the harmless
                // direction - it opens a window nobody needs rather than closing one somebody does.
                Logger.LogWarning($"Could not record a clean shutdown: {e.Message}. The next start will treat this as a crash.");
            }
        }

        // ---- file ------------------------------------------------------------------------------------

        internal static State Read() {
            string path = Path();
            try {
                if (!File.Exists(path)) { return null; }
                string text = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(text)) { return null; }
                return DataObjects.yamldeserializer.Deserialize<State>(text);
            } catch (Exception e) {
                // Unreadable is not "clean". Reporting a crash over an unparseable marker opens a window that
                // costs nothing; reporting clean would close one that might be needed.
                Logger.LogWarning($"Could not read {FileName} ({e.Message}); treating the previous shutdown as unclean.");
                return new State { CleanShutdown = false };
            }
        }

        private static void Write(State state) {
            AtomicFile.WriteYaml(Path(), state, DataObjects.yamlserializer);
        }
    }
}
