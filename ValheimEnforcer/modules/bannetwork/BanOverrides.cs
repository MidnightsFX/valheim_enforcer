using System;
using System.Collections.Generic;
using System.IO;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.bannetwork {

    /// <summary>One owner decision, parsed.</summary>
    internal sealed class OverrideRecord {
        internal string Id;
        internal string Hash;
        internal bool Allow;
        /// <summary>Empty means the override applies to every category.</summary>
        internal List<BanCategory> Categories = new List<BanCategory>();
        internal string Reason;
        internal DateTime? AddedUtc;
        internal string AddedBy;

        internal string Subject {
            get { return string.IsNullOrEmpty(Id) ? Hash : Id; }
        }

        internal bool Covers(List<BanCategory> categories) {
            if (Categories == null || Categories.Count == 0) { return true; }
            return BanCategories.AnyIn(categories, Categories);
        }
    }

    /// <summary>
    /// Owns BanNetwork/Overrides.yaml: the server owner's decisions about entries the ban network published.
    ///
    /// This file exists so that "the list is editable by the server owner" can be true without the owner ever
    /// editing the pulled list. The pull rewrites its own file every cycle; an owner editing that file would
    /// be racing a timer. So the pulled list stays machine-owned and disposable, and every human decision
    /// lives here, where nothing overwrites it.
    ///
    /// An entry is addressable by raw platform id or by the network's subject hash, because an owner who wants
    /// to pre-emptively clear someone has the id, while an owner reacting to an opaque pulled entry has only
    /// the hash.
    /// </summary>
    internal static class BanOverrides {

        internal const string FolderName = "BanNetwork";
        internal const string FileName = "Overrides.yaml";

        internal static string FolderPath {
            get {
                string path = Path.Combine(ValConfig.GetSecondaryConfigDirectoryPath(), FolderName);
                if (!Directory.Exists(path)) { Directory.CreateDirectory(path); }
                return path;
            }
        }

        internal static string FilePath {
            get { return Path.Combine(FolderPath, FileName); }
        }

        private static readonly List<OverrideRecord> Overrides = new List<OverrideRecord>();
        private static readonly object Gate = new object();

        internal static int Count {
            get { lock (Gate) { return Overrides.Count; } }
        }

        internal static void Initialize() {
            string path = FilePath;
            if (!File.Exists(path)) {
                lock (Gate) { Overrides.Clear(); }
                return;
            }
            try {
                LoadFromText(File.ReadAllText(path), announce: false);
                if (Count > 0) { Logger.LogInfo($"Ban overrides loaded ({Count})."); }
            } catch (Exception e) {
                string backup = ConfigFileBackup.BackupUnreadable(path);
                Logger.LogError($"Could not read {FileName}: {ConfigFileBackup.DescribeParseFailure(e)}. A copy was kept at {backup}.");
            }
        }

        /// <summary>
        /// Replaces the in-memory set. A parse failure leaves the previous set in place, so a broken hand edit
        /// cannot silently re-ban everyone the owner had cleared.
        /// </summary>
        internal static void LoadFromText(string yaml, bool announce = true) {
            List<OverrideRecord> parsed = new List<OverrideRecord>();
            DataObjects.OverridesFile file;
            try {
                file = DataObjects.yamlconfigdeserializer.Deserialize<DataObjects.OverridesFile>(yaml);
            } catch (Exception e) {
                Logger.LogError($"{FileName} could not be parsed and was NOT applied: {ConfigFileBackup.DescribeParseFailure(e)}. "
                              + "The previously loaded overrides are still in effect.");
                return;
            }

            if (file != null && file.Overrides != null) {
                foreach (DataObjects.BanOverride entry in file.Overrides) {
                    if (entry == null) { continue; }
                    if (string.IsNullOrEmpty(entry.Id) && string.IsNullOrEmpty(entry.Hash)) {
                        Logger.LogWarning($"Skipping an override in {FileName} with neither an id nor a hash.");
                        continue;
                    }
                    string decision = (entry.Decision ?? "").Trim().ToLowerInvariant();
                    if (decision != "allow" && decision != "deny") {
                        Logger.LogWarning($"Skipping the override for {entry.Id ?? entry.Hash} in {FileName}: "
                                        + $"decision must be 'allow' or 'deny', not '{entry.Decision}'.");
                        continue;
                    }
                    parsed.Add(new OverrideRecord {
                        Id = entry.Id,
                        Hash = entry.Hash,
                        Allow = decision == "allow",
                        Categories = BanCategories.Parse(entry.Categories, FileName),
                        Reason = entry.Reason,
                        AddedUtc = BanTime.Parse(entry.AddedUtc),
                        AddedBy = entry.AddedBy,
                    });
                }
            }

            lock (Gate) {
                Overrides.Clear();
                Overrides.AddRange(parsed);
            }
            if (announce) { Logger.LogInfo($"Ban overrides reloaded ({parsed.Count})."); }
        }

        /// <summary>
        /// The override covering this account, or null. <paramref name="subjectHash"/> may be null before the
        /// hash for a connection has been worked out; id matching still applies in that window.
        /// </summary>
        internal static OverrideRecord Find(string hostId, string subjectHash) {
            lock (Gate) {
                foreach (OverrideRecord record in Overrides) {
                    if (!string.IsNullOrEmpty(record.Id) && PlatformIds.Matches(record.Id, hostId)) { return record; }
                    if (!string.IsNullOrEmpty(record.Hash) && !string.IsNullOrEmpty(subjectHash)
                        && string.Equals(record.Hash, subjectHash, StringComparison.OrdinalIgnoreCase)) {
                        return record;
                    }
                }
            }
            return null;
        }

        internal static List<OverrideRecord> All() {
            lock (Gate) { return new List<OverrideRecord>(Overrides); }
        }

        /// <summary>Adds or replaces an override, keyed on whichever of id/hash it carries.</summary>
        internal static void Set(OverrideRecord record) {
            if (record == null || string.IsNullOrEmpty(record.Subject)) { return; }
            if (record.AddedUtc == null) { record.AddedUtc = DateTime.UtcNow; }

            lock (Gate) {
                int existing = IndexOf(record.Subject);
                if (existing >= 0) { Overrides[existing] = record; } else { Overrides.Add(record); }
            }
            Save();
        }

        /// <summary>Removes any override matching this id or hash. Returns how many went.</summary>
        internal static int Clear(string idOrHash) {
            if (string.IsNullOrEmpty(idOrHash)) { return 0; }
            int removed;
            lock (Gate) {
                removed = Overrides.RemoveAll(record => Matches(record, idOrHash));
            }
            if (removed > 0) { Save(); }
            return removed;
        }

        private static int IndexOf(string idOrHash) {
            for (int i = 0; i < Overrides.Count; i++) {
                if (Matches(Overrides[i], idOrHash)) { return i; }
            }
            return -1;
        }

        private static bool Matches(OverrideRecord record, string idOrHash) {
            if (!string.IsNullOrEmpty(record.Id) && PlatformIds.Matches(record.Id, idOrHash)) { return true; }
            return !string.IsNullOrEmpty(record.Hash)
                && string.Equals(record.Hash, idOrHash, StringComparison.OrdinalIgnoreCase);
        }

        private static void Save() {
            string path = FilePath;
            try {
                List<DataObjects.BanOverride> entries = new List<DataObjects.BanOverride>();
                lock (Gate) {
                    foreach (OverrideRecord record in Overrides) {
                        entries.Add(new DataObjects.BanOverride {
                            Id = record.Id,
                            Hash = record.Hash,
                            Decision = record.Allow ? "allow" : "deny",
                            Categories = BanCategories.ToNames(record.Categories),
                            Reason = record.Reason,
                            AddedUtc = record.AddedUtc.HasValue ? BanTime.Stamp(record.AddedUtc.Value) : null,
                            AddedBy = record.AddedBy,
                        });
                    }
                }

                DataObjects.OverridesFile file = new DataObjects.OverridesFile { Version = 1, Overrides = entries };
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
                Logger.LogError($"Failed to write {FileName} at {path}: {e.Message}. The override applies for this session but will not survive a restart.");
            }
        }

        internal static string Header() {
            return
@"#################################################
# Valheim Enforcer - Ban Network Overrides
#
# Your decisions about players the ban network listed. This file is yours; the
# hourly pull never rewrites it.
#
# Identify a player by 'id' (their platform id) or by 'hash' (the subject hash
# printed by enforcer-ban-list for an entry this server has never seen).
#
#   decision: allow   let them in even though the network banned them
#   decision: deny    ban them even though the network did not
#   categories:       optional - limit the override to these categories only
#
# enforcer-ban-allow / enforcer-ban-deny / enforcer-ban-override-clear edit this
# file for you, and keep your comments.
#################################################
";
        }

        internal static string EmptyFile() {
            return Header() + DataObjects.yamlserializer.Serialize(
                new DataObjects.OverridesFile { Version = 1, Overrides = new List<DataObjects.BanOverride>() });
        }
    }
}
