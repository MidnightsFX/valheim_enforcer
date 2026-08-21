using BepInEx;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.character {

    /// <summary>
    /// Reads what characters the server has saves for, from whichever store is in use.
    ///
    /// The commands and their tab completion both need this, and both used to open-code it. The flat-file
    /// half of that open-coding was wrong in two ways: it listed the character folder with GetFiles, which
    /// returns nothing because accounts are directories, and it cut path components on '/', which is not the
    /// separator on the platform most servers run on. The result was a player list that printed nothing at
    /// all and gave no hint why.
    /// </summary>
    internal static class CharacterSaves {

        /// <summary>Account id to the character names saved under it. Empty when nothing has been saved yet.</summary>
        internal static Dictionary<string, List<string>> All() {
            if (ValConfig.InternalStorageMode.Value) {
                return InternalDataStore.GetAccountRegistry() ?? new Dictionary<string, List<string>>();
            }

            Dictionary<string, List<string>> map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            string root = Path.Combine(Paths.ConfigPath, ValConfig.ValheimEnforcer, ValConfig.CharacterFolder);
            if (!Directory.Exists(root)) { return map; }

            foreach (string accountDir in Directory.GetDirectories(root)) {
                string account = Path.GetFileName(accountDir);
                List<string> characters = Directory.GetFiles(accountDir, "*.yaml")
                    .Select(Path.GetFileNameWithoutExtension)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                map[account] = characters;
            }
            return map;
        }

        internal static List<string> Accounts() {
            return All().Keys.OrderBy(id => id, StringComparer.Ordinal).ToList();
        }

        internal static List<string> CharactersFor(string account) {
            if (string.IsNullOrEmpty(account)) { return new List<string>(); }
            Dictionary<string, List<string>> map = All();
            List<string> characters;
            // Account ids are typed by hand off a log line, so match them the forgiving way rather than
            // making a wrong-case id look like an account that does not exist.
            foreach (KeyValuePair<string, List<string>> entry in map) {
                if (string.Equals(entry.Key, account, StringComparison.OrdinalIgnoreCase)) {
                    return entry.Value ?? new List<string>();
                }
            }
            return map.TryGetValue(account, out characters) && characters != null ? characters : new List<string>();
        }

        /// <summary>
        /// Whether the server has a save for this exact account and character, used to tell "no confiscated
        /// items" apart from "that is not a character I have ever seen", which are very different answers to
        /// an admin trying to give somebody their cloak back.
        /// </summary>
        internal static bool Exists(string account, string name) {
            return CharactersFor(account).Any(character => string.Equals(character, name, StringComparison.OrdinalIgnoreCase));
        }
    }
}
