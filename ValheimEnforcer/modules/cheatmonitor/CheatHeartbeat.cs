using System;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using UnityEngine;
using ValheimEnforcer.modules.network;

namespace ValheimEnforcer.modules.cheatmonitor {

    /// <summary>
    /// A periodic check that each client's cheat scan is still running and still reporting.
    ///
    /// The detector otherwise only speaks when it has found something, which means a client whose detector
    /// has stopped looks exactly like a clean one. Here the server asks, on its own schedule and with a fresh
    /// value each time, and the client has to answer whether or not it has anything to report. The answer says
    /// what state the detector is in, the last report it sent, and anything its self-check found.
    ///
    /// Server side: a client that stops answering is logged and, after CheatHeartbeatMissesAllowed misses in
    /// a row, fed to PeerTrust as the "cheat-heartbeat" guard. An answer showing the detector stopped, reports
    /// that never arrived, or tampering is a medium-confidence sighting (CheatSuspicion). Nothing here kicks
    /// or bans by itself.
    /// </summary>
    internal static class CheatHeartbeat {

        private const int WireVersion = 1;
        internal const int MaxAnswerBytes = 4 * 1024;
        private const int NonceBytes = 16;

        /// <summary>How long after a player spawns before their first check. Loading in is busy enough.</summary>
        private const float SettleSeconds = 120f;
        /// <summary>At most this many challenges go out per pass, so a restart does not ask everyone at once.</summary>
        private const int MaxChallengesPerPass = 4;
        internal const string MissGuard = "cheat-heartbeat";

        internal enum DetectorState { NotStarted = 0, Running = 1, Disabled = 2 }

        // ---- Client -------------------------------------------------------------------------------------

        /// <summary>
        /// Client handler: answers the server's challenge straight away. Answers even when there is no detector,
        /// because "not running" is itself the answer the server is asking for.
        /// </summary>
        internal static IEnumerator OnClientReceive(long sender, ZPackage package) {
            if (ZNet.instance == null || ZNet.instance.IsServer()) { yield break; }
            if (!ValConfig.FromServer(sender)) {
                Logger.LogDebug($"Ignoring a heartbeat check not from the server (sender {sender}).");
                yield break;
            }
            string nonce;
            try {
                package.ReadInt(); // wire version; nothing differs between versions yet
                nonce = package.ReadString();
            } catch (Exception e) {
                Logger.LogDebug($"Could not read a heartbeat check: {e.Message}");
                yield break;
            }

            ZNetPeer server = ZNet.instance.GetServerPeer();
            if (server == null || ValConfig.CheatHeartbeatRPC == null) { yield break; }
            try {
                ValConfig.CheatHeartbeatRPC.SendPackage(server.m_uid, BuildAnswer(nonce));
            } catch (Exception e) {
                Logger.LogDebug($"Could not answer a heartbeat check: {e.Message}");
            }
        }

        internal static ZPackage BuildAnswer(string nonce) {
            CheatDetector.HeartbeatSnapshot(out DetectorState state, out float sinceUpdate, out int lastSeq, out List<string> findings);
            ZPackage answer = new ZPackage();
            answer.Write(WireVersion);
            answer.Write(nonce ?? "");
            answer.Write((int)state);
            answer.Write(sinceUpdate);
            answer.Write(lastSeq);
            int count = findings == null ? 0 : Math.Min(findings.Count, CheatSelfCheck.MaxFindings);
            answer.Write(count);
            for (int i = 0; i < count; i++) { answer.Write(findings[i] ?? ""); }
            return answer;
        }

        // ---- Server -------------------------------------------------------------------------------------

        private sealed class PeerState {
            internal float SpawnedAt = -1f;
            internal float NextChallenge;
            internal string Outstanding;
            internal float Deadline;
            internal int Misses;
            internal bool MissReported;
            internal bool SeenRunning;
            internal bool DetectorFlagged;
            internal int MaxSeqReceived;
            internal int SeqStreak;
            internal bool SeqFlagged;
            internal readonly HashSet<string> FindingsSeen = new HashSet<string>(StringComparer.Ordinal);
        }

        // Keyed by peer uid, which is new on every connection, so a reconnect starts clean and a late
        // disconnect of the old connection cannot touch the new one's state. Main thread only.
        private static readonly Dictionary<long, PeerState> states = new Dictionary<long, PeerState>();
        private static readonly List<long> departedScratch = new List<long>();
        private static float nextPass;

        private static readonly RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();

        private static bool Active() {
            return ValConfig.CheatScanHeartbeat != null && ValConfig.CheatScanHeartbeat.Value
                && ValConfig.EnableCheatDetection != null && ValConfig.EnableCheatDetection.Value;
        }

        /// <summary>Server main-thread tick, driven by the full-sync scheduler's behaviour.</summary>
        internal static void Tick() {
            if (!Active()) {
                if (states.Count > 0) { states.Clear(); }
                return;
            }
            float now = Time.unscaledTime;
            if (now < nextPass) { return; }
            nextPass = now + 1f;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) { return; }

            float interval = Mathf.Max(1, ValConfig.CheatHeartbeatIntervalMinutes.Value) * 60f;
            float timeout = Mathf.Max(5, ValConfig.CheatHeartbeatTimeoutSeconds.Value);
            int missesAllowed = Mathf.Max(1, ValConfig.CheatHeartbeatMissesAllowed.Value);
            int sent = 0;

            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            for (int i = 0; i < peers.Count; i++) {
                ZNetPeer peer = peers[i];
                if (peer == null || !peer.IsReady() || peer.m_socket == null) { continue; }
                if (peer.m_characterID == ZDOID.None) { continue; }
                string hostId = peer.m_socket.GetHostName();
                if (!ModManager.IsValidatedHost(hostId)) { continue; }
                if (RpcGuardPolicy.IsExempt(peer)) { continue; }

                PeerState st = StateFor(peer.m_uid);
                if (st.SpawnedAt < 0f) {
                    st.SpawnedAt = now;
                    st.NextChallenge = now + Mathf.Max(SettleSeconds, UnityEngine.Random.Range(0f, interval));
                } else if (st.NextChallenge > now + interval) {
                    st.NextChallenge = now + interval; // the interval was shortened
                }

                if (st.Outstanding != null && now > st.Deadline) {
                    st.Outstanding = null;
                    st.Misses++;
                    Logger.LogDebug($"Heartbeat check missed by {peer.m_playerName} ({hostId}): {st.Misses} in a row.");
                    if (st.Misses >= missesAllowed && !st.MissReported) {
                        st.MissReported = true;
                        string detail = $"{st.Misses} heartbeat check(s) in a row went unanswered";
                        Logger.LogWarning($"Heartbeat: {peer.m_playerName} ({hostId}) - {detail}. Their cheat scan may not be running.");
                        PeerTrust.NoteGuardTrip(peer, MissGuard, detail);
                    }
                }

                if (st.Outstanding == null && now >= st.NextChallenge && sent < MaxChallengesPerPass) {
                    Challenge(peer, st, now, timeout);
                    st.NextChallenge = now + interval;
                    sent++;
                }
            }

            if (states.Count > peers.Count) {
                departedScratch.Clear();
                foreach (KeyValuePair<long, PeerState> entry in states) {
                    if (ZNet.instance.GetPeer(entry.Key) == null) { departedScratch.Add(entry.Key); }
                }
                for (int i = 0; i < departedScratch.Count; i++) { states.Remove(departedScratch[i]); }
                departedScratch.Clear();
            }
        }

        private static PeerState StateFor(long uid) {
            if (!states.TryGetValue(uid, out PeerState st)) {
                st = new PeerState();
                states[uid] = st;
            }
            return st;
        }

        private static void Challenge(ZNetPeer peer, PeerState st, float now, float timeout) {
            if (ValConfig.CheatHeartbeatRPC == null) { return; }
            byte[] bytes = new byte[NonceBytes];
            lock (rng) { rng.GetBytes(bytes); }
            string nonce = Convert.ToBase64String(bytes);
            ZPackage pkg = new ZPackage();
            pkg.Write(WireVersion);
            pkg.Write(nonce);
            try {
                ValConfig.CheatHeartbeatRPC.SendPackage(peer.m_uid, pkg);
                st.Outstanding = nonce;
                st.Deadline = now + timeout;
            } catch (Exception e) {
                Logger.LogDebug($"Could not send a heartbeat check to {peer.m_playerName}: {e.Message}");
            }
        }

        /// <summary>Server handler: a client's answer to a check.</summary>
        internal static IEnumerator OnServerReceive(long sender, ZPackage package) {
            if (!ValConfig.WithinLimit(package, MaxAnswerBytes, sender, "heartbeat answer")) { yield break; }
            if (!Active() || ZNet.instance == null) { yield break; }
            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            if (peer == null || !states.TryGetValue(sender, out PeerState st)) { yield break; }

            string nonce;
            DetectorState state;
            float sinceUpdate;
            int lastSeq;
            List<string> findings = new List<string>();
            try {
                package.ReadInt(); // wire version
                nonce = package.ReadString();
                state = (DetectorState)package.ReadInt();
                sinceUpdate = package.ReadSingle();
                lastSeq = package.ReadInt();
                int count = Math.Min(package.ReadInt(), CheatSelfCheck.MaxFindings);
                for (int i = 0; i < count; i++) { findings.Add(ValConfig.TruncateCheatField(package.ReadString())); }
            } catch (Exception e) {
                Logger.LogDebug($"Unreadable heartbeat answer from {sender}: {e.Message}");
                yield break;
            }

            // An answer to anything but the check in flight changes nothing: the check times out as a miss.
            if (st.Outstanding == null || !string.Equals(nonce, st.Outstanding, StringComparison.Ordinal)) {
                Logger.LogDebug($"Heartbeat answer from {peer.m_playerName} did not match the check in flight.");
                yield break;
            }
            st.Outstanding = null;
            st.Misses = 0;
            st.MissReported = false;

            float interval = Mathf.Max(1, ValConfig.CheatHeartbeatIntervalMinutes.Value) * 60f;
            bool running = state == DetectorState.Running && !(sinceUpdate > 2f * interval);
            if (running) {
                st.SeenRunning = true;
            } else if (st.SeenRunning && !st.DetectorFlagged) {
                st.DetectorFlagged = true;
                string what = state == DetectorState.Running ? $"has not run for {sinceUpdate:F0}s" : $"reports {state}";
                CheatSuspicion.Report(peer, "Cheat scan stopped", null, $"the cheat detector was running earlier in this session and now {what}");
            }

            if (lastSeq > st.MaxSeqReceived) {
                st.SeqStreak++;
                if (st.SeqStreak >= 2 && !st.SeqFlagged) {
                    st.SeqFlagged = true;
                    CheatSuspicion.Report(peer, "Cheat reports missing", $"client sent #{lastSeq}, server received #{st.MaxSeqReceived}",
                        "the client says it sent cheat reports that never reached the server");
                }
            } else {
                st.SeqStreak = 0;
            }

            foreach (string finding in findings) {
                if (string.IsNullOrEmpty(finding) || !st.FindingsSeen.Add(finding)) { continue; }
                CheatSuspicion.Report(peer, CheatSelfCheck.TamperLabel, finding, "reported by the client's anti-cheat self-check");
            }
        }

        /// <summary>
        /// Records the sequence number of a cheat report the server received, before anything else is done
        /// with it. Server main thread.
        /// </summary>
        internal static void NoteReportSeq(long sender, int seq) {
            if (seq <= 0 || !Active()) { return; }
            PeerState st = StateFor(sender);
            if (seq > st.MaxSeqReceived) { st.MaxSeqReceived = seq; }
        }

        /// <summary>
        /// Records a tampering finding the client already delivered in a report, so the same finding repeated
        /// in a heartbeat answer is not reported twice. Returns false when it was already known.
        /// </summary>
        internal static bool NoteFindingReported(long sender, string finding) {
            if (string.IsNullOrEmpty(finding)) { return true; }
            if (!Active()) { return true; }
            return StateFor(sender).FindingsSeen.Add(finding);
        }

        internal static int TrackedCount => states.Count;

        /// <summary>Drops everything. Called when the server shuts down.</summary>
        internal static void Reset() {
            states.Clear();
            nextPass = 0f;
        }
    }
}
