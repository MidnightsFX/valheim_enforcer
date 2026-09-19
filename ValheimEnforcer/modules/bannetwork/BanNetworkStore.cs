using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.bannetwork {

    /// <summary>
    /// Owns BanNetwork/NetworkBans.yaml: this server's copy of the network list.
    ///
    /// <para><b>Machine-owned, and that is the whole point of it being a separate file.</b> Every pull
    /// rewrites or appends here. If the pulled list lived in the same file an owner hand-edits, the hourly
    /// cycle would be racing their text editor and the file watcher would bounce their save back at them.
    /// So the network's opinion lives here, disposable, and every human decision lives in Overrides.yaml,
    /// where nothing overwrites it.</para>
    ///
    /// <para><b>Append-only NDJSON, compacted.</b> One JSON flow mapping per line, the AuditLog day-file
    /// convention. Appending a delta is cheap and crash-safe in a way that rewriting several thousand entries
    /// hourly is not; a torn append loses at most the last line, which is skipped on load. Replaying the file
    /// in order gives the live set, because a later line for a subject supersedes an earlier one - which is
    /// exactly the semantics of the feed's seq.</para>
    /// </summary>
    internal static class BanNetworkStore {

        internal const string FileName = "NetworkBans.yaml";

        internal static string FilePath {
            get { return Path.Combine(BanOverrides.FolderPath, FileName); }
        }

        /// <summary>
        /// Compaction threshold. Entries change - a new reporter, a widened category set - and each change
        /// is another line for the same subject, so the file grows faster than the list does.
        /// </summary>
        private const int CompactFactor = 3;
        private const int CompactFloor = 256;

        /// <summary>Live entries by subject. Revoked ones are removed, which is what a tombstone means.</summary>
        private static readonly Dictionary<string, NetworkBanRecord> Live = new Dictionary<string, NetworkBanRecord>();

        private static readonly object Gate = new object();
        private static int linesOnDisk;

        internal static int Count { get { lock (Gate) { return Live.Count; } } }

        // ---- Loading --------------------------------------------------------------------------------------

        internal static void Load() {
            lock (Gate) {
                Live.Clear();
                linesOnDisk = 0;
            }

            string path = FilePath;
            if (!File.Exists(path)) { return; }

            int malformed = 0;
            try {
                foreach (string line in File.ReadAllLines(path)) {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed[0] == '#') { continue; }
                    lock (Gate) { linesOnDisk++; }
                    NetworkBanRecord record = BanNetworkJson.ParseLine(trimmed, out string error);
                    if (record == null) {
                        malformed++;
                        Logger.LogDebug($"Ban network: skipping a line of {FileName}: {error}");
                        continue;
                    }
                    ApplyToMap(record);
                }
            } catch (Exception e) {
                Logger.LogWarning($"Ban network: could not read {FileName} ({e.Message}). "
                                + "The list will rebuild on the next pull.");
                return;
            }

            if (malformed > 0) {
                Logger.LogWarning($"Ban network: {malformed} unreadable line(s) in {FileName} were skipped.");
            }
            Logger.LogInfo($"Ban network: {Count} entr{(Count == 1 ? "y" : "ies")} loaded from the cached network list.");
        }

        // ---- Queries --------------------------------------------------------------------------------------

        /// <summary>The live entry for a subject, or null. Revoked subjects are absent, not returned.</summary>
        internal static NetworkBanRecord Find(string subject) {
            if (string.IsNullOrEmpty(subject)) { return null; }
            lock (Gate) {
                return Live.TryGetValue(subject.ToLowerInvariant(), out NetworkBanRecord record) ? record : null;
            }
        }

        internal static List<NetworkBanRecord> All() {
            lock (Gate) { return new List<NetworkBanRecord>(Live.Values); }
        }

        // ---- Applying a pull ------------------------------------------------------------------------------

        /// <summary>
        /// Applies a batch of pulled entries: appends them to the file and folds them into the live map.
        /// Returns the subjects that became newly enforceable, for mid-session enforcement.
        /// </summary>
        internal static List<NetworkBanRecord> Apply(List<NetworkBanRecord> batch,
                                                     List<BanCategory> enforced, int minReporters) {
            List<NetworkBanRecord> newlyEnforceable = new List<NetworkBanRecord>();
            if (batch == null || batch.Count == 0) { return newlyEnforceable; }

            StringBuilder appended = new StringBuilder();
            foreach (NetworkBanRecord record in batch) {
                if (record == null || string.IsNullOrEmpty(record.Subject)) { continue; }

                bool wasEnforceable;
                lock (Gate) {
                    wasEnforceable = Live.TryGetValue(record.Subject, out NetworkBanRecord existing)
                                     && existing.Enforceable(enforced, minReporters);
                }
                ApplyToMap(record);
                if (!wasEnforceable && record.Enforceable(enforced, minReporters)) {
                    newlyEnforceable.Add(record);
                }
                appended.Append(BanNetworkJson.WriteLine(record));
            }

            if (appended.Length > 0) {
                try {
                    // Appended rather than rewritten: an hourly rewrite of the whole list is both slower and
                    // a wider window in which a crash loses everything, where a torn append loses one line.
                    File.AppendAllText(FilePath, appended.ToString(), new UTF8Encoding(false));
                    lock (Gate) { linesOnDisk += batch.Count; }
                } catch (Exception e) {
                    Logger.LogWarning($"Ban network: could not append to {FileName} ({e.Message}). "
                                    + "The entries are in effect for this session but will be re-fetched after a restart.");
                }
            }

            MaybeCompact();
            return newlyEnforceable;
        }

        private static void ApplyToMap(NetworkBanRecord record) {
            lock (Gate) {
                if (Live.TryGetValue(record.Subject, out NetworkBanRecord existing) && existing.Seq > record.Seq) {
                    // An out-of-order line, which a replay of an appended file can contain after a resync.
                    // The higher seq is the network's more recent word, so it wins.
                    return;
                }
                if (record.Revoked) {
                    Live.Remove(record.Subject);
                } else {
                    Live[record.Subject] = record;
                }
            }
        }

        /// <summary>
        /// Rewrites the file from the live map once it has accumulated enough superseded lines. Atomic,
        /// because this one is a full rewrite and a crash halfway through it would lose the list.
        /// </summary>
        private static void MaybeCompact() {
            int lines;
            int live;
            lock (Gate) {
                lines = linesOnDisk;
                live = Live.Count;
            }
            if (lines < CompactFloor || lines < live * CompactFactor) { return; }

            try {
                StringBuilder sb = new StringBuilder();
                sb.Append(Header());
                foreach (NetworkBanRecord record in All()) { sb.Append(BanNetworkJson.WriteLine(record)); }
                AtomicFile.WriteText(FilePath, sb.ToString());
                lock (Gate) { linesOnDisk = live; }
                Logger.LogDebug($"Ban network: compacted {FileName} from {lines} to {live} line(s).");
            } catch (Exception e) {
                Logger.LogWarning($"Ban network: could not compact {FileName}: {e.Message}");
            }
        }

        /// <summary>Drops the cached list entirely. Pairs with resetting the cursor.</summary>
        internal static void Purge() {
            lock (Gate) {
                Live.Clear();
                linesOnDisk = 0;
            }
            try {
                AtomicFile.WriteText(FilePath, Header());
            } catch (Exception e) {
                Logger.LogWarning($"Ban network: could not clear {FileName}: {e.Message}");
            }
        }

        internal static void EnsureFile() {
            try {
                if (!File.Exists(FilePath)) { AtomicFile.WriteText(FilePath, Header()); }
            } catch (Exception e) {
                Logger.LogWarning($"Ban network: could not create {FileName}: {e.Message}");
            }
        }

        private static string Header() {
            return
"# ValheimEnforcer - cached ban network list. GENERATED; edits here are lost on the next pull.\n"
+ "# One JSON object per line. To let one of these players in, do not edit this file - use\n"
+ "# enforcer-ban-allow, which writes BanNetwork/Overrides.yaml and is never overwritten.\n";
        }
    }
}
