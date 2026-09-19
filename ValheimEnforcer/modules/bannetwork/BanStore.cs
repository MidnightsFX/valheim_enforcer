using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.bannetwork {

    /// <summary>
    /// Owns Bans.yaml: every ban this server issued or inherited, and the only ban list an admin edits by hand.
    ///
    /// This supersedes the old KnownCheaters.yaml, which was a flat {id, reason} list with no categories, no
    /// timestamps, no provenance and no way to remove an entry. Two consequences worth stating, because both
    /// are deliberate changes rather than accidents of the rewrite:
    ///
    /// 1. The embedded seed is applied ONCE, when the file is first created, not re-applied on every reload.
    ///    KnownCheaterTracker re-merged it after every edit specifically so an admin could not drop an entry.
    ///    That is incompatible with a network whose whole point is that the server owner gets the final say,
    ///    so a seeded entry is now an ordinary ban: removable with enforcer-unban, overridable with
    ///    enforcer-ban-allow.
    /// 2. The file is only ever rewritten by a command. The ban network's hourly pull writes its own files and
    ///    never touches this one, so an admin editing it cannot have their work overwritten mid-edit - the
    ///    failure mode that the Mods.yaml postmortem in ValConfig.CreateModsFile exists to warn about.
    /// </summary>
    internal static class BanStore {

        internal const string FileName = "Bans.yaml";

        internal static string FilePath {
            get { return Path.Combine(ValConfig.GetSecondaryConfigDirectoryPath(), FileName); }
        }

        private const string EmbeddedSeedName = "ValheimEnforcer.assets.KnownCheaters.yaml";

        /// <summary>
        /// Insertion-ordered so a rewrite keeps the file looking the way the admin last saw it. Lookups walk it
        /// - see Find - because ids arrive under more than one platform spelling and a dictionary keyed on one
        /// spelling would miss the other.
        /// </summary>
        private static readonly List<BanRecord> Bans = new List<BanRecord>();

        private static readonly object Gate = new object();

        internal static int Count {
            get { lock (Gate) { return Bans.Count; } }
        }

        // ---- Lifecycle ------------------------------------------------------------------------------------

        /// <summary>
        /// Reads Bans.yaml into memory. Runs from the ValConfig constructor, immediately after LoadYamlConfigs
        /// has created the file if it was missing (which is where a legacy migration happens - see
        /// <see cref="BuildInitialFile"/>).
        /// </summary>
        internal static void Initialize() {
            string path = FilePath;
            if (!File.Exists(path)) {
                Logger.LogWarning($"{FileName} is missing after creation; starting with an empty ban list.");
                lock (Gate) { Bans.Clear(); }
                return;
            }

            try {
                LoadFromText(File.ReadAllText(path), announce: false);
                Logger.LogInfo($"Ban list loaded ({Count} entr{(Count == 1 ? "y" : "ies")}).");
            } catch (Exception e) {
                string backup = ConfigFileBackup.BackupUnreadable(path);
                Logger.LogError($"Could not read {FileName}: {ConfigFileBackup.DescribeParseFailure(e)}. "
                              + $"A copy was kept at {backup}. Starting with an empty ban list - fix the file and it will reload.");
            }
        }

        /// <summary>
        /// Replaces the in-memory list from yaml text. Used by the file watcher, so a parse failure must leave
        /// the previously loaded bans in place: half a ban list is worse than a stale one, and an admin who
        /// saves a broken file mid-edit should not have the server forget everyone it had banned.
        /// </summary>
        internal static void LoadFromText(string yaml, bool announce = true) {
            List<BanRecord> parsed = new List<BanRecord>();
            DataObjects.BansFile file;
            try {
                file = DataObjects.yamlconfigdeserializer.Deserialize<DataObjects.BansFile>(yaml);
            } catch (Exception e) {
                Logger.LogError($"{FileName} could not be parsed and was NOT applied: {ConfigFileBackup.DescribeParseFailure(e)}. "
                              + "The previously loaded bans are still in effect.");
                return;
            }

            if (file != null && file.Bans != null) {
                foreach (DataObjects.BanEntry entry in file.Bans) {
                    BanRecord record = BanRecord.FromEntry(entry, FileName);
                    if (record == null) {
                        Logger.LogWarning($"Skipping a ban entry in {FileName} with no id.");
                        continue;
                    }
                    parsed.Add(record);
                }
            }

            lock (Gate) {
                Bans.Clear();
                Bans.AddRange(parsed);
            }
            if (announce) { Logger.LogInfo($"Ban list reloaded ({parsed.Count} entries)."); }
        }

        // ---- Queries --------------------------------------------------------------------------------------

        /// <summary>
        /// The ban covering this account, or null. Tolerates a platform prefix difference, because the same
        /// account reaches us as "76561..." from one path and "Steam_76561..." from another.
        ///
        /// Expired entries are skipped rather than removed: pruning from a read would mean writing the file
        /// from the connection handshake, and an expiry that quietly deleted the record would also destroy the
        /// history an admin needs to see why someone was banned last month.
        /// </summary>
        internal static BanRecord Find(string hostId) {
            if (string.IsNullOrEmpty(hostId)) { return null; }
            lock (Gate) {
                foreach (BanRecord record in Bans) {
                    if (record.Expired) { continue; }
                    if (PlatformIds.Matches(record.Id, hostId)) { return record; }
                }
            }
            return null;
        }

        /// <summary>Every ban including expired ones, for listings. A copy: callers must not hold the lock.</summary>
        internal static List<BanRecord> All() {
            lock (Gate) { return new List<BanRecord>(Bans); }
        }

        internal static bool IsBanned(string hostId) {
            return Find(hostId) != null;
        }

        // ---- Mutation -------------------------------------------------------------------------------------

        /// <summary>
        /// Adds or replaces a ban and persists it. Replacing rather than refusing a duplicate is deliberate:
        /// re-banning someone with a new category or a longer reason should update the record, and the old
        /// AddCheater silently ignored the second call, so a correction never took.
        ///
        /// Returns true when this was a new ban, false when it replaced one.
        /// </summary>
        internal static bool Add(BanRecord record) {
            if (record == null || string.IsNullOrEmpty(record.Id)) { return false; }
            if (record.AddedUtc == null) { record.AddedUtc = DateTime.UtcNow; }

            bool added;
            lock (Gate) {
                int existing = IndexOf(record.Id);
                if (existing >= 0) {
                    Bans[existing] = record;
                    added = false;
                } else {
                    Bans.Add(record);
                    added = true;
                }
            }
            Save();
            return added;
        }

        /// <summary>Removes every ban matching this account. Returns how many went.</summary>
        internal static int Remove(string hostId) {
            if (string.IsNullOrEmpty(hostId)) { return 0; }
            int removed;
            lock (Gate) {
                removed = Bans.RemoveAll(record => PlatformIds.Matches(record.Id, hostId));
            }
            if (removed > 0) { Save(); }
            return removed;
        }

        private static int IndexOf(string hostId) {
            for (int i = 0; i < Bans.Count; i++) {
                if (PlatformIds.Matches(Bans[i].Id, hostId)) { return i; }
            }
            return -1;
        }

        /// <summary>
        /// Offers a ban to the ban network, and says in the log when it deliberately was not published.
        ///
        /// Separate from <see cref="Add"/> rather than folded into it, because plenty of things record a ban
        /// that must never be republished - the migration, the embedded seed, and above all an entry pulled
        /// from the network itself. Making publication an explicit second step means the default for any new
        /// caller is "does not publish", which is the safe direction to be wrong in.
        /// </summary>
        internal static void OfferToNetwork(BanRecord record) {
            if (record == null) { return; }
            if (BanOutbox.Enqueue(record, out string why)) {
                Logger.LogDebug($"Ban network: queued a report for {record.Id}.");
                BanNetworkScheduler.PushSoon();
                return;
            }
            if (!string.IsNullOrEmpty(why)) {
                Logger.LogDebug($"Ban network: not reporting {record.Id} - {why}.");
            }
        }

        /// <summary>Withdraws this server's report for an account, if it had published one.</summary>
        internal static void RetractFromNetwork(string hostId) {
            if (BanOutbox.EnqueueUnban(hostId, out string why)) {
                BanNetworkScheduler.PushSoon();
            } else if (!string.IsNullOrEmpty(why)) {
                Logger.LogDebug($"Ban network: not retracting {hostId} - {why}.");
            }
        }

        // ---- Persistence ----------------------------------------------------------------------------------

        /// <summary>
        /// Writes the list back, keeping the admin's comments and taking a one-off backup first. Never called
        /// from the pull path - only a command reaches here - which is what keeps a hand edit safe.
        /// </summary>
        private static void Save() {
            string path = FilePath;
            try {
                List<DataObjects.BanEntry> entries = new List<DataObjects.BanEntry>();
                lock (Gate) {
                    foreach (BanRecord record in Bans) { entries.Add(record.ToEntry()); }
                }

                DataObjects.BansFile file = new DataObjects.BansFile { Version = 1, Bans = entries };
                string yaml = DataObjects.yamlserializer.Serialize(file);

                if (File.Exists(path)) {
                    ConfigFileBackup.TryBackupOnce(path);
                    YamlComments.Captured captured = YamlComments.Capture(File.ReadAllText(path));
                    yaml = YamlComments.Reapply(yaml, captured);
                } else {
                    yaml = Header() + yaml;
                }

                AtomicFile.WriteText(path, yaml);
                ConfigFileWatcher.NoteSelfWrite(path);
            } catch (Exception e) {
                Logger.LogError($"Failed to write {FileName} at {path}: {e.Message}. The ban is in effect for this session but will not survive a restart.");
            }
        }

        // ---- First-run creation and migration ---------------------------------------------------------------

        /// <summary>
        /// Builds the contents of a brand new Bans.yaml, folding in the embedded seed and anything the server
        /// already had in the pre-collapse KnownCheaters.yaml.
        ///
        /// Called only when the file does not exist, which is the whole once-only trigger the migration needs -
        /// no marker file, no version stamp to keep in sync. The old file is deliberately left on disk: this
        /// repo does not delete an admin's data to tidy up, and being able to look at what was migrated is
        /// worth more than a clean config folder.
        /// </summary>
        internal static string BuildInitialFile() {
            List<DataObjects.BanEntry> entries = new List<DataObjects.BanEntry>();
            HashSet<string> seen = new HashSet<string>();
            string now = BanTime.Now();

            foreach (DataObjects.KnownCheaterEntry seed in ReadLegacyList(ReadEmbeddedSeed(), "the embedded seed")) {
                if (!seen.Add(PlatformIds.Normalize(seed.Id))) { continue; }
                entries.Add(LegacyToEntry(seed, BanSources.Builtin, now));
            }

            string legacyPath = ValConfig.KnownCheatersFilePath;
            int migrated = 0;
            if (File.Exists(legacyPath)) {
                string legacyStamp = BanTime.Stamp(File.GetLastWriteTimeUtc(legacyPath));
                string text = null;
                try {
                    text = File.ReadAllText(legacyPath);
                } catch (Exception e) {
                    Logger.LogWarning($"Could not read {ValConfig.KnownCheatersFileName} to migrate it: {e.Message}. Any entries in it will not carry over.");
                }
                foreach (DataObjects.KnownCheaterEntry legacy in ReadLegacyList(text, ValConfig.KnownCheatersFileName)) {
                    if (!seen.Add(PlatformIds.Normalize(legacy.Id))) { continue; }
                    entries.Add(LegacyToEntry(legacy, BanSources.Legacy, legacyStamp));
                    migrated++;
                }
                Logger.LogInfo($"Migrated {migrated} entr{(migrated == 1 ? "y" : "ies")} from {ValConfig.KnownCheatersFileName} into {FileName}. "
                             + $"{ValConfig.KnownCheatersFileName} is no longer read and can be deleted.");
            }

            DataObjects.BansFile file = new DataObjects.BansFile { Version = 1, Bans = entries };
            return Header() + DataObjects.yamlserializer.Serialize(file);
        }

        /// <summary>
        /// A legacy entry carried no category, and the only thing that list was ever used for was cheating -
        /// KnownCheaterTracker was fed exclusively by BanHost, from the cheat report and guard paths. So
        /// "cheating" is a faithful reading of what the entry meant, not a guess.
        /// </summary>
        private static DataObjects.BanEntry LegacyToEntry(DataObjects.KnownCheaterEntry legacy, string source, string stamp) {
            return new DataObjects.BanEntry {
                Id = legacy.Id,
                Categories = new List<string> { BanCategories.Name(BanCategory.Cheating) },
                Reason = string.IsNullOrEmpty(legacy.Reason) ? "Imported with no reason recorded" : legacy.Reason,
                AddedUtc = stamp,
                AddedBy = source,
                Source = source,
                // Neither a shipped seed entry nor an inherited one is something this server witnessed, so
                // neither may be published to the ban network. See BanSources.Reportable.
                Share = false,
            };
        }

        private static List<DataObjects.KnownCheaterEntry> ReadLegacyList(string yaml, string context) {
            if (string.IsNullOrWhiteSpace(yaml)) { return new List<DataObjects.KnownCheaterEntry>(); }
            try {
                List<DataObjects.KnownCheaterEntry> parsed =
                    DataObjects.yamldeserializer.Deserialize<List<DataObjects.KnownCheaterEntry>>(yaml);
                return parsed ?? new List<DataObjects.KnownCheaterEntry>();
            } catch (Exception e) {
                Logger.LogWarning($"Could not parse {context}: {e.Message}. Nothing was imported from it.");
                return new List<DataObjects.KnownCheaterEntry>();
            }
        }

        private static string ReadEmbeddedSeed() {
            try {
                using (Stream stream = typeof(ValheimEnforcer).Assembly.GetManifestResourceStream(EmbeddedSeedName)) {
                    if (stream == null) {
                        Logger.LogWarning($"Embedded ban seed '{EmbeddedSeedName}' was not found.");
                        return null;
                    }
                    using (StreamReader reader = new StreamReader(stream)) { return reader.ReadToEnd(); }
                }
            } catch (Exception e) {
                Logger.LogWarning($"Failed to read the embedded ban seed: {e.Message}");
                return null;
            }
        }

        private static string Header() {
            return
@"#################################################
# Valheim Enforcer - Bans (server side)
#
# Every ban this server issues or inherits. Edit it freely; comments are kept.
# Only the enforcer-ban commands rewrite this file - the ban network never does.
#
# categories: cheating, rulebreaking, griefing, toxic (one or more)
# source:     local (an admin), auto (a detector), builtin (shipped), legacy (migrated)
# expiresUtc: leave unset for a permanent ban
# share:      false keeps a ban off the ban network even when reporting is on
#
# To let someone in that the ban network banned, do not edit this file - use
# enforcer-ban-allow, which writes BanNetwork/Overrides.yaml.
#################################################
";
        }
    }
}
