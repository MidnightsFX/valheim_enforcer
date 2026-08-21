using System.Collections.Generic;
using ValheimEnforcer.modules.character;

namespace ValheimEnforcer.common {
    internal static partial class TerminalManager {

        private static void RegisterPlayerCommands() {
            _ = new EnforcerCommand("enforcer-player-list",
                "Lists every account the server has character saves for, and the characters under each. Server admins only.",
                PlayerList, CommandArea.Player,
                serverAuthoritative: true, requiresAdmin: true,
                aliases: "Enforcer-List-Players");
        }

        private static void PlayerList(EnforcerCommandArgs args) {
            Dictionary<string, List<string>> accounts = CharacterSaves.All();
            if (accounts.Count == 0) {
                args.Output.Warning(ValConfig.InternalStorageMode.Value
                    ? "No characters are stored in this world yet. They appear the first time somebody joins."
                    : $"No character saves found under BepInEx/config/{ValConfig.ValheimEnforcer}/{ValConfig.CharacterFolder}. They appear the first time somebody joins.");
                return;
            }

            int characters = 0;
            // The listing itself is for the person who asked; only the summary is worth a log line.
            foreach (KeyValuePair<string, List<string>> account in accounts) {
                args.Output.Info($"  Account: {account.Key}", log: false);
                foreach (string character in account.Value) {
                    characters++;
                    args.Output.Detail($"    {character}", log: false);
                }
                if (account.Value.Count == 0) {
                    args.Output.Warning("    (no characters saved)", log: false);
                }
            }

            args.Output.Info($"{accounts.Count} account(s), {characters} character(s), stored in {(ValConfig.InternalStorageMode.Value ? "the world file" : "the character folder")}.");
        }
    }
}
