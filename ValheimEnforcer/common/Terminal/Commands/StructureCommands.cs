using System;
using System.Collections.Generic;
using ValheimEnforcer.modules.worldintegrity;

namespace ValheimEnforcer.common {
    internal static partial class TerminalManager {

        private static void RegisterStructureCommands() {
            _ = new EnforcerCommand("enforcer-structures-scan",
                "Format: scan|remove [confirm] [prefabFilter] Reports structures in the world that no build tool can place, or whose health is above what their prefab allows. Runs both checks whatever the World Integrity settings say, and never touches anything inside a generated location. eg: enforcer-structures-scan scan dvergrtown",
                StructuresScan, CommandArea.Structures, ScanOptions,
                serverAuthoritative: true, requiresAdmin: true,
                aliases: "Enforcer-Scan-Structures");
        }

        private static List<string> ScanOptions(string[] input) {
            if (input.Length <= 2) { return new List<string>() { "scan", "remove" }; }
            if (input.Length == 3 && input.GetString(1, "").Equals("remove", StringComparison.OrdinalIgnoreCase)) {
                return new List<string>() { "confirm" };
            }
            return new List<string>();
        }

        private static void StructuresScan(EnforcerCommandArgs args) {
            string mode = args.Args.GetString(0, "scan");
            bool remove = string.Equals(mode, "remove", StringComparison.OrdinalIgnoreCase);
            if (!remove && !string.Equals(mode, "scan", StringComparison.OrdinalIgnoreCase)) {
                args.Output.Error($"Unknown mode '{mode}'. Use 'scan', or 'remove confirm' to delete what it finds.");
                return;
            }

            // The confirm word sits between the mode and the filter so a filter cannot be mistaken for it.
            string filter;
            if (remove) {
                if (!args.Args.IsWord(1, "confirm")) {
                    args.Output.Error("Removal deletes objects out of the world and cannot be undone. Run 'enforcer-structures-scan scan' first, read the list, then 'enforcer-structures-scan remove confirm'.");
                    return;
                }
                filter = args.Args.GetString(2, null);
            } else {
                filter = args.Args.GetString(1, null);
            }

            if (!StructureSweep.Start(args.Output, remove, filter, out string problem)) {
                args.Output.Error(problem);
                return;
            }

            // The sweep runs across frames and keeps writing to this same sink, so the closing summary is
            // its own; this line is only the acknowledgement that it started.
            args.Output.Info($"Structure scan started{(remove ? " with removal enabled" : "")}{(filter != null ? $", filtered to '{filter}'" : "")}. Results follow when it finishes.");
        }
    }
}
