using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace ValheimEnforcer.modules.modsync {

    /// <summary>
    /// Hands staged mod changes to the native helper (<see cref="ModSyncHelper"/>) to apply after Valheim exits.
    ///
    /// The changes cannot be made from inside the game: every loaded plugin DLL is locked for as long as the
    /// process lives, and a plugin being replaced is by definition loaded. So the game writes a plan, starts the
    /// helper with it, and quits; the helper waits for the game's process to be gone, moves the files, and starts
    /// the game again.
    ///
    /// The plan format is defined in tools/modsync-helper/src/plan.rs. Building its text is kept apart from
    /// launching anything so it can be checked without starting a process.
    /// </summary>
    internal static class ModSyncRestart {

        /// <summary>Everything the helper does, in the order it does it.</summary>
        internal sealed class Ops {
            /// <summary>DLL -> DLL.old. The player's copies being turned off.</summary>
            internal readonly List<string> Disables = new List<string>();
            /// <summary>Staged folder -> BepInEx/plugins/Owner-Name, moving any existing folder aside first.</summary>
            internal readonly List<KeyValuePair<string, string>> Folders = new List<KeyValuePair<string, string>>();
            /// <summary>Staged config file -> BepInEx/config, only where no such file exists.</summary>
            internal readonly List<KeyValuePair<string, string>> Configs = new List<KeyValuePair<string, string>>();
            /// <summary>Where replaced plugin folders are moved to rather than deleted.</summary>
            internal string BackupDir;
        }

        /// <summary>How the helper starts the game again.</summary>
        internal sealed class Relaunch {
            internal string Mode;
            internal string Exe;
            internal List<string> Args = new List<string>();
            internal string Cwd;
            /// <summary>Written into the plan when set; otherwise the helper passes on its own environment.</summary>
            internal List<KeyValuePair<string, string>> Env;

            /// <summary>
            /// This program, these arguments. Windows and Proton: Doorstop there is a proxy DLL beside the game,
            /// configured by its ini file or by arguments, so the command line is all that has to come back.
            /// </summary>
            internal static Relaunch Explicit(string exe, IEnumerable<string> args, string cwd) {
                return new Relaunch { Mode = "explicit", Exe = exe, Args = new List<string>(args), Cwd = cwd };
            }

            /// <summary>
            /// The same, plus the game's environment written into the plan. macOS: Doorstop is loaded there through
            /// DYLD_INSERT_LIBRARIES and DOORSTOP_* variables, which have to reach the relaunched game - but the
            /// helper itself is started without DYLD_INSERT_LIBRARIES (see <see cref="Launch"/>), so it cannot
            /// simply pass its own environment on.
            /// </summary>
            internal static Relaunch ExplicitWithEnvironment(string exe, IEnumerable<string> args, string cwd, IDictionary environment) {
                Relaunch relaunch = Explicit(exe, args, cwd);
                relaunch.Env = new List<KeyValuePair<string, string>>();
                foreach (DictionaryEntry entry in environment) {
                    string key = entry.Key as string;
                    if (string.IsNullOrEmpty(key) || key.IndexOf('=') >= 0) { continue; }
                    relaunch.Env.Add(new KeyValuePair<string, string>(key, entry.Value as string ?? ""));
                }
                relaunch.Env.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
                return relaunch;
            }

            /// <summary>
            /// Linux: the helper reads the game's own executable, command line, environment and working directory
            /// out of /proc before the game exits. Doorstop there is LD_PRELOAD and DOORSTOP_* variables set by a
            /// launch script or mod manager, so the environment has to come back exactly too.
            /// </summary>
            internal static Relaunch FromProc() {
                return new Relaunch { Mode = "proc" };
            }
        }

        // How long the helper waits for the game to exit before giving up without changing anything. Valheim
        // saves on the way out, so this is generous; a game still running after it is hung, and moving files
        // out from under a hung process is worse than leaving them.
        internal const int ExitWaitSeconds = 180;

        internal static string BuildPlan(int pid, Ops ops, string stagingRoot, string logPath, Relaunch relaunch, string rejoinAddress, int exitWaitSeconds = ExitWaitSeconds) {
            StringBuilder plan = new StringBuilder();
            Line(plan, "ve-modsync", "1");
            Line(plan, "pid", pid.ToString(CultureInfo.InvariantCulture));
            Line(plan, "timeout", exitWaitSeconds.ToString(CultureInfo.InvariantCulture));
            Line(plan, "log", logPath);
            foreach (string dll in ops.Disables) { Line(plan, "disable", dll); }
            foreach (KeyValuePair<string, string> folder in ops.Folders) { Line(plan, "replace", folder.Key, folder.Value, ops.BackupDir); }
            foreach (KeyValuePair<string, string> config in ops.Configs) { Line(plan, "copy-new", config.Key, config.Value); }
            Line(plan, "cleanup", stagingRoot);

            Line(plan, "relaunch", relaunch == null ? "none" : relaunch.Mode);
            if (relaunch != null && relaunch.Mode == "explicit") {
                Line(plan, "exe", relaunch.Exe);
                foreach (string arg in relaunch.Args) { Line(plan, "arg", arg); }
                if (!string.IsNullOrEmpty(relaunch.Cwd)) { Line(plan, "cwd", relaunch.Cwd); }
                if (relaunch.Env != null) {
                    foreach (KeyValuePair<string, string> variable in relaunch.Env) { Line(plan, "env", variable.Key, variable.Value); }
                }
            }
            // Whatever join the game was started with goes; the join back to the server that asked for these mods
            // replaces it, when there is one to express.
            Line(plan, "drop-arg-pair", "+connect");
            Line(plan, "drop-arg-pair", "+connect_lobby");
            if (!string.IsNullOrEmpty(rejoinAddress)) {
                Line(plan, "append-arg", "+connect");
                Line(plan, "append-arg", rejoinAddress);
            }
            return plan.ToString();
        }

        private static void Line(StringBuilder plan, params string[] fields) {
            for (int i = 0; i < fields.Length; i++) {
                if (i > 0) { plan.Append('\t'); }
                plan.Append(Escape(fields[i]));
            }
            plan.Append('\n');
        }

        /// <summary>Backslash first, so the escapes added after it are not themselves escaped again.</summary>
        internal static string Escape(string field) {
            return (field ?? "").Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        /// <summary>
        /// Writes the plan and starts the helper on it. The caller quits the game straight afterwards; the helper
        /// is waiting for exactly that.
        ///
        /// The helper inherits this process's environment, less DYLD_INSERT_LIBRARIES. That is how Doorstop is
        /// loaded on a Mac, and the helper is the same x86_64 as the Doorstop library, so it would be injected into
        /// the helper too; the Mac plan carries the full environment for the relaunched game instead. Doorstop's
        /// runtime markers are the helper's to remove, and on Linux it reads the game's environment from /proc.
        /// </summary>
        internal static bool Launch(string helperPath, string planPath, string plan, out string failure) {
            failure = null;
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(planPath));
                File.WriteAllText(planPath, plan, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                ProcessStartInfo start = new ProcessStartInfo(helperPath, "\"" + planPath + "\"") {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(planPath),
                };
                start.EnvironmentVariables.Remove("DYLD_INSERT_LIBRARIES");
                Process.Start(start);
                return true;
            } catch (Exception e) {
                failure = e.Message;
                return false;
            }
        }
    }
}
