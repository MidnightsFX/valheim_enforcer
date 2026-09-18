using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Threading;
using ValheimEnforcer.common;

// Folder name note: this is modules/archive rather than modules/backup because the repository .gitignore
// carries Visual Studio's stock "Backup*/" rule, and git on Windows matches it case-insensitively - so a
// modules/backup folder is silently ignored and never committed.
namespace ValheimEnforcer.modules.archive {

    /// <summary>
    /// Rolling, compressed archives of the world save and the character store.
    ///
    /// What this adds over what the game already does. Vanilla rotates its own backups
    /// (ZNet.ConsiderAutoBackup -> SaveSystem.ConsiderBackup) but keeps two of them, uncompressed, and only of
    /// the world: the Enforcer character saves - which are where every player's items, skills and progression
    /// live - are not in them at all. So restoring one of vanilla's backups puts the world back and leaves
    /// every character exactly as they were, which is its own kind of rollback. This keeps a configurable
    /// history of both together, compressed, so a restore is one archive rather than two halves from
    /// different moments.
    ///
    /// It is also genuinely expensive, which is why it defaults off and to a small keep count. A real world
    /// .db reaches 200 MB; vanilla's own rotation already costs around 1.2 GB of disk before this runs at all.
    ///
    /// Threading. The zip is written on a background thread, because compressing 200 MB on the main thread
    /// would stall the server for seconds. Everything that reads a ConfigEntry or touches a vanilla API -
    /// including SaveSystem, which owns a cache the save thread invalidates - happens on the main thread and
    /// is handed to the worker as a finished <see cref="Job"/> of plain strings. Same contract as
    /// CharacterStore's worker, and for the same reasons.
    ///
    /// Timing is the part that is easy to get wrong. ZNet.SaveWorld returns as soon as it has STARTED the
    /// save: the write happens on vanilla's own background thread afterwards, so archiving from a SaveWorld
    /// postfix would zip a world that is still being written. The trigger here is a two-step one - the
    /// postfix only notes that a save is running, and <see cref="Tick"/> waits for vanilla's save thread to
    /// finish before anything is read.
    /// </summary>
    internal static class SaveArchiver {

        /// <summary>Folder under the Enforcer config directory that archives are written to by default.</summary>
        internal const string ArchiveFolder = "Archives";

        /// <summary>Written while the zip is being built, renamed on success. A crash mid-write therefore
        /// leaves a .tmp that rotation ignores, rather than a truncated .zip it would count as a good one.</summary>
        private const string TempSuffix = ".tmp";

        private const string Extension = ".zip";

        /// <summary>Separates the world name from the timestamp. Chosen because a world name may contain
        /// spaces and underscores but the game's own backup suffixes already use '-'.</summary>
        private const string StampSeparator = "-";

        /// <summary>How long Tick waits for vanilla's save thread before giving up on this cycle. A save of a
        /// 200 MB world takes seconds; ten minutes is "something is wrong", not "still working".</summary>
        private static readonly TimeSpan SaveWaitBound = TimeSpan.FromMinutes(10);

        // ---------------------------------------------------------------------------------------------
        // Main-thread state
        // ---------------------------------------------------------------------------------------------

        private static bool saveRunning;
        private static DateTime saveStartedUtc;
        private static DateTime lastArchiveUtc = DateTime.MinValue;
        private static bool forcedNext;
        private static string forcedReason;

        internal static bool Enabled {
            get {
                return ValConfig.EnableSaveArchives != null && ValConfig.EnableSaveArchives.Value
                       && ZNet.instance != null && ZNet.instance.IsServer();
            }
        }

        /// <summary>
        /// Main thread. A world save has just been started - not finished. Called from the ZNet.SaveWorld
        /// postfix, which is the last point a patch can speak from before the write moves to vanilla's own
        /// thread.
        /// </summary>
        internal static void NoteSaveStarted() {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) { return; }
            saveRunning = true;
            saveStartedUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// Main thread. Asks for an archive of the next completed save regardless of the interval. Used by
        /// enforcer-archive-now, which an admin runs expecting something to happen now rather than in two
        /// hours.
        /// </summary>
        internal static void RequestNow(string reason) {
            forcedNext = true;
            forcedReason = reason;
        }

        /// <summary>True when an admin has asked for one and it has not been taken yet.</summary>
        internal static bool Pending {
            get { return forcedNext; }
        }

        /// <summary>
        /// Main thread, every frame, server-side. Cheap when there is nothing to do.
        /// </summary>
        internal static void Tick() {
            if (!saveRunning) { return; }
            if (!Enabled) { saveRunning = false; return; }

            ZNet znet = ZNet.instance;
            Thread saveThread = znet.m_saveThread;
            if (saveThread != null && saveThread.IsAlive) {
                if (DateTime.UtcNow - saveStartedUtc <= SaveWaitBound) { return; }
                // Not archived rather than archived from a half-written world. The next save gets another go.
                Logger.LogWarning($"Skipping this save archive: the game's own save thread has been running for over {SaveWaitBound.TotalMinutes:F0} minutes.");
                saveRunning = false;
                return;
            }

            saveRunning = false;
            if (!DueNow()) { return; }

            Job job = BuildJob();
            if (job == null) { return; }
            lastArchiveUtc = DateTime.UtcNow;
            forcedNext = false;
            forcedReason = null;
            EnsureWorker();
            jobs.Enqueue(job);
            signal.Set();
        }

        private static bool DueNow() {
            if (forcedNext) { return true; }
            int minutes = ValConfig.SaveArchiveIntervalMinutes != null ? ValConfig.SaveArchiveIntervalMinutes.Value : 120;
            if (lastArchiveUtc == DateTime.MinValue) { return true; }
            return DateTime.UtcNow - lastArchiveUtc >= TimeSpan.FromMinutes(Math.Max(1, minutes));
        }

        // Main thread. Everything the worker will need, read here because the worker may touch neither a
        // ConfigEntry (the file watcher can reload it underneath) nor a vanilla API.
        private static Job BuildJob() {
            string worldName = ZNet.instance.GetWorldName();
            if (string.IsNullOrEmpty(worldName)) {
                Logger.LogWarning("Not archiving this save: the server could not name its own world.");
                return null;
            }

            List<string> worldFiles = new List<string>();
            try {
                // The save's own file list, rather than a glob. It is the only thing that gets both layouts
                // right: a legacy world is a flat <World>.fwl plus <World>.db, and a current one is a
                // directory of _main.<n>.fwl2/.db2/.ok plus chunk files whose number changes every save.
                // Vanilla's save thread invalidates this cache in its finally block, so by the time the
                // thread has stopped - which Tick has just established - this is the save that was written.
                if (SaveSystem.TryGetSaveByName(worldName, SaveDataType.World, out SaveWithBackups save)
                    && save != null && !save.IsDeleted && save.PrimaryFile != null) {
                    foreach (string path in save.PrimaryFile.AllPaths) {
                        if (!string.IsNullOrEmpty(path)) { worldFiles.Add(path); }
                    }
                }
            } catch (Exception e) {
                Logger.LogWarning($"Not archiving this save: the world's file list could not be read ({e.Message}).");
                return null;
            }

            if (worldFiles.Count == 0) {
                Logger.LogWarning($"Not archiving this save: the game reports no files for world '{worldName}'.");
                return null;
            }

            string output = ValConfig.SaveArchivePath != null ? ValConfig.SaveArchivePath.Value : "";
            if (string.IsNullOrWhiteSpace(output)) {
                output = Path.Combine(ValConfig.GetSecondaryConfigDirectoryPath(), ArchiveFolder);
            }

            long maxTotalBytes = 0L;
            if (ValConfig.SaveArchiveMaxTotalMB != null && ValConfig.SaveArchiveMaxTotalMB.Value > 0) {
                maxTotalBytes = (long)ValConfig.SaveArchiveMaxTotalMB.Value * 1024L * 1024L;
            }

            return new Job {
                WorldName = worldName,
                WorldFiles = worldFiles,
                CharacterRoot = (ValConfig.SaveArchiveIncludeCharacters == null || ValConfig.SaveArchiveIncludeCharacters.Value)
                    ? ValConfig.CharacterFilePath : null,
                ConfigRoot = (ValConfig.SaveArchiveIncludeConfig == null || ValConfig.SaveArchiveIncludeConfig.Value)
                    ? ValConfig.GetSecondaryConfigDirectoryPath() : null,
                OutputDir = output,
                Compression = ParseCompression(),
                KeepCount = ValConfig.SaveArchiveKeepCount != null ? ValConfig.SaveArchiveKeepCount.Value : 5,
                MaxTotalBytes = maxTotalBytes,
                Reason = forcedReason,
            };
        }

        private static CompressionLevel ParseCompression() {
            string raw = ValConfig.SaveArchiveCompression != null ? ValConfig.SaveArchiveCompression.Value : "Fastest";
            if (string.Equals(raw, "Optimal", StringComparison.OrdinalIgnoreCase)) { return CompressionLevel.Optimal; }
            if (string.Equals(raw, "NoCompression", StringComparison.OrdinalIgnoreCase)) { return CompressionLevel.NoCompression; }
            return CompressionLevel.Fastest;
        }

        // ---------------------------------------------------------------------------------------------
        // Worker
        // ---------------------------------------------------------------------------------------------

        private sealed class Job {
            public string WorldName;
            public List<string> WorldFiles;
            public string CharacterRoot;   // null = not included
            public string ConfigRoot;      // null = not included
            public string OutputDir;
            public CompressionLevel Compression;
            public int KeepCount;
            public long MaxTotalBytes;     // 0 = no cap
            public string Reason;          // non-null when an admin asked for it
        }

        /// <summary>What the archiver is holding, for enforcer-memory and enforcer-archive-list.</summary>
        internal struct Stats {
            public int Written;
            public int Failed;
            public int Pruned;
            public DateTime LastUtc;
            public long LastBytes;
            public bool Running;
            public int QueueDepth;
        }

        private static readonly ConcurrentQueue<Job> jobs = new ConcurrentQueue<Job>();
        private static readonly AutoResetEvent signal = new AutoResetEvent(false);
        private static readonly object startLock = new object();
        private static Thread worker;
        private static volatile bool running;
        private static volatile bool busy;

        private static volatile int written;
        private static volatile int failed;
        private static volatile int pruned;
        private static long lastBytes;
        private static DateTime lastWrittenUtc = DateTime.MinValue;

        internal static Stats Snapshot() {
            return new Stats {
                Written = written,
                Failed = failed,
                Pruned = pruned,
                LastUtc = lastWrittenUtc,
                LastBytes = Interlocked.Read(ref lastBytes),
                Running = busy,
                QueueDepth = jobs.Count,
            };
        }

        private static void EnsureWorker() {
            if (running) { return; }
            lock (startLock) {
                if (running) { return; }
                running = true;
                worker = new Thread(WorkerLoop) { IsBackground = true, Name = "VE-SaveArchiver" };
                worker.Start();
            }
        }

        /// <summary>Stops the worker. Called from the server shutdown path.</summary>
        internal static void Shutdown() {
            saveRunning = false;
            forcedNext = false;
            if (!running) { return; }
            running = false;
            signal.Set();
            // Not joined for long: an archive in flight is a 200 MB zip, and blocking shutdown on it would
            // look like a hang. An unfinished one leaves a .tmp, which rotation ignores and the next run
            // deletes.
            worker?.Join(TimeSpan.FromSeconds(5));
        }

        private static void WorkerLoop() {
            while (running) {
                signal.WaitOne(1000);
                Drain();
            }
            Drain();
        }

        private static void Drain() {
            while (jobs.TryDequeue(out Job job)) {
                busy = true;
                StallWatch watch = StallWatch.StartBackground("archive.write");
                try {
                    RunJob(job);
                } catch (Exception e) {
                    failed++;
                    Logger.LogError($"Save archive failed: {e.GetType().Name}: {e.Message}");
                } finally {
                    watch.Stop();
                    busy = false;
                }
            }
        }

        private static void RunJob(Job job) {
            Directory.CreateDirectory(job.OutputDir);
            string stamp = DateTime.Now.ToString(SaveSystem.s_defaultDateFormat, CultureInfo.InvariantCulture);
            string baseName = SafeName(job.WorldName) + StampSeparator + stamp;
            string finalPath = Path.Combine(job.OutputDir, baseName + Extension);
            string tempPath = finalPath + TempSuffix;
            if (File.Exists(tempPath)) { File.Delete(tempPath); }

            int entries = 0;
            try {
                using (FileStream stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
                using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create)) {
                    foreach (string path in job.WorldFiles) {
                        if (AddFile(archive, path, "world/" + Path.GetFileName(path), job.Compression)) { entries++; }
                    }
                    if (job.CharacterRoot != null) { entries += AddTree(archive, job.CharacterRoot, "characters", job.Compression); }
                    if (job.ConfigRoot != null) { entries += AddConfig(archive, job.ConfigRoot, job.Compression); }
                }
            } catch (Exception) {
                // Never leave a half-written temp behind to be mistaken for anything.
                TryDelete(tempPath);
                throw;
            }

            if (File.Exists(finalPath)) { File.Delete(finalPath); } // same second, same name; the newer one wins
            File.Move(tempPath, finalPath);

            long size = new FileInfo(finalPath).Length;
            Interlocked.Exchange(ref lastBytes, size);
            lastWrittenUtc = DateTime.UtcNow;
            written++;
            Logger.LogInfo($"Wrote save archive {Path.GetFileName(finalPath)} ({entries} file(s), {Describe(size)})"
                           + (job.Reason == null ? "." : $" - {job.Reason}."));

            Prune(job);
        }

        // Opened with FileShare.ReadWrite | Delete on purpose: the game, a mod manager or a backup tool may
        // hold any of these open, and an exclusive open would turn one busy file into a failed archive.
        private static bool AddFile(ZipArchive archive, string path, string entryName, CompressionLevel level) {
            try {
                if (!File.Exists(path)) { return false; }
                ZipArchiveEntry entry = archive.CreateEntry(entryName.Replace('\\', '/'), level);
                entry.LastWriteTime = File.GetLastWriteTime(path);
                using (FileStream source = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                          FileShare.ReadWrite | FileShare.Delete, 64 * 1024))
                using (Stream target = entry.Open()) {
                    source.CopyTo(target, 64 * 1024);
                }
                return true;
            } catch (Exception e) {
                // One unreadable file does not fail the archive; an incomplete archive that says so beats no
                // archive at all, and the log names what is missing from it.
                Logger.LogWarning($"Save archive could not include {path}: {e.Message}");
                return false;
            }
        }

        private static int AddTree(ZipArchive archive, string root, string prefix, CompressionLevel level) {
            if (!Directory.Exists(root)) { return 0; }
            int added = 0;
            foreach (string path in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) {
                // A save mid-write is published by renaming a .tmp over the real file, so the .tmp is either
                // incomplete or about to stop existing. Neither is worth archiving.
                if (path.EndsWith(AtomicFile.TempSuffix, StringComparison.Ordinal)) { continue; }
                string relative = path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (AddFile(archive, path, prefix + "/" + relative, level)) { added++; }
            }
            return added;
        }

        // The Enforcer config files only - not the whole BepInEx config directory, which belongs to every
        // other mod on the server and is not this feature's to copy around.
        private static int AddConfig(ZipArchive archive, string root, CompressionLevel level) {
            if (!Directory.Exists(root)) { return 0; }
            int added = 0;
            foreach (string path in Directory.GetFiles(root, "*.yaml", SearchOption.TopDirectoryOnly)) {
                if (AddFile(archive, path, "config/" + Path.GetFileName(path), level)) { added++; }
            }
            // The plugin's own .cfg, named by BepInEx from the GUID rather than by us, so it is asked for
            // rather than composed.
            string cfg = ValConfig.cfg != null ? ValConfig.cfg.ConfigFilePath : null;
            if (!string.IsNullOrEmpty(cfg) && AddFile(archive, cfg, "config/" + Path.GetFileName(cfg), level)) { added++; }
            return added;
        }

        // ---------------------------------------------------------------------------------------------
        // Rotation
        // ---------------------------------------------------------------------------------------------

        /// <summary>One archive on disk, as rotation sees it.</summary>
        internal struct Existing {
            public string Path;
            public DateTime Stamp;
            public long Bytes;
        }

        /// <summary>
        /// Archives in <paramref name="directory"/>, newest first.
        ///
        /// Ordered by the timestamp parsed out of the FILENAME, never the filesystem's own mtime: a restore,
        /// a copy or a sync tool rewrites mtimes, and rotation deciding what to delete from a rewritten mtime
        /// would delete the wrong archive. A name that does not parse is not ours and is left alone entirely.
        /// </summary>
        internal static List<Existing> List(string directory) {
            List<Existing> found = new List<Existing>();
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) { return found; }
            foreach (string path in Directory.GetFiles(directory, "*" + Extension, SearchOption.TopDirectoryOnly)) {
                if (!TryStampOf(path, out DateTime stamp)) { continue; }
                long bytes;
                try {
                    bytes = new FileInfo(path).Length;
                } catch (Exception) {
                    continue;
                }
                found.Add(new Existing { Path = path, Stamp = stamp, Bytes = bytes });
            }
            found.Sort((a, b) => b.Stamp.CompareTo(a.Stamp));
            return found;
        }

        /// <summary>
        /// Reads the timestamp a name ends with, strictly. Anything else is not an archive of ours and is
        /// left alone.
        ///
        /// Every dash from the end is tried rather than only the last one, because the game's own timestamp
        /// format CONTAINS a dash - "yyyyMMdd-HHmmss" - so splitting on the last one hands back "143000",
        /// which does not parse, and every archive then looks like somebody else's file. Trying each in turn
        /// also keeps this working for a world name with dashes of its own, and does not bake in the length
        /// of a format that belongs to the game rather than to us.
        /// </summary>
        internal static bool TryStampOf(string path, out DateTime stamp) {
            stamp = DateTime.MinValue;
            string name = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(name)) { return false; }
            int cut = name.Length;
            while ((cut = name.LastIndexOf(StampSeparator, cut - 1, StringComparison.Ordinal)) > 0) {
                string tail = name.Substring(cut + 1);
                if (DateTime.TryParseExact(tail, SaveSystem.s_defaultDateFormat, CultureInfo.InvariantCulture,
                                           DateTimeStyles.None, out stamp)) {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Deletes archives past the keep count and past the size cap, oldest first.
        ///
        /// Deliberately not the audit log's "at most one per pass". That rule exists so a server which has
        /// been off for a month does not spend its first tick deleting thirty files; here the deletes happen
        /// immediately after writing a zip that may be hundreds of megabytes, so a handful of file deletions
        /// is nothing beside it - and this is a feature about disk space, so lowering the keep count has to
        /// give the disk back now rather than one archive per cycle for the next two days.
        /// </summary>
        private static void Prune(Job job) {
            List<Existing> existing = List(job.OutputDir);
            PruneNow(WouldPrune(existing, job.KeepCount, job.MaxTotalBytes));

            // A .tmp left by a run that died mid-zip. Cleared here rather than at startup so it is tidied
            // even on a server that never restarts.
            foreach (string path in SafeGetFiles(job.OutputDir, "*" + Extension + TempSuffix)) {
                try {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromHours(1)) { continue; }
                } catch (Exception) {
                    continue;
                }
                if (TryDelete(path)) { Logger.LogInfo($"Removed an abandoned partial save archive {Path.GetFileName(path)}."); }
            }
        }

        /// <summary>
        /// Which of <paramref name="existing"/> - newest first - are past the keep count or the size ceiling.
        /// Pure: it decides, and nothing else, so the prune command can show an admin the list before
        /// anything is deleted.
        ///
        /// The newest archive is never in the result. A ceiling set below the size of a single archive would
        /// otherwise delete every copy the moment one was written, which is the opposite of what somebody
        /// setting a backup ceiling wants.
        /// </summary>
        internal static List<string> WouldPrune(List<Existing> existing, int keepCount, long maxTotalBytes) {
            List<string> doomed = new List<string>();
            if (existing == null) { return doomed; }
            int keep = Math.Max(1, keepCount);
            long total = 0;
            for (int i = 0; i < existing.Count; i++) {
                total += existing[i].Bytes;
                bool overCount = i >= keep;
                bool overSize = maxTotalBytes > 0 && total > maxTotalBytes && i > 0;
                if (overCount || overSize) { doomed.Add(existing[i].Path); }
            }
            return doomed;
        }

        /// <summary>Deletes the archives <see cref="WouldPrune"/> named, and returns how many went.</summary>
        internal static int PruneNow(List<string> doomed) {
            int deleted = 0;
            if (doomed == null) { return 0; }
            foreach (string path in doomed) {
                if (!TryDelete(path)) { continue; }
                pruned++;
                deleted++;
                Logger.LogInfo($"Removed old save archive {Path.GetFileName(path)}.");
            }
            return deleted;
        }

        private static string[] SafeGetFiles(string directory, string pattern) {
            try {
                return Directory.Exists(directory) ? Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly) : new string[0];
            } catch (Exception) {
                return new string[0];
            }
        }

        private static bool TryDelete(string path) {
            try {
                if (File.Exists(path)) { File.Delete(path); }
                return true;
            } catch (Exception e) {
                Logger.LogWarning($"Could not delete {Path.GetFileName(path)}: {e.Message}");
                return false;
            }
        }

        // ---------------------------------------------------------------------------------------------

        // A world name is admin-chosen and becomes a file name. Nothing here rejects it - refusing to archive
        // a world because of its name would be worse than the problem - but anything a path could object to
        // is replaced.
        private static string SafeName(string worldName) {
            char[] invalid = Path.GetInvalidFileNameChars();
            char[] chars = worldName.ToCharArray();
            for (int i = 0; i < chars.Length; i++) {
                if (chars[i] < ' ') { chars[i] = '_'; continue; }
                for (int j = 0; j < invalid.Length; j++) {
                    if (chars[i] == invalid[j]) { chars[i] = '_'; break; }
                }
            }
            string safe = new string(chars).Trim();
            return safe.Length == 0 ? "world" : safe;
        }

        internal static string Describe(long bytes) {
            if (bytes < 1024) { return $"{bytes} B"; }
            if (bytes < 1024 * 1024) { return $"{bytes / 1024.0:F1} KB"; }
            if (bytes < 1024L * 1024L * 1024L) { return $"{bytes / (1024.0 * 1024.0):F1} MB"; }
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }

        /// <summary>The folder archives are written to right now, for the commands to report.</summary>
        internal static string CurrentOutputDir() {
            string output = ValConfig.SaveArchivePath != null ? ValConfig.SaveArchivePath.Value : "";
            if (string.IsNullOrWhiteSpace(output)) {
                output = Path.Combine(ValConfig.GetSecondaryConfigDirectoryPath(), ArchiveFolder);
            }
            return output;
        }
    }
}
