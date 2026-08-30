using System;
using System.Collections.Generic;
using ValheimEnforcer.modules.network;

namespace ValheimEnforcer.common {
    internal static partial class TerminalManager {

        private static void RegisterNetworkCommands() {
            _ = new EnforcerCommand("enforcer-trust",
                "Lists connected players who have tripped a server-side guard this session, with what they declared at join. This is the report to read before acting on ContradictionAction - a guard trip says something does not add up, not who lied. Server admins only.",
                TrustList, CommandArea.Network,
                serverAuthoritative: true, requiresAdmin: true,
                aliases: "Enforcer-List-Contradictions");
        }

        private static void TrustList(EnforcerCommandArgs args) {
            if (ValConfig.ReportClientContradictions == null || !ValConfig.ReportClientContradictions.Value) {
                args.Output.Warning("ReportClientContradictions is off, so nothing is being correlated. Turn it on in the Network Integrity section.");
                return;
            }

            List<PeerTrust.Record> flagged = PeerTrust.Flagged();
            if (flagged.Count == 0) {
                args.Output.Info("No connected player has tripped a server-side guard this session.");
                return;
            }

            foreach (PeerTrust.Record record in flagged) {
                string who = string.IsNullOrEmpty(record.PlayerName) ? record.HostId : $"{record.PlayerName} ({record.HostId})";
                args.Output.Warning($"  {who}", log: false);

                string[] guards = new string[record.Guards.Count];
                record.Guards.CopyTo(guards);
                Array.Sort(guards, StringComparer.Ordinal);
                args.Output.Detail($"    Guards tripped: {string.Join(", ", guards)}", log: false);

                if (record.DeclaredMods < 0) {
                    args.Output.Detail("    Declared at join: nothing on file for this connection", log: false);
                } else {
                    args.Output.Detail($"    Declared at join: {record.DeclaredMods} mod(s), {record.DeclaredPatchers} patcher(s)", log: false);
                    if (record.BaselineOnly) {
                        // The one case where the correlation is tight rather than merely suggestive, so it is
                        // called out rather than left for the reader to work out from the counts.
                        args.Output.Detail("    All of them mods this server requires, and this server offers no optional mods -", log: false);
                        args.Output.Detail("    so nothing they declared accounts for the traffic above.", log: false);
                    }
                }

                args.Output.Detail($"    First trip: {record.FirstTrip:u}, last: {record.LastTrip:u}", log: false);
                if (!string.IsNullOrEmpty(record.FirstDetail)) {
                    args.Output.Detail($"    First detail: {record.FirstDetail}", log: false);
                }
                if (record.Acted) {
                    args.Output.Detail($"    ContradictionAction has already been applied to this connection.", log: false);
                }
            }

            args.Output.Info($"{flagged.Count} connected player(s) with at least one guard trip. Action fires at {ValConfig.ContradictionThreshold.Value} distinct guard(s); it is currently {ValConfig.ContradictionAction.Value}.");
        }
    }
}
