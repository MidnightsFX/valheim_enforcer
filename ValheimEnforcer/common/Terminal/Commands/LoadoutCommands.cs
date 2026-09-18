using System;
using System.Collections.Generic;
using ValheimEnforcer.modules.character;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.common {
    internal static partial class TerminalManager {

        private static void RegisterLoadoutCommands() {
            _ = new EnforcerCommand("enforcer-loadout-list",
                "Lists the starter loadouts in Loadouts.yaml and says which one new characters are getting. Add a name to see what is in one. Format: [name]. eg: enforcer-loadout-list starter",
                LoadoutList, CommandArea.Characters, LoadoutNames,
                serverAuthoritative: true, requiresAdmin: true);

            _ = new EnforcerCommand("enforcer-loadout-apply",
                "Adds a loadout to a character the server already has a save for - a starter kit for somebody who joined before you set one up, or a replacement for a player who lost something. Skills are only ever raised. Format: <accountId> <characterName> <name> confirm. eg: enforcer-loadout-apply 76561198012345678 Bjorn starter confirm",
                LoadoutApply, CommandArea.Characters, LoadoutApplyOptions,
                serverAuthoritative: true, requiresAdmin: true);
        }

        private static List<string> LoadoutNames(string[] input) {
            if (input.Length <= 2) { return StarterLoadouts.Names(); }
            return new List<string>();
        }

        private static List<string> LoadoutApplyOptions(string[] input) {
            if (input.Length <= 2) { return TerminalArgs.KnownAccounts(input); }
            if (input.Length == 3) { return TerminalArgs.KnownCharacters(input); }
            if (input.Length == 4) { return StarterLoadouts.Names(); }
            if (input.Length == 5) { return new List<string>() { "confirm" }; }
            return new List<string>();
        }

        private static void LoadoutList(EnforcerCommandArgs args) {
            string wanted = args.Args.GetString(0, null);
            List<string> names = StarterLoadouts.Names();

            if (string.IsNullOrEmpty(wanted)) {
                if (names.Count == 0) {
                    args.Output.Info($"No loadouts in {StarterLoadouts.FileName}. The file has a commented example in it to start from.");
                    return;
                }
                string active = ValConfig.DefaultStarterLoadout != null ? ValConfig.DefaultStarterLoadout.Value : "";
                foreach (string name in names) {
                    Loadout entry = StarterLoadouts.Find(name);
                    string mark = string.Equals(name, active, StringComparison.OrdinalIgnoreCase) ? "  <- new characters get this" : "";
                    args.Output.Detail($"  {name}: {Summarize(entry)}{mark}", log: false);
                }
                // Both halves are worth saying: a file full of loadouts with the feature off does nothing, and
                // so does the feature on with nothing selected.
                if (!StarterLoadouts.Enabled) {
                    args.Output.Warning($"{names.Count} loadout(s), but EnableStarterLoadouts is off, so no new character gets one.");
                } else if (string.IsNullOrWhiteSpace(active)) {
                    args.Output.Warning($"{names.Count} loadout(s), but DefaultStarterLoadout is empty, so no new character gets one.");
                } else {
                    args.Output.Info($"{names.Count} loadout(s). New characters get '{active}'.");
                }
                return;
            }

            Loadout loadout = StarterLoadouts.Find(wanted);
            if (loadout == null) {
                args.Output.Error($"No loadout called '{wanted}' in {StarterLoadouts.FileName}.");
                return;
            }
            if (!string.IsNullOrEmpty(loadout.description)) { args.Output.Detail($"  {loadout.description}", log: false); }
            if (loadout.items != null) {
                foreach (LoadoutItem item in loadout.items) {
                    if (item == null) { continue; }
                    args.Output.Detail($"  item   {item.prefabName} x{Math.Max(1, item.stack)}"
                                       + (item.quality > 1 ? $" quality {item.quality}" : "")
                                       + (item.equipped ? " (equipped)" : ""), log: false);
                }
            }
            if (loadout.skills != null) {
                foreach (KeyValuePair<Skills.SkillType, float> skill in loadout.skills) {
                    args.Output.Detail($"  skill  {skill.Key} to {skill.Value:F0}", log: false);
                }
            }
            if (loadout.knownMaterials != null && loadout.knownMaterials.Count > 0) {
                args.Output.Detail($"  knows  {loadout.knownMaterials.Count} material(s)"
                                   + (ProgressionSync.KnownItemsEnabled ? "" : " - ignored, SyncKnownItems is off"), log: false);
            }
            if (loadout.knownRecipes != null && loadout.knownRecipes.Count > 0) {
                args.Output.Detail($"  knows  {loadout.knownRecipes.Count} recipe(s)"
                                   + (ProgressionSync.KnownItemsEnabled ? "" : " - ignored, SyncKnownItems is off"), log: false);
            }
            if (loadout.haveSpawnPoint) {
                args.Output.Detail($"  spawn  {loadout.spawnX:F0}, {loadout.spawnY:F0}, {loadout.spawnZ:F0}"
                                   + (ProgressionSync.SpawnEnabled ? "" : " - ignored, SyncSpawnPoint is off"), log: false);
            }
            args.Output.Info($"Loadout '{wanted}': {Summarize(loadout)}.");
        }

        private static string Summarize(Loadout loadout) {
            if (loadout == null) { return "empty"; }
            List<string> parts = new List<string>();
            if (loadout.items != null && loadout.items.Count > 0) { parts.Add($"{loadout.items.Count} item(s)"); }
            if (loadout.skills != null && loadout.skills.Count > 0) { parts.Add($"{loadout.skills.Count} skill(s)"); }
            int known = (loadout.knownMaterials?.Count ?? 0) + (loadout.knownRecipes?.Count ?? 0);
            if (known > 0) { parts.Add($"{known} known item(s)"); }
            if (loadout.haveSpawnPoint) { parts.Add("a spawn point"); }
            return parts.Count == 0 ? "empty" : string.Join(", ", parts.ToArray());
        }

        private static void LoadoutApply(EnforcerCommandArgs args) {
            string usage = "Format: enforcer-loadout-apply <accountId> <characterName> <name> confirm";
            if (!ReadProgressTarget(args, usage, out string account, out string name)) { return; }

            string loadoutName = args.Args.GetString(2, null);
            if (string.IsNullOrEmpty(loadoutName)) {
                args.Output.Error($"A loadout name is required. {usage}");
                return;
            }
            Loadout loadout = StarterLoadouts.Find(loadoutName);
            if (loadout == null) {
                args.Output.Error($"No loadout called '{loadoutName}' in {StarterLoadouts.FileName}.");
                return;
            }

            if (!args.Has("confirm")) {
                args.Output.Warning($"This adds {Summarize(loadout)} to {name}'s stored save. Items are added, not replaced, and skills are only raised - but there is no undo. Re-run with 'confirm' on the end.");
                return;
            }

            DataObjects.Character character = ValConfig.LoadCharacterFromSave(account, name);
            if (character == null) {
                args.Output.Error($"Could not read the save for {name} under account {account}; see the server log.");
                return;
            }

            StarterLoadouts.Result result = StarterLoadouts.ApplyToRecord(
                character, loadout, ProgressionSync.KnownItemsEnabled, ProgressionSync.SpawnEnabled);
            if (!result.Changed) {
                args.Output.Info($"'{loadoutName}' added nothing to {name} - they already have at least as much of it.");
                return;
            }

            ValConfig.WritePlayerCharacterToSave(account, character);
            CharacterStore.Invalidate(account, name);

            // Same caveat as every other command that edits a stored save while somebody may be holding their
            // own copy of it.
            bool online = ValConfig.GetPeerByPlatformID(account) != null;
            args.Output.Info($"Added '{loadoutName}' to {name}: {result.Describe()}. "
                             + (online
                                ? "They are connected and still hold their own copy, so this takes effect on their next join."
                                : "They are offline; the save is what counts and it is written."));
        }
    }
}
