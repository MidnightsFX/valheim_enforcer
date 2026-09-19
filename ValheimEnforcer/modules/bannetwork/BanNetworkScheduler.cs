using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using ValheimEnforcer.common;
using ValheimEnforcer.modules.character;

namespace ValheimEnforcer.modules.bannetwork {

    /// <summary>
    /// Runs the hourly pull.
    ///
    /// <para>Shaped after FullSyncScheduler: a hidden DontDestroyOnLoad GameObject created from a server-only
    /// ZNet.Start postfix, destroyed on ZNet.Shutdown, with an Update that compares Time.unscaledTime against
    /// a deadline. Config is read fresh each tick so a file-watcher reload takes effect without a restart.</para>
    ///
    /// <para>HTTP runs on a Task; results come back through a ConcurrentQueue drained on the main thread,
    /// because applying them touches ZNet. The worker captures every value it needs before it starts - it
    /// must never read a ConfigEntry or a ZNet field itself.</para>
    /// </summary>
    internal static class BanNetworkScheduler {

        /// <summary>Pages per cycle. A first sync of a large list finishes over a few cycles rather than
        /// holding one connection open for thousands of entries.</summary>
        private const int MaxPagesPerCycle = 20;
        private const int PageSize = 500;

        /// <summary>Hard floor regardless of config, so an edited setting cannot stampede the endpoint.</summary>
        private const double MinPullIntervalMinutes = 15;
        private const double MaxPullIntervalMinutes = 360;

        private static GameObject host;
        private static readonly ConcurrentQueue<PullResult> Results = new ConcurrentQueue<PullResult>();
        private static volatile bool inFlight;
        private static volatile bool pushInFlight;
        private static float nextPull;
        private static float nextPush;
        private static readonly ConcurrentQueue<PushResult> PushResults = new ConcurrentQueue<PushResult>();
        /// <summary>Rolling window of send times, for the self-imposed hourly ceiling.</summary>
        private static readonly List<DateTime> RecentReports = new List<DateTime>();
        private static int serviceMinIntervalMinutes;
        private static System.Random jitter;

        internal static bool Running { get { return host != null; } }
        internal static DateTime? NextPullLocal;

        // ---- Lifecycle ------------------------------------------------------------------------------------

        internal static void Initialize() {
            if (host != null) { return; }

            BanApiKey.EnsureFile();
            BanNetworkStore.EnsureFile();
            BanOutbox.EnsureFile();
            BanNetworkState.Load();
            BanNetworkStore.Load();
            BanOutbox.Load();

            // Seeded from the server id rather than the clock, so this server keeps a stable position in the
            // hour across restarts instead of re-rolling into a fresh collision with everyone else on every
            // boot. The realistic stampede is a Thunderstore release: hundreds of servers updating and
            // restarting within the same hour.
            BanApiKey.Read(out _, out string serverId);
            jitter = new System.Random(string.IsNullOrEmpty(serverId)
                ? Environment.TickCount
                : serverId.GetHashCode());

            // Watched so pasting a key takes effect without a restart - and, more importantly, so a
            // terminal 'rejected' state is cleared when the admin replaces the key that caused it. Without
            // this they would fix the key and see nothing happen until they restarted the server.
            ConfigFileWatcher.Register(BanApiKey.FilePath, _ => OnKeyFileChanged());

            host = new GameObject("VE_BanNetworkScheduler");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<BanNetworkSchedulerBehaviour>();

            // A minute to let the server finish starting, then anywhere in the first interval.
            nextPull = Time.unscaledTime + 60f + (float)(jitter.NextDouble() * IntervalSeconds());
            NextPullLocal = DateTime.UtcNow.AddSeconds(nextPull - Time.unscaledTime);
            // Anything left in the outbox from last session goes out shortly after startup, not an interval
            // from now: those reports have already waited through a restart.
            nextPush = Time.unscaledTime + 30f;
        }

        internal static void Teardown() {
            if (host != null) {
                UnityEngine.Object.Destroy(host);
                host = null;
            }
            while (Results.TryDequeue(out _)) { }
            while (PushResults.TryDequeue(out _)) { }
            inFlight = false;
            pushInFlight = false;
            NextPullLocal = null;
        }

        // ---- Tick -----------------------------------------------------------------------------------------

        internal static void Tick() {
            DrainResults();
            DrainPushResults();

            if (Time.unscaledTime >= nextPush && !pushInFlight) {
                nextPush = Time.unscaledTime + (float)(PushIntervalMinutes() * 60);
                RequestPush(force: false);
            }

            if (Time.unscaledTime < nextPull || inFlight) { return; }
            ScheduleNext();
            RequestCycle(force: false);
        }

        // ---- Push -----------------------------------------------------------------------------------------

        /// <summary>
        /// Sends whatever is queued. Everything the worker needs is read here, on the main thread.
        /// </summary>
        internal static bool RequestPush(bool force) {
            if (pushInFlight) { return false; }
            if (ValConfig.EnableBanNetwork == null || !ValConfig.EnableBanNetwork.Value) { return false; }
            if (BanOutbox.Count == 0) { return false; }

            if (!force && BanNetworkState.Blocked(out string why)) {
                Logger.LogDebug($"Ban network: holding {BanOutbox.Count} queued report(s) - {why}.");
                return false;
            }

            BanApiKey.Shape shape = BanApiKey.Read(out string key, out string serverId);
            if (shape != BanApiKey.Shape.Valid) { NoteKeyProblem(shape); return false; }
            BanNetworkState.ServerId = serverId;

            if (!BanNetworkClient.TryBuildBase(ValConfig.BanNetworkEndpoint.Value, out Uri baseUri, out string error)) {
                Logger.LogError($"Ban network: BanNetworkEndpoint is unusable ({error}). Nothing was sent.");
                return false;
            }

            int allowance = HourlyAllowance();
            if (allowance <= 0) {
                Logger.LogDebug("Ban network: the self-imposed hourly report ceiling has been reached; holding.");
                return false;
            }

            List<DataObjects.BanReportLine> batch = BanOutbox.Take(Math.Min(BanOutbox.MaxBatch, allowance));
            if (batch.Count == 0) { return false; }

            string body = BanNetworkJson.WriteReportBody(batch);
            // Validated once more as a whole before it leaves. A malformed body would be rejected by the
            // endpoint on every retry forever; this turns that into a single log line.
            if (!JsonWellFormed.Validate("[" + body.Trim().Replace("\n", ",") + "]", out string invalid)) {
                Logger.LogError($"Ban network: the outgoing report batch is not valid JSON ({invalid}); it was not sent.");
                return false;
            }

            NoteReportsSent(batch.Count);
            pushInFlight = true;
            Task.Run(() => RunPush(baseUri, key, body));
            return true;
        }

        private static async Task RunPush(Uri baseUri, string key, string body) {
            try {
                PushResults.Enqueue(await BanNetworkClient.Push(baseUri, key, body).ConfigureAwait(false));
            } catch (Exception e) {
                PushResults.Enqueue(new PushResult { Error = e.Message });
            } finally {
                pushInFlight = false;
            }
        }

        private static void DrainPushResults() {
            while (PushResults.TryDequeue(out PushResult result)) {
                try {
                    ApplyPush(result);
                } catch (Exception e) {
                    Logger.LogWarning($"Ban network: could not apply a publish result: {e.Message}");
                }
            }
        }

        private static void ApplyPush(PushResult result) {
            if (result.RateLimited) {
                // Not a key problem and not recorded as one: the service is asking this server to slow down.
                int wait = result.RetryAfterMinutes > 0 ? result.RetryAfterMinutes : 10;
                nextPush = Time.unscaledTime + wait * 60f;
                Logger.LogWarning($"Ban network: the service is rate limiting this server's reports; holding for {wait} minute(s).");
                return;
            }

            if (!result.Ok) {
                BanNetworkState.NoteFailure(result.State, result.Note, result.Error);
                if (KeyStates.Terminal(result.State)) {
                    Logger.LogError($"Ban network: {KeyStates.Describe(result.State, result.Note)}. "
                                  + $"{BanOutbox.Count} queued report(s) are kept and will be sent if this is resolved.");
                } else if (BanNetworkState.ConsecutiveFailures == 1 || BanNetworkState.ConsecutiveFailures % 6 == 0) {
                    Logger.LogWarning($"Ban network: could not publish reports ({result.Error}). {BanOutbox.Count} still queued.");
                }
                return;
            }

            BanNetworkState.NoteSuccess();
            BanNetworkState.LastPushUtc = DateTime.UtcNow;

            List<string> accepted = new List<string>();
            int rejected = 0;
            foreach (DataObjects.BanReportResult line in result.Results) {
                if (line.Accepted) {
                    accepted.Add(line.ReportId);
                } else {
                    rejected++;
                    // Dropped rather than retried: a rejection is about the content of the report, so the
                    // same bytes would be refused every time and the outbox would never drain.
                    accepted.Add(line.ReportId);
                    Logger.LogWarning($"Ban network: a report was refused and has been discarded: {line.Error}");
                }
            }

            BanOutbox.MarkDelivered(accepted);
            BanNetworkState.Save();

            int published = accepted.Count - rejected;
            if (published > 0) {
                Logger.LogInfo($"Ban network: published {published} ban report(s)"
                             + (BanOutbox.Count > 0 ? $"; {BanOutbox.Count} still queued." : "."));
            }
        }

        /// <summary>How many more reports this server is willing to send in the current hour.</summary>
        private static int HourlyAllowance() {
            int ceiling = ValConfig.BanNetworkMaxReportsPerHour?.Value ?? 60;
            DateTime cutoff = DateTime.UtcNow.AddHours(-1);
            lock (RecentReports) {
                RecentReports.RemoveAll(when => when < cutoff);
                return Math.Max(0, ceiling - RecentReports.Count);
            }
        }

        private static void NoteReportsSent(int count) {
            lock (RecentReports) {
                for (int i = 0; i < count; i++) { RecentReports.Add(DateTime.UtcNow); }
            }
        }

        private static double PushIntervalMinutes() {
            double configured = ValConfig.BanNetworkPushIntervalMinutes?.Value ?? 5;
            return Math.Min(60, Math.Max(1, configured));
        }

        /// <summary>Brings the next publish forward, for enforcer-ban-network-sync and for a fresh ban.</summary>
        internal static void PushSoon() {
            if (host == null) { return; }
            nextPush = Time.unscaledTime + 5f;
        }

        /// <summary>
        /// Sends what is queued, synchronously, on the way down.
        ///
        /// A clean shutdown should not strand a ban an admin issued a minute earlier. Bounded hard, because
        /// this runs on the shutdown path - the DiscordNotifier.NotifySync precedent.
        /// </summary>
        internal static void FlushOnShutdown() {
            if (BanOutbox.Count == 0) { return; }
            if (ValConfig.EnableBanNetwork == null || !ValConfig.EnableBanNetwork.Value) { return; }
            if (BanNetworkState.Blocked(out _)) { return; }
            if (BanApiKey.Read(out string key, out _) != BanApiKey.Shape.Valid) { return; }
            if (!BanNetworkClient.TryBuildBase(ValConfig.BanNetworkEndpoint.Value, out Uri baseUri, out _)) { return; }

            try {
                List<DataObjects.BanReportLine> batch = BanOutbox.Take(BanOutbox.MaxBatch);
                if (batch.Count == 0) { return; }
                Logger.LogInfo($"Ban network: sending {batch.Count} queued report(s) before shutting down.");

                Task<PushResult> task = BanNetworkClient.Push(baseUri, key, BanNetworkJson.WriteReportBody(batch));
                if (!task.Wait(TimeSpan.FromSeconds(8))) {
                    Logger.LogInfo("Ban network: the shutdown flush ran out of time; the reports are still queued "
                                 + "and will be sent on the next start.");
                    return;
                }

                PushResult result = task.Result;
                if (result == null || !result.Ok) { return; }

                List<string> accepted = new List<string>();
                foreach (DataObjects.BanReportResult line in result.Results) { accepted.Add(line.ReportId); }
                BanOutbox.MarkDelivered(accepted);
            } catch (Exception e) {
                // Never let a shutdown path throw. Anything unsent is still in the outbox for next time,
                // which is exactly the situation the outbox exists to handle.
                Logger.LogDebug($"Ban network: the shutdown flush did not complete: {e.Message}");
            }
        }

        /// <summary>
        /// Starts a pull. Everything the worker needs is read here, on the main thread.
        /// </summary>
        internal static bool RequestCycle(bool force) {
            if (inFlight) { return false; }
            if (ValConfig.EnableBanNetwork == null || !ValConfig.EnableBanNetwork.Value) { return false; }

            if (!force && BanNetworkState.Blocked(out string why)) {
                Logger.LogDebug($"Ban network: skipping this cycle - {why}.");
                return false;
            }

            BanApiKey.Shape shape = BanApiKey.Read(out string key, out string serverId);
            if (shape != BanApiKey.Shape.Valid) {
                NoteKeyProblem(shape);
                return false;
            }
            BanNetworkState.ServerId = serverId;

            if (!BanNetworkClient.TryBuildBase(ValConfig.BanNetworkEndpoint.Value, out Uri baseUri, out string error)) {
                Logger.LogError($"Ban network: BanNetworkEndpoint is unusable ({error}). No request was made.");
                BanNetworkState.NoteFailure(KeyState.Error, null, $"endpoint rejected: {error}");
                return false;
            }

            long cursor = BanNetworkState.Cursor;
            inFlight = true;
            Task.Run(() => RunCycle(baseUri, key, cursor));
            return true;
        }

        private static void NoteKeyProblem(BanApiKey.Shape shape) {
            KeyState state = shape == BanApiKey.Shape.Missing ? KeyState.NoKey : KeyState.Malformed;
            if (BanNetworkState.Key == state) { return; } // said once, not once an hour
            BanNetworkState.NoteFailure(state, null, null);
            if (state == KeyState.NoKey) {
                Logger.LogWarning($"Ban network: enabled, but there is no API key. Put one in {BanApiKey.FilePath} "
                                + "and run enforcer-ban-network-sync. Registration is reviewed by a person.");
            } else {
                Logger.LogError($"Ban network: the key in {BanApiKey.FilePath} is not a valid key, so nothing was sent. "
                              + "It should look like vebn_ab12cd34_<43 characters>.");
            }
        }

        /// <summary>Background: pulls pages until the feed says there are no more, or the page cap is hit.</summary>
        private static async Task RunCycle(Uri baseUri, string key, long cursor) {
            try {
                for (int page = 0; page < MaxPagesPerCycle; page++) {
                    PullResult result = await BanNetworkClient.Pull(baseUri, key, cursor, PageSize)
                        .ConfigureAwait(false);
                    Results.Enqueue(result);
                    if (!result.Ok || !result.More) { break; }
                    if (result.NextCursor <= cursor) { break; } // no progress; stop rather than spin
                    cursor = result.NextCursor;
                }
            } catch (Exception e) {
                Results.Enqueue(new PullResult { Error = e.Message });
            } finally {
                inFlight = false;
            }
        }

        // ---- Applying results (main thread) ---------------------------------------------------------------

        private static void DrainResults() {
            while (Results.TryDequeue(out PullResult result)) {
                try {
                    Apply(result);
                } catch (Exception e) {
                    Logger.LogWarning($"Ban network: could not apply a pull result: {e.Message}");
                }
            }
        }

        private static void Apply(PullResult result) {
            if (result.MinIntervalMinutes > 0) { serviceMinIntervalMinutes = result.MinIntervalMinutes; }

            if (result.ResyncRequired) {
                Logger.LogWarning("Ban network: this server's cursor is older than the network's retention window. "
                                + "Clearing the cached list and rebuilding it from scratch.");
                BanNetworkStore.Purge();
                BanNetworkState.ResetCursor();
                nextPull = Time.unscaledTime + 10f; // rebuild promptly rather than waiting out the hour
                return;
            }

            if (!result.Ok) {
                bool terminal = KeyStates.Terminal(result.State);
                BanNetworkState.NoteFailure(result.State, result.Note, result.Error);
                string description = KeyStates.Describe(result.State, result.Note);
                if (terminal) {
                    Logger.LogError($"Ban network: {description}. No further requests will be made this session. "
                                  + "The cached list is kept and still enforced. See enforcer-ban-network-status.");
                } else if (result.State == KeyState.Pending) {
                    Logger.LogInfo($"Ban network: {description}. Will check again later.");
                } else if (BanNetworkState.ConsecutiveFailures == 1 || BanNetworkState.ConsecutiveFailures % 6 == 0) {
                    // Once, then every sixth attempt: an endpoint that is down for a day should not write a
                    // line an hour into somebody's log.
                    Logger.LogWarning($"Ban network: {description} ({result.Error}).");
                }
                return;
            }

            BanNetworkState.NoteSuccess();
            BanNetworkState.LastPullUtc = DateTime.UtcNow;

            List<BanCategory> enforced = EnforcedCategories();
            int minReporters = ValConfig.BanNetworkMinReporters.Value;

            List<NetworkBanRecord> newlyEnforceable = BanNetworkStore.Apply(result.Entries, enforced, minReporters);

            if (result.NextCursor > BanNetworkState.Cursor) { BanNetworkState.Cursor = result.NextCursor; }
            BanNetworkState.Save();

            if (result.Entries.Count > 0) {
                Logger.LogInfo($"Ban network: {result.Entries.Count} entr{(result.Entries.Count == 1 ? "y" : "ies")} received "
                             + $"({BanNetworkStore.Count} held, cursor {BanNetworkState.Cursor})"
                             + (result.Skipped > 0 ? $", {result.Skipped} unreadable line(s) skipped" : "") + ".");
            }

            EnforceAgainstConnected(newlyEnforceable);
        }

        /// <summary>
        /// Applies newly enforceable entries to players who are already in the world.
        ///
        /// Without this a ban that lands at the top of the hour does nothing until that player happens to
        /// reconnect - which the player being banned has no reason to do.
        /// </summary>
        private static void EnforceAgainstConnected(List<NetworkBanRecord> newlyEnforceable) {
            if (newlyEnforceable.Count == 0 || ZNet.instance == null) { return; }

            string action = ValConfig.BanNetworkAction?.Value ?? "Ban";
            if (action == "Log") { return; }

            foreach (ZNetPeer peer in ZNet.instance.GetPeers()) {
                if (peer == null || peer.m_socket == null) { continue; }
                string hostId = PeerIdentity.AccountFor(peer);
                if (string.IsNullOrEmpty(hostId)) { continue; }

                string subject = BanSubjectCache.Hash(hostId);
                if (string.IsNullOrEmpty(subject)) { continue; }

                NetworkBanRecord match = null;
                foreach (NetworkBanRecord record in newlyEnforceable) {
                    if (string.Equals(record.Subject, subject, StringComparison.Ordinal)) { match = record; break; }
                }
                if (match == null) { continue; }

                // An override the owner set beats the network, here as everywhere else.
                BanVerdict verdict = BanPolicy.Evaluate(hostId, subject);
                if (!verdict.Reject) { continue; }

                Logger.LogWarning($"Ban network: {peer.m_playerName} ({hostId}) is now listed - {match.Describe()}. Applying {action}.");
                BanNotifications.Enforced(peer.m_playerName, hostId, verdict, action + "ned mid-session");
                if (action == "Ban") {
                    // Deliberately NOT ValConfig.BanHost: that records a local ban, which would relabel a
                    // pulled entry as something this server witnessed and feed it straight back to the
                    // network as independent corroboration. Vanilla's ban list is enough to keep them out;
                    // the network entry is what remembers why.
                    ZNet.instance.Ban(hostId);
                } else {
                    ZNet.instance.Kick(hostId);
                }
            }
        }

        // ---- Timing ---------------------------------------------------------------------------------------

        internal static List<BanCategory> EnforcedCategories() {
            BanCategories.TryParseList(ValConfig.BanNetworkEnforceCategories?.Value, out List<BanCategory> parsed, out _);
            return parsed;
        }

        private static double IntervalMinutes() {
            double configured = ValConfig.BanNetworkPullIntervalMinutes?.Value ?? 60;
            if (serviceMinIntervalMinutes > configured) { configured = serviceMinIntervalMinutes; }
            return Math.Min(MaxPullIntervalMinutes, Math.Max(MinPullIntervalMinutes, configured));
        }

        private static float IntervalSeconds() {
            return (float)(IntervalMinutes() * 60);
        }

        /// <summary>Plus or minus ten percent, so servers that started together do not stay in lockstep.</summary>
        private static void ScheduleNext() {
            double spread = 0.9 + (jitter?.NextDouble() ?? 0.5) * 0.2;
            float seconds = (float)(IntervalSeconds() * spread);
            nextPull = Time.unscaledTime + seconds;
            NextPullLocal = DateTime.UtcNow.AddSeconds(seconds);
        }

        private static void OnKeyFileChanged() {
            BanApiKey.Shape shape = BanApiKey.Read(out _, out string serverId);
            if (shape != BanApiKey.Shape.Valid) {
                NoteKeyProblem(shape);
                return;
            }
            Logger.LogInfo($"Ban network: a new API key was saved (server id {serverId}). Trying it now.");
            BanNetworkState.Retry();
            BanNetworkState.ServerId = serverId;
            PullSoon();
        }

        /// <summary>Brings the next pull forward, for enforcer-ban-network-sync.</summary>
        internal static void PullSoon() {
            nextPull = Time.unscaledTime;
        }
    }

    internal class BanNetworkSchedulerBehaviour : MonoBehaviour {
        private void Update() {
            BanNetworkScheduler.Tick();
        }
    }
}
