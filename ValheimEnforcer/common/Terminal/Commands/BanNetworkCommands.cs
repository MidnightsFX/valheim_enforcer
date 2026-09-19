using System;
using System.Collections.Generic;
using ValheimEnforcer.modules.bannetwork;

namespace ValheimEnforcer.common {

    internal static partial class TerminalManager {

        private static void RegisterBanNetworkCommands() {
            _ = new EnforcerCommand("enforcer-ban-network-status",
                "Reports what this server's ban network connection is doing: whether the key is accepted, how many entries are held, when the last and next pull are, and what went wrong if anything did. The first thing to run when the ban network is not behaving.",
                BanNetworkStatus, CommandArea.Bans,
                serverAuthoritative: true, requiresAdmin: true,
                aliases: "Enforcer-Ban-Network");

            _ = new EnforcerCommand("enforcer-ban-network-sync",
                "Talks to the ban network now instead of waiting for the next scheduled cycle. Format: [pull|push|both] - default both. Also clears a rejected or revoked key state and any backoff, so a freshly pasted key can be tested without restarting the server.",
                BanNetworkSync, CommandArea.Bans, SyncOptions,
                serverAuthoritative: true, requiresAdmin: true);

            _ = new EnforcerCommand("enforcer-ban-network-purge",
                "Discards the cached ban network list and resets the cursor, so the next pull rebuilds it from scratch. Your own bans and overrides are untouched. Requires --confirm. Format: enforcer-ban-network-purge --confirm",
                BanNetworkPurge, CommandArea.Bans, PurgeOptions,
                serverAuthoritative: true, requiresAdmin: true);
        }

        private static void BanNetworkStatus(EnforcerCommandArgs args) {
            if (ValConfig.EnableBanNetwork == null || !ValConfig.EnableBanNetwork.Value) {
                args.Output.Info("Ban network: off. Set EnableBanNetwork to true to take part.");
                args.Output.Detail("  Your own bans and overrides work regardless; this only affects the shared list.", log: false);
                return;
            }

            args.Output.Info($"Ban network: {KeyStates.Describe(BanNetworkState.Key, BanNetworkState.KeyNote)}");

            BanApiKey.Shape shape = BanApiKey.Read(out _, out string serverId);
            switch (shape) {
                case BanApiKey.Shape.Missing:
                    args.Output.Detail($"  No key yet. Put one in {BanApiKey.FilePath}", log: false);
                    args.Output.Detail("  Keys are issued by a person after reviewing a registration.", log: false);
                    break;
                case BanApiKey.Shape.Malformed:
                    args.Output.Detail($"  The key in {BanApiKey.FilePath} is not the right shape - expected vebn_<8 chars>_<43 chars>.", log: false);
                    break;
                default:
                    // The server id and nothing else: it identifies the registration and authorises nothing.
                    args.Output.Detail($"  Server id: {serverId}", log: false);
                    break;
            }

            args.Output.Detail($"  Entries held: {BanNetworkStore.Count}  (cursor {BanNetworkState.Cursor})", log: false);
            args.Output.Detail($"  Reporting: {DescribeReporting()}", log: false);
            if (BanOutbox.Count > 0) {
                args.Output.Detail($"  Queued to publish: {BanOutbox.Count} report(s)", log: false);
            }
            args.Output.Detail($"  Enforcing: {DescribeEnforced()}, minimum {ValConfig.BanNetworkMinReporters.Value} reporting server(s), action {ValConfig.BanNetworkAction.Value}", log: false);
            args.Output.Detail($"  Known account ids indexed: {BanSubjectCache.Count}", log: false);

            args.Output.Detail(BanNetworkState.LastPullUtc.HasValue
                ? $"  Last pull: {BanTime.Stamp(BanNetworkState.LastPullUtc.Value)} UTC"
                : "  Last pull: never", log: false);

            if (BanNetworkState.NextAttemptUtc.HasValue && BanNetworkState.NextAttemptUtc.Value > DateTime.UtcNow) {
                args.Output.Detail($"  Backing off after {BanNetworkState.ConsecutiveFailures} failure(s); next attempt {BanTime.Stamp(BanNetworkState.NextAttemptUtc.Value)} UTC", log: false);
            } else if (BanNetworkScheduler.NextPullLocal.HasValue) {
                args.Output.Detail($"  Next pull: about {BanTime.Stamp(BanNetworkScheduler.NextPullLocal.Value)} UTC", log: false);
            }

            if (!string.IsNullOrEmpty(BanNetworkState.LastError)) {
                args.Output.Warning($"  Last error: {BanNetworkState.LastError}");
            }
            if (KeyStates.Terminal(BanNetworkState.Key)) {
                args.Output.Warning("  No further requests will be made until this is resolved. Fix the key and run enforcer-ban-network-sync.");
                args.Output.Detail("  Entries already pulled are kept and still enforced.", log: false);
            }
        }

        private static void BanNetworkSync(EnforcerCommandArgs args) {
            if (ValConfig.EnableBanNetwork == null || !ValConfig.EnableBanNetwork.Value) {
                args.Output.Error("The ban network is off. Set EnableBanNetwork to true first.");
                return;
            }

            // Clearing the stored state is the whole reason to run this after fixing a key: without it a
            // rejected key stays rejected until the next restart, and the admin has no way to test the fix.
            if (KeyStates.Terminal(BanNetworkState.Key)) {
                args.Output.Info($"Clearing the previous '{BanNetworkState.Key}' state and trying again.");
            }
            BanNetworkState.Retry();

            if (!BanNetworkScheduler.Running) {
                args.Output.Error("The ban network scheduler is not running. It starts with the server when EnableBanNetwork is on - restart, or check the log for a startup failure.");
                return;
            }

            string mode = (args.Args.GetString(0, "both") ?? "both").ToLowerInvariant();
            if (mode != "pull" && mode != "push" && mode != "both") {
                args.Output.Error($"'{mode}' is not one of pull, push or both.");
                return;
            }

            bool did = false;
            if (mode == "pull" || mode == "both") {
                if (BanNetworkScheduler.RequestCycle(force: true)) {
                    args.Output.Info("Fetching ban network entries now.");
                    did = true;
                } else {
                    args.Output.Warning("Could not start a pull - either one is already running, or there is no usable key.");
                }
            }
            if (mode == "push" || mode == "both") {
                if (BanOutbox.Count == 0) {
                    args.Output.Detail("Nothing is queued to publish.", log: false);
                } else if (BanNetworkScheduler.RequestPush(force: true)) {
                    args.Output.Info($"Publishing {BanOutbox.Count} queued report(s) now.");
                    did = true;
                } else {
                    args.Output.Warning("Could not publish - either a publish is already running, reporting is off, or there is no usable key.");
                }
            }

            if (did) {
                args.Output.Detail("Run enforcer-ban-network-status in a moment for the result.", log: false);
            } else {
                args.Output.Detail("enforcer-ban-network-status says what is in the way.", log: false);
            }
        }

        private static void BanNetworkPurge(EnforcerCommandArgs args) {
            if (!args.Has("--confirm")) {
                args.Output.Warning($"This discards all {BanNetworkStore.Count} cached ban network entr(ies) and resets the cursor; the next pull rebuilds from scratch.");
                args.Output.Detail("  Your own bans in Bans.yaml and your overrides are not touched.", log: false);
                args.Output.Detail("  Run again with --confirm to go ahead.", log: false);
                return;
            }

            int had = BanNetworkStore.Count;
            BanNetworkStore.Purge();
            BanNetworkState.ResetCursor();
            args.Output.Info($"Discarded {had} cached ban network entr(ies) and reset the cursor. The next pull will rebuild the list.");
            BanNetworkScheduler.PullSoon();
        }

        private static string DescribeEnforced() {
            List<BanCategory> enforced = BanNetworkScheduler.EnforcedCategories();
            string canonical = BanCategories.Canonical(enforced);
            return string.IsNullOrEmpty(canonical) ? "nothing (BanNetworkEnforceCategories is empty)" : canonical;
        }

        private static string DescribeReporting() {
            if (ValConfig.BanNetworkReportBans == null || !ValConfig.BanNetworkReportBans.Value) {
                return "off - this server pulls the list without publishing to it";
            }
            string auto = ValConfig.BanNetworkReportAutoCheatBans != null && ValConfig.BanNetworkReportAutoCheatBans.Value
                ? "admin bans and automatic detections"
                : "admin bans only";
            string names = ValConfig.BanNetworkSharePlayerName == null || ValConfig.BanNetworkSharePlayerName.Value
                ? "with character names"
                : "without character names";
            return $"{auto}, {names}, up to {ValConfig.BanNetworkMaxReportsPerHour.Value}/hour";
        }

        private static List<string> SyncOptions(string[] input) {
            return input.Length <= 2 ? new List<string> { "both", "pull", "push" } : new List<string>();
        }

        private static List<string> PurgeOptions(string[] input) {
            return input.Length <= 2 ? new List<string> { "--confirm" } : new List<string>();
        }
    }
}
