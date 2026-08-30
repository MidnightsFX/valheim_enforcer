using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace ValheimEnforcer.common {

    /// <summary>
    /// Times an operation and reports it when it runs long enough to be felt as a frame hitch.
    ///
    /// This exists because the stalls this mod caused were invisible in an ordinary player log: the
    /// work was real (a blocking process enumeration, a full inventory diff, a synchronous save) but
    /// nothing recorded how long any of it took, so the only evidence was players describing a
    /// freeze. A warning above <see cref="ValConfig.StallWarningThresholdMs"/> makes a regression
    /// visible without anyone having to enable debug logging first, and the per-call debug line
    /// gives the detail once they do.
    ///
    /// Safe to use from a worker thread: the cooldown map is concurrent and BepInEx logging is
    /// already written to from the CharacterStore worker.
    /// </summary>
    internal struct StallWatch {

        // One warning per label per minute. A stall that repeats every tick would otherwise fill the
        // log with the same line and bury whatever else was happening.
        private const double WarnCooldownSeconds = 60d;
        private static readonly ConcurrentDictionary<string, DateTime> lastWarned = new ConcurrentDictionary<string, DateTime>();

        private readonly string label;
        private readonly Stopwatch watch;

        private StallWatch(string label) {
            this.label = label;
            watch = Stopwatch.StartNew();
        }

        internal static StallWatch Start(string label) {
            return new StallWatch(label);
        }

        /// <summary>
        /// Stops the timer and reports it. Never throws - this only ever exists to describe work that
        /// already happened, so a problem here must not take the caller down with it.
        /// </summary>
        internal void Stop() {
            if (watch == null) { return; } // default(StallWatch), never started
            watch.Stop();
            try {
                long ms = watch.ElapsedMilliseconds;
                if (Logger.DebugEnabled) { Logger.LogDebug($"[timing] {label} took {ms}ms."); }

                // Bound rather than defaulted: the config is not yet bound this early in startup.
                int threshold = ValConfig.StallWarningThresholdMs != null
                    ? ValConfig.StallWarningThresholdMs.Value
                    : 100;
                if (threshold <= 0 || ms < threshold) { return; }

                DateTime now = DateTime.UtcNow;
                if (lastWarned.TryGetValue(label, out DateTime previous)
                    && (now - previous).TotalSeconds < WarnCooldownSeconds) {
                    return;
                }
                lastWarned[label] = now;
                Logger.LogWarning($"{label} took {ms}ms, which is long enough to show as a frame hitch. " +
                                  "Further warnings for this operation are suppressed for a minute.");
            } catch (Exception e) {
                Logger.LogDebug($"StallWatch could not report {label}: {e.Message}");
            }
        }
    }
}
