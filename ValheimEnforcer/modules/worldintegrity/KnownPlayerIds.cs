using System;
using System.Collections.Generic;
using System.IO;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.worldintegrity {

    /// <summary>
    /// The set of player ids this server has ever seen a real player report.
    ///
    /// A crafted item records who made it, and that id is <c>PlayerProfile.GetPlayerID()</c> - a value that
    /// lives in the crafter's own profile and is not the account or Steam id. So the server has no independent
    /// way to know a given id is real: it can only recognise the ones players have presented to it. Every
    /// character update carries the sender's own id, so the registry fills itself as people play, and is kept
    /// on disk so a restart does not throw it away.
    ///
    /// The point is items whose crafter never existed. A player who spawns gear and then thinks to stamp a
    /// crafter on it has to pick a number, and any number they invent is one no player on this server has ever
    /// reported. Trading is unaffected on purpose: the check is "is this id known", not "is this id yours", so
    /// a sword one player made and gave to another is fine because its maker is on file.
    ///
    /// Known cost: an item crafted by somebody who has not joined since this feature was switched on has a
    /// crafter the registry does not know yet. Which is why the detection this feeds is a warning, and why it
    /// only ever looks at items that have just appeared rather than at whole inventories.
    /// </summary>
    internal static class KnownPlayerIds {

        private const string FileName = "PlayerIds.yaml";

        // id -> the account that first reported it, for the log line when something does not line up.
        private static readonly Dictionary<long, string> known = new Dictionary<long, string>();
        private static bool loaded;
        private static bool dirty;

        internal static string FilePath {
            get { return Path.Combine(ValConfig.GetSecondaryConfigDirectoryPath(), FileName); }
        }

        internal static void Initialize() {
            known.Clear();
            loaded = false;
            Load();
        }

        private static void Load() {
            if (loaded) { return; }
            loaded = true;
            try {
                string path = FilePath;
                if (!File.Exists(path)) { return; }
                Dictionary<long, string> parsed = DataObjects.yamldeserializer
                    .Deserialize<Dictionary<long, string>>(File.ReadAllText(path));
                if (parsed == null) { return; }
                foreach (KeyValuePair<long, string> entry in parsed) { known[entry.Key] = entry.Value; }
                Logger.LogDebug($"Loaded {known.Count} known player id(s).");
            } catch (Exception e) {
                // A registry we cannot read means "we know nobody", and Check fails open on an empty registry,
                // so a corrupt file costs detection rather than producing false accusations.
                Logger.LogWarning($"Could not read {FileName}: {e.Message}. Starting with an empty player id registry.");
            }
        }

        /// <summary>
        /// Records the id a connected player reported for themselves. Cheap and idempotent; the write to disk
        /// only happens when something was actually new.
        /// </summary>
        internal static void Note(long playerId, string account) {
            if (playerId == 0L) { return; }
            Load();
            if (known.ContainsKey(playerId)) { return; }
            known[playerId] = account ?? "";
            dirty = true;
            Logger.LogDebug($"Recorded a new player id ({playerId}) for {account}.");
            Save();
        }

        /// <summary>
        /// True when this id belongs to somebody the server has seen. An empty registry answers true for
        /// everything: a server that knows nobody yet must not decide that everybody is forging.
        /// </summary>
        internal static bool IsKnown(long playerId) {
            if (playerId == 0L) { return true; } // "no crafter" is a different question, handled elsewhere
            Load();
            if (known.Count == 0) { return true; }
            return known.ContainsKey(playerId);
        }

        internal static int Count() {
            Load();
            return known.Count;
        }

        private static void Save() {
            if (!dirty) { return; }
            try {
                ValConfig.GetSecondaryConfigDirectoryPath();
                File.WriteAllText(FilePath, DataObjects.yamlserializer.Serialize(known));
                dirty = false;
            } catch (Exception e) {
                Logger.LogWarning($"Could not write {FileName}: {e.Message}");
            }
        }
    }
}
