using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ValheimEnforcer.common;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.modules.character {
    /// <summary>
    /// Server-side, asynchronous, coalescing persistence for player character saves (disk storage mode).
    ///
    /// Motivation: the vanilla server periodically broadcasts "save player profiles" to every client at
    /// once, which makes all connected clients send a full character save to the server in the same
    /// instant. Previously each save was deserialized, re-serialized and written to disk synchronously
    /// inside the Jotunn RPC handler — i.e. on the Unity main thread — so a burst of large saves could
    /// stall the server long enough for peers to time out and be dropped. This store moves all YAML
    /// (de)serialization and disk I/O onto a single background worker thread and coalesces repeated
    /// writes to the same character, so a save/delta burst can never block the main thread.
    ///
    /// Threading contract:
    ///  - The main thread only ever calls the Submit*/Seed/GetYaml/IsCached/Flush/Shutdown API and hands
    ///    the worker immutable strings (or objects it will not touch again after handoff).
    ///  - The worker thread is the SOLE owner and mutator of cached <see cref="DataObjects.Character"/>
    ///    objects. The main thread only reads the cached YAML string (an immutable snapshot), so reads
    ///    are race-free without locking.
    ///  - Internal-storage (ZDO) writes are intentionally NOT handled here: ZDOs are main-thread only.
    ///    Callers use this store for disk mode and keep the existing synchronous path for internal mode.
    /// </summary>
    internal static class CharacterStore {

        private sealed class Entry {
            public DataObjects.Character Character; // may be null when seeded from YAML only; parsed lazily by the worker
            public string Yaml;
            // UTC last-write time of the on-disk file this cached YAML corresponds to. MinValue = unknown
            // (never written/seeded from disk), which always compares as older than a real file mtime so an
            // external edit is detected. Used by GetYamlIfCurrent to spot out-of-band edits at login.
            public DateTime SourceMtime;
        }

        private abstract class Message { }
        private sealed class FullSaveMessage : Message {
            public string RawYaml;
            public long Sender;
            // Who the SERVER believes this peer is, resolved from the socket at connect time. The payload
            // carries its own HostID/Name, but those are written by the client and are not evidence of
            // anything - see the identity check in Apply.
            public string SenderAccountId;
            public string SenderCharacterName;
            // Non-null only when the server's connect-time lookup found no save for this sender AND an admin
            // has server-side enforcement on. Captured on the main thread: the worker must never read a
            // ConfigEntry, which the config file watcher can reload underneath it at any moment.
            public NewCharacterRules.Policy NewCharacterPolicy;
        }
        private sealed class DeltaMessage : Message { public DeltaSummaryUpdate Delta; public long Sender; }

        /// <summary>Why GetOrLoad returned what it did. The distinction is load-bearing: a save that exists but
        /// will not parse must never be mistaken for a character that has never been here, or a corrupt file
        /// would get a real player's inventory confiscated.</summary>
        internal enum LoadState { Found, Missing, Unreadable }

        /// <summary>The worker sanitized a first save and the client needs to be told, so its live inventory
        /// matches what the server now holds. Sending needs ZNet, so - exactly like DriftResync - the request
        /// is handed back to the main thread instead of being sent from the worker.</summary>
        internal sealed class SanitizedPush {
            public long Sender;
            public string HostID;
            public string Name;
        }

        /// <summary>A delta merge on the worker thread found our copy had drifted from the client's baseline.
        /// Sending the recovery RPC requires ZNet, so the request is handed back to the main thread instead.</summary>
        internal sealed class DriftResync {
            public long Sender;
            public string HostID;
            public string Name;
        }

        private static readonly ConcurrentDictionary<string, Entry> cache = new ConcurrentDictionary<string, Entry>();
        private static readonly ConcurrentQueue<Message> messages = new ConcurrentQueue<Message>();
        private static readonly ConcurrentQueue<DriftResync> driftResyncs = new ConcurrentQueue<DriftResync>();
        private static readonly ConcurrentQueue<SanitizedPush> sanitizedPushes = new ConcurrentQueue<SanitizedPush>();
        private static readonly AutoResetEvent signal = new AutoResetEvent(false);
        private static readonly object startLock = new object();
        private static Thread worker;
        private static volatile bool running;
        private static volatile bool workerBusy;

        internal static string KeyFor(string id, string name) {
            return $"{id}/{name}";
        }

        private static void EnsureWorker() {
            if (running) { return; }
            lock (startLock) {
                if (running) { return; }
                running = true;
                worker = new Thread(WorkerLoop) { IsBackground = true, Name = "VE-CharacterStore" };
                worker.Start();
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Main-thread API
        // ---------------------------------------------------------------------------------------------

        /// <summary>Persist a full character save received from a client. The raw YAML is parsed and
        /// written on the worker thread, so the caller does no serialization work.</summary>
        internal static void SubmitFullSave(string rawYaml, long sender, string senderAccountId, string senderCharacterName,
                                            NewCharacterRules.Policy newCharacterPolicy) {
            EnsureWorker();
            messages.Enqueue(new FullSaveMessage {
                RawYaml = rawYaml,
                Sender = sender,
                SenderAccountId = senderAccountId,
                SenderCharacterName = senderCharacterName,
                NewCharacterPolicy = newCharacterPolicy,
            });
            signal.Set();
        }

        /// <summary>Apply an incremental delta update (already parsed on the main thread — the delta
        /// payload is small by design) and persist the result on the worker thread. <paramref name="sender"/> is
        /// carried through so a drift detected during the merge can be answered with a full-sync request once the
        /// main thread picks it back up.</summary>
        internal static void SubmitDelta(DeltaSummaryUpdate delta, long sender) {
            EnsureWorker();
            messages.Enqueue(new DeltaMessage { Delta = delta, Sender = sender });
            signal.Set();
        }

        /// <summary>Main thread: take the next queued drift-recovery request, or null when there are none.
        /// Drained by FullSyncSchedulerBehaviour, which already ticks server-side every frame.</summary>
        internal static DriftResync TryDequeueDriftResync() {
            return driftResyncs.TryDequeue(out DriftResync r) ? r : null;
        }

        /// <summary>Drop any queued drift-recovery requests (server shutting down; the peers are going away).</summary>
        internal static void ClearDriftResyncs() {
            while (driftResyncs.TryDequeue(out _)) { }
        }

        /// <summary>Main thread: take the next queued sanitized-character push, or null when there are none.
        /// Drained by FullSyncSchedulerBehaviour alongside the drift resyncs.</summary>
        internal static SanitizedPush TryDequeueSanitizedPush() {
            return sanitizedPushes.TryDequeue(out SanitizedPush p) ? p : null;
        }

        /// <summary>Drop any queued sanitized-character pushes (server shutting down).</summary>
        internal static void ClearSanitizedPushes() {
            while (sanitizedPushes.TryDequeue(out _)) { }
        }

        /// <summary>Drop any cached state for a character so the next access reloads from disk. Used
        /// after a synchronous out-of-band write (e.g. the admin confiscated-item return path) so the
        /// async store cannot later overwrite it with a stale cached copy.</summary>
        internal static void Invalidate(string id, string name) {
            cache.TryRemove(KeyFor(id, name), out _);
        }

        /// <summary>True if we already hold authoritative state for this character (so an incoming delta
        /// can be applied instead of requesting a full sync).</summary>
        internal static bool IsCached(string id, string name) {
            return cache.ContainsKey(KeyFor(id, name));
        }

        /// <summary>Latest serialized YAML for a character, or null if not cached. Safe on any thread.</summary>
        internal static string GetYaml(string id, string name) {
            return cache.TryGetValue(KeyFor(id, name), out Entry e) ? e.Yaml : null;
        }

        /// <summary>Latest serialized YAML for a character, but only when the on-disk save has NOT been
        /// modified out-of-band since we cached it. Returns null when there is no cached entry, or when
        /// <paramref name="diskMtime"/> is strictly newer than the cached copy's source mtime (an external
        /// edit) — the caller should then reload from disk and re-seed. A pending async write leaves the disk
        /// mtime unchanged/older than what we recorded, so the (newer) cache still wins via the equality case.
        /// Safe on any thread.</summary>
        internal static string GetYamlIfCurrent(string id, string name, DateTime diskMtime) {
            string key = KeyFor(id, name);
            if (!cache.TryGetValue(key, out Entry e)) { return null; }
            if (diskMtime > e.SourceMtime) {
                Logger.LogInfo($"On-disk save for {key} is newer than cache; reloading from disk.");
                return null;
            }
            return e.Yaml;
        }

        /// <summary>Warm the cache from an already-loaded save (e.g. the connect-time disk read) without
        /// enqueuing a write. Stores the YAML only; the worker parses the <see cref="Character"/> lazily
        /// on the first delta, keeping this call cheap on the connection path.</summary>
        internal static void Seed(string id, string name, string yaml, DateTime sourceMtime) {
            if (string.IsNullOrEmpty(yaml)) { return; }
            cache[KeyFor(id, name)] = new Entry { Character = null, Yaml = yaml, SourceMtime = sourceMtime };
        }

        /// <summary>Block until currently-queued work has been drained to disk. Intended for shutdown /
        /// world-save; do not call on a hot path. Bounded by <paramref name="timeout"/>.</summary>
        internal static void Flush(TimeSpan timeout) {
            if (!running) { return; }
            signal.Set();
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline) {
                if (messages.IsEmpty && !workerBusy) { return; }
                Thread.Sleep(15);
            }
            Logger.LogWarning("CharacterStore flush timed out; some pending saves may not have been written.");
        }

        /// <summary>Flush and stop the worker. Called from the server shutdown path.</summary>
        internal static void Shutdown() {
            if (!running) { return; }
            Flush(TimeSpan.FromSeconds(10));
            running = false;
            signal.Set();
            worker?.Join(TimeSpan.FromSeconds(5));
        }

        // ---------------------------------------------------------------------------------------------
        // Worker thread
        // ---------------------------------------------------------------------------------------------

        private static void WorkerLoop() {
            while (running) {
                signal.WaitOne(1000);
                DrainOnce();
            }
            DrainOnce(); // final drain so nothing queued before Shutdown() is lost
        }

        private static void DrainOnce() {
            workerBusy = true;
            try {
                // Apply every message in order (updates must not be reordered), collecting the set of
                // characters that changed. Writing per distinct key AFTER draining coalesces a burst of
                // updates for the same character into a single disk write.
                HashSet<string> dirty = new HashSet<string>();
                while (messages.TryDequeue(out Message msg)) {
                    try {
                        string key = Apply(msg);
                        if (key != null) { dirty.Add(key); }
                    } catch (Exception e) {
                        Logger.LogWarning($"CharacterStore failed to apply an update: {e.Message}");
                    }
                }
                foreach (string key in dirty) {
                    if (cache.TryGetValue(key, out Entry entry) && entry.Character != null && entry.Yaml != null) {
                        try {
                            // Record the mtime the OS reports for our own write so a later login can tell an
                            // out-of-band edit apart from a file we wrote ourselves.
                            entry.SourceMtime = WriteToDisk(entry.Character, entry.Yaml);
                        } catch (Exception e) {
                            Logger.LogWarning($"CharacterStore failed to write {key} to disk: {e.Message}");
                        }
                    }
                }
            } finally {
                workerBusy = false;
            }
        }

        // Applies a single message to the in-memory cache and returns the coalescing key that now needs a
        // disk write, or null if nothing should be written.
        private static string Apply(Message msg) {
            switch (msg) {
                case FullSaveMessage full: {
                    DataObjects.Character c = yamldeserializer.Deserialize<DataObjects.Character>(full.RawYaml);
                    if (c == null || string.IsNullOrEmpty(c.HostID) || string.IsNullOrEmpty(c.Name)) {
                        Logger.LogWarning("CharacterStore received a full save with no HostID/Name; dropping.");
                        return null;
                    }
                    // A save is only ever accepted for the identity the SERVER resolved for this peer at connect.
                    // Without this the payload names its own destination, and a client that names someone
                    // else's can both overwrite that character's save and slip past the first-save check below
                    // (which asks "does a save exist?" about whatever HostID/Name the payload supplies - name a
                    // character that does exist and the answer is Found, so enforcement is skipped).
                    if (!IdentityMatchesSender(c, full)) { return null; }

                    string key = KeyFor(c.HostID, c.Name);
                    // The incoming save replaces everything EXCEPT the confiscated list, which the server owns:
                    // the client only reports what it confiscated this session, and an overwrite would resurrect
                    // entries an admin cleared or returned mid-session. See Character.MergeConfiscatedItems.
                    List<PackedItem> reported = c.ConfiscatedItems;
                    DataObjects.Character existing = GetOrLoad(key, c.HostID, c.Name, out LoadState state);
                    c.ConfiscatedItems = existing?.ConfiscatedItems ?? new List<PackedItem>();
                    int appended = c.MergeConfiscatedItems(reported);
                    if (appended > 0) {
                        Logger.LogInfo($"Recorded {appended} newly confiscated item(s) for {c.Name}.");
                    }

                    // The server's own copy of the new-character rules. The client is supposed to have applied
                    // these already (CharacterManager.BuildNewCharacter), but the client is the thing being
                    // defended against, so this runs regardless of whether it did.
                    //
                    // Two conditions, both required. The policy is non-null only when the server's own
                    // connect-time lookup found nothing for this peer - a fact no client input can influence.
                    // LoadState.Missing then confirms there is still no save to merge onto, and specifically
                    // is not `existing == null`: that would also be true for a save that exists but failed to
                    // parse, and stripping one of those would wipe a real character over a corrupt file.
                    //
                    // Placed after the confiscation merge and before the re-serialize below, so the entries
                    // this records are in the YAML that gets written.
                    bool pushSanitized = false;
                    if (full.NewCharacterPolicy != null && state == LoadState.Missing) {
                        NewCharacterRules.Result sanitized = NewCharacterRules.Apply(c, full.NewCharacterPolicy, recordConfiscation: true);
                        if (sanitized.Changed) {
                            Logger.LogWarning($"First save for {c.Name} ({c.HostID}) held to the new-character rules: {sanitized.Describe()}");
                            pushSanitized = true;
                        }
                    }

                    // Re-serialize from the parsed object so on-disk format is always server-canonical.
                    cache[key] = new Entry { Character = c, Yaml = yamlserializer.Serialize(c) };
                    // Queued strictly AFTER the cache entry is published. The main thread answers this request
                    // by reading the cached YAML back out, and it runs concurrently with this worker - enqueuing
                    // first would let it read the pre-sanitization copy, or none at all.
                    if (pushSanitized) {
                        sanitizedPushes.Enqueue(new SanitizedPush { Sender = full.Sender, HostID = c.HostID, Name = c.Name });
                    }
                    Logger.LogInfo($"Recieved Player data update - {c.Name}|{c.HostID}");
                    return key;
                }
                case DeltaMessage deltaMsg: {
                    DeltaSummaryUpdate d = deltaMsg.Delta;
                    string key = KeyFor(d.HostID, d.Name);
                    DataObjects.Character cur = GetOrLoad(key, d.HostID, d.Name, out _);
                    if (cur == null) {
                        // No authoritative save to apply onto (the main thread requests a full sync when it
                        // can detect this up front; here it means a save vanished between check and apply).
                        Logger.LogWarning($"CharacterStore dropped a delta for {d.Name} ({d.HostID}): no existing save to apply onto.");
                        return null;
                    }
                    if (ValConfig.MergeDelta(d, cur)) {
                        // Worker thread - queue the recovery request rather than touching ZNet from here.
                        driftResyncs.Enqueue(new DriftResync { Sender = deltaMsg.Sender, HostID = d.HostID, Name = d.Name });
                    }
                    cache[key] = new Entry { Character = cur, Yaml = yamlserializer.Serialize(cur) };
                    Logger.LogInfo($"Saved delta update for {cur.Name}.");
                    return key;
                }
            }
            return null;
        }

        // Worker-thread only. Pure string comparison, no ZNet access - the caller resolved the peer's identity
        // on the main thread and passed it along.
        //
        // The account id is compared with PlatformIds.Matches because the same account legitimately reaches us
        // under more than one spelling; the character name has to match what the peer connected as. When the
        // server could not resolve an identity at all the check is skipped rather than failing closed, so an
        // unrecognised socket type cannot stop every save on the server from being written.
        private static bool IdentityMatchesSender(DataObjects.Character c, FullSaveMessage full) {
            if (string.IsNullOrEmpty(full.SenderAccountId) || string.IsNullOrEmpty(full.SenderCharacterName)) {
                Logger.LogDebug($"No resolved identity for sender {full.Sender}; accepting the save for {c.Name} unchecked.");
                return true;
            }
            if (!PlatformIds.Matches(full.SenderAccountId, c.HostID)) {
                Logger.LogWarning($"Refusing a character save from {full.SenderCharacterName} ({full.SenderAccountId}): it claims to belong to account {c.HostID}.");
                return false;
            }
            if (!string.Equals(full.SenderCharacterName, c.Name, StringComparison.OrdinalIgnoreCase)) {
                Logger.LogWarning($"Refusing a character save from {full.SenderAccountId}: they connected as '{full.SenderCharacterName}' but uploaded a save for '{c.Name}'.");
                return false;
            }
            return true;
        }

        // Worker-thread only. Returns the authoritative character for a key, parsing a seeded YAML entry
        // or reading from disk on demand.
        //
        // state says WHY the result is null, which callers need: Missing means this character has genuinely
        // never been stored here, Unreadable means a save is sitting on disk that could not be parsed. Those
        // used to be the same answer (null), which is fine for "drop this delta" but is not fine for any
        // decision that treats a first-time character differently from a returning one.
        private static DataObjects.Character GetOrLoad(string key, string id, string name, out LoadState state) {
            if (cache.TryGetValue(key, out Entry e)) {
                if (e.Character != null) { state = LoadState.Found; return e.Character; }
                if (!string.IsNullOrEmpty(e.Yaml)) {
                    try {
                        e.Character = yamldeserializer.Deserialize<DataObjects.Character>(e.Yaml);
                        if (e.Character != null) { state = LoadState.Found; return e.Character; }
                        Logger.LogWarning($"CharacterStore parsed the seeded save for {key} to nothing. Falling back to disk.");
                    } catch (Exception ex) {
                        Logger.LogWarning($"CharacterStore failed to parse seeded save for {key}: {ex.Message}. Falling back to disk.");
                    }
                }
            }

            string path = Path.Combine(ValConfig.CharacterFilePath, id, $"{name}.yaml");
            if (!File.Exists(path)) { state = LoadState.Missing; return null; }
            try {
                string text = File.ReadAllText(path);
                DataObjects.Character c = yamldeserializer.Deserialize<DataObjects.Character>(text);
                if (c == null) {
                    // An empty or whitespace-only file deserializes to null WITHOUT throwing. Reporting that as
                    // Found-with-nothing would quietly disable the first-save check for this character (a save
                    // file is present, so they are not new; but there is nothing to validate against either).
                    // Unreadable is the honest answer, and it fails open.
                    Logger.LogWarning($"CharacterStore found an empty save file for {key}. Treating it as unreadable rather than as a missing character.");
                    state = LoadState.Unreadable;
                    return null;
                }
                cache[key] = new Entry { Character = c, Yaml = text, SourceMtime = File.GetLastWriteTimeUtc(path) };
                state = LoadState.Found;
                return c;
            } catch (Exception ex) {
                // Leave a present-but-corrupt save alone; dropping the update avoids overwriting it.
                Logger.LogWarning($"CharacterStore failed to load existing save for {key}: {ex.Message}. Update dropped.");
                state = LoadState.Unreadable;
                return null;
            }
        }

        // Returns the UTC last-write time the OS records for the file we just wrote, so the caller can store
        // it as the entry's SourceMtime and later distinguish our own write from an out-of-band edit.
        private static DateTime WriteToDisk(DataObjects.Character c, string yaml) {
            Directory.CreateDirectory(ValConfig.CharacterFilePath);
            string dir = Path.Combine(ValConfig.CharacterFilePath, c.HostID);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"{c.Name}.yaml");
            File.WriteAllText(path, yaml);
            Logger.LogInfo($"Writing to {path}");
            return File.GetLastWriteTimeUtc(path);
        }
    }
}
