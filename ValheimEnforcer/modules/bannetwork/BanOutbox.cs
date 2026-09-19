using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.bannetwork {

    /// <summary>
    /// Holds reports that have not reached the ban network yet.
    ///
    /// <para><b>Why it exists.</b> A ban is issued at a moment when the network may be unreachable, the key
    /// may not be approved yet, or the server may be about to stop. Without somewhere durable to put it, the
    /// report is simply lost and the rest of the network never hears about a cheater this server caught.</para>
    ///
    /// <para><b>Append on enqueue, rewrite on success.</b> Enqueueing appends one short line, which is cheap
    /// and crash-safe enough - a torn append loses at most the last line, and that line is skipped on load.
    /// The full atomic rewrite happens only after a batch is accepted, where the cost is paid once per cycle
    /// rather than once per ban. Rewriting the whole file on every enqueue would be the wrong trade in both
    /// directions: slower, and a wider window in which a crash loses everything rather than one line.</para>
    ///
    /// <para><b>At-least-once, with idempotency.</b> A crash between the append and the POST replays the
    /// line; a crash between the POST and the rewrite does too. That is what reportId is for - the network
    /// upserts on it, so a replayed report updates a row instead of creating a second one.</para>
    /// </summary>
    internal static class BanOutbox {

        internal const string FileName = "outbox.yaml";

        /// <summary>
        /// Beyond this the oldest are dropped. A server with five thousand undelivered reports has a problem
        /// that a growing file in the config folder will not fix, and unbounded growth there is worse than
        /// losing the oldest of them.
        /// </summary>
        private const int MaxLines = 5000;

        /// <summary>Batch ceiling, matching what the endpoint accepts in one request.</summary>
        internal const int MaxBatch = 200;

        internal static string FilePath {
            get { return Path.Combine(BanOverrides.FolderPath, FileName); }
        }

        private static readonly List<DataObjects.BanReportLine> Pending = new List<DataObjects.BanReportLine>();
        private static readonly object Gate = new object();

        internal static int Count { get { lock (Gate) { return Pending.Count; } } }

        // ---- Lifecycle ------------------------------------------------------------------------------------

        internal static void Load() {
            lock (Gate) { Pending.Clear(); }
            string path = FilePath;
            if (!File.Exists(path)) { return; }

            int skipped = 0;
            try {
                foreach (string raw in File.ReadAllLines(path)) {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') { continue; }
                    DataObjects.BanReportLine parsed = ParseLine(line);
                    if (parsed == null) { skipped++; continue; }
                    lock (Gate) { Pending.Add(parsed); }
                }
            } catch (Exception e) {
                Logger.LogWarning($"Ban network: could not read {FileName} ({e.Message}); queued reports may be lost.");
                return;
            }
            if (Count > 0 || skipped > 0) {
                Logger.LogInfo($"Ban network: {Count} report(s) waiting to be sent"
                             + (skipped > 0 ? $"; {skipped} unreadable line(s) skipped." : "."));
            }
        }

        private static DataObjects.BanReportLine ParseLine(string line) {
            if (line.Length > BanNetworkJson.MaxLineBytes) { return null; }
            // Written by this server, but read back through the same strict gate as anything else: the file
            // sits in a config folder an admin can reach, and a half-written line after a crash is normal.
            if (!JsonWellFormed.Validate(line, out _)) { return null; }
            try {
                DataObjects.BanReportLine parsed =
                    DataObjects.yamldeserializer.Deserialize<DataObjects.BanReportLine>(line);
                if (parsed == null || string.IsNullOrEmpty(parsed.PlayerId) || string.IsNullOrEmpty(parsed.ReportId)) {
                    return null;
                }
                return parsed;
            } catch (Exception) {
                return null;
            }
        }

        // ---- Enqueue --------------------------------------------------------------------------------------

        /// <summary>
        /// Queues a ban for publication, if this server's policy allows it.
        ///
        /// Returns false, with a reason, when the report is deliberately not sent - which is the common case
        /// and not an error. The caller uses the reason to tell the admin why their ban stayed local.
        /// </summary>
        internal static bool Enqueue(BanRecord record, out string why) {
            why = null;
            if (record == null || string.IsNullOrEmpty(record.Id)) { why = "no account id"; return false; }

            if (ValConfig.EnableBanNetwork == null || !ValConfig.EnableBanNetwork.Value) {
                why = "the ban network is off";
                return false;
            }
            if (ValConfig.BanNetworkReportBans == null || !ValConfig.BanNetworkReportBans.Value) {
                why = "BanNetworkReportBans is off, so this server pulls without publishing";
                return false;
            }

            // The first and most important anti-amplification rule. A ban this server only knows about
            // because it pulled it must never go back out as though this server had witnessed it - that is
            // how a single report becomes an echo that looks like independent corroboration.
            if (!BanSources.Reportable(record.Source)) {
                why = $"its source is '{record.Source}', which this server did not witness";
                return false;
            }
            if (!record.Share) {
                why = "the ban is marked share: false";
                return false;
            }
            if (record.Source == BanSources.Auto
                && (ValConfig.BanNetworkReportAutoCheatBans == null || !ValConfig.BanNetworkReportAutoCheatBans.Value)) {
                why = "BanNetworkReportAutoCheatBans is off, so automatic detections stay local";
                return false;
            }

            return Add(BuildLine(record, "ban"), out why);
        }

        /// <summary>
        /// Queues a retraction. Deliberately looser than Enqueue: withdrawing a report this server may have
        /// published is always allowed, even when publishing new ones is switched off, because an admin who
        /// turns reporting off should still be able to take back what they already said.
        /// </summary>
        internal static bool EnqueueUnban(string hostId, out string why) {
            why = null;
            if (string.IsNullOrEmpty(hostId)) { why = "no account id"; return false; }
            if (ValConfig.EnableBanNetwork == null || !ValConfig.EnableBanNetwork.Value) {
                why = "the ban network is off";
                return false;
            }
            return Add(new DataObjects.BanReportLine {
                ReportId = Guid.NewGuid().ToString("N"),
                PlayerId = hostId,
                Op = "unban",
                OccurredUtc = BanTime.Now(),
            }, out why);
        }

        private static DataObjects.BanReportLine BuildLine(BanRecord record, string op) {
            bool shareName = ValConfig.BanNetworkSharePlayerName == null || ValConfig.BanNetworkSharePlayerName.Value;
            return new DataObjects.BanReportLine {
                ReportId = Guid.NewGuid().ToString("N"),
                PlayerId = record.Id,
                Op = op,
                Categories = BanCategories.ToNames(record.Categories),
                Reason = record.Reason,
                Name = shareName ? record.Name : null,
                OccurredUtc = record.AddedUtc.HasValue ? BanTime.Stamp(record.AddedUtc.Value) : BanTime.Now(),
                ExpiresUtc = record.ExpiresUtc.HasValue ? BanTime.Stamp(record.ExpiresUtc.Value) : null,
            };
        }

        private static bool Add(DataObjects.BanReportLine line, out string why) {
            why = null;
            string json;
            try {
                json = BanNetworkJson.WriteReportLine(line);
            } catch (Exception e) {
                why = $"the report could not be serialized: {e.Message}";
                return false;
            }

            // Validated before it is ever stored, let alone sent. A malformed body would be rejected by the
            // endpoint on every retry forever, so catching it here turns an infinite 400 loop into one line
            // in the log.
            if (!JsonWellFormed.Validate(json.Trim(), out string invalid)) {
                why = $"the report did not serialize to valid JSON ({invalid})";
                Logger.LogError($"Ban network: refusing to queue a malformed report: {invalid}");
                return false;
            }

            lock (Gate) {
                // Replace rather than duplicate: re-banning someone should update what the network is told,
                // not queue a second opinion from the same server.
                Pending.RemoveAll(existing => PlatformIds.Matches(existing.PlayerId, line.PlayerId));
                Pending.Add(line);
                if (Pending.Count > MaxLines) {
                    int drop = Pending.Count - MaxLines;
                    Pending.RemoveRange(0, drop);
                    Logger.LogWarning($"Ban network: the outbox reached {MaxLines} reports; the oldest {drop} were dropped.");
                    Rewrite();
                    return true;
                }
            }

            try {
                File.AppendAllText(FilePath, json, new UTF8Encoding(false));
            } catch (Exception e) {
                Logger.LogWarning($"Ban network: could not append to {FileName} ({e.Message}). "
                                + "The report is queued in memory but will not survive a restart.");
            }
            return true;
        }

        // ---- Sending --------------------------------------------------------------------------------------

        /// <summary>A snapshot of what to send next. Nothing is removed until the network accepts it.</summary>
        internal static List<DataObjects.BanReportLine> Take(int max) {
            lock (Gate) {
                int count = Math.Min(max, Pending.Count);
                return Pending.GetRange(0, count);
            }
        }

        /// <summary>
        /// Drops the reports the network accepted and rewrites the file with what is left.
        ///
        /// The atomic rewrite happens here, on success, rather than on every enqueue - see the class note.
        /// </summary>
        internal static void MarkDelivered(List<string> reportIds) {
            if (reportIds == null || reportIds.Count == 0) { return; }
            HashSet<string> delivered = new HashSet<string>(reportIds);
            lock (Gate) {
                Pending.RemoveAll(line => delivered.Contains(line.ReportId));
            }
            Rewrite();
        }

        private static void Rewrite() {
            try {
                StringBuilder sb = new StringBuilder();
                sb.Append(Header());
                foreach (DataObjects.BanReportLine line in Take(int.MaxValue)) {
                    sb.Append(BanNetworkJson.WriteReportLine(line));
                }
                AtomicFile.WriteText(FilePath, sb.ToString());
            } catch (Exception e) {
                Logger.LogWarning($"Ban network: could not rewrite {FileName}: {e.Message}");
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
"# ValheimEnforcer - ban reports waiting to be sent to the ban network. GENERATED.\n"
+ "# Lines disappear as the network accepts them. A report here has not been published yet.\n";
        }
    }
}
