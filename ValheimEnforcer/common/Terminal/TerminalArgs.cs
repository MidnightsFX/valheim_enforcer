using System;
using System.Collections.Generic;
using System.Linq;
using ValheimEnforcer.modules.character;

namespace ValheimEnforcer.common {

    /// <summary>Argument readers and the option lists commands share for tab completion.</summary>
    internal static class TerminalArgs {

        internal static string GetString(this string[] args, int index, string fallback = "") {
            if (args == null || args.Length <= index) { return fallback; }
            return args[index];
        }

        internal static string GetStringFrom(this string[] args, int index, string fallback = "") {
            if (args == null || args.Length <= index) { return fallback; }
            return string.Join(" ", args.Skip(index));
        }

        internal static int GetInt(this string[] args, int index, int fallback = 0) {
            string raw = args.GetString(index, null);
            if (raw == null) { return fallback; }
            return int.TryParse(raw, out int parsed) ? parsed : fallback;
        }

        internal static bool IsWord(this string[] args, int index, string word) {
            return string.Equals(args.GetString(index, null), word, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Reads a required account id, rejecting the obvious mistakes with a message that shows the format
        /// rather than leaving the admin to guess which of three arguments was wrong.
        /// </summary>
        internal static bool ReadAccount(this EnforcerCommandArgs args, int index, string usage, out string account) {
            account = args.Args.GetString(index, null);
            if (string.IsNullOrEmpty(account)) {
                args.Output.Error($"An account id is required. {usage}");
                return false;
            }
            return true;
        }

        internal static bool ReadName(this EnforcerCommandArgs args, int index, string usage, out string name) {
            name = args.Args.GetString(index, null);
            if (string.IsNullOrEmpty(name)) {
                args.Output.Error($"A character name is required. {usage}");
                return false;
            }
            return true;
        }

        /// <summary>
        /// The item filter shared by the confiscated-item commands: 'all', or a comma-separated prefab list.
        /// Returns null for 'all' so callers can tell the two apart without re-parsing the string.
        /// </summary>
        internal static bool ReadItemFilter(this EnforcerCommandArgs args, int index, string usage, out string raw, out List<string> prefabs) {
            prefabs = null;
            raw = args.Args.GetString(index, null);
            if (string.IsNullOrEmpty(raw)) {
                args.Output.Error($"An item filter is required - use 'all' or a comma-separated list of prefab names. {usage}");
                return false;
            }
            if (string.Equals(raw, "all", StringComparison.OrdinalIgnoreCase)) { return true; }

            prefabs = raw.Split(',').Select(entry => entry.Trim()).Where(entry => entry.Length > 0).ToList();
            if (prefabs.Count == 0) {
                args.Output.Error($"'{raw}' contains no prefab names. Use 'all' or a comma-separated list. {usage}");
                return false;
            }
            return true;
        }

        internal static List<string> Names<T>() where T : struct, Enum {
            return Enum.GetNames(typeof(T)).ToList();
        }

        /// <summary>
        /// Account ids the server already has saves for. Turns the three-argument confiscated-item commands
        /// from something you have to run enforcer-player-list for first into something you can tab through.
        /// </summary>
        internal static List<string> KnownAccounts(string[] input) {
            try {
                return CharacterSaves.Accounts();
            } catch (Exception) {
                return new List<string>();
            }
        }

        /// <summary>Character names saved under whichever account id has already been typed.</summary>
        internal static List<string> KnownCharacters(string[] input) {
            try {
                string account = input.GetString(1, null);
                if (string.IsNullOrEmpty(account)) { return new List<string>(); }
                return CharacterSaves.CharactersFor(account);
            } catch (Exception) {
                return new List<string>();
            }
        }

        internal static List<string> ItemFilters(string[] input) {
            return new List<string>() { "all" };
        }
    }
}
