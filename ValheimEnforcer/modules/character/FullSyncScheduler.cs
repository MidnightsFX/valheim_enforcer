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
            // Nothing is left to drain the queues, and every peer they referenced is going away with the server.
            CharacterStore.ClearDriftResyncs();
            CharacterStore.ClearSanitizedPushes();
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

            if (cycleRunning) { return; }
            if (Time.unscaledTime < nextCycle) { return; }
            StartCoroutine(RunPullCycle());
        }

        // Pushes queued by the CharacterStore worker after it held a first save to the new-character rules.
        // Same reason as the drift resyncs: the worker thread must not touch ZNet, and this is already a
        // server-only main-thread tick. Kept separate from the pull cycle so a push is not delayed behind a
        // wave that happens to be in flight - the player is standing there holding items the server has
        // already taken off their record.
        private static void DrainSanitizedPushes() {
            CharacterStore.SanitizedPush push;
            while ((push = CharacterStore.TryDequeueSanitizedPush()) != null) {
                ValConfig.SendSanitizedCharacterToClient(push.Sender, push.HostID, push.Name);
            }
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
