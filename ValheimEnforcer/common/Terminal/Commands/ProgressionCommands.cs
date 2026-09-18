using System;
using System.Collections.Generic;
using ValheimEnforcer.modules.character;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.common {
    internal static partial class TerminalManager {

        private const string ProgressUsage = "Format: <accountId> <characterName>";

        private static void RegisterProgressionCommands() {
            _ = new EnforcerCommand("enforcer-progress-show",
                $"Shows what this server holds of a character's map, known recipes, trophies, statistics and spawn point - the progression that lives in the player profile rather than in the character record. Reads only; changes nothing. {ProgressUsage}. eg: enforcer-progress-show 76561198012345678 Bjorn",
                ProgressShow, CommandArea.Characters, ProgressAccountThenCharacter,
                serverAuthoritative: true, requiresAdmin: true);

            _ = new EnforcerCommand("enforcer-progress-clear",
                $"Permanently deletes part of what this server holds of a character's progression, so their next join is treated as having none of it. {ProgressUsage} <map|items|stats|spawn|all> confirm. There is no undo and no confiscation record - unlike an item, a forgotten recipe cannot be handed back. eg: enforcer-progress-clear 76561198012345678 Bjorn map confirm",
                ProgressClear, CommandArea.Characters, ProgressAccountThenCharacterThenSection,
                serverAuthoritative: true, requiresAdmin: true);
        }

        private static List<string> ProgressAccountThenCharacter(string[] input) {
            if (input.Length <= 2) { return TerminalArgs.KnownAccounts(input); }
            if (input.Length == 3) { return TerminalArgs.KnownCharacters(input); }
            return new List<string>();
        }

        private static List<string> ProgressAccountThenCharacterThenSection(string[] input) {
            if (input.Length == 4) { return new List<string>() { "map", "items", "stats", "spawn", "all" }; }
            if (input.Length == 5) { return new List<string>() { "confirm" }; }
            return ProgressAccountThenCharacter(input);
        }

        // Resolves the pair to the spelling actually on disk. Composing a path from what an admin typed
        // fails on a case-sensitive filesystem, which is what most servers run on.
        private static bool ReadProgressTarget(EnforcerCommandArgs args, string usage,
                                               out string account, out string name) {
            name = null;
            if (!args.ReadAccount(0, usage, out account)) { return false; }
            if (!args.ReadName(1, usage, out name)) { return false; }

            if (!CharacterSaves.TryResolveSave(account, name, out string resolvedAccount, out string resolvedName, out bool lookupFailed)) {
                args.Output.Error(lookupFailed
                    ? $"Could not read the character store to look up '{name}' under account {account}; see the server log."
                    : $"No save for character '{name}' under account {account}. Run enforcer-player-list to see what the server has.");
                return false;
            }
            account = resolvedAccount;
            name = resolvedName;
            return true;
        }

        private static void ProgressShow(EnforcerCommandArgs args) {
            string usage = "Format: enforcer-progress-show <accountId> <characterName>";
            if (!ReadProgressTarget(args, usage, out string account, out string name)) { return; }

            DataObjects.Character character = ValConfig.LoadCharacterFromSave(account, name);
            if (character == null) {
                args.Output.Error($"Could not read the save for {name} under account {account}; see the server log.");
                return;
            }

            args.Output.Detail($"  save sequence: {character.SaveSequence}", log: false);

            Progression progress = character.Progress;
            if (progress == null) {
                args.Output.Info($"{name} has no progression stored. Either nothing in this group has been switched on, or they have not saved since it was.");
                ReportStoredMap(args, account, name, null);
                return;
            }

            // "not tracked" and "tracked, and empty" are different answers everywhere in this record, so the
            // output has to be able to say both - an admin looking at a character who legitimately knows
            // nothing must not read it as the feature being off.
            args.Output.Detail($"  known recipes:   {Count(progress.KnownRecipes)}", log: false);
            args.Output.Detail($"  known materials: {Count(progress.KnownMaterials)}", log: false);
            args.Output.Detail($"  known stations:  {CountMap(progress.KnownStations)}", log: false);
            args.Output.Detail($"  known biomes:    {Count(progress.KnownBiomes)}", log: false);
            args.Output.Detail($"  runestone texts: {CountTexts(progress.KnownTexts)}", log: false);
            args.Output.Detail($"  unique keys:     {Count(progress.Uniques)}", log: false);
            args.Output.Detail($"  tutorials seen:  {Count(progress.ShownTutorials)}", log: false);
            args.Output.Detail($"  trophies:        {Count(progress.Trophies)}", log: false);
            args.Output.Detail($"  statistics:      {DescribeStats(progress.Stats)}", log: false);
            args.Output.Detail($"  spawn point:     {DescribeSpawn(progress.Spawn)}", log: false);

            ReportStoredMap(args, account, name, progress.MapHash);
            args.Output.Info($"Progression stored for {name} under account {account}.");
        }

        private static void ReportStoredMap(EnforcerCommandArgs args, string account, string name, string recordedHash) {
            byte[] blob = MapSync.Load(account, name);
            if (blob == null) {
                args.Output.Detail(recordedHash == null
                    ? "  map:             none stored"
                    : "  map:             none stored, although the save records one - the .map file is missing", log: false);
                return;
            }
            string actual = MapSync.Hash(blob);
            string agreement = recordedHash == null
                ? " (the save records no hash for it)"
                : (string.Equals(actual, recordedHash, StringComparison.Ordinal) ? "" : " (does NOT match the hash in the save)");
            args.Output.Detail($"  map:             {blob.Length} bytes, {Short(actual)}{agreement}", log: false);
        }

        private static void ProgressClear(EnforcerCommandArgs args) {
            string usage = "Format: enforcer-progress-clear <accountId> <characterName> <map|items|stats|spawn|all> confirm";
            if (!ReadProgressTarget(args, usage, out string account, out string name)) { return; }

            string section = args.Args.GetString(2, null);
            if (string.IsNullOrEmpty(section)) {
                args.Output.Error($"A section is required. {usage}");
                return;
            }
            bool all = string.Equals(section, "all", StringComparison.OrdinalIgnoreCase);
            bool map = all || string.Equals(section, "map", StringComparison.OrdinalIgnoreCase);
            bool items = all || string.Equals(section, "items", StringComparison.OrdinalIgnoreCase);
            bool stats = all || string.Equals(section, "stats", StringComparison.OrdinalIgnoreCase);
            bool spawn = all || string.Equals(section, "spawn", StringComparison.OrdinalIgnoreCase);
            if (!map && !items && !stats && !spawn) {
                args.Output.Error($"Unknown section '{section}'. Use map, items, stats, spawn or all.");
                return;
            }

            if (!args.Has("confirm")) {
                args.Output.Warning($"This permanently deletes the '{section}' progression this server holds for {name}, and it cannot be undone or handed back. Re-run with 'confirm' on the end to do it.");
                return;
            }

            DataObjects.Character character = ValConfig.LoadCharacterFromSave(account, name);
            if (character == null) {
                args.Output.Error($"Could not read the save for {name} under account {account}; see the server log.");
                return;
            }

            List<string> cleared = new List<string>();
            if (map && MapSync.Delete(account, name)) { cleared.Add("map"); }

            Progression progress = character.Progress;
            if (progress != null) {
                if (map && progress.MapHash != null) { progress.MapHash = null; }
                if (items) {
                    // Emptied rather than nulled, which is the same distinction the new-character rules draw:
                    // null would have their next join adopt whatever the client turns up holding, and the
                    // point of clearing is that it does not.
                    progress.KnownRecipes = new List<string>();
                    progress.KnownMaterials = new List<string>();
                    progress.KnownStations = new Dictionary<string, int>();
                    progress.KnownBiomes = new List<string>();
                    progress.KnownTexts = new Dictionary<string, string>();
                    progress.Uniques = new List<string>();
                    progress.ShownTutorials = new List<string>();
                    progress.Trophies = new List<string>();
                    cleared.Add("known items and trophies");
                }
                if (stats) {
                    progress.Stats = new Dictionary<string, StatBucket>();
                    cleared.Add("statistics");
                }
                if (spawn) {
                    progress.Spawn = new SpawnPoints();
                    cleared.Add("spawn point");
                }
            }

            // Written through the same path an admin's item return uses, which also drops the async store's
            // cached copy - otherwise a pending write would put back what was just removed.
            ValConfig.WritePlayerCharacterToSave(account, character);
            CharacterStore.Invalidate(account, name);

            if (cleared.Count == 0) {
                args.Output.Info($"{name} had nothing stored under '{section}', so nothing was deleted.");
                return;
            }
            // Whoever is holding this character is also holding their own copy of everything above and will
            // push it back on their next full save, so an online player keeps what was just deleted until
            // they reconnect. Say which case it is rather than leaving an admin to find out.
            bool online = ValConfig.GetPeerByPlatformID(account) != null;
            args.Output.Info($"Cleared {string.Join(", ", cleared.ToArray())} for {name} under account {account}. "
                             + (online
                                ? "They are connected and still hold their own copy, so this takes effect on their next join."
                                : "They are offline; the save is what counts and it is written."));
        }

        private static string Count(List<string> values) {
            return values == null ? "not tracked" : values.Count.ToString();
        }

        private static string CountMap(Dictionary<string, int> values) {
            return values == null ? "not tracked" : values.Count.ToString();
        }

        private static string CountTexts(Dictionary<string, string> values) {
            return values == null ? "not tracked" : values.Count.ToString();
        }

        private static string DescribeStats(Dictionary<string, StatBucket> stats) {
            if (stats == null) { return "not tracked"; }
            if (stats.Count == 0) { return "0 buckets"; }
            int counters = 0;
            foreach (KeyValuePair<string, StatBucket> bucket in stats) {
                if (bucket.Value?.Stats != null) { counters += bucket.Value.Stats.Count; }
            }
            return $"{stats.Count} difficulty bucket(s), {counters} non-zero counter(s)";
        }

        private static string DescribeSpawn(SpawnPoints spawn) {
            if (spawn == null) { return "not tracked"; }
            if (!spawn.HaveCustomSpawn) { return "no bed claimed here"; }
            return $"{spawn.SpawnX:F0}, {spawn.SpawnY:F0}, {spawn.SpawnZ:F0}";
        }

        private static string Short(string hash) {
            if (string.IsNullOrEmpty(hash)) { return "no hash"; }
            return hash.Length <= 12 ? hash : hash.Substring(0, 12);
        }
    }
}
