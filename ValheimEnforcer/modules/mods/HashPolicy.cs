using System;
using System.Collections.Generic;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.mods {

    /// <summary>Outcome of comparing one client-reported plugin file against the server's record.</summary>
    internal enum HashVerdict {
        /// <summary>Verified, or verification is not in force for this mod.</summary>
        Pass,
        /// <summary>The client's file does not match anything the server accepts.</summary>
        Mismatch,
        /// <summary>The server holds a record but the client reported no usable hash.</summary>
        Unverifiable,
        /// <summary>Strict only: an enforced mod the server has no hash for. Admin misconfiguration.</summary>
        NotRecorded
    }

    /// <summary>
    /// Resolves whether a given mod's file hash is enforced, and what the comparison came out to.
    /// </summary>
    internal static class HashPolicy {

        internal const string Off = "Off";
        internal const string WhenKnown = "WhenKnown";
        internal const string Strict = "Strict";

        internal const string SourceLocal = "Local";
        internal const string SourceManual = "Manual";
        internal const string SourceThunderstore = "Thunderstore";

        // Per-mod overrides we have already complained about, so a typo logs once rather than once per
        // connecting client.
        private static readonly HashSet<string> warnedOverrides = new HashSet<string>();

        /// <summary>
        /// The mode in force for one mod: its own override when it has a valid one, otherwise the server
        /// setting. An unrecognised override falls back to the global value and warns - a typo in Mods.yaml
        /// must not silently switch enforcement off.
        /// </summary>
        internal static string EffectiveMode(DataObjects.Mod authoritative) {
            return EffectiveMode(authoritative, ValConfig.HashEnforcement.Value);
        }

        /// <summary>
        /// The same, against a server setting that is not this machine's own. The client mod sync needs this:
        /// before login nothing has been synced, so the client's copy of HashEnforcement is its own default
        /// rather than the server's, and the server sends its value alongside its mod list instead.
        /// </summary>
        internal static string EffectiveMode(DataObjects.Mod authoritative, string globalMode) {
            string global = NormalizeMode(globalMode) ?? WhenKnown;
            string over = authoritative?.HashEnforcement;
            if (string.IsNullOrWhiteSpace(over)) { return global; }

            if (string.Equals(over, Off, StringComparison.OrdinalIgnoreCase)) { return Off; }
            if (string.Equals(over, WhenKnown, StringComparison.OrdinalIgnoreCase)) { return WhenKnown; }
            if (string.Equals(over, Strict, StringComparison.OrdinalIgnoreCase)) { return Strict; }

            WarnBadOverrideOnce(authoritative, over, global);
            return global;
        }

        /// <summary>One of the three modes in its canonical spelling, or null for anything else.</summary>
        private static string NormalizeMode(string mode) {
            if (string.Equals(mode, Off, StringComparison.OrdinalIgnoreCase)) { return Off; }
            if (string.Equals(mode, WhenKnown, StringComparison.OrdinalIgnoreCase)) { return WhenKnown; }
            if (string.Equals(mode, Strict, StringComparison.OrdinalIgnoreCase)) { return Strict; }
            return null;
        }

        private static void WarnBadOverrideOnce(DataObjects.Mod authoritative, string over, string global) {
            string key = $"{authoritative?.PluginID}|{over}";
            lock (warnedOverrides) {
                if (!warnedOverrides.Add(key)) { return; }
            }
            Logger.LogWarning($"Mods.yaml entry '{authoritative?.PluginID}' has hashEnforcement '{over}', which is not one of Off/WhenKnown/Strict. Falling back to the server setting ({global}). Fix the value.");
        }

        /// <summary>
        /// Compares one client-reported mod against the server's authoritative record.
        /// </summary>
        /// <param name="authoritative">The server's record for this mod.</param>
        /// <param name="reported">What the client said about it.</param>
        /// <param name="requiredOrAdmin">
        /// True when the mod came from the Required or AdminOnly list. Only those are rejected under Strict for
        /// having no recorded hash; an optional mod is never rejected for that, since opting a single optional
        /// mod in is what the per-mod override is for.
        /// </param>
        internal static HashVerdict Evaluate(DataObjects.Mod authoritative, DataObjects.Mod reported, bool requiredOrAdmin) {
            return Evaluate(authoritative, reported, requiredOrAdmin, ValConfig.HashEnforcement.Value);
        }

        /// <summary>The same comparison, under a server setting that is not this machine's own.</summary>
        internal static HashVerdict Evaluate(DataObjects.Mod authoritative, DataObjects.Mod reported, bool requiredOrAdmin, string globalMode) {
            if (authoritative == null || reported == null) { return HashVerdict.Pass; }

            string mode = EffectiveMode(authoritative, globalMode);
            if (mode == Off) { return HashVerdict.Pass; }

            // Listed with no version: the admin is allowing any version, and a file check would pin one. So nothing
            // recorded automatically counts against it, and Strict does not demand a hash for it either. A hash
            // typed in by hand still applies - that is the admin asking for exactly this check.
            if (!authoritative.IsVersioned() && (!authoritative.HasRecordedHash() || authoritative.HashesAreAutomatic())) {
                return HashVerdict.Pass;
            }

            if (!authoritative.HasRecordedHash()) {
                if (mode == Strict && requiredOrAdmin) {
                    // A resolve for this very mod may still be in flight. Rejecting every client for the
                    // handful of seconds the first pass after a restart takes would be a worse failure than
                    // the one Strict exists to prevent, so the window is bounded: ResolutionSettled flips once
                    // the first pass finishes, success or failure.
                    if (!string.IsNullOrEmpty(authoritative.ThunderstorePackage) && !ThunderstoreResolver.ResolutionSettled) {
                        Logger.LogInfo($"Thunderstore hash resolution has not settled yet; deferring Strict enforcement for {authoritative.PluginID} on this connection.");
                        return HashVerdict.Pass;
                    }
                    return HashVerdict.NotRecorded;
                }
                return HashVerdict.Pass; // WhenKnown: nothing recorded, nothing to enforce
            }

            // A missing hash is never a pass. If it were, omitting the field would be the cheapest possible
            // bypass of the whole feature.
            if (string.IsNullOrEmpty(reported.Hash)) { return HashVerdict.Unverifiable; }

            return authoritative.AcceptsHash(reported.Hash) ? HashVerdict.Pass : HashVerdict.Mismatch;
        }
    }
}
