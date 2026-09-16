using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimEnforcer.common;
using Logger = ValheimEnforcer.Logger;

namespace ValheimEnforcer.modules.audit {

    /// <summary>
    /// Keeps a rolling window of how much damage each player is dealing, and writes down the spikes.
    ///
    /// Damage in Valheim is resolved on the client that owns the victim, from a HitData the ATTACKER's client
    /// wrote, and nothing validates it. The server is only the relay - which makes the relay the single place
    /// the numbers can be seen at all. This hangs off the parse RoutedRpcFilter already performs, so the
    /// packet is deserialized once whether the damage guard, this, or both are switched on.
    ///
    /// What is being counted is the pre-mitigation damage the attacker CLAIMED, before the victim applies
    /// armour, resistances and difficulty scaling. That is the right number for spotting somebody sending
    /// impossible hits and the wrong number for anything resembling balance analysis, and every report says
    /// so rather than leaving an admin to assume.
    ///
    /// Only hits whose attacker is the sending peer's own character are counted. A client also relays damage
    /// dealt by creatures it owns - a tamed wolf, a fire it lit - and attributing those to the player would
    /// turn a full boar farm into a damage spike.
    /// </summary>
    internal static class DamageAudit {

        /// <summary>Hard ceiling on the window, so a mistyped config cannot allocate an absurd ring.</summary>
        private const int MaxWindowSeconds = 600;

        private sealed class Window {
            internal string Account;
            internal string Character;

            // One slot per second, indexed by (unix second % length). Buckets rather than a list of hits:
            // a busy fight is hundreds of hits a minute per player, and this has to stay allocation-free
            // once it is running.
            internal float[] Damage;
            internal int[] Hits;
            internal float[] MaxHit;
            internal int[] MaxTargetPrefab;

            // Running totals over the live slots, maintained as slots are added to and cleared. The spike
            // check consults them on every single hit, and rescanning the ring each time would mean sixty
            // float additions per hit on the busiest path the mod touches.
            internal float Total;
            internal int HitCount;

            internal long LastSecond;
            /// <summary>Unix second of the last spike event, so a sustained fight cannot flood the log.</summary>
            internal long LastSpike;
            internal long LastActivity;
        }

        private static readonly Dictionary<string, Window> windows = new Dictionary<string, Window>(StringComparer.Ordinal);

        /// <summary>Players with a live damage ring, for enforcer-memory. Main thread.</summary>
        internal static int WindowCount => windows.Count;

        // ---- Observation ----------------------------------------------------------------------------------

        /// <summary>
        /// Counts one relayed hit. Called from RoutedRpcFilter with the HitData it has already parsed.
        ///
        /// Deliberately not wrapped in a StallWatch: this runs per hit on the busiest path the mod touches,
        /// and a stopwatch plus a dictionary probe per call would cost more than the work being measured.
        /// The enclosing RoutedRpcGuard prefix is the right granularity if this ever needs timing.
        /// </summary>
        internal static void Observe(ZNetPeer peer, HitData hit, ZDOID victim) {
            try {
                if (peer == null || hit == null) { return; }
                if (!AuditPolicy.Recorded(peer)) { return; }

                // Only the sender's own character. Anything else in this packet is a creature the client
                // happens to own, and is not this player's doing.
                if (peer.m_characterID == ZDOID.None || hit.m_attacker != peer.m_characterID) { return; }

                // The struct's own sum, never HitData.GetTotalDamage(): that one resolves the attacker
                // through ZNetScene to add a world-level bonus, and the server has no instance for a remote
                // attacker, so it would return through a null.
                float total = hit.m_damage.GetTotalDamage();
                if (float.IsNaN(total) || float.IsInfinity(total) || total <= 0f) { return; }

                string account = AuditPolicy.AccountOf(peer);
                if (string.IsNullOrEmpty(account)) { return; }

                long second = Now();
                Window window = WindowFor(account, AuditPolicy.CharacterOf(peer), second);
                if (window == null) { return; }

                int victimPrefab = PrefabOf(victim);
                int slot = (int)(second % window.Damage.Length);
                window.Damage[slot] += total;
                window.Hits[slot] += 1;
                window.Total += total;
                window.HitCount += 1;
                if (total > window.MaxHit[slot]) {
                    window.MaxHit[slot] = total;
                    window.MaxTargetPrefab[slot] = victimPrefab;
                }
                window.LastActivity = second;

                CheckSpike(window, hit, total, victimPrefab, second);
            } catch (Exception e) {
                // Sits inside the routed RPC relay. A recording problem must never cost a packet.
                Logger.LogDebug($"Damage audit could not observe a hit: {e.Message}");
            }
        }

        private static void CheckSpike(Window window, HitData hit, float total, int victimPrefab, long second) {
            float hitLimit = ValConfig.AuditHighDamageThreshold != null ? ValConfig.AuditHighDamageThreshold.Value : float.MaxValue;
            float windowLimit = ValConfig.AuditHighDamagePerWindow != null ? ValConfig.AuditHighDamagePerWindow.Value : float.MaxValue;

            // Both decisions come off the running totals, so the common case - nothing unusual - costs two
            // float comparisons and returns. The ring is only walked once something is actually being
            // reported, which is at most once per player per window.
            bool bigHit = total > hitLimit;
            bool bigWindow = window.Total > windowLimit;
            if (!bigHit && !bigWindow) { return; }

            // One spike per player per window, checked before anything is built. A cheat tool one-shotting a
            // boss produces the same event a hundred times a second otherwise, and the hundredth copy tells a
            // moderator nothing the first did not - the rolling summary is where the sustained picture lives.
            int windowSeconds = window.Damage.Length;
            if (window.LastSpike != 0 && second - window.LastSpike < windowSeconds) { return; }
            window.LastSpike = second;

            Summary summary = Summarise(window);

            AuditEvent entry = AuditPolicy.Event(window.Account, window.Character, AuditEvent.Kinds.DamageSpike);
            entry.Amount = bigHit ? total : summary.Total;
            entry.Target = NameOf(victimPrefab);
            entry.Note = bigHit
                ? $"single hit above the {hitLimit:G6} threshold; {summary.Total:G6} total over {windowSeconds}s from {summary.Hits} hit(s)"
                : $"{summary.Total:G6} total over {windowSeconds}s from {summary.Hits} hit(s), above the {windowLimit:G6} threshold";
            AuditPolicy.Record(entry);

            Logger.LogWarning($"Audit: {window.Character} ({window.Account}) {entry.Note}. " +
                              "This is the pre-mitigation damage their client claimed, not what landed.");
        }

        // ---- Windows --------------------------------------------------------------------------------------

        private static Window WindowFor(string account, string character, long second) {
            int size = WindowSize();
            if (!windows.TryGetValue(account, out Window window) || window.Damage.Length != size) {
                // A changed window length means the config was edited; start the ring again rather than try
                // to rescale it, which would misreport the seconds either side of the change.
                window = new Window {
                    Account = account,
                    Character = character,
                    Damage = new float[size],
                    Hits = new int[size],
                    MaxHit = new float[size],
                    MaxTargetPrefab = new int[size],
                    LastSecond = second
                };
                windows[account] = window;
                Advance(window, second);
                return window;
            }

            window.Character = character; // a rename or a new character on the same account
            Advance(window, second);
            return window;
        }

        /// <summary>
        /// Clears the slots for the seconds that have elapsed since this window was last touched, which is
        /// what makes the ring a rolling window rather than a running total.
        /// </summary>
        private static void Advance(Window window, long second) {
            if (second <= window.LastSecond) { return; }
            int size = window.Damage.Length;
            long elapsed = second - window.LastSecond;
            if (elapsed >= size) {
                Array.Clear(window.Damage, 0, size);
                Array.Clear(window.Hits, 0, size);
                Array.Clear(window.MaxHit, 0, size);
                Array.Clear(window.MaxTargetPrefab, 0, size);
                window.Total = 0f;
                window.HitCount = 0;
            } else {
                for (long s = window.LastSecond + 1; s <= second; s++) {
                    int slot = (int)(s % size);
                    // The running totals are the sum of the live slots, so what a slot held has to come back
                    // out of them as it is cleared or they would only ever grow.
                    window.Total -= window.Damage[slot];
                    window.HitCount -= window.Hits[slot];
                    window.Damage[slot] = 0f;
                    window.Hits[slot] = 0;
                    window.MaxHit[slot] = 0f;
                    window.MaxTargetPrefab[slot] = 0;
                }
                // Repeated subtraction of floats drifts; an empty ring must read as exactly zero or a player
                // who has stopped fighting keeps a residue in every report.
                if (window.HitCount <= 0) { window.HitCount = 0; window.Total = 0f; }
            }
            window.LastSecond = second;
        }

        internal struct Summary {
            internal string Account;
            internal string Character;
            internal float Total;
            internal int Hits;
            internal float MaxHit;
            internal string MaxTarget;
            internal int WindowSeconds;

            internal float PerSecond { get { return WindowSeconds > 0 ? Total / WindowSeconds : 0f; } }
        }

        private static Summary Summarise(Window window) {
            Summary summary = new Summary {
                Account = window.Account,
                Character = window.Character,
                WindowSeconds = window.Damage.Length
            };
            summary.Total = window.Total;
            summary.Hits = window.HitCount;
            // Only the largest hit needs the ring walked; the totals are maintained as the window moves.
            int best = 0;
            for (int i = 0; i < window.MaxHit.Length; i++) {
                if (window.MaxHit[i] > summary.MaxHit) {
                    summary.MaxHit = window.MaxHit[i];
                    best = window.MaxTargetPrefab[i];
                }
            }
            summary.MaxTarget = NameOf(best);
            return summary;
        }

        /// <summary>
        /// The live summary for every player who has dealt damage inside the window, busiest first.
        /// Main thread only - it reads the scene to name what was hit.
        /// </summary>
        internal static List<Summary> Live() {
            List<Summary> results = new List<Summary>();
            long second = Now();
            List<string> stale = null;

            foreach (KeyValuePair<string, Window> entry in windows) {
                Window window = entry.Value;
                // Two windows of silence and the player is not fighting; drop the ring rather than keep
                // clearing it for somebody who logged out an hour ago.
                if (second - window.LastActivity > window.Damage.Length * 2L) {
                    (stale ?? (stale = new List<string>())).Add(entry.Key);
                    continue;
                }
                Advance(window, second);
                Summary summary = Summarise(window);
                if (summary.Hits == 0) { continue; }
                results.Add(summary);
            }

            if (stale != null) {
                foreach (string key in stale) { windows.Remove(key); }
            }
            results.Sort((left, right) => right.Total.CompareTo(left.Total));
            return results;
        }

        // ---- Helpers --------------------------------------------------------------------------------------

        private static int WindowSize() {
            int seconds = ValConfig.AuditDamageWindowSeconds != null ? ValConfig.AuditDamageWindowSeconds.Value : 60;
            return Mathf.Clamp(seconds, 1, MaxWindowSeconds);
        }

        private static long Now() {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        /// <summary>The prefab hash of whatever was hit, or 0 when the victim cannot be resolved.</summary>
        private static int PrefabOf(ZDOID victim) {
            if (victim == ZDOID.None || ZDOMan.instance == null) { return 0; }
            ZDO zdo = ZDOMan.instance.GetZDO(victim);
            return zdo != null ? zdo.GetPrefab() : 0;
        }

        /// <summary>
        /// A readable name for a prefab hash. Resolved at report time rather than per hit, because a name is
        /// only ever needed for the handful of hits that become a spike or a summary line.
        /// </summary>
        private static string NameOf(int prefabHash) {
            if (prefabHash == 0) { return null; }
            try {
                GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabHash) : null;
                return prefab != null ? prefab.name : prefabHash.ToString();
            } catch (Exception) {
                return prefabHash.ToString();
            }
        }

        internal static void Forget(string account) {
            if (!string.IsNullOrEmpty(account)) { windows.Remove(account); }
        }

        internal static void Reset() {
            windows.Clear();
        }
    }
}
