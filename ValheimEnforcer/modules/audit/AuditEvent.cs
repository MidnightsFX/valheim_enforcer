using System;
using System.Globalization;

namespace ValheimEnforcer.modules.audit {

    /// <summary>
    /// One recorded thing a player did.
    ///
    /// Deliberately a flat record rather than a kind plus a property bag. Every field is optional and the
    /// serializer omits defaults, so an item event and a damage event cost the same to write while a line
    /// stays greppable by an admin who has never read this file - "grep container-took audit-2026-08-30.yaml"
    /// has to be the whole skill required to use this feature at three in the morning.
    ///
    /// Times are stored as an ISO-8601 UTC string rather than a DateTime. It round-trips through YAML without
    /// depending on the deserializer's date handling or the server's locale, it sorts lexically (which is what
    /// makes a day file readable in order), and it is the form an admin reading the raw file expects.
    /// </summary>
    [Serializable]
    internal sealed class AuditEvent {

        /// <summary>ISO-8601 UTC, e.g. 2026-08-30T14:03:11Z. See <see cref="TimeUtc"/> to read it back.</summary>
        public string T { get; set; }

        /// <summary>Platform account id, resolved from the socket - never from a payload.</summary>
        public string Acct { get; set; }

        public string Character { get; set; }

        /// <summary>One of the Kinds constants below.</summary>
        public string Kind { get; set; }

        // ---- Item / container detail ----------------------------------------------------------------------

        public string Prefab { get; set; }
        public int Qty { get; set; }
        public int Quality { get; set; }
        public long CrafterId { get; set; }
        public string Crafter { get; set; }

        /// <summary>Prefab name of the container involved, for the container kinds.</summary>
        public string Container { get; set; }

        /// <summary>Rounded world position, as "x/y/z". A moderator needs somewhere to fly to, not precision.</summary>
        public string Pos { get; set; }

        // ---- Damage detail --------------------------------------------------------------------------------

        /// <summary>Damage total. Pre-mitigation, as the attacker's client claimed it - see DamageAudit.</summary>
        public float Amount { get; set; }

        /// <summary>Prefab name of what was hit, or the player's name when the victim is a player.</summary>
        public string Target { get; set; }

        /// <summary>Free text, only where a kind genuinely needs it. Not a dumping ground.</summary>
        public string Note { get; set; }

        // ---- Kinds ----------------------------------------------------------------------------------------

        internal static class Kinds {
            internal const string ItemGained = "item-gained";
            internal const string ItemLost = "item-lost";
            internal const string ContainerOpened = "container-opened";
            internal const string ContainerTook = "container-took";
            internal const string ContainerStored = "container-stored";
            internal const string DamageSpike = "damage-spike";

            /// <summary>What the history command accepts as a filter word, and what each word covers.</summary>
            internal static string[] For(string filter) {
                switch ((filter ?? "all").ToLowerInvariant()) {
                    case "items": return new[] { ItemGained, ItemLost };
                    case "containers": return new[] { ContainerOpened, ContainerTook, ContainerStored };
                    case "damage": return new[] { DamageSpike };
                    case "all": return null; // null means "no filter", not "nothing"
                    default: return new string[0];
                }
            }

            internal static readonly string[] Filters = { "all", "items", "containers", "damage" };
        }

        // ---- Helpers --------------------------------------------------------------------------------------

        internal const string TimeFormat = "yyyy-MM-ddTHH:mm:ssZ";

        internal static string Stamp(DateTime utc) {
            return utc.ToString(TimeFormat, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The parsed form of <see cref="T"/>, once it has been worked out. A private field, so the YAML
        /// serializer - which reads public properties - neither writes it to a day file nor looks for it in
        /// one; the stamp on the wire stays the single stored representation.
        /// </summary>
        private DateTime parsedTime;
        private bool timeParsed;

        /// <summary>
        /// Sets the time from a DateTime the caller already holds, which is every event the server records.
        ///
        /// Truncated to the second on the way in, because that is all <see cref="Stamp"/> writes: an event
        /// remembering a more precise time than its own line would compare differently before and after a
        /// flush, and a report that changed when the buffer drained would be a genuinely confusing bug.
        /// </summary>
        internal void SetTime(DateTime utc) {
            parsedTime = new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            timeParsed = true;
            T = Stamp(parsedTime);
        }

        /// <summary>
        /// The event's time, or <see cref="DateTime.MinValue"/> when it cannot be read. Callers filtering a
        /// window treat MinValue as "outside it" rather than guessing, so a corrupt line is skipped instead of
        /// silently landing in the middle of a report.
        ///
        /// Worked out at most once per event. The write path never parses at all - <see cref="SetTime"/> knew
        /// the value - and a flush that fails and re-groups the same batch on the next pass does not re-parse
        /// what it already read.
        /// </summary>
        internal DateTime TimeUtc() {
            if (timeParsed) { return parsedTime; }
            timeParsed = true;
            parsedTime = DateTime.MinValue;
            if (string.IsNullOrEmpty(T)) { return parsedTime; }
            if (DateTime.TryParseExact(T, TimeFormat, CultureInfo.InvariantCulture,
                                       DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                                       out DateTime parsed)) {
                parsedTime = parsed;
            }
            return parsedTime;
        }

        /// <summary>One human-readable line for a terminal report.</summary>
        internal string Describe() {
            switch (Kind) {
                case Kinds.ItemGained:
                    return $"gained {Prefab} x{Qty}{QualityPart()}{CrafterPart()}";
                case Kinds.ItemLost:
                    return $"lost {Prefab} x{Qty}{QualityPart()}";
                case Kinds.ContainerOpened:
                    return $"opened {Container}{PosPart()}";
                case Kinds.ContainerTook:
                    return $"took {Prefab} x{Qty} from {Container}{PosPart()}";
                case Kinds.ContainerStored:
                    return $"put {Prefab} x{Qty} into {Container}{PosPart()}";
                case Kinds.DamageSpike:
                    return $"dealt {Amount:G6} damage{(string.IsNullOrEmpty(Target) ? "" : $" to {Target}")}"
                         + (string.IsNullOrEmpty(Note) ? "" : $" ({Note})");
                default:
                    return $"{Kind} {Prefab}".Trim();
            }
        }

        private string QualityPart() { return Quality > 1 ? $" (quality {Quality})" : ""; }
        private string PosPart() { return string.IsNullOrEmpty(Pos) ? "" : $" at {Pos}"; }

        private string CrafterPart() {
            if (!string.IsNullOrEmpty(Crafter)) { return $", crafted by {Crafter}"; }
            // Worth calling out rather than leaving blank: no crafter is normal for loot and chest items, but
            // it is also what conjured gear looks like, and it is the first thing an admin looks for.
            return CrafterId == 0 ? ", no crafter" : $", crafter id {CrafterId}";
        }
    }
}
