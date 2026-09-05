using Jotunn.Managers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ValheimEnforcer.modules.audit;
using ValheimEnforcer.modules.character;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.common {
    internal static partial class TerminalManager {

        /// <summary>How many history lines one report prints before it says it stopped.</summary>
        private const int AuditHistoryCap = 300;

        private static void RegisterAuditCommands() {
            _ = new EnforcerCommand("enforcer-audit-inventory",
                "Shows what the server has stored for a character: every item, its quality and who crafted it. This is the last synced save, not a live read. Format: <accountId> <characterName>. eg: enforcer-audit-inventory 76561198012345678 Bjorn",
                AuditInventory, CommandArea.Audit, AccountThenCharacter,
                serverAuthoritative: true, requiresAdmin: true);

            _ = new EnforcerCommand("enforcer-audit-history",
                "Timeline of what a player gained, lost, took from containers and stored, from the audit log. Format: <accountId> <characterName> [minutes] [all|items|containers|damage]. Defaults to the last 60 minutes. eg: enforcer-audit-history 76561198012345678 Bjorn 120 containers",
                AuditHistory, CommandArea.Audit, AuditHistoryOptions,
                serverAuthoritative: true, requiresAdmin: true);

            _ = new EnforcerCommand("enforcer-audit-damage",
                "Live damage summary for everyone currently fighting, largest total first. Shows the damage each player's client CLAIMED before the victim applied armour and resistances. Format: [characterName] [seconds]. eg: enforcer-audit-damage Bjorn",
                AuditDamageReport, CommandArea.Audit, AuditDamageOptions,
                serverAuthoritative: true, requiresAdmin: true);

            // The two below are deliberately NOT server-authoritative: their whole point is a direct
            // request/response with the server that carries a file rather than console lines, so the command
            // body runs here and talks to the audit RPC. The admin check still happens on the server.
            _ = new EnforcerCommand("enforcer-audit-available",
                "Asks the server what audit history it still holds for a character, day by day. Format: <accountId> <characterName>. eg: enforcer-audit-available 76561198012345678 Bjorn",
                AuditAvailable, CommandArea.Audit, AccountThenCharacter,
                serverAuthoritative: false, requiresAdmin: true);

            _ = new EnforcerCommand("enforcer-audit-download",
                "Downloads a character's recorded history to this machine, under BepInEx/config/ValheimEnforcer/AuditDownloads. Note the copy is yours to keep and is not covered by the server's retention window. Format: <accountId> <characterName> [days | fromDate [toDate]]. Defaults to the last 7 days. eg: enforcer-audit-download 76561198012345678 Bjorn 3",
                AuditDownload, CommandArea.Audit, AccountThenCharacter,
                serverAuthoritative: false, requiresAdmin: true);
        }

        private static List<string> AuditHistoryOptions(string[] input) {
            if (input.Length == 4) { return new List<string>() { "15", "60", "180", "1440" }; }
            if (input.Length == 5) { return AuditEvent.Kinds.Filters.ToList(); }
            return AccountThenCharacter(input);
        }

        /// <summary>
        /// Completes on who is actually connected, not on who has a save. This command reports a live window,
        /// so an offline character is never a useful suggestion.
        /// </summary>
        private static List<string> AuditDamageOptions(string[] input) {
            if (input.Length > 2) { return new List<string>(); }
            try {
                List<string> names = new List<string>();
                if (ZNet.instance == null) { return names; }
                foreach (ZNetPeer peer in ZNet.instance.GetPeers()) {
                    if (peer != null && peer.IsReady() && !string.IsNullOrEmpty(peer.m_playerName)) { names.Add(peer.m_playerName); }
                }
                return names;
            } catch (Exception) {
                return new List<string>();
            }
        }

        /// <summary>
        /// Every audit command opens with this. An admin whose report comes back empty needs to know whether
        /// nothing happened or nothing was ever recorded - those are completely different answers.
        /// </summary>
        private static bool AuditEnabled(EnforcerCommandArgs args) {
            if (ValConfig.EnableAuditLog != null && ValConfig.EnableAuditLog.Value) { return true; }
            args.Output.Error("The audit log is off. Set EnableAuditLog to true in the Audit section of the config to start recording; nothing before that point exists.");
            return false;
        }

        // ---- Inventory ------------------------------------------------------------------------------------

        private static void AuditInventory(EnforcerCommandArgs args) {
            string usage = "Format: enforcer-audit-inventory <accountId> <characterName>";
            if (!ReadTarget(args, usage, out string account, out string name)) { return; }

            DataObjects.Character character = ValConfig.LoadCharacterFromSave(account, name);
            if (character == null) {
                args.Output.Error($"Could not read the save for {name} under account {account}.");
                return;
            }

            List<PackedItem> items = character.PlayerItems ?? new List<PackedItem>();
            if (items.Count == 0) {
                args.Output.Info($"{name} has nothing in their stored inventory.");
                return;
            }

            int noCrafter = 0;
            foreach (IGrouping<string, PackedItem> group in items.GroupBy(item => item.prefabName)
                                                                 .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)) {
                int total = group.Sum(item => item.m_stack);
                // Quality and crafter are what an investigation actually reads: a legendary-quality item
                // nobody crafted is the shape spawned gear has.
                string qualities = string.Join(", ", group.Select(item => item.m_quality).Distinct().OrderBy(q => q).Select(q => q.ToString()));
                string crafters = string.Join(", ", group.Select(Crafter).Distinct().OrderBy(c => c, StringComparer.OrdinalIgnoreCase));
                noCrafter += group.Count(item => string.IsNullOrEmpty(item.m_crafterName) && item.m_crafterID == 0);
                string equipped = group.Any(item => item.m_equipped) ? " [equipped]" : "";
                args.Output.Detail($"  {group.Key} x{total} (quality {qualities}; {crafters}){equipped}", log: false);
            }

            // Said plainly rather than implied. The server holds what the client last sent it, and an admin
            // acting on a stale list is the failure mode worth spending a line on.
            args.Output.Info($"{name} has {items.Count} stored item entr{(items.Count == 1 ? "y" : "ies")}, {noCrafter} with no crafter recorded. "
                           + $"Last known session state: {character.LastDisconnect}. This is the server's stored copy as of the last sync, not a live read of their inventory.");
        }

        private static string Crafter(PackedItem item) {
            if (!string.IsNullOrEmpty(item.m_crafterName)) { return $"crafted by {item.m_crafterName}"; }
            return item.m_crafterID == 0 ? "no crafter" : $"crafter id {item.m_crafterID}";
        }

        // ---- History --------------------------------------------------------------------------------------

        private static void AuditHistory(EnforcerCommandArgs args) {
            string usage = "Format: enforcer-audit-history <accountId> <characterName> [minutes] [all|items|containers|damage]";
            if (!AuditEnabled(args)) { return; }
            if (!args.ReadAccount(0, usage, out string account)) { return; }
            if (!args.ReadName(1, usage, out string name)) { return; }

            int minutes = args.Args.GetInt(2, 60);
            if (minutes <= 0) {
                args.Output.Error($"Minutes must be a positive number. {usage}");
                return;
            }
            string filter = args.Args.GetString(3, "all");
            string[] kinds = AuditEvent.Kinds.For(filter);
            if (kinds != null && kinds.Length == 0) {
                args.Output.Error($"Unknown filter '{filter}'. Use one of: {string.Join(", ", AuditEvent.Kinds.Filters)}.");
                return;
            }

            DateTime to = DateTime.UtcNow;
            DateTime from = to.AddMinutes(-minutes);

            // Reading day files is disk work in the megabytes and this command runs on the server's main
            // thread. It goes to the audit thread - which also owns those files, so a report cannot catch a
            // flush half way through one - and the output sink is written to when the answer comes back.
            // TerminalOutput is built for exactly this: StructureSweep hands its sink to a coroutine the
            // same way, and Flush is safe to call after the peer has gone.
            TerminalOutput output = args.Output;
            AuditLog.SubmitJob(() => {
                List<AuditEvent> events = AuditLog.Query(account, name, from, to, kinds, AuditHistoryCap, out bool capped);
                AuditLog.QueueMainThread(() => {
                    if (events.Count == 0) {
                        output.Info($"Nothing recorded for {name} in the last {minutes} minute(s) matching '{filter}'. "
                                  + "Check enforcer-audit-available if you expected older activity.");
                        output.Flush();
                        return;
                    }

                    foreach (AuditEvent entry in events) {
                        output.Detail($"  {entry.T}  {entry.Describe()}", log: false);
                    }

                    // A truncated report that looks complete is worse than no report, never left implied.
                    string truncated = capped
                        ? $" Only the most recent {AuditHistoryCap} are shown - narrow the window or use enforcer-audit-download for the whole range."
                        : "";
                    output.Info($"{events.Count} event(s) for {name} over the last {minutes} minute(s), filter '{filter}'.{truncated}");
                    output.Flush();
                });
            });
        }

        // ---- Damage ---------------------------------------------------------------------------------------

        private static void AuditDamageReport(EnforcerCommandArgs args) {
            if (!AuditEnabled(args)) { return; }

            string wanted = args.Args.GetString(0, null);
            List<DamageAudit.Summary> live = DamageAudit.Live();
            if (!string.IsNullOrEmpty(wanted)) {
                live = live.Where(summary => string.Equals(summary.Character, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (live.Count == 0) {
                args.Output.Info(string.IsNullOrEmpty(wanted)
                    ? "Nobody has dealt damage inside the current window."
                    : $"{wanted} has dealt no damage inside the current window.");
                return;
            }

            foreach (DamageAudit.Summary summary in live) {
                args.Output.Detail(
                    $"  {summary.Character} ({summary.Account}): {summary.Total:G6} over {summary.WindowSeconds}s "
                  + $"from {summary.Hits} hit(s), {summary.PerSecond:F1}/s, biggest {summary.MaxHit:G6}"
                  + (string.IsNullOrEmpty(summary.MaxTarget) ? "" : $" on {summary.MaxTarget}"), log: false);
            }

            args.Output.Info($"{live.Count} player(s) fighting. These are the pre-mitigation totals each client CLAIMED, "
                           + "before the victim applied armour, resistances and difficulty scaling - use them to spot the impossible, not to compare builds.");
        }

        // ---- Available / download -------------------------------------------------------------------------

        private static void AuditAvailable(EnforcerCommandArgs args) {
            string usage = "Format: enforcer-audit-available <accountId> <characterName>";
            if (!AuditEnabled(args)) { return; }
            if (!args.ReadAccount(0, usage, out string account)) { return; }
            if (!args.ReadName(1, usage, out string name)) { return; }

            if (RunsHere()) {
                ListLocally(args, account, name);
                return;
            }
            if (!SendAuditRequest(args, AuditTransfer.ModeList, account, name, DateTime.UtcNow.Date, DateTime.UtcNow.Date)) { return; }
            args.Output.Info($"Asked the server what history it holds for {name}; its answer follows.");
        }

        private static void AuditDownload(EnforcerCommandArgs args) {
            string usage = "Format: enforcer-audit-download <accountId> <characterName> [days | fromDate [toDate]]";
            if (!AuditEnabled(args)) { return; }
            if (!args.ReadAccount(0, usage, out string account)) { return; }
            if (!args.ReadName(1, usage, out string name)) { return; }
            if (!ReadRange(args, usage, out DateTime from, out DateTime to)) { return; }

            if (RunsHere()) {
                DownloadLocally(args, account, name, from, to);
                return;
            }
            if (!SendAuditRequest(args, AuditTransfer.ModeDownload, account, name, from, to)) { return; }
            args.Output.Info($"Asked the server for {name}'s history from {AuditTransfer.Day(from)} to {AuditTransfer.Day(to)}; it is saved here when it arrives.");
        }

        /// <summary>
        /// Reads the optional range: a plain day count, or an explicit from/to pair. Defaults to a week,
        /// matching the default retention so "everything you have" is what an admin gets by typing nothing.
        /// </summary>
        private static bool ReadRange(EnforcerCommandArgs args, string usage, out DateTime from, out DateTime to) {
            to = DateTime.UtcNow.Date;
            from = to.AddDays(-6);

            string first = args.Args.GetString(2, null);
            if (string.IsNullOrEmpty(first)) { return true; }

            if (int.TryParse(first, out int days)) {
                if (days <= 0) {
                    args.Output.Error($"A day count must be positive. {usage}");
                    return false;
                }
                from = to.AddDays(-(days - 1));
                return true;
            }

            if (!AuditTransfer.TryDay(first, out from)) {
                args.Output.Error($"'{first}' is neither a day count nor a yyyy-MM-dd date. {usage}");
                return false;
            }
            string second = args.Args.GetString(3, null);
            if (!string.IsNullOrEmpty(second) && !AuditTransfer.TryDay(second, out to)) {
                args.Output.Error($"'{second}' is not a yyyy-MM-dd date. {usage}");
                return false;
            }
            if (to < from) { DateTime swap = from; from = to; to = swap; }
            return true;
        }

        /// <summary>
        /// True when this machine is the server, so the command answers directly instead of sending an RPC.
        /// Covers both a dedicated server console and a listen host - and the listen host is the reason this
        /// exists at all, because a host is not one of its own peers, so the request would have nobody to
        /// send to and the reply nobody to come back from.
        /// </summary>
        private static bool RunsHere() {
            return ZNet.instance != null && ZNet.instance.IsServer();
        }

        private static bool SendAuditRequest(EnforcerCommandArgs args, byte mode, string account, string name,
                                             DateTime from, DateTime to) {
            if (ZNet.instance == null) {
                args.Output.Error("You must be in a world to ask the server for audit history.");
                return false;
            }
            // For a clear message only; the server checks the sender itself and is the gate that counts.
            if (SynchronizationManager.Instance.PlayerIsAdmin == false) {
                args.Output.Error("Only server admins can request audit history.");
                return false;
            }
            ZNetPeer server = ZNet.instance.GetServerPeer();
            if (server == null) {
                args.Output.Error("No server connection, so the request cannot be sent.");
                return false;
            }

            // Remembered so the reply is filed under what THIS machine asked for rather than under anything
            // the server echoes back, and so the answer prints in the console the admin typed into.
            AuditTransfer.RememberRequest(account, name);
            ValConfig.AuditRequestRPC.SendPackage(server.m_uid, AuditTransfer.BuildRequest(mode, account, name, from, to));
            return true;
        }

        // Both of the local paths read - and in the download case write - whole day files. On a listen host
        // that is the game's own main thread, so the work goes to the audit thread and only the printing
        // comes back. A dedicated server console would survive doing it inline; a listen host would hitch.

        private static void ListLocally(EnforcerCommandArgs args, string account, string name) {
            TerminalOutput output = args.Output;
            AuditLog.SubmitJob(() => {
                List<AuditLog.DayInfo> days = AuditLog.AvailableDays();
                List<string> rows = new List<string>();
                long total = 0;
                foreach (AuditLog.DayInfo day in days) {
                    List<AuditEvent> events = AuditLog.Query(account, name, day.Day, day.Day.AddDays(1).AddTicks(-1), null, 0, out bool _);
                    total += events.Count;
                    rows.Add($"  {AuditTransfer.Day(day.Day)}   {events.Count,7} event(s)   {day.Bytes / 1024.0:F1} KB in the day file");
                }

                long found = total;
                AuditLog.QueueMainThread(() => {
                    if (rows.Count == 0) {
                        output.Info("This server holds no audit history yet.");
                    } else {
                        foreach (string row in rows) { output.Detail(row, log: false); }
                        output.Info($"{found} recorded event(s) for {name} across {rows.Count} day(s), keeping {ValConfig.AuditRetentionDays.Value} day(s).");
                    }
                    output.Flush();
                });
            });
        }

        private static void DownloadLocally(EnforcerCommandArgs args, string account, string name, DateTime from, DateTime to) {
            TerminalOutput output = args.Output;
            AuditLog.SubmitJob(() => {
                List<AuditEvent> events = AuditLog.Query(account, name, from, to.AddDays(1).AddTicks(-1), null, 0, out bool _);
                string path = null;
                if (events.Count > 0) {
                    string text = string.Join(Environment.NewLine, AuditLog.ToLines(events)) + Environment.NewLine;
                    path = AuditTransfer.WriteLocal(account, name, AuditTransfer.Day(from), AuditTransfer.Day(to), text);
                }

                int count = events.Count;
                string written = path;
                AuditLog.QueueMainThread(() => {
                    if (count == 0) {
                        output.Info($"Nothing recorded for {name} between {AuditTransfer.Day(from)} and {AuditTransfer.Day(to)}.");
                    } else if (written == null) {
                        output.Error("Could not write the history file; see the log.");
                    } else {
                        output.Info($"Wrote {count} event(s) for {name} to {written}. That copy is outside the server's retention window and will not be purged.");
                    }
                    output.Flush();
                });
            });
        }
    }
}
