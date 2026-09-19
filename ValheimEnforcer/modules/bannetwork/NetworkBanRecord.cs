using System;
using System.Collections.Generic;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.bannetwork {

    /// <summary>
    /// One network entry, parsed. The wire twin is <see cref="DataObjects.NetworkBanEntry"/>.
    ///
    /// Parsing is total: anything unrecognised degrades to a default rather than throwing, because these
    /// arrive from a service this server does not control and one malformed field must not cost the batch.
    /// </summary>
    internal sealed class NetworkBanRecord {

        internal long Seq;
        internal string Subject;
        internal List<BanCategory> Categories = new List<BanCategory>();
        internal string Reason;
        internal string Name;
        internal int Reporters;
        internal DateTime? FirstSeenUtc;
        internal DateTime? UpdatedUtc;
        /// <summary>Null means permanent.</summary>
        internal DateTime? ExpiresUtc;
        internal bool Revoked;

        /// <summary>
        /// Expiry is enforced here rather than centrally on purpose: nothing on the network re-runs a
        /// projection just because a date passed, so an expired entry keeps its row until something else
        /// touches it. Every client checking the date it was given makes that harmless.
        /// </summary>
        internal bool Expired {
            get { return ExpiresUtc.HasValue && ExpiresUtc.Value <= DateTime.UtcNow; }
        }

        internal static NetworkBanRecord FromEntry(DataObjects.NetworkBanEntry entry) {
            if (entry == null || string.IsNullOrEmpty(entry.Subject)) { return null; }
            if (!BanSubject.LooksLikeHash(entry.Subject)) { return null; }
            return new NetworkBanRecord {
                Seq = entry.Seq,
                Subject = entry.Subject.ToLowerInvariant(),
                Categories = BanCategories.Parse(entry.Categories, "the ban network feed"),
                Reason = entry.Reason,
                Name = entry.Name,
                Reporters = entry.Reporters,
                FirstSeenUtc = BanTime.Parse(entry.FirstSeenUtc),
                UpdatedUtc = BanTime.Parse(entry.UpdatedUtc),
                ExpiresUtc = BanTime.Parse(entry.ExpiresUtc),
                Revoked = entry.Revoked,
            };
        }

        internal DataObjects.NetworkBanEntry ToEntry() {
            return new DataObjects.NetworkBanEntry {
                Seq = Seq,
                Subject = Subject,
                Categories = BanCategories.ToNames(Categories),
                Reason = Reason,
                Name = Name,
                Reporters = Reporters,
                FirstSeenUtc = FirstSeenUtc.HasValue ? BanTime.Stamp(FirstSeenUtc.Value) : null,
                UpdatedUtc = UpdatedUtc.HasValue ? BanTime.Stamp(UpdatedUtc.Value) : null,
                ExpiresUtc = ExpiresUtc.HasValue ? BanTime.Stamp(ExpiresUtc.Value) : null,
                Revoked = Revoked,
            };
        }

        /// <summary>
        /// Whether this server should act on the entry, given its own policy.
        ///
        /// Two independent gates, and both matter. The category filter is about what this owner considers
        /// actionable; the reporter threshold is about how much corroboration they want before acting on
        /// somebody else's word. An entry that fails either is advisory - recorded and visible, not enforced.
        /// </summary>
        internal bool Enforceable(List<BanCategory> enforced, int minReporters) {
            if (Revoked || Expired) { return false; }
            if (Reporters < minReporters) { return false; }
            return BanCategories.AnyIn(Categories, enforced);
        }

        internal string Describe() {
            string categories = BanCategories.Canonical(Categories);
            if (string.IsNullOrEmpty(categories)) { categories = "unspecified"; }
            string reason = string.IsNullOrEmpty(Reason) ? "no reason recorded" : Reason;
            string window = ExpiresUtc.HasValue
                ? (Expired ? ", expired" : $", until {BanTime.Stamp(ExpiresUtc.Value)}")
                : "";
            return $"[{categories}] {reason} ({Reporters} reporting server{(Reporters == 1 ? "" : "s")}{window})";
        }
    }
}
