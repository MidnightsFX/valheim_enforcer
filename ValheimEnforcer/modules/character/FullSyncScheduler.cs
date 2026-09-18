using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.character {
    /// <summary>
    /// Server-side driver that periodically asks connected players to upload a full character save.
    ///
    /// Full saves used to be produced client-side, riding the vanilla world/profile autosave (a Player.Save
    /// patch) and a client-side timer. That tied every client's full upload to the same save cadence,
    /// producing a synchronized "thundering herd" the server could not pace. This scheduler moves the
    /// trigger to the server: every <see cref="ValConfig.FullSyncPullIntervalMinutes"/> it walks the
    /// connected peers and asks each for a full save, but never more than
    /// <see cref="ValConfig.FullSyncMaxConcurrentPlayers"/> at once — larger player counts are spread across
    /// successive waves so incoming saves never spike the server's bandwidth.
    ///
    /// With <see cref="ValConfig.FullSyncSpreadAcrossInterval"/> on, which is the default, the waves give way
    /// to a timer per player spread across the whole interval (see SpreadPull): the same saves, the same
    /// cadence for each player, without the once-an-interval burst of parsing and rewriting them all at once.
    ///
    /// Full saves are only a periodic reconciliation on top of the incremental delta stream
    /// (<see cref="DeltaChangeTracker"/>), so a coarse interval (default 25 minutes) is intentional.
    /// </summary>
    internal static class FullSyncScheduler {
        // Gap between successive waves within a single pull cycle. Long enough for a wave's uploads to clear
        // the wire before the next wave starts, short enough that even a large server finishes a cycle in
        // seconds. Not exposed as config: the interval and wave size are the meaningful knobs.
        internal const float WaveStaggerSeconds = 3f;

        private static GameObject host;

        internal static void Initialize() {
            if (host != null) { return; }
            host = new GameObject("VE_FullSyncScheduler");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<FullSyncSchedulerBehaviour>();
            Logger.LogDebug("FullSyncScheduler initialized.");
        }

        internal static void Teardown() {
            if (host == null) { return; }
            UnityEngine.Object.Destroy(host); // also stops the running pull coroutine
            host = null;
            modules.archive.SaveArchiver.Shutdown();
            // Nothing is left to drain the queues, and every peer they referenced is going away with the server.
            CharacterStore.ClearDriftResyncs();
            CharacterStore.ClearSanitizedPushes();
            CharacterStore.ClearSealedPushes();
        }

        // Spawn the scheduler only on the server, once ZNet is up.
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Start))]
        public static class ZNet_Start_Patch {
            [HarmonyPostfix]
            private static void Postfix(ZNet __instance) {
                if (__instance != null && __instance.IsServer()) { Initialize(); }
            }
        }

        // Drop the scheduler when the server stops so a listen host that returns to the menu and hosts again
        // does not leak a second driver.
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
        public static class ZNet_Shutdown_Patch {
            [HarmonyPostfix]
            private static void Postfix() {
                Teardown();
            }
        }
    }

    internal class FullSyncSchedulerBehaviour : MonoBehaviour {
        private float nextCycle;
        private bool cycleRunning;

        // Once a minute: drop the small per-character tables that would otherwise grow with every character
        // ever seen, and write the memory summary when an admin has asked for one on a schedule.
        private const float HousekeepingSeconds = 60f;
        private float nextHousekeeping;
        private float nextMemoryReport;

        private void Start() {
            // Don't fire immediately on boot; wait a full interval. Freshly-joined players already push a full
            // save on connect (LoadAndValidatePlayer), so there is nothing to reconcile right away.
            nextCycle = Time.unscaledTime + IntervalSeconds();
        }

        private void Update() {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) { return; }

            // Drain any recovery requests the CharacterStore worker queued after a delta merge found the server
            // copy had drifted. They are issued here because the worker thread must not touch ZNet, and this
            // behaviour is already a server-only main-thread tick. Independent of the pull cycle below, so a
            // repair is not delayed by a wave that happens to be in flight.
            DrainDriftResyncs();
            DrainSanitizedPushes();
            modules.recovery.RecoveryManager.DrainSealed();
            modules.recovery.RecoveryManager.Tick();
            Housekeeping();
            // Watches for the game's own save thread to finish so an archive is never taken of a world that
            // is still being written. Returns immediately unless a save has just started - see SaveArchiver.
            modules.archive.SaveArchiver.Tick();

            if (ValConfig.FullSyncSpreadAcrossInterval != null && ValConfig.FullSyncSpreadAcrossInterval.Value) {
                SpreadPull();
                return;
            }

            if (cycleRunning) { return; }
            if (Time.unscaledTime < nextCycle) { return; }
            StartCoroutine(RunPullCycle());
        }

        // ---- Spread pulls ---------------------------------------------------------------------------------

        // When each connected peer falls due for a full save. Keyed by peer uid; entries for peers that have
        // left are dropped on the pass that notices.
        private readonly Dictionary<long, float> nextAsk = new Dictionary<long, float>();
        private readonly List<long> departedScratch = new List<long>();
        private float nextSpreadCheck;

        /// <summary>How long after first being seen a peer is left alone. A fresh join has just pushed a full
        /// save of its own accord and is still loading in; asking again straight away would be a second copy of
        /// the same save from a client that is busy.</summary>
        private const float SettleSeconds = 60f;

        /// <summary>
        /// Gives every player a timer of their own, started at a random point in the interval, so that
        /// everybody is asked once per FullSyncPullIntervalMinutes without everybody being asked in the same
        /// minute.
        ///
        /// A full save is the heaviest thing a client sends: the server parses the whole character, merges the
        /// lists it owns into it and writes it back out. The wave cycle below did that for every player inside
        /// a minute or so, once per interval - a hundred players was half a gigabyte of short-lived memory in
        /// that minute, and then nothing for the next twenty-four. The same hundred saves arriving one every
        /// fifteen seconds is no load at all.
        ///
        /// The random start is what makes that hold after a restart, which is the case that matters: everybody
        /// rejoins inside a couple of minutes, and timers started from the join would all come due inside the
        /// same couple of minutes, every interval, for as long as those players stayed. Started at a random
        /// phase they are spread from the first round on, and a player's own cadence after that is exactly the
        /// interval.
        ///
        /// At most one player is asked per pass. With timers spread across the interval two coming due in the
        /// same second is rare, and the second one simply goes a second later.
        ///
        /// Costs one float comparison a frame; the peer list is walked once a second, and nothing is allocated.
        /// </summary>
        private void SpreadPull() {
            float now = Time.unscaledTime;
            if (now < nextSpreadCheck) { return; }
            nextSpreadCheck = now + 1f;

            float interval = IntervalSeconds();
            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            ZNetPeer due = null;
            float dueSince = float.MaxValue;
            int ready = 0;
            for (int i = 0; i < peers.Count; i++) {
                ZNetPeer peer = peers[i];
                if (peer == null || !peer.IsReady()) { continue; }
                ready++;
                if (!nextAsk.TryGetValue(peer.m_uid, out float when)) {
                    // Somewhere in the coming interval, but never inside the settle period.
                    when = now + Mathf.Max(SettleSeconds, Random.Range(0f, interval));
                    nextAsk[peer.m_uid] = when;
                } else if (when > now + interval) {
                    // The interval was shortened; do not make them wait out the old one.
                    when = now + interval;
                    nextAsk[peer.m_uid] = when;
                }
                // Whoever has been due longest goes first, so nobody is starved by a run of others.
                if (when <= now && when < dueSince) {
                    dueSince = when;
                    due = peer;
                }
            }

            // Forget whoever has left. Only worth a second walk when the table has outgrown the peer list.
            if (nextAsk.Count > ready) {
                departedScratch.Clear();
                foreach (KeyValuePair<long, float> entry in nextAsk) {
                    if (ZNet.instance.GetPeer(entry.Key) == null) { departedScratch.Add(entry.Key); }
                }
                for (int i = 0; i < departedScratch.Count; i++) { nextAsk.Remove(departedScratch[i]); }
                departedScratch.Clear();
            }

            if (due == null) { return; }
            nextAsk[due.m_uid] = now + interval;
            RequestFullSync(due);
        }

        // Pushes queued by the CharacterStore worker after it held a first save to the new-character rules.
        // Same reason as the drift resyncs: the worker thread must not touch ZNet, and this is already a
        // server-only main-thread tick. Kept separate from the pull cycle so a push is not delayed behind a
        // wave that happens to be in flight - the player is standing there holding items the server has
        // already taken off their record.
        private static void DrainSanitizedPushes() {
            CharacterStore.SanitizedPush push;
            while ((push = CharacterStore.TryDequeueSanitizedPush()) != null) {
                ValConfig.SendSanitizedYamlToClient(push.Sender, push.Name, push.StrippedYaml);
            }
        }

        private void Housekeeping() {
            float now = Time.unscaledTime;
            if (now >= nextHousekeeping) {
                nextHousekeeping = now + HousekeepingSeconds;
                ValConfig.PruneDriftResyncTracking();
            }

            int reportMinutes = ValConfig.MemoryReportIntervalMinutes != null ? ValConfig.MemoryReportIntervalMinutes.Value : 0;
            if (reportMinutes <= 0) {
                nextMemoryReport = 0f; // switched off; switching back on starts a fresh interval
                return;
            }
            float interval = reportMinutes * 60f;
            if (nextMemoryReport <= 0f) {
                nextMemoryReport = now + interval; // first report one interval after it is switched on
                return;
            }
            // A shortened interval takes effect at once rather than after the old one runs out.
            if (nextMemoryReport > now + interval) { nextMemoryReport = now + interval; }
            if (now < nextMemoryReport) { return; }
            nextMemoryReport = now + interval;
            MemoryReport.LogSummary();
        }

        private static void DrainDriftResyncs() {
            CharacterStore.DriftResync request;
            while ((request = CharacterStore.TryDequeueDriftResync()) != null) {
                // Rate limiting and the "peer already left" check both live in RequestFullSyncForDrift.
                ValConfig.RequestFullSyncForDrift(request.Sender, request.HostID, request.Name);
            }
        }

        private static float IntervalSeconds() {
            return Mathf.Max(1, ValConfig.FullSyncPullIntervalMinutes.Value) * 60f;
        }

        private IEnumerator RunPullCycle() {
            cycleRunning = true;
            try {
                List<ZNetPeer> peers = ReadyClientPeers();
                if (peers.Count == 0) { yield break; }

                int batch = Mathf.Clamp(ValConfig.FullSyncMaxConcurrentPlayers.Value, 1, peers.Count);
                Logger.LogDebug($"FullSyncScheduler: requesting full character saves from {peers.Count} player(s) in waves of {batch}.");

                for (int i = 0; i < peers.Count; i += batch) {
                    // Re-check we are still a live server (the previous wait may have spanned a shutdown).
                    if (ZNet.instance == null || !ZNet.instance.IsServer()) { yield break; }

                    int end = Mathf.Min(i + batch, peers.Count);
                    for (int j = i; j < end; j++) {
                        RequestFullSync(peers[j]);
                    }

                    // Stagger the next wave so their uploads don't overlap on the wire.
                    if (end < peers.Count) {
                        yield return new WaitForSecondsRealtime(FullSyncScheduler.WaveStaggerSeconds);
                    }
                }
            } finally {
                cycleRunning = false;
                nextCycle = Time.unscaledTime + IntervalSeconds();
            }
        }

        // Connected, ready remote clients. On a dedicated server every peer is a client; on a listen host the
        // host's own character is persisted locally on join/logout and is not pulled over the network.
        private static List<ZNetPeer> ReadyClientPeers() {
            List<ZNetPeer> result = new List<ZNetPeer>();
            foreach (ZNetPeer peer in ZNet.instance.GetPeers()) {
                if (peer != null && peer.IsReady()) { result.Add(peer); }
            }
            return result;
        }

        private static void RequestFullSync(ZNetPeer peer) {
            // The client handler (OnClientReceiveFullSyncRequest) ignores the payload and re-sends its full
            // character, so an empty package is all that is needed.
            ValConfig.FullSyncRequestRPC.SendPackage(peer.m_uid, new ZPackage());
            Logger.LogDebug($"FullSyncScheduler: requested full character save from {peer.m_playerName}.");
        }
    }
}
