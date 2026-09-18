using System;
using System.Collections.Generic;
using ValheimEnforcer.modules.recovery;

namespace ValheimEnforcer.common {
    internal static partial class TerminalManager {

        private static void RegisterRecoveryCommands() {
            _ = new EnforcerCommand("enforcer-recovery-status",
                "Reports whether the crash-recovery window is open, why, how long it has left, and which key this server is sealing snapshots with. Reads only. eg: enforcer-recovery-status",
                RecoveryStatus, CommandArea.Recovery,
                serverAuthoritative: true, requiresAdmin: true);

            _ = new EnforcerCommand("enforcer-recovery-open",
                "Opens a crash-recovery window by hand, for a rollback this server could not have noticed - a restore from a backup, or a host reverted underneath the game. Connected players are asked for their snapshots straight away; anyone else is asked when they join. Format: confirm. eg: enforcer-recovery-open confirm",
                RecoveryOpen, CommandArea.Recovery, ConfirmOnly,
                serverAuthoritative: true, requiresAdmin: true);

            _ = new EnforcerCommand("enforcer-recovery-close",
                "Closes the crash-recovery window now. Nothing a client hands back is accepted afterwards. eg: enforcer-recovery-close",
                RecoveryClose, CommandArea.Recovery,
                serverAuthoritative: true, requiresAdmin: true);

            _ = new EnforcerCommand("enforcer-recovery-key-rotate",
                "Replaces this server's recovery key. Every snapshot every client is currently holding becomes permanently unopenable, so do not run this while recovering from anything. Format: confirm. eg: enforcer-recovery-key-rotate confirm",
                RecoveryKeyRotate, CommandArea.Recovery, ConfirmOnly,
                serverAuthoritative: true, requiresAdmin: true);
        }

        private static List<string> ConfirmOnly(string[] input) {
            if (input.Length <= 2) { return new List<string>() { "confirm" }; }
            return new List<string>();
        }

        private static bool RecoveryEnabled(EnforcerCommandArgs args) {
            if (ValConfig.EnableCrashRecovery != null && ValConfig.EnableCrashRecovery.Value) { return true; }
            args.Output.Error("Crash recovery is off. Set EnableCrashRecovery to true first; nothing in this feature runs until it is on.");
            return false;
        }

        private static void RecoveryStatus(EnforcerCommandArgs args) {
            bool on = ValConfig.EnableCrashRecovery != null && ValConfig.EnableCrashRecovery.Value;
            args.Output.Detail($"  enabled:       {(on ? "yes" : "no")}", log: false);
            args.Output.Detail($"  auto-restore:  {(ValConfig.CrashRecoveryAutoRestore != null && ValConfig.CrashRecoveryAutoRestore.Value ? "yes" : "no")}", log: false);

            string keyId = RecoveryKey.KeyId;
            args.Output.Detail($"  key:           {(keyId ?? "not loaded")}", log: false);

            CrashMarker.State previous = CrashMarker.Read();
            if (previous == null) {
                args.Output.Detail("  last shutdown: no marker on disk (a first run, or the file was removed)", log: false);
            } else if (previous.CleanShutdown) {
                args.Output.Detail($"  last shutdown: clean, {previous.LastShutdownUtc}", log: false);
            } else {
                args.Output.Detail("  last shutdown: not clean, or this server is still running", log: false);
            }

            if (CrashMarker.WindowOpen) {
                TimeSpan left = CrashMarker.WindowRemaining;
                args.Output.Warning($"The recovery window is OPEN for another {left.TotalMinutes:F0} minute(s): {CrashMarker.WindowReason}. A snapshot handed back is adopted when it verifies and is newer than the save held here.");
            } else {
                args.Output.Info("The recovery window is closed; nothing a client hands back will be accepted.");
            }
        }

        private static void RecoveryOpen(EnforcerCommandArgs args) {
            if (!RecoveryEnabled(args)) { return; }
            if (CrashMarker.WindowOpen) {
                args.Output.Info($"The recovery window is already open: {CrashMarker.WindowReason}. {CrashMarker.WindowRemaining.TotalMinutes:F0} minute(s) left.");
                return;
            }
            if (!args.Has("confirm")) {
                args.Output.Warning("This lets connected players hand back a character from before whatever went wrong, and lets the server adopt it when it verifies and is newer than the save here. If the WORLD has also rolled back, anything they took out of it since may end up existing twice. Re-run with 'confirm' on the end to open it.");
                return;
            }
            CrashMarker.Open("an admin opened it by hand");
            args.Output.Info($"Recovery window open for {ValConfig.CrashRecoveryWindowMinutes.Value} minute(s). Connected players are being asked for their snapshots now; anyone who was offline is asked when they join. Watch the server log - every snapshot adopted is written there.");
        }

        private static void RecoveryClose(EnforcerCommandArgs args) {
            if (!CrashMarker.WindowOpen) {
                args.Output.Info("The recovery window is already closed.");
                return;
            }
            CrashMarker.Close("an admin closed it");
            args.Output.Info("Recovery window closed. Nothing a client hands back will be accepted.");
        }

        private static void RecoveryKeyRotate(EnforcerCommandArgs args) {
            if (!RecoveryEnabled(args)) { return; }
            if (!args.Has("confirm")) {
                args.Output.Warning($"Rotating the key makes every snapshot every client is currently holding permanently unopenable - including any you might be about to need. The current key is {RecoveryKey.KeyId ?? "not loaded"}. Re-run with 'confirm' on the end.");
                return;
            }
            if (CrashMarker.WindowOpen) {
                args.Output.Error("Not rotating the key while a recovery window is open - that would destroy exactly the snapshots the window exists to accept. Run enforcer-recovery-close first if you really mean it.");
                return;
            }
            if (!RecoveryKey.Rotate(out string newId)) {
                args.Output.Error("Could not rotate the recovery key; see the server log.");
                return;
            }
            args.Output.Info($"Recovery key rotated to {newId}. Every snapshot clients were holding is now unopenable; fresh ones go out at the next push interval.");
        }
    }
}
