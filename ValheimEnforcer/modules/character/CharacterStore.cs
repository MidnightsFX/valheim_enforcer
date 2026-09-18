using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
    /// What it holds, and for how long: the file on disk is the authority, and every change reaches it
    /// within a drain (about a second). So the store keeps only the parsed object for a character, only
    /// while that character is being played, and never a serialized copy. It used to keep the whole YAML
    /// string as well and rebuild it on every incremental update - a fresh copy of the entire save, hundreds
    /// of kilobytes, per player per update - and it kept every character it had ever seen until restart.
    /// On a large server that is what memory did with uptime. Entries idle for longer than
    /// <see cref="ValConfig.CharacterCacheIdleMinutes"/> are dropped; the next update for that character
    /// reads the file back, on the worker.
    ///
    /// Threading contract:
    ///  - The main thread only ever calls the Submit*/Seed/IsCached/HasUnwrittenChanges/Snapshot/Flush/
    ///    Shutdown API and hands the worker immutable strings (or objects it will not touch again after
    ///    handoff). It never dereferences a cached <see cref="DataObjects.Character"/>.
    ///  - The worker thread is the SOLE owner and mutator of cached <see cref="DataObjects.Character"/>
    ///    objects, and the only thread that reads or writes an entry's fields, with two exceptions that are
    ///    each a single word: <see cref="Entry.Dirty"/> is volatile and the main thread reads it, and
    ///    <see cref="Entry.LastTouchedTicks"/> is written with Interlocked from both sides.
    ///  - Internal-storage (ZDO) writes are intentionally NOT handled here: ZDOs are main-thread only.
    ///    Callers use this store for disk mode and keep the existing synchronous path for internal mode.
    /// </summary>
    internal static class CharacterStore {

        private sealed class Entry {
            // Worker-only. Null is a placeholder: a login (or an out-of-band edit) said the file exists, and
            // nothing has needed the parsed object yet - GetOrLoadEntry reads the file on first use.
            public DataObjects.Character Character;
            // UTC last-write time of the on-disk file this entry corresponds to. MinValue = unknown (an
            // applied change not yet written, which always compares as older than a real file mtime, so an
            // external edit is detected). Worker-only after construction; Seed reads it on the main thread,
            // and a stale read there only ever costs one extra file read on the worker.
            public DateTime SourceMtime;
            // Set by the worker when Apply changes Character, cleared only after a successful write. The one
            // field the main thread reads on a live entry (HasUnwrittenChanges, Snapshot), hence volatile.
            public volatile bool Dirty;
            // UTC ticks of the last Seed, load or Apply. Read by the sweep; written through Interlocked
            // because a login touches it from the main thread.
            public long LastTouchedTicks;
            // Peer uid of the last message applied. If an admin's out-of-band write replaces this entry before
            // the change is written, that client is asked for a full save so the change is not simply lost.
            public long LastSender;
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
            // Non-null only when the connecting peer was a RETURNING player and ServerSideJoinEnforcement is on,
            // so the first full save of the session is re-validated against the stored character server-side.
            // Snapshotted on the main thread for the same reason as above.
            public ReturningCharacterRules.Policy ReturningPolicy;
        }
        private sealed class DeltaMessage : Message { public DeltaSummaryUpdate Delta; public long Sender; }
        private sealed class DeathMessage : Message { public string HostID; public string Name; }
        /// <summary>Seal this character into a crash-recovery snapshot for the peer that is playing it. The
        /// keys are an immutable object captured on the main thread, so the worker never reads the key file
        /// and an admin rotating it mid-seal cannot change what this one produces.</summary>
        private sealed class SealMessage : Message {
            public string HostID;
            public string Name;
            public long Sender;
            public recovery.RecoverySeal.Keys Keys;
            public long WorldUid;
        }

        /// <summary>A snapshot handed back by a client. Arrives still sealed: verifying, unsealing and
        /// parsing it is the expensive half, and it belongs here rather than on a frame.</summary>
        private sealed class RecoveryMessage : Message {
            public string HostID;
            public string Name;
            public byte[] Blob;
            public recovery.RecoverySeal.Keys Keys;
            public long WorldUid;
            public long Sender;
        }

        /// <summary>A map blob for a character. The bytes are opaque and already compressed by vanilla, and
        /// the main thread hands them over and never touches them again, so the threading contract holds.</summary>
        private sealed class MapMessage : Message {
            public string HostID;
            public string Name;
            public byte[] Blob;
            public string Hash;
            public long Sender;
        }

        /// <summary>Why GetOrLoad returned what it did. The distinction is load-bearing: a save that exists but
        /// will not parse must never be mistaken for a character that has never been here, or a corrupt file
        /// would get a real player's inventory confiscated.</summary>
        internal enum LoadState { Found, Missing, Unreadable }

        /// <summary>The worker sanitized a first save and the client needs to be told, so its live inventory
        /// matches what the server now holds. Sending needs ZNet, so - exactly like DriftResync - the request
        /// is handed back to the main thread instead of being sent from the worker. It carries the payload,
        /// serialized once on the worker with the server-owned lists withheld, so the main thread has nothing
        /// to read back out of the store - which may have moved on, or dropped the entry, by the time it is
        /// drained.</summary>
        internal sealed class SanitizedPush {
            public long Sender;
            public string HostID;
            public string Name;
            public string StrippedYaml;
        }

        /// <summary>A finished crash-recovery snapshot, waiting for the main thread to put it on the wire.
        /// Same handoff as <see cref="SanitizedPush"/>, and for the same reason: sealing happens on the
        /// worker, and ZNet may only be touched from the main thread.</summary>
        internal sealed class SealedPush {
            public long Sender;
            public string Name;
            public byte[] Blob;
        }

        /// <summary>A delta merge on the worker thread found our copy had drifted from the client's baseline.
        /// Sending the recovery RPC requires ZNet, so the request is handed back to the main thread instead.</summary>
        internal sealed class DriftResync {
            public long Sender;
            public string HostID;
            public string Name;
        }

        /// <summary>What the store is holding, for enforcer-memory. Safe to take on the main thread.</summary>
        internal struct Stats {
            public int Cached;
            public int Parsed;
            public int Placeholders;
            public int Dirty;
            public int QueueDepth;
            public int IdleMinutes;
            public int LastSweepEvicted;
            public int TotalEvicted;
            public DateTime LastSweepUtc;
        }

        private static readonly ConcurrentDictionary<string, Entry> cache = new ConcurrentDictionary<string, Entry>();
        private static readonly ConcurrentQueue<Message> messages = new ConcurrentQueue<Message>();
        private static readonly ConcurrentQueue<DriftResync> driftResyncs = new ConcurrentQueue<DriftResync>();
        private static readonly ConcurrentQueue<SanitizedPush> sanitizedPushes = new ConcurrentQueue<SanitizedPush>();
        private static readonly ConcurrentQueue<SealedPush> sealedPushes = new ConcurrentQueue<SealedPush>();
        private static readonly AutoResetEvent signal = new AutoResetEvent(false);
        private static readonly object startLock = new object();
        private static Thread worker;
        private static volatile bool running;
        private static volatile bool workerBusy;

        // ---- Idle eviction --------------------------------------------------------------------------------

        /// <summary>How often the worker looks for idle entries. Not configurable: the idle threshold is the
        /// meaningful knob, and a minute of slack on top of it changes nothing.</summary>
        private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

        // Snapshot of CharacterCacheIdleMinutes, written only from the main thread (SetIdleEvictionMinutes)
        // and read by the worker's sweep. 0 = never evict. Starts at 0 so nothing is evicted before the
        // config is bound.
        private static volatile int idleEvictionMinutes;

        // Worker-only, except the counters the report reads, which are single words.
        private static DateTime lastSweepUtc = DateTime.UtcNow;
        private static volatile int lastSweepEvicted;
        private static volatile int totalEvicted;

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
                                            NewCharacterRules.Policy newCharacterPolicy, ReturningCharacterRules.Policy returningPolicy) {
            EnsureWorker();
            messages.Enqueue(new FullSaveMessage {
                RawYaml = rawYaml,
                Sender = sender,
                SenderAccountId = senderAccountId,
                SenderCharacterName = senderCharacterName,
                NewCharacterPolicy = newCharacterPolicy,
                ReturningPolicy = returningPolicy,
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

        /// <summary>
        /// Persist a character's map blob. Queued through the worker with everything else so it is ordered
        /// against a save already in flight for the same character - the hash the save records has to be the
        /// hash of the blob that ends up on disk.
        ///
        /// Both identifiers are the ones the SERVER resolved for this peer, never what the payload claimed;
        /// the caller has already checked them with PeerIdentity.IsSafeToken because they become path
        /// segments here.
        /// </summary>
        internal static void SubmitMap(string hostId, string name, byte[] blob, string hash, long sender) {
            if (string.IsNullOrEmpty(hostId) || string.IsNullOrEmpty(name) || blob == null || blob.Length == 0) { return; }
            EnsureWorker();
            messages.Enqueue(new MapMessage { HostID = hostId, Name = name, Blob = blob, Hash = hash, Sender = sender });
            signal.Set();
        }

        /// <summary>Record that a character died: clear its item list and mark it a dirty disconnect, so the
        /// pre-death inventory can no longer be replayed as authoritative. Enqueued through the worker so it is
        /// ordered FIFO with any save/delta already in flight for the same character - a pre-death full save
        /// still in the queue is written first and then cleared, rather than racing the clear and winning.</summary>
        internal static void SubmitDeath(string hostId, string name) {
            if (string.IsNullOrEmpty(hostId) || string.IsNullOrEmpty(name)) { return; }
            EnsureWorker();
            messages.Enqueue(new DeathMessage { HostID = hostId, Name = name });
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

        /// <summary>Seal a character into a crash-recovery snapshot for one peer. Queued with everything else
        /// so it is ordered against a save already in flight - a snapshot must never carry a sequence number
        /// older than the save it is supposed to be newer than.</summary>
        internal static void SubmitSeal(string hostId, string name, long sender, recovery.RecoverySeal.Keys keys, long worldUid) {
            if (string.IsNullOrEmpty(hostId) || string.IsNullOrEmpty(name) || keys == null) { return; }
            EnsureWorker();
            messages.Enqueue(new SealMessage { HostID = hostId, Name = name, Sender = sender, Keys = keys, WorldUid = worldUid });
            signal.Set();
        }

        /// <summary>Verify a snapshot a client handed back and adopt it when it is genuinely newer.</summary>
        internal static void SubmitRecoveryRestore(string hostId, string name, byte[] blob,
                                                   recovery.RecoverySeal.Keys keys, long worldUid, long sender) {
            if (string.IsNullOrEmpty(hostId) || string.IsNullOrEmpty(name) || blob == null || keys == null) { return; }
            EnsureWorker();
            messages.Enqueue(new RecoveryMessage { HostID = hostId, Name = name, Blob = blob, Keys = keys, WorldUid = worldUid, Sender = sender });
            signal.Set();
        }

        /// <summary>Main thread: take the next sealed snapshot to send, or null when there are none.</summary>
        internal static SealedPush TryDequeueSealedPush() {
            return sealedPushes.TryDequeue(out SealedPush p) ? p : null;
        }

        /// <summary>Drop any queued snapshots (server shutting down).</summary>
        internal static void ClearSealedPushes() {
            while (sealedPushes.TryDequeue(out _)) { }
        }

        /// <summary>Drop any cached state for a character so the next access reloads from disk. Used
        /// after a synchronous out-of-band write (e.g. the admin confiscated-item return path) so the
        /// async store cannot later overwrite it with a stale cached copy. A change the worker had applied
        /// but not yet written is not written over the admin's file; that client is asked for a full save
        /// instead (see DrainOnce).</summary>
        internal static void Invalidate(string id, string name) {
            cache.TryRemove(KeyFor(id, name), out _);
        }

        /// <summary>True if we already hold state for this character - parsed, or known to be on disk - so
        /// an incoming delta can be applied instead of requesting a full sync.</summary>
        internal static bool IsCached(string id, string name) {
            return cache.ContainsKey(KeyFor(id, name));
        }

        /// <summary>
        /// True when the file on disk may be behind what the store holds: a change applied and not yet
        /// written, or anything still queued. The login path asks this before reading the file, and waits
        /// (bounded) when the answer is yes. Coarse on purpose - it does not know which character a queued
        /// message is for - because the cost of a false yes is a wait of a few milliseconds on one connect.
        /// </summary>
        internal static bool HasUnwrittenChanges(string id, string name) {
            if (cache.TryGetValue(KeyFor(id, name), out Entry e) && e.Dirty) { return true; }
            return !messages.IsEmpty || workerBusy;
        }

        /// <summary>
        /// Tell the store what is on disk for a character, from the connect-time read. Adds a placeholder if
        /// the character is not held at all, so its first delta applies without a full-sync round trip. If a
        /// parsed copy is held and the file is newer than the copy came from - an admin edited the save while
        /// the player was offline - the copy is dropped so the worker re-reads the edited file rather than
        /// writing the old contents back over it. Never touches an entry with an unwritten change: that entry
        /// is newer than the file, and the login path has already waited for its write.
        /// </summary>
        internal static void Seed(string id, string name, DateTime sourceMtime) {
            string key = KeyFor(id, name);
            long now = DateTime.UtcNow.Ticks;
            Entry fresh = new Entry { Character = null, SourceMtime = sourceMtime, LastTouchedTicks = now };
            if (cache.TryAdd(key, fresh)) { return; }
            if (!cache.TryGetValue(key, out Entry existing)) {
                cache.TryAdd(key, fresh); // removed between the two calls; either outcome is fine
                return;
            }
            if (existing.Dirty) { return; }
            if (sourceMtime > existing.SourceMtime) {
                // Conditional on the reference, so a worker replacement in the same instant is never clobbered.
                cache.TryUpdate(key, fresh, existing);
                return;
            }
            Touch(existing); // a login is activity; do not evict the character somebody just joined with
        }

        /// <summary>What the store is holding right now. Safe on any thread.</summary>
        internal static Stats Snapshot() {
            Stats stats = new Stats {
                IdleMinutes = idleEvictionMinutes,
                LastSweepEvicted = lastSweepEvicted,
                TotalEvicted = totalEvicted,
                LastSweepUtc = lastSweepUtc,
            };
            foreach (KeyValuePair<string, Entry> kv in cache) {
                stats.Cached++;
                if (kv.Value.Character != null) { stats.Parsed++; } else { stats.Placeholders++; }
                if (kv.Value.Dirty) { stats.Dirty++; }
            }
            stats.QueueDepth = messages.Count;
            return stats;
        }

        /// <summary>Main thread only. Re-reads the idle threshold into the worker's snapshot; wired to the
        /// setting's SettingChanged so a config reload takes effect at the next sweep.</summary>
        internal static void SetIdleEvictionMinutes(int minutes) {
            idleEvictionMinutes = minutes < 0 ? 0 : minutes;
        }

        /// <summary>Block until currently-queued work has been drained to disk, or the timeout passes. Intended
        /// for shutdown / world-save / a login that arrived behind a pending write; do not call on a hot path.
        /// Returns false when the timeout passed with work still pending.</summary>
        internal static bool Flush(TimeSpan timeout, int pollMs = 15) {
            if (!running) { return true; }
            signal.Set();
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline) {
                if (messages.IsEmpty && !workerBusy) { return true; }
                Thread.Sleep(pollMs);
            }
            return messages.IsEmpty && !workerBusy;
        }

        /// <summary>Flush and stop the worker. Called from the server shutdown path.</summary>
        internal static void Shutdown() {
            if (!running) { return; }
            if (!Flush(TimeSpan.FromSeconds(10))) {
                Logger.LogWarning("CharacterStore flush timed out; some pending saves may not have been written.");
            }
            running = false;
            signal.Set();
            bool joined = worker?.Join(TimeSpan.FromSeconds(5)) ?? true;
            if (joined) {
                // Nothing will read these again on this server; a listen host that returns to the menu and hosts
                // again should not start with the last world's characters in memory.
                cache.Clear();
            } else {
                Logger.LogWarning("CharacterStore worker did not stop in time; its cached characters are kept until it does.");
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Worker thread
        // ---------------------------------------------------------------------------------------------

        private static void WorkerLoop() {
            while (running) {
                signal.WaitOne(1000);
                DrainOnce();
                SweepIfDue();
            }
            DrainOnce(); // final drain so nothing queued before Shutdown() is lost
        }

        private static void DrainOnce() {
            workerBusy = true;
            try {
                // Apply every message in order (updates must not be reordered), collecting the entries that
                // changed. Writing per distinct key AFTER draining coalesces a burst of updates for the same
                // character into a single serialize and a single disk write.
                Dictionary<string, Entry> dirty = new Dictionary<string, Entry>();
                while (messages.TryDequeue(out Message msg)) {
                    try {
                        Entry changed = Apply(msg, out string key);
                        if (changed != null) { dirty[key] = changed; }
                    } catch (Exception e) {
                        Logger.LogWarning($"CharacterStore failed to apply an update: {e.Message}");
                    }
                }
                foreach (KeyValuePair<string, Entry> kv in dirty) {
                    Entry entry = kv.Value;
                    if (entry.Character == null) { continue; }
                    if (!cache.TryGetValue(kv.Key, out Entry current) || !ReferenceEquals(current, entry)) {
                        // Invalidate (an admin's own synchronous write) or a login that found the file newer took
                        // the entry out from under this change. The file they wrote wins: writing ours would put
                        // back whatever the admin just took out. The change itself is not lost - the client still
                        // has it, so ask them for a full save, which lands on top of the admin's file.
                        Logger.LogInfo($"Not writing {kv.Key}: its stored copy was replaced while an update was pending; asking the client for a full save instead.");
                        if (entry.LastSender != 0L) {
                            driftResyncs.Enqueue(new DriftResync { Sender = entry.LastSender, HostID = entry.Character.HostID, Name = entry.Character.Name });
                        }
                        continue;
                    }
                    try {
                        // Record the mtime the OS reports for our own write so a later login can tell an
                        // out-of-band edit apart from a file we wrote ourselves.
                        entry.SourceMtime = WriteToDisk(entry.Character);
                        entry.Dirty = false;
                    } catch (Exception e) {
                        // Stays dirty: never evicted, and written on the next attempt for this character.
                        Logger.LogWarning($"CharacterStore failed to write {kv.Key} to disk: {e.Message}");
                    }
                }
            } finally {
                workerBusy = false;
            }
        }

        /// <summary>
        /// Drops entries nobody has touched for longer than the idle threshold. The file is the authority and
        /// an idle entry has no unwritten change (a dirty one is never dropped), so dropping it loses nothing;
        /// the next update for that character reads the file back. This is what keeps memory proportional to
        /// who is playing rather than to who has ever joined.
        /// </summary>
        private static void SweepIfDue() {
            int idleMinutes = idleEvictionMinutes;
            if (idleMinutes <= 0) { return; }
            DateTime now = DateTime.UtcNow;
            if (now - lastSweepUtc < SweepInterval) { return; }
            lastSweepUtc = now;

            long cutoff = now.Ticks - TimeSpan.FromMinutes(idleMinutes).Ticks;
            int evicted = 0;
            // Removing through the collection interface removes the pair only while the value is still the
            // exact entry inspected, so a placeholder Seed put in the same instant survives.
            ICollection<KeyValuePair<string, Entry>> pairs = cache;
            foreach (KeyValuePair<string, Entry> kv in cache) {
                Entry entry = kv.Value;
                if (entry.Dirty) { continue; }
                if (Interlocked.Read(ref entry.LastTouchedTicks) > cutoff) { continue; }
                if (pairs.Remove(kv)) { evicted++; }
            }
            lastSweepEvicted = evicted;
            if (evicted > 0) {
                totalEvicted += evicted;
                Logger.LogDebug($"CharacterStore dropped {evicted} character(s) idle for over {idleMinutes} minute(s); {cache.Count} still held.");
            }
        }

        private static void Touch(Entry entry) {
            Interlocked.Exchange(ref entry.LastTouchedTicks, DateTime.UtcNow.Ticks);
        }

        // Applies a single message to the in-memory cache and returns the entry that now needs a disk write
        // (with its coalescing key), or null if nothing should be written. Never serializes: that happens
        // once per changed character in DrainOnce, however many messages for it this drain carried.
        private static Entry Apply(Message msg, out string key) {
            key = null;
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

                    // Shed pass-through compat keys (the ExtraSlots inventory backup) before the save is
                    // merged and written below - also scrubs the stale copies saves written before
                    // pass-through handling still carry. Safe here: CompatCustomData reads a volatile
                    // snapshot rather than a ConfigEntry.
                    compat.CompatCustomData.StripPassthroughKeys(c.PlayerCustomData);

                    key = KeyFor(c.HostID, c.Name);
                    // The incoming save replaces everything EXCEPT the confiscated list, which the server owns:
                    // the client only reports what it confiscated this session, and an overwrite would resurrect
                    // entries an admin cleared or returned mid-session. See Character.MergeConfiscatedItems.
                    List<PackedItem> reported = c.ConfiscatedItems;
                    Entry existingEntry = GetOrLoadEntry(key, c.HostID, c.Name, out LoadState state);
                    DataObjects.Character existing = existingEntry?.Character;
                    c.ConfiscatedItems = existing?.ConfiscatedItems ?? new List<PackedItem>();
                    int appended = c.MergeConfiscatedItems(reported);
                    if (appended > 0) {
                        Logger.LogInfo($"Recorded {appended} newly confiscated item(s) for {c.Name}.");
                    }
                    // The skill-reduction record and any pending restore are server-owned the same way, and for
                    // the same reason: the client reports what it lowered this session, and never decides what
                    // an admin has since restored or cleared.
                    List<SkillReduction> reportedReductions = c.SkillReductions;
                    c.SkillReductions = existing?.SkillReductions;
                    int reductions = c.MergeSkillReductions(reportedReductions);
                    if (reductions > 0) {
                        Logger.LogInfo($"Recorded {reductions} skill reduction(s) reported by {c.Name}.");
                    }
                    c.PendingSkillRestores = existing?.PendingSkillRestores;

                    // Server-owned, for the same reason the lists above are: the client is told these, so it
                    // can echo them back with anything it likes in them. SaveSequence is the number crash
                    // recovery orders two copies of a character by, so a client that could set it could
                    // replay an old snapshot over a newer one; MapHash names a file only the server writes.
                    c.SaveSequence = existing?.SaveSequence ?? 0L;
                    if (c.Progress != null) {
                        c.Progress.MapHash = existing?.Progress?.MapHash;
                    } else if (existing?.Progress?.MapHash != null) {
                        // The client is not tracking progression but the server still holds a map for this
                        // character. Keep the pointer to it rather than orphaning the file.
                        c.Progress = new Progression { MapHash = existing.Progress.MapHash };
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
                    // Placed after the confiscation merge and before the entry is published below, so the
                    // entries this records are in the file that gets written.
                    bool pushSanitized = false;
                    if (full.NewCharacterPolicy != null && state == LoadState.Missing) {
                        NewCharacterRules.Result sanitized = NewCharacterRules.Apply(c, full.NewCharacterPolicy, record: true);
                        if (sanitized.Changed) {
                            Logger.LogWarning($"First save for {c.Name} ({c.HostID}) held to the new-character rules: {sanitized.Describe()}");
                            pushSanitized = true;
                        }
                    }
                    // The returning-character counterpart: re-validate the first save of a session against the
                    // stored character (state Found means we have one to validate against). Same reasoning as
                    // the new-character rules - the client runs this too, but the client is what we defend
                    // against. Only fires once per session because ClearReturning drops the policy after.
                    else if (full.ReturningPolicy != null && state == LoadState.Found && existing != null) {
                        ReturningCharacterRules.Result reconciled = ReturningCharacterRules.Apply(c, existing, full.ReturningPolicy);
                        if (reconciled.Changed) {
                            Logger.LogWarning($"Returning save for {c.Name} ({c.HostID}) reconciled to the stored character: {reconciled.Describe()}");
                            pushSanitized = true;
                        }
                        FirstSaveEnforcement.ClearReturning(full.Sender);
                    }

                    // Bound any impossible skill value (>100, negative, NaN) to the valid range, independent
                    // of the enforcement policies above.
                    SkillClamp.Apply(c.SkillLevels, c.Name);
                    // With the rules above settled this save is the client's word on its skills, so any pending
                    // restore it now meets has landed.
                    c.ConsumePendingSkillRestores();

                    // A full save is a new object, not a mutation, so it replaces the entry. The file mtime
                    // carries over: the file is still the one the previous copy came from, until DrainOnce
                    // writes this one.
                    c.SaveSequence++;
                    Entry fresh = new Entry {
                        Character = c,
                        Dirty = true,
                        LastSender = full.Sender,
                        SourceMtime = existingEntry?.SourceMtime ?? DateTime.MinValue,
                        LastTouchedTicks = DateTime.UtcNow.Ticks,
                    };
                    cache[key] = fresh;
                    // Queued strictly AFTER the cache entry is published, and carrying its own copy of the
                    // payload: the main thread drains this concurrently with the worker, and by then the
                    // entry may have been replaced by a later save or dropped by the sweep.
                    if (pushSanitized) {
                        sanitizedPushes.Enqueue(new SanitizedPush { Sender = full.Sender, HostID = c.HostID, Name = c.Name, StrippedYaml = SerializeStripped(c) });
                    }
                    Logger.LogInfo($"Recieved Player data update - {c.Name}|{c.HostID}");
                    return fresh;
                }
                case DeltaMessage deltaMsg: {
                    DeltaSummaryUpdate d = deltaMsg.Delta;
                    key = KeyFor(d.HostID, d.Name);
                    Entry entry = GetOrLoadEntry(key, d.HostID, d.Name, out _);
                    DataObjects.Character cur = entry?.Character;
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
                    cur.SaveSequence++;
                    entry.Dirty = true;
                    entry.LastSender = deltaMsg.Sender;
                    Logger.LogInfo($"Saved delta update for {cur.Name}.");
                    return entry;
                }
                case DeathMessage death: {
                    key = KeyFor(death.HostID, death.Name);
                    Entry entry = GetOrLoadEntry(key, death.HostID, death.Name, out _);
                    DataObjects.Character cur = entry?.Character;
                    if (cur == null) { return null; } // no stored save; nothing to clear, nothing to dupe
                    bool alreadyCleared = (cur.PlayerItems == null || cur.PlayerItems.Count == 0)
                                          && cur.LastDisconnect == DisconnectionState.DirtyDisconnect;
                    if (alreadyCleared) { return null; } // honest client already cleared and pushed
                    Logger.LogInfo($"Death recorded for {cur.Name} ({cur.HostID}); clearing the stored item list so the grave cannot be duplicated on rejoin.");
                    if (cur.PlayerItems == null) { cur.PlayerItems = new List<PackedItem>(); } else { cur.PlayerItems.Clear(); }
                    cur.ActiveCharacterEffects?.Clear();
                    cur.Foods?.Clear(); // vanilla empties the stomach on death; null stays null (not tracked)
                    cur.LastDisconnect = DisconnectionState.DirtyDisconnect;
                    cur.SaveSequence++;
                    entry.Dirty = true;
                    return entry;
                }
                case SealMessage seal: {
                    // Deliberately returns null: sealing reads the character and changes nothing about it, so
                    // there is no write to schedule and the entry must not be marked dirty.
                    string sealKey = KeyFor(seal.HostID, seal.Name);
                    DataObjects.Character c = GetOrLoad(sealKey, seal.HostID, seal.Name, out LoadState sealState);
                    if (c == null) {
                        Logger.LogDebug($"No character to seal a recovery snapshot from for {sealKey} ({sealState}).");
                        return null;
                    }
                    try {
                        // The FULL record, server-owned lists included. The client cannot read any of it, and
                        // a restore that dropped the confiscation history would hand back items an admin had
                        // already taken - the snapshot has to be the save, not a version of it.
                        recovery.RecoverySeal.Header header = new recovery.RecoverySeal.Header {
                            KeyId = seal.Keys.Id,
                            WorldUid = seal.WorldUid,
                            AccountId = c.HostID,
                            CharacterName = c.Name,
                            Sequence = c.SaveSequence,
                            SealedUtc = DateTime.UtcNow,
                        };
                        byte[] blob = recovery.RecoverySeal.Seal(seal.Keys, header, yamlserializer.Serialize(c));
                        sealedPushes.Enqueue(new SealedPush { Sender = seal.Sender, Name = c.Name, Blob = blob });
                    } catch (Exception e) {
                        Logger.LogWarning($"Could not seal a recovery snapshot for {sealKey}: {e.Message}");
                    }
                    return null;
                }
                case RecoveryMessage recovery_: {
                    key = KeyFor(recovery_.HostID, recovery_.Name);
                    recovery.RecoverySeal.Verdict verdict = recovery.RecoverySeal.Open(
                        recovery_.Keys, recovery_.Blob, out recovery.RecoverySeal.Header header, out string yaml);
                    if (verdict != recovery.RecoverySeal.Verdict.Ok) {
                        // Each verdict is a different thing to tell an admin, which is why they are distinct.
                        Logger.LogWarning($"Refused a recovery snapshot for {key}: {Describe(verdict)}.");
                        key = null;
                        return null;
                    }

                    // Re-checked against the AUTHENTICATED header. The main thread checked the same two
                    // fields before this was queued, but it read them out of a header nothing had verified
                    // yet - so that was a filter and this is the decision.
                    if (header.WorldUid != recovery_.WorldUid) {
                        Logger.LogWarning($"Refused a recovery snapshot for {key}: it is sealed for a different world.");
                        key = null;
                        return null;
                    }
                    if (!PlatformIds.Matches(recovery_.HostID, header.AccountId)
                        || !string.Equals(recovery_.Name, header.CharacterName, StringComparison.OrdinalIgnoreCase)) {
                        Logger.LogWarning($"Refused a recovery snapshot for {key}: it is sealed for {header.CharacterName} ({header.AccountId}).");
                        key = null;
                        return null;
                    }

                    Entry existingEntry = GetOrLoadEntry(key, recovery_.HostID, recovery_.Name, out LoadState restoreState);
                    if (restoreState == LoadState.Unreadable) {
                        // A save that is present but will not parse. Overwriting it would destroy whatever is
                        // still recoverable from it by hand, and there is no sequence to compare against.
                        Logger.LogWarning($"Not applying a recovery snapshot for {key}: the stored save exists but could not be read.");
                        key = null;
                        return null;
                    }
                    long held = existingEntry?.Character?.SaveSequence ?? 0L;
                    if (header.Sequence <= held) {
                        // The anti-rollback check, and the reason SaveSequence exists. A snapshot that is not
                        // strictly newer is either a replay of an old one or simply nothing new.
                        Logger.LogInfo($"Not applying a recovery snapshot for {key}: its sequence {header.Sequence} is not newer than the {held} held here.");
                        key = null;
                        return null;
                    }

                    DataObjects.Character restored;
                    try {
                        restored = yamldeserializer.Deserialize<DataObjects.Character>(yaml);
                    } catch (Exception e) {
                        Logger.LogWarning($"Refused a recovery snapshot for {key}: it verified but would not parse ({e.Message}).");
                        key = null;
                        return null;
                    }
                    if (restored == null) { key = null; return null; }

                    // Past the snapshot's own number, so the very same blob offered twice is refused the
                    // second time rather than reapplied.
                    restored.SaveSequence = header.Sequence + 1L;
                    Entry adopted = new Entry {
                        Character = restored,
                        Dirty = true,
                        LastSender = recovery_.Sender,
                        SourceMtime = existingEntry?.SourceMtime ?? DateTime.MinValue,
                        LastTouchedTicks = DateTime.UtcNow.Ticks,
                    };
                    cache[key] = adopted;
                    Logger.LogWarning($"CRASH RECOVERY: adopted the snapshot {restored.Name} ({restored.HostID}) handed back - sequence {header.Sequence}, sealed {header.SealedUtc:u}, replacing the sequence {held} this server held. Items and progression in it are from before the rollback; if the WORLD also rolled back, anything they took out of it since may now exist twice.");
                    return adopted;
                }
                case MapMessage map: {
                    key = KeyFor(map.HostID, map.Name);
                    Entry entry = GetOrLoadEntry(key, map.HostID, map.Name, out LoadState state);
                    DataObjects.Character cur = entry?.Character;
                    if (cur == null) {
                        // No save to hang the hash off. Dropping the blob is right: a .map file with no
                        // character beside it would never be read, and would never be cleaned up either.
                        Logger.LogWarning($"CharacterStore dropped a map for {map.Name} ({map.HostID}): no existing save to record it against ({state}).");
                        return null;
                    }
                    // Written here rather than queued into the coalescing pass below, which keys one write per
                    // character and would have the blob and the YAML fight over that slot. A client may only
                    // send a map once every MapSyncIntervalMinutes, so there is no burst to coalesce.
                    try {
                        AtomicFile.WriteBytes(MapSync.PathFor(map.HostID, map.Name), map.Blob);
                    } catch (Exception e) {
                        // The save is not marked dirty, so the hash is not recorded either - the two stay in
                        // agreement, and the client sends the same blob again on its next cadence.
                        Logger.LogWarning($"CharacterStore failed to write the map for {key}: {e.Message}");
                        return null;
                    }
                    if (cur.Progress == null) { cur.Progress = new Progression(); }
                    cur.Progress.MapHash = map.Hash;
                    cur.SaveSequence++;
                    entry.Dirty = true;
                    entry.LastSender = map.Sender;
                    Logger.LogInfo($"Stored the map for {cur.Name} ({map.Blob.Length} bytes).");
                    return entry;
                }
            }
            return null;
        }

        private static string Describe(recovery.RecoverySeal.Verdict verdict) {
            switch (verdict) {
                case recovery.RecoverySeal.Verdict.NotOurs: return "it is not in a format this server wrote";
                case recovery.RecoverySeal.Verdict.WrongKey: return "it is sealed under a different key - this server's key has been rotated, or the snapshot is from somewhere else";
                case recovery.RecoverySeal.Verdict.Tampered: return "it has been altered since this server sealed it";
                case recovery.RecoverySeal.Verdict.Unreadable: return "it verified but could not be unsealed";
                default: return verdict.ToString();
            }
        }

        // Worker-thread only. Pure string comparison, no ZNet access - the caller resolved the peer's identity
        // on the main thread and passed it along.
        //
        // The account id is compared with PlatformIds.Matches because the same account legitimately reaches us
        // under more than one spelling; the character name has to match what the peer connected as. When the
        // server could not resolve an identity at all the check is skipped rather than failing closed, so an
        // unrecognised socket type cannot stop every save on the server from being written.
        private static bool IdentityMatchesSender(DataObjects.Character c, FullSaveMessage full) {
            // Fail CLOSED when the main thread could not resolve the sender's identity (see the twin check in
            // ValConfig.SaveBelongsToSender). Empty identity is the pre-handshake state, not a normal one.
            if (string.IsNullOrEmpty(full.SenderAccountId) || string.IsNullOrEmpty(full.SenderCharacterName)) {
                Logger.LogWarning($"Refusing a character save from sender {full.Sender}: the server could not resolve who they are.");
                return false;
            }
            // HostID/Name become path segments in WriteToDisk; refuse a traversal-shaped one here too, so the
            // worker never composes a path outside the character folder even if a caller forgot to check.
            if (!PeerIdentity.IsSafeToken(c.HostID) || !PeerIdentity.IsSafeToken(c.Name)) {
                Logger.LogWarning($"Refusing a character save from {full.SenderCharacterName} ({full.SenderAccountId}): the account id or character name is not a safe file name.");
                return false;
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

        // Worker-thread only. The character as the client is sent it: the server-owned lists withheld, the
        // same way every other server -> client character payload withholds them (see
        // ValConfig.SendSanitizedCharacterToClient). The object is restored before this returns; it is the
        // authoritative copy and must never be left stripped.
        private static string SerializeStripped(DataObjects.Character c) {
            List<PackedItem> held = c.ConfiscatedItems;
            List<SkillReduction> heldReductions = c.SkillReductions;
            try {
                c.ConfiscatedItems = null;
                c.SkillReductions = null;
                return yamlserializer.Serialize(c);
            } finally {
                c.ConfiscatedItems = held;
                c.SkillReductions = heldReductions;
            }
        }

        // Worker-thread only. Returns the authoritative character for a key, or null with the reason.
        private static DataObjects.Character GetOrLoad(string key, string id, string name, out LoadState state) {
            return GetOrLoadEntry(key, id, name, out state)?.Character;
        }

        // Worker-thread only. Returns the entry holding the authoritative character for a key, reading the
        // file on demand when the entry is a placeholder or absent.
        //
        // state says WHY the result is null, which callers need: Missing means this character has genuinely
        // never been stored here, Unreadable means a save is sitting on disk that could not be parsed. Those
        // used to be the same answer (null), which is fine for "drop this delta" but is not fine for any
        // decision that treats a first-time character differently from a returning one.
        private static Entry GetOrLoadEntry(string key, string id, string name, out LoadState state) {
            if (cache.TryGetValue(key, out Entry e) && e.Character != null) {
                Touch(e);
                state = LoadState.Found;
                return e;
            }

            string path = Path.Combine(ValConfig.CharacterFilePath, id, $"{name}.yaml");
            if (!File.Exists(path)) { state = LoadState.Missing; return null; }
            try {
                // Streamed rather than File.ReadAllText: a save is hundreds of kilobytes, and this is the path
                // every dropped-then-touched character comes back through.
                DataObjects.Character c;
                using (StreamReader reader = new StreamReader(path, Encoding.UTF8, true, 16 * 1024)) {
                    c = yamldeserializer.Deserialize<DataObjects.Character>(reader);
                }
                if (c == null) {
                    // An empty or whitespace-only file deserializes to null WITHOUT throwing. Reporting that as
                    // Found-with-nothing would quietly disable the first-save check for this character (a save
                    // file is present, so they are not new; but there is nothing to validate against either).
                    // Unreadable is the honest answer, and it fails open.
                    Logger.LogWarning($"CharacterStore found an empty save file for {key}. Treating it as unreadable rather than as a missing character.");
                    state = LoadState.Unreadable;
                    return null;
                }
                Entry loaded = new Entry { Character = c, SourceMtime = File.GetLastWriteTimeUtc(path), LastTouchedTicks = DateTime.UtcNow.Ticks };
                cache[key] = loaded;
                state = LoadState.Found;
                return loaded;
            } catch (Exception ex) {
                // Leave a present-but-corrupt save alone; dropping the update avoids overwriting it.
                Logger.LogWarning($"CharacterStore failed to load existing save for {key}: {ex.Message}. Update dropped.");
                state = LoadState.Unreadable;
                return null;
            }
        }

        // Returns the UTC last-write time the OS records for the file we just wrote, so the caller can store
        // it as the entry's SourceMtime and later distinguish our own write from an out-of-band edit.
        private static DateTime WriteToDisk(DataObjects.Character c) {
            Directory.CreateDirectory(ValConfig.CharacterFilePath);
            string dir = Path.Combine(ValConfig.CharacterFilePath, c.HostID);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"{c.Name}.yaml");
            DateTime written = AtomicFile.WriteYaml(path, c, yamlserializer);
            Logger.LogInfo($"Writing to {path}");
            return written;
        }
    }
}
