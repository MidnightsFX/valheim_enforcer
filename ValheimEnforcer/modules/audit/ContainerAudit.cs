using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimEnforcer.common;
using Logger = ValheimEnforcer.Logger;

namespace ValheimEnforcer.modules.audit {

    /// <summary>
    /// Records what players take out of and put into containers.
    ///
    /// This deliberately does NOT watch the RequestOpen / RequestTakeAll routed RPCs, which is the obvious
    /// approach and the wrong one. ZNetView.InvokeRPC addresses those to the ZDO's current owner, and
    /// ZRoutedRpc.InvokeRoutedRPC handles a message locally when the target is the sender itself - so a
    /// player who already owns the chest, which is the normal case because they were just standing next to
    /// it, opens and empties it without the server seeing a single packet. A moderation tool whose blind spot
    /// is "the player was already there" is not worth having.
    ///
    /// What the server does always hold is the container's ZDO. Contents live in the "items" string
    /// (Container.Save), whether the chest is open is mirrored into "InUse" by its owner
    /// (Container.UpdateUseVisual), and the owner is the person who opened it, because
    /// Container.RPC_RequestOpen transfers ownership to them. Watching the ZDO catches every route into a
    /// container - taking, storing, Take All, and a modified client writing the item list directly.
    ///
    /// Attribution is to the peer whose packet the change arrived in, which is exact: the server knows which
    /// socket it is reading from, and nothing in the payload is consulted.
    /// </summary>
    internal static class ContainerAudit {

        // ---- Per-packet state -----------------------------------------------------------------------------

        // Non-null only while inside ZDOMan.RPC_ZDOData for a peer being recorded. Everything below keys off
        // it, which is what keeps world load and the server's own writes out of the path entirely.
        private static ZNetPeer inboundPeer;
        private static string inboundAccount;
        private static string inboundCharacter;

        // ---- Snapshots ------------------------------------------------------------------------------------

        /// <summary>
        /// What a container held the last time we looked.
        ///
        /// Two halves, and the split is the point. <see cref="ItemsHash"/> answers "did the contents change"
        /// for the overwhelmingly common case where they did not - a chest replicating because somebody walked
        /// past, a cart replicating because it is being pulled - and costs one pass over the incoming blob,
        /// which is what comparing the blobs cost anyway. <see cref="Items"/> is the parsed form, kept so a
        /// change is diffed against something already in memory.
        ///
        /// Keeping the previous base64 instead did both jobs, but a full chest's blob is a couple of kilobytes
        /// and a string holds two bytes per character, so the table cost roughly four kilobytes per tracked
        /// container - twenty megabytes at the default ceiling and far more at the configurable one - and every
        /// change re-parsed a blob that had already been parsed once, when it was the "after" side of the
        /// previous change. The parsed form is both smaller and the thing the diff actually wants.
        ///
        /// <see cref="Items"/> is null when the blob could not be read. That is a missing baseline rather than
        /// an empty container: the next change re-seeds instead of reporting a phantom emptying.
        /// </summary>
        private sealed class Snapshot {
            internal long ItemsHash;
            internal Dictionary<string, InventoryBlob.Slot> Items;
            internal bool InUse;
            internal long LastTouch;
        }

        private static readonly Dictionary<ZDOID, Snapshot> snapshots = new Dictionary<ZDOID, Snapshot>();
        private static long touchCounter;

        // ---- Container prefab index -----------------------------------------------------------------------

        private static bool indexBuilt;
        private static readonly HashSet<int> containerPrefabs = new HashSet<int>();
        private static readonly Dictionary<int, string> containerNames = new Dictionary<int, string>();

        /// <summary>
        /// Works out which prefabs are containers, once per world. Everything with a Container component
        /// counts, which picks up chests, ships, carts and - usefully for a moderator - tombstones, so
        /// somebody looting a grave that is not theirs is recorded by the same code path.
        ///
        /// Returns false until the scene is far enough along to answer, in which case nothing is recorded
        /// this packet. A detector that cannot tell what a prefab is must not guess.
        /// </summary>
        private static bool EnsureIndex() {
            if (indexBuilt) { return true; }
            if (ZNetScene.instance == null || ZNetScene.instance.m_prefabs == null) { return false; }

            try {
                containerPrefabs.Clear();
                containerNames.Clear();
                foreach (GameObject prefab in ZNetScene.instance.m_prefabs) {
                    if (prefab == null) { continue; }
                    if (prefab.GetComponent<Container>() == null) { continue; }
                    int hash = prefab.name.GetStableHashCode();
                    containerPrefabs.Add(hash);
                    containerNames[hash] = prefab.name;
                }
                if (containerPrefabs.Count == 0) {
                    Logger.LogDebug("Container audit deferred: no container prefabs are loaded yet.");
                    return false;
                }
                indexBuilt = true;
                Logger.LogInfo($"Container audit index built: {containerPrefabs.Count} container prefab(s).");
                return true;
            } catch (Exception e) {
                Logger.LogWarning($"Could not build the container audit index; container auditing is inactive: {e.Message}");
                return false;
            }
        }

        internal static void InvalidateIndex() {
            indexBuilt = false;
            containerPrefabs.Clear();
            containerNames.Clear();
        }

        // ---- Hook installation ----------------------------------------------------------------------------

        /// <summary>
        /// Whether the ZDO.Deserialize hook is present in this process, decided once at patch time by
        /// <see cref="WantsInspectHook"/>.
        /// </summary>
        internal static bool InspectHookInstalled { get; private set; }

        private static bool warnedHookMissing;

        /// <summary>
        /// Whether to install the ZDO.Deserialize hook, called from that patch's Harmony Prepare. Reads config
        /// bound in the plugin's Awake, which runs before PatchAll.
        ///
        /// The point of asking: ZDO.Deserialize is the hottest method a Valheim server runs, and a Harmony
        /// wrapper on it costs the same whether the body does anything or returns on its first line. The body
        /// here is already about as small as it can be - a field read and one hash lookup - so on a server
        /// with container auditing off the wrapper IS the cost, and the only way to not pay it is to not
        /// install it.
        ///
        /// Deliberately does not go through <see cref="AuditPolicy.Active"/>: that asks ZNet whether we are
        /// the server, and there is no ZNet at patch time. Which side we are on is settled per packet in
        /// <see cref="BeginPacket"/> instead.
        /// </summary>
        internal static bool WantsInspectHook() {
            try {
                InspectHookInstalled = ValConfig.EnableAuditLog != null && ValConfig.EnableAuditLog.Value;
            } catch (Exception e) {
                // Patch time. Losing container auditing is survivable; failing to patch is not.
                InspectHookInstalled = false;
                Logger.LogWarning($"Could not read the audit settings while patching; container auditing is off for this session: {e.Message}");
            }
            return InspectHookInstalled;
        }

        /// <summary>
        /// Says once that the audit is on but this half of it has no hook, because EnableAuditLog was off when
        /// the process started. Once per session: this is inside the packet loop.
        /// </summary>
        private static void WarnHookMissing() {
            if (warnedHookMissing) { return; }
            warnedHookMissing = true;
            Logger.LogWarning("EnableAuditLog was turned on after the server started, so container auditing has no hook "
                              + "and chest activity is not being recorded. Restart the server to enable it. The item and "
                              + "damage halves of the audit are already running.");
        }

        // ---- Packet bracket -------------------------------------------------------------------------------

        /// <summary>Opens the bracket around one client's ZDOData packet, so Inspect knows who it is reading.</summary>
        internal static void BeginPacket(ZRpc rpc) {
            inboundPeer = null;
            inboundAccount = null;
            inboundCharacter = null;

            // Inside the ZDO stream, which is the one thing on a server that must never throw - a broken
            // packet loop desyncs or disconnects everybody. Going quiet is always the better failure.
            try {
                if (!AuditPolicy.Active()) { return; }
                if (!InspectHookInstalled) { WarnHookMissing(); return; }
                if (!EnsureIndex()) { return; }

                ZNetPeer peer = modules.character.PeerIdentity.PeerFor(rpc);
                if (peer == null || !AuditPolicy.Recorded(peer)) { return; }

                string account = AuditPolicy.AccountOf(peer);
                if (string.IsNullOrEmpty(account)) { return; }

                inboundPeer = peer;
                inboundAccount = account;
                inboundCharacter = AuditPolicy.CharacterOf(peer);
            } catch (Exception e) {
                inboundPeer = null;
                Logger.LogDebug($"Container audit could not open a packet: {e.Message}");
            }
        }

        /// <summary>
        /// Closes the bracket. A Harmony finalizer rather than a postfix, matching the structure validator:
        /// an exception out of vanilla must not leave the peer set, or the next ZDOs the server writes itself
        /// would be attributed to whoever sent the packet that failed.
        /// </summary>
        internal static void EndPacket() {
            inboundPeer = null;
            inboundAccount = null;
            inboundCharacter = null;
        }

        // ---- Inspection -----------------------------------------------------------------------------------

        /// <summary>Looks at one fully-populated ZDO that arrived in the current packet.</summary>
        internal static void Inspect(ZDO zdo) {
            if (inboundPeer == null || zdo == null) { return; }
            try {
                Evaluate(zdo);
            } catch (Exception e) {
                Logger.LogDebug($"Container audit could not inspect an object: {e.Message}");
            }
        }

        private static void Evaluate(ZDO zdo) {
            int prefab = zdo.GetPrefab();
            // The whole hot path for a non-container: one int hash lookup. Every ZDO in every client packet
            // reaches here, so nothing more expensive may happen before this returns.
            if (!containerPrefabs.Contains(prefab)) { return; }

            string raw = zdo.GetString(ZDOVars.s_items, "");
            bool inUse = zdo.GetInt(ZDOVars.s_inUse, 0) == 1;
            long hash = InventoryBlob.HashOf(raw);

            if (!snapshots.TryGetValue(zdo.m_uid, out Snapshot snapshot)) {
                // First sight of this container. There is no baseline to diff against, so seed one and say
                // nothing - reporting its whole contents as "stored" would be a fabrication.
                snapshots[zdo.m_uid] = new Snapshot {
                    ItemsHash = hash,
                    Items = InventoryBlob.Parse(raw),
                    InUse = inUse,
                    LastTouch = ++touchCounter
                };
                Evict();
                return;
            }

            snapshot.LastTouch = ++touchCounter;
            if (hash == snapshot.ItemsHash && inUse == snapshot.InUse) { return; }

            string containerName = containerNames.TryGetValue(prefab, out string name) ? name : prefab.ToString();
            string position = AuditPolicy.Position(zdo.GetPosition());

            if (inUse && !snapshot.InUse) {
                AuditEvent opened = AuditPolicy.Event(inboundAccount, inboundCharacter, AuditEvent.Kinds.ContainerOpened);
                opened.Container = containerName;
                opened.Pos = position;
                AuditPolicy.Record(opened);
            }

            if (hash != snapshot.ItemsHash) {
                RecordChanges(snapshot, raw, containerName, position);
                snapshot.ItemsHash = hash;
            }

            snapshot.InUse = inUse;
        }

        /// <summary>
        /// Reports what moved into or out of a container, and leaves the parsed new state on the snapshot as
        /// the baseline for the next change.
        ///
        /// Only the arriving blob is parsed; the previous contents are whatever the last call left behind.
        /// An unreadable blob clears the baseline rather than reporting against a guess, so the change after
        /// it re-seeds and the one after that is diffed again - the same behaviour a container has when it is
        /// first seen.
        /// </summary>
        private static void RecordChanges(Snapshot snapshot, string afterRaw, string containerName, string position) {
            Dictionary<string, InventoryBlob.Slot> before = snapshot.Items;
            Dictionary<string, InventoryBlob.Slot> after = InventoryBlob.Parse(afterRaw);
            snapshot.Items = after;

            // Either side unreadable means any diff would be invented.
            if (before == null || after == null) { return; }

            foreach (InventoryBlob.Change change in InventoryBlob.Diff(before, after)) {
                AuditEvent entry = AuditPolicy.Event(inboundAccount, inboundCharacter,
                    change.Delta > 0 ? AuditEvent.Kinds.ContainerStored : AuditEvent.Kinds.ContainerTook);
                entry.Prefab = change.Slot.Prefab;
                entry.Qty = Math.Abs(change.Delta);
                entry.Quality = change.Slot.Quality;
                entry.Container = containerName;
                entry.Pos = position;
                // Only meaningful on a take: it says where the item the player now holds came from.
                if (change.Delta < 0) {
                    entry.CrafterId = change.Slot.CrafterId;
                    entry.Crafter = change.Slot.CrafterName;
                }
                AuditPolicy.Record(entry);
            }
        }

        // Reused between evictions. The table hovers at the ceiling once it is full - it is trimmed the moment
        // it exceeds it - so both of these are allocated once and then kept for the life of the world.
        private static long[] touchScratch = new long[0];
        private static readonly List<ZDOID> evictScratch = new List<ZDOID>();

        /// <summary>
        /// Keeps the snapshot table under its ceiling by dropping the least recently seen containers.
        ///
        /// Removes a batch rather than one at a time so the sort is amortised instead of running on every
        /// insert once the table is full. A container that loses its baseline is not lost from the record -
        /// its next change re-seeds and the one after that is diffed again - but that one change goes
        /// unrecorded, which is why the ceiling is configurable.
        /// </summary>
        private static void Evict() {
            int limit = ValConfig.AuditContainerTrackingLimit != null
                ? Math.Max(64, ValConfig.AuditContainerTrackingLimit.Value) : 5000;
            int count = snapshots.Count;
            if (count <= limit) { return; }

            int drop = Math.Max(1, limit / 10);
            if (drop > count) { drop = count; }
            if (touchScratch.Length < count) { touchScratch = new long[count]; }

            // The cutoff is found by sorting the touch stamps on their own. Sorting the entries themselves
            // meant a comparison delegate per step - on a 5000 entry table that is tens of thousands of
            // delegate calls - plus a fresh list holding every snapshot in the table, all of it on the main
            // thread inside the ZDO packet loop. A primitive sort over a reused buffer costs neither.
            int i = 0;
            foreach (Snapshot snapshot in snapshots.Values) { touchScratch[i++] = snapshot.LastTouch; }
            Array.Sort(touchScratch, 0, count);
            long cutoff = touchScratch[drop - 1];

            // Stamps come from ++touchCounter, so every entry holds a distinct one and this takes exactly the
            // `drop` least recently seen. Collected first because a dictionary cannot be written while it is
            // being enumerated.
            evictScratch.Clear();
            foreach (KeyValuePair<ZDOID, Snapshot> entry in snapshots) {
                if (entry.Value.LastTouch <= cutoff) { evictScratch.Add(entry.Key); }
            }
            for (int j = 0; j < evictScratch.Count; j++) { snapshots.Remove(evictScratch[j]); }
            Logger.LogDebug($"Container audit evicted {evictScratch.Count} least-recently-seen container snapshot(s) (ceiling {limit}).");
            evictScratch.Clear();
        }

        /// <summary>Drops all per-world state. Called from the ZNet.Shutdown teardown.</summary>
        internal static void Reset() {
            inboundPeer = null;
            inboundAccount = null;
            inboundCharacter = null;
            snapshots.Clear();
            touchCounter = 0;
            touchScratch = new long[0];
            evictScratch.Clear();
            InvalidateIndex();
        }
    }
}
