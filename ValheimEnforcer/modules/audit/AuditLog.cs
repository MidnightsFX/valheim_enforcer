using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using UnityEngine;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using ValheimEnforcer.common;
using Logger = ValheimEnforcer.Logger;

namespace ValheimEnforcer.modules.audit {

    /// <summary>
    /// Where recorded activity goes, and how it comes back.
    ///
    /// One file per UTC day under BepInEx/config/ValheimEnforcer/Audit, appended to from a dedicated
    /// background thread - the same shape as <see cref="modules.character.CharacterStore"/> and for the same
    /// reason: this writes on every item change, container change and damage spike on the server, and none of
    /// that may touch the disk on the main thread.
    ///
    /// Events sit in an in-memory buffer until they are flushed. A query reads the day files for its window
    /// AND that buffer, so a report never misses the last thirty seconds. The two cannot double-count: the
    /// buffer holds only what has not been written yet, and entries leave it only once their append has
    /// succeeded.
    ///
    /// Retention deletes at most one day-file per pass rather than sweeping everything over the limit at
    /// once. A server that has been off for a month would otherwise spend its first tick deleting thirty
    /// files; this drains a backlog at roughly twelve an hour and is invisible in the normal case, where
    /// there is exactly one file to remove per day.
    /// </summary>
    internal static class AuditLog {

        internal const string AuditFolder = "Audit";
        private const string FilePrefix = "audit-";
        private const string FileSuffix = ".yaml";
        internal const string DayFormat = "yyyy-MM-dd";

        /// <summary>Flush early once the buffer reaches this, so a burst is not held for the whole interval.</summary>
        private const int FlushEventCount = 500;

        /// <summary>
        /// Absolute ceiling on the unflushed buffer. Only reachable if the disk is failing or the writer
        /// thread has died; at that point dropping the oldest events is better than growing until the server
        /// falls over. It is reported rather than silently absorbed.
        /// </summary>
        private const int BufferHardCap = 50000;

        private static readonly TimeSpan PurgeInterval = TimeSpan.FromMinutes(5);

        /// <summary>How long the worker sleeps between wake-ups when there is nothing to do.</summary>
        private const int IdlePollMs = 1000;

        // ---- State ----------------------------------------------------------------------------------------

        private static readonly object bufferLock = new object();

        /// <summary>Events not yet on disk, oldest first. Appends only ever land at the end.</summary>
        private static readonly List<AuditEvent> buffer = new List<AuditEvent>();

        private static readonly ConcurrentQueue<Action> backgroundJobs = new ConcurrentQueue<Action>();
        private static readonly ConcurrentQueue<Action> mainThreadWork = new ConcurrentQueue<Action>();

        private static Thread worker;
        private static volatile bool running;
        private static AutoResetEvent wake;
        private static GameObject host;
        private static DateTime lastPurge = DateTime.MinValue;
        private static bool warnedBufferFull;

        /// <summary>
        /// Writes one event per line as a JSON-compatible flow mapping. JSON is valid YAML, so this is read
        /// back by an ordinary YAML deserializer while staying one line per event - which is what makes a day
        /// file appendable, tailable, and greppable without any tooling.
        /// </summary>
        private static readonly ISerializer LineSerializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults)
            .DisableAliases()
            .JsonCompatible()
            .Build();

        // ---- Lifecycle ------------------------------------------------------------------------------------

        /// <summary>
        /// Starts the writer. Called from the ZNet.Start patch rather than plugin Awake, so a client that
        /// never hosts anything never starts a thread, and a listen host that re-hosts gets a fresh one.
        /// </summary>
        internal static void Initialize() {
            if (!AuditPolicy.Active()) { return; }
            if (worker != null) { return; }

            try {
                Directory.CreateDirectory(Root());
            } catch (Exception e) {
                Logger.LogWarning($"Could not create the audit folder; the audit log is inactive: {e.Message}");
                return;
            }

            if (host == null) {
                host = new GameObject("VE_AuditLog");
                UnityEngine.Object.DontDestroyOnLoad(host);
                host.hideFlags = HideFlags.HideAndDontSave;
                host.AddComponent<AuditLogBehaviour>();
            }

            running = true;
            wake = new AutoResetEvent(false);
            worker = new Thread(WorkerLoop) { Name = "VE-AuditLog", IsBackground = true };
            worker.Start();
            Logger.LogInfo($"Audit log started; writing to {Root()} and keeping {ValConfig.AuditRetentionDays.Value} day(s).");
        }

        /// <summary>
        /// Stops the writer, flushing whatever is buffered first. A restart that loses the last thirty
        /// seconds of evidence would lose it exactly when somebody was being kicked.
        /// </summary>
        internal static void Shutdown() {
            if (worker == null) {
                // Still drain: events can have been buffered before the thread ever started.
                FlushOnce();
                Teardown();
                return;
            }

            running = false;
            try { wake?.Set(); } catch (Exception) { /* already disposed */ }
            try {
                if (!worker.Join(TimeSpan.FromSeconds(5))) {
                    Logger.LogWarning("The audit writer did not stop within five seconds; flushing on this thread instead.");
                }
            } catch (Exception e) {
                Logger.LogDebug($"Waiting for the audit writer to stop failed: {e.Message}");
            }

            FlushOnce(); // whatever the worker did not get to
            Teardown();
        }

        private static void Teardown() {
            worker = null;
            running = false;
            try { wake?.Close(); } catch (Exception) { }
            wake = null;
            if (host != null) { UnityEngine.Object.Destroy(host); host = null; }
            lastPurge = DateTime.MinValue;
            warnedBufferFull = false;
            while (backgroundJobs.TryDequeue(out _)) { }
            while (mainThreadWork.TryDequeue(out _)) { }
        }

        // ---- Producers ------------------------------------------------------------------------------------

        internal static void Enqueue(AuditEvent entry) {
            if (entry == null) { return; }
            bool full = false;
            int count;
            lock (bufferLock) {
                if (buffer.Count >= BufferHardCap) {
                    buffer.RemoveAt(0);
                    full = true;
                }
                buffer.Add(entry);
                count = buffer.Count;
            }

            if (full && !warnedBufferFull) {
                warnedBufferFull = true;
                Logger.LogWarning($"The audit buffer hit its {BufferHardCap} event ceiling and is dropping the oldest events. The writer thread is not keeping up or the disk is unwritable - audit history is incomplete from now on.");
            }
            if (count >= FlushEventCount) { try { wake?.Set(); } catch (Exception) { } }
        }

        /// <summary>
        /// Runs a piece of file work on the audit thread, off the main thread and clear of a flush.
        ///
        /// Falls back to running it inline when there is no worker - the audit folder could not be created,
        /// or the feature was switched on and the writer has not started. A command that queued its only
        /// answer onto a thread that does not exist would print nothing at all and look like a hang, which
        /// is a far worse failure than a brief stall on a server that is already not recording anything.
        /// </summary>
        internal static void SubmitJob(Action job) {
            if (job == null) { return; }
            if (worker == null) {
                Logger.LogDebug("The audit writer is not running; answering this request on the calling thread.");
                try {
                    job();
                } catch (Exception e) {
                    Logger.LogWarning($"An audit task failed: {e.Message}");
                }
                DrainMainThread(); // nothing else will, without the behaviour the worker brings up
                return;
            }
            backgroundJobs.Enqueue(job);
            try { wake?.Set(); } catch (Exception) { }
        }

        /// <summary>Hands a result back to the main thread, where ZNet and the RPCs may be touched.</summary>
        internal static void QueueMainThread(Action work) {
            if (work != null) { mainThreadWork.Enqueue(work); }
        }

        internal static void DrainMainThread() {
            while (mainThreadWork.TryDequeue(out Action work)) {
                try {
                    work();
                } catch (Exception e) {
                    Logger.LogWarning($"An audit main-thread task failed: {e.Message}");
                }
            }
        }

        // ---- Worker ---------------------------------------------------------------------------------------

        private static void WorkerLoop() {
            int intervalMs = Math.Max(1, ValConfig.AuditFlushIntervalSeconds.Value) * 1000;
            DateTime nextFlush = DateTime.UtcNow.AddMilliseconds(intervalMs);

            while (running) {
                try {
                    wake?.WaitOne(IdlePollMs);
                } catch (Exception) {
                    break; // handle disposed under us during shutdown
                }

                try {
                    while (backgroundJobs.TryDequeue(out Action job)) {
                        try {
                            job();
                        } catch (Exception e) {
                            Logger.LogWarning($"An audit background task failed: {e.Message}");
                        }
                    }

                    int buffered;
                    lock (bufferLock) { buffered = buffer.Count; }
                    if (buffered >= FlushEventCount || DateTime.UtcNow >= nextFlush) {
                        FlushOnce();
                        // Re-read the interval each cycle so an admin editing it mid-session takes effect.
                        intervalMs = Math.Max(1, ValConfig.AuditFlushIntervalSeconds.Value) * 1000;
                        nextFlush = DateTime.UtcNow.AddMilliseconds(intervalMs);
                    }

                    if (DateTime.UtcNow - lastPurge >= PurgeInterval) {
                        lastPurge = DateTime.UtcNow;
                        PurgeOneExpiredDay();
                    }
                } catch (Exception e) {
                    // The thread must survive anything: it holds the only copy of the unflushed buffer.
                    Logger.LogWarning($"The audit writer hit an error and is continuing: {e.Message}");
                }
            }

            FlushOnce();
        }

        /// <summary>
        /// Writes everything currently buffered. Entries are removed only after their append succeeded, so a
        /// failed write leaves them buffered for the next attempt instead of losing them.
        /// </summary>
        private static void FlushOnce() {
            AuditEvent[] batch;
            lock (bufferLock) {
                if (buffer.Count == 0) { return; }
                batch = buffer.ToArray();
            }

            int written = 0;
            try {
                foreach (IGrouping<DateTime, AuditEvent> day in batch.GroupBy(e => e.TimeUtc().Date)) {
                    string path = PathForDay(day.Key);
                    List<string> lines = new List<string>();
                    foreach (AuditEvent entry in day) {
                        string line = Format(entry);
                        if (line != null) { lines.Add(line); }
                    }
                    if (lines.Count == 0) { continue; }
                    File.AppendAllLines(path, lines, Encoding.UTF8);
                    written += lines.Count;
                }
            } catch (Exception e) {
                Logger.LogWarning($"Could not write the audit log; {batch.Length} event(s) stay buffered for the next flush: {e.Message}");
                return;
            }

            lock (bufferLock) {
                // Appends only ever land at the end, so the first batch.Length entries are exactly the ones
                // just written - even if producers added more while the append was in flight.
                int remove = Math.Min(batch.Length, buffer.Count);
                buffer.RemoveRange(0, remove);
            }
            if (written > 0) { Logger.LogDebug($"Audit log flushed {written} event(s)."); }
        }

        /// <summary>
        /// Events rendered in exactly the on-disk form. Used by a download so what an admin receives is
        /// byte-for-byte the shape of the server's own files, and can be read back by the same tools.
        /// </summary>
        internal static List<string> ToLines(IEnumerable<AuditEvent> events) {
            List<string> lines = new List<string>();
            if (events == null) { return lines; }
            foreach (AuditEvent entry in events) {
                string line = Format(entry);
                if (line != null) { lines.Add(line); }
            }
            return lines;
        }

        private static string Format(AuditEvent entry) {
            try {
                // JsonCompatible still emits a document-terminating newline; a day file is one event per line.
                return LineSerializer.Serialize(entry).Replace("\r", "").Replace("\n", "").Trim();
            } catch (Exception e) {
                Logger.LogDebug($"Could not serialize an audit event: {e.Message}");
                return null;
            }
        }

        // ---- Retention ------------------------------------------------------------------------------------

        /// <summary>
        /// Deletes the single oldest day-file that is past the retention window, if there is one.
        ///
        /// Only files matching this module's own naming inside its own folder are ever considered, and the
        /// date is taken from the name rather than from the filesystem - a file whose name does not parse is
        /// left alone rather than guessed at.
        /// </summary>
        private static void PurgeOneExpiredDay() {
            try {
                if (!Directory.Exists(Root())) { return; }
                int keepDays = Math.Max(1, ValConfig.AuditRetentionDays.Value);
                DateTime cutoff = DateTime.UtcNow.Date.AddDays(-(keepDays - 1));

                string oldestPath = null;
                DateTime oldestDay = DateTime.MaxValue;
                foreach (string path in Directory.GetFiles(Root(), FilePrefix + "*" + FileSuffix)) {
                    if (!TryDayOf(path, out DateTime day)) { continue; }
                    if (day >= cutoff) { continue; }
                    if (day >= oldestDay) { continue; }
                    oldestDay = day;
                    oldestPath = path;
                }

                if (oldestPath == null) { return; }
                File.Delete(oldestPath);
                Logger.LogInfo($"Audit retention removed {Path.GetFileName(oldestPath)} (older than the {keepDays} day window).");
            } catch (Exception e) {
                Logger.LogWarning($"Audit retention could not remove an expired day file: {e.Message}");
            }
        }

        // ---- Reading --------------------------------------------------------------------------------------

        /// <summary>
        /// Events for one character between two times, oldest first.
        ///
        /// Accounts are compared with <see cref="PlatformIds.Matches"/>, not string equality: an event is
        /// filed under the socket's host name while a save may be filed under a differently prefixed spelling
        /// of the same account, and an admin types whichever one a listing showed them.
        ///
        /// <paramref name="capped"/> reports that the cap cut the result short. Callers must say so - a
        /// truncated report that looks complete is worse than no report.
        /// </summary>
        internal static List<AuditEvent> Query(string account, string character, DateTime fromUtc, DateTime toUtc,
                                               string[] kinds, int cap, out bool capped) {
            capped = false;
            List<AuditEvent> found = new List<AuditEvent>();
            HashSet<string> kindFilter = kinds == null || kinds.Length == 0
                ? null : new HashSet<string>(kinds, StringComparer.Ordinal);

            // Files first: they hold everything up to the last flush.
            try {
                if (Directory.Exists(Root())) {
                    for (DateTime day = fromUtc.Date; day <= toUtc.Date; day = day.AddDays(1)) {
                        string path = PathForDay(day);
                        if (!File.Exists(path)) { continue; }
                        ReadDayInto(path, account, character, fromUtc, toUtc, kindFilter, found);
                    }
                }
            } catch (Exception e) {
                Logger.LogWarning($"Could not read the audit history for {character}: {e.Message}");
            }

            // Then whatever has not reached disk yet, so a report is current to this second.
            lock (bufferLock) {
                foreach (AuditEvent entry in buffer) {
                    if (Matches(entry, account, character, fromUtc, toUtc, kindFilter)) { found.Add(entry); }
                }
            }

            found.Sort((left, right) => string.CompareOrdinal(left.T, right.T));
            if (cap > 0 && found.Count > cap) {
                capped = true;
                // Keep the most recent, which is what an investigation is asking about.
                found = found.GetRange(found.Count - cap, cap);
            }
            return found;
        }

        private static void ReadDayInto(string path, string account, string character,
                                        DateTime fromUtc, DateTime toUtc, HashSet<string> kinds,
                                        List<AuditEvent> into) {
            // A fresh deserializer per file: a query can run on the audit thread (a download) or the main
            // thread (a command), and YamlDotNet makes no concurrency promise about a shared instance.
            IDeserializer reader = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            using (StreamReader stream = new StreamReader(path, Encoding.UTF8)) {
                string line;
                while ((line = stream.ReadLine()) != null) {
                    if (line.Length == 0) { continue; }
                    AuditEvent entry;
                    try {
                        entry = reader.Deserialize<AuditEvent>(line);
                    } catch (Exception) {
                        continue; // one unreadable line must not cost the rest of the day
                    }
                    if (entry != null && Matches(entry, account, character, fromUtc, toUtc, kinds)) { into.Add(entry); }
                }
            }
        }

        private static bool Matches(AuditEvent entry, string account, string character,
                                    DateTime fromUtc, DateTime toUtc, HashSet<string> kinds) {
            if (entry == null) { return false; }
            if (kinds != null && !kinds.Contains(entry.Kind ?? "")) { return false; }
            if (!string.IsNullOrEmpty(account) && !PlatformIds.Matches(entry.Acct, account)) { return false; }
            if (!string.IsNullOrEmpty(character)
                && !string.Equals(entry.Character, character, StringComparison.OrdinalIgnoreCase)) { return false; }
            DateTime when = entry.TimeUtc();
            return when != DateTime.MinValue && when >= fromUtc && when <= toUtc;
        }

        /// <summary>One row per day the server still holds, newest first. Used by enforcer-audit-available.</summary>
        internal static List<DayInfo> AvailableDays() {
            List<DayInfo> days = new List<DayInfo>();
            try {
                if (!Directory.Exists(Root())) { return days; }
                foreach (string path in Directory.GetFiles(Root(), FilePrefix + "*" + FileSuffix)) {
                    if (!TryDayOf(path, out DateTime day)) { continue; }
                    days.Add(new DayInfo { Day = day, Bytes = new FileInfo(path).Length });
                }
            } catch (Exception e) {
                Logger.LogWarning($"Could not list the audit history: {e.Message}");
            }
            days.Sort((left, right) => right.Day.CompareTo(left.Day));
            return days;
        }

        internal sealed class DayInfo {
            internal DateTime Day;
            internal long Bytes;
        }

        // ---- Paths ----------------------------------------------------------------------------------------

        internal static string Root() {
            return Path.Combine(ValConfig.GetSecondaryConfigDirectoryPath(), AuditFolder);
        }

        /// <summary>
        /// The file for a day. The name is composed here from a DateTime and never taken from a caller, which
        /// is what makes it safe to serve a client-requested range - a request names a date, not a file.
        /// </summary>
        internal static string PathForDay(DateTime day) {
            return Path.Combine(Root(), FilePrefix + day.ToString(DayFormat, CultureInfo.InvariantCulture) + FileSuffix);
        }

        internal static bool TryDayOf(string path, out DateTime day) {
            day = DateTime.MinValue;
            string name = Path.GetFileNameWithoutExtension(path);
            if (name == null || !name.StartsWith(FilePrefix, StringComparison.Ordinal)) { return false; }
            string stamp = name.Substring(FilePrefix.Length);
            return DateTime.TryParseExact(stamp, DayFormat, CultureInfo.InvariantCulture,
                                          DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out day);
        }
    }

    /// <summary>
    /// Drains work the audit thread handed back for the main thread - sending an RPC, touching ZNet - the
    /// same arrangement FullSyncSchedulerBehaviour uses for the character store's queues.
    /// </summary>
    internal class AuditLogBehaviour : MonoBehaviour {
        public void Update() {
            AuditLog.DrainMainThread();
        }
    }
}
