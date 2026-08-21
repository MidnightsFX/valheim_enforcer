using System;
using System.Collections.Generic;

namespace ValheimEnforcer.common {
    internal static partial class TerminalManager {

        private static void RegisterCharacterCommands() {
            _ = new EnforcerCommand("enforcer-characters-import",
                "Format: dryrun|import [force] Imports character saves left behind by the ServerCharacters mod. Run 'dryrun' first to see what it would do. Characters that already have a save here are skipped unless 'force' is given. eg: enforcer-characters-import dryrun",
                CharactersImport, CommandArea.Characters, ImportOptions,
                serverAuthoritative: true, requiresAdmin: true,
                aliases: "Enforcer-Import-ServerCharacters");
        }

        private static List<string> ImportOptions(string[] input) {
            if (input.Length <= 2) { return new List<string>() { "dryrun", "import" }; }
            if (input.Length == 3) { return new List<string>() { "force" }; }
            return new List<string>();
        }

        private static void CharactersImport(EnforcerCommandArgs args) {
            string mode = args.Args.GetString(0, null);
            if (string.IsNullOrEmpty(mode)) {
                args.Output.Error("A mode is required. Format: enforcer-characters-import dryrun|import [force]");
                return;
            }

            bool dryRun;
            if (string.Equals(mode, "dryrun", StringComparison.OrdinalIgnoreCase)) {
                dryRun = true;
            } else if (string.Equals(mode, "import", StringComparison.OrdinalIgnoreCase)) {
                dryRun = false;
            } else {
                args.Output.Error($"Unknown mode '{mode}'. Use 'dryrun' or 'import'.");
                return;
            }
            bool force = args.Has("force");

            args.Output.Detail(dryRun
                ? "Dry run: reading ServerCharacters saves, writing nothing."
                : $"Importing ServerCharacters saves{(force ? ", overwriting any that already exist here" : ", skipping any that already exist here")}.");

            string summary = modules.migration.ServerCharactersImport.Run(dryRun, force).Summary();
            args.Output.Info(summary);
            args.Output.Info(dryRun
                ? "Dry run complete. Nothing was written; re-run with 'import' to apply it."
                : "Import complete.");
        }
    }
}
