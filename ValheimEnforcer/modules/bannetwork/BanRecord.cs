using System;
using System.Collections.Generic;
using System.Globalization;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.bannetwork {

    /// <summary>Where a ban came from. Decides whether it may ever be reported to the ban network.</summary>
    internal static class BanSources {
        /// <summary>An admin issued it with enforcer-ban.</summary>
        internal const string Local = "local";
        /// <summary>A detector issued it - cheat report, structure validator, RPC guard.</summary>
        internal const string Auto = "auto";
        /// <summary>Shipped inside the mod as the embedded seed.</summary>
        internal const string Builtin = "builtin";
        /// <summary>Imported from the pre-collapse KnownCheaters.yaml.</summary>
        internal const string Legacy = "legacy";
        /// <summary>Pulled from the ban network. Never stored in Bans.yaml.</summary>
        internal const string Network = "network";

        /// <summary>
        /// Whether a ban with this source may be published to the ban network.
        ///
        /// This is the first and most important of the anti-amplification rules: a ban this server only ever
        /// learned about by pulling it must never be re-reported as though this server had witnessed it, or
        /// the network's reporter count becomes an echo of itself rather than a count of independent sightings.
        /// Builtin and legacy are excluded for the same reason - neither is something this server observed.
        /// </summary>
        internal static bool Reportable(string source) {
            return source == Local || source == Auto;
        }
    }

    /// <summary>
    /// Timestamps for everything in this module, in the same spelling the audit log uses
    /// (<c>AuditEvent.TimeFormat</c>), so the two read alike and both survive a trip through JSON unchanged.
    /// </summary>
    internal static class BanTime {

        internal const string Format = "yyyy-MM-ddTHH:mm:ssZ";

        internal static string Stamp(DateTime utc) {
            return utc.ToString(Format, CultureInfo.InvariantCulture);
        }

        internal static string Now() {
            return Stamp(DateTime.UtcNow);
        }

        /// <summary>
        /// Parses a stored stamp. Returns null for anything unreadable rather than throwing or guessing - a
        /// corrupt "added" date must not take a ban entry down, and a corrupt "expires" date must not be read
        /// as an expiry that has already passed.
        /// </summary>
        internal static DateTime? Parse(string value) {
            if (string.IsNullOrEmpty(value)) { return null; }
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed)) {
                return parsed;
            }
            Logger.LogWarning($"Could not read the timestamp '{value}' in a ban entry; treating it as absent.");
            return null;
        }
    }

    /// <summary>
    /// One ban, parsed. The on-disk twin is <see cref="DataObjects.BanEntry"/>; this is what the rest of the
    /// module works with, with the categories and dates already resolved so nothing re-parses them per join.
    /// </summary>
    internal sealed class BanRecord {

        internal string Id;
        internal string Name;
        internal List<BanCategory> Categories = new List<BanCategory>();
        internal string Reason;
        internal DateTime? AddedUtc;
        internal string AddedBy;
        internal string Source = BanSources.Local;
        internal DateTime? ExpiresUtc;
        internal bool Share = true;

        internal bool Expired {
            get { return ExpiresUtc.HasValue && ExpiresUtc.Value <= DateTime.UtcNow; }
        }

        internal bool Reportable {
            get { return Share && BanSources.Reportable(Source); }
        }

        /// <summary>A one-line description for a log, a command listing or a rejection message.</summary>
        internal string Describe() {
            string categories = BanCategories.Canonical(Categories);
            if (string.IsNullOrEmpty(categories)) { categories = "unspecified"; }
            string reason = string.IsNullOrEmpty(Reason) ? "no reason recorded" : Reason;
            return $"[{categories}] {reason}";
        }

        internal static BanRecord FromEntry(DataObjects.BanEntry entry, string context) {
            if (entry == null || string.IsNullOrEmpty(entry.Id)) { return null; }
            return new BanRecord {
                Id = entry.Id,
                Name = entry.Name,
                Categories = BanCategories.Parse(entry.Categories, context),
                Reason = entry.Reason,
                AddedUtc = BanTime.Parse(entry.AddedUtc),
                AddedBy = entry.AddedBy,
                Source = string.IsNullOrEmpty(entry.Source) ? BanSources.Local : entry.Source,
                ExpiresUtc = BanTime.Parse(entry.ExpiresUtc),
                Share = entry.Share,
            };
        }

        internal DataObjects.BanEntry ToEntry() {
            return new DataObjects.BanEntry {
                Id = Id,
                Name = Name,
                Categories = BanCategories.ToNames(Categories),
                Reason = Reason,
                AddedUtc = AddedUtc.HasValue ? BanTime.Stamp(AddedUtc.Value) : null,
                AddedBy = AddedBy,
                Source = Source,
                ExpiresUtc = ExpiresUtc.HasValue ? BanTime.Stamp(ExpiresUtc.Value) : null,
                Share = Share,
            };
        }
    }
}
