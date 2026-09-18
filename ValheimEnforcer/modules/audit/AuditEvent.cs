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

        // ---- The on-disk line -----------------------------------------------------------------------------

        /// <summary>How many public properties <see cref="AppendJsonLine"/> knows how to write.</summary>
        private const int WrittenProperties = 14;

        /// <summary>
        /// False when this class has a property <see cref="AppendJsonLine"/> does not write, in which case the
        /// audit log goes back to the serializer rather than silently leaving the new field out of every line.
        /// Checked once. The cost of forgetting to extend the writer is therefore speed, never a gap in the
        /// record - and the log says which, the first time it matters.
        /// </summary>
        internal static readonly bool HandWrittenLineUsable = CheckWriterCoversEveryProperty();

        private static bool CheckWriterCoversEveryProperty() {
            try {
                int found = typeof(AuditEvent).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Length;
                if (found == WrittenProperties) { return true; }
                Logger.LogWarning($"AuditEvent has {found} properties but its line writer covers {WrittenProperties}; audit lines are being written by the slower serializer until AppendJsonLine is updated.");
                return false;
            } catch (Exception) {
                return false;
            }
        }

        /// <summary>
        /// Appends this event as the one-line JSON object a day file holds, in exactly the shape the
        /// JsonCompatible serializer produced: camelCase keys in declaration order, and every field still at its
        /// default left out. Read back by the ordinary YAML deserializer, because JSON is YAML.
        ///
        /// Anything a YAML or JSON reader could trip over inside a string is escaped, including the three
        /// characters YAML treats as line breaks that a line-oriented reader does not - a player name containing
        /// one must not be able to split an event across two lines.
        /// </summary>
        internal void AppendJsonLine(System.Text.StringBuilder line) {
            line.Append('{');
            bool any = false;
            AppendField(line, ref any, "t", T);
            AppendField(line, ref any, "acct", Acct);
            AppendField(line, ref any, "character", Character);
            AppendField(line, ref any, "kind", Kind);
            AppendField(line, ref any, "prefab", Prefab);
            AppendField(line, ref any, "qty", Qty);
            AppendField(line, ref any, "quality", Quality);
            AppendField(line, ref any, "crafterId", CrafterId);
            AppendField(line, ref any, "crafter", Crafter);
            AppendField(line, ref any, "container", Container);
            AppendField(line, ref any, "pos", Pos);
            // A total that is not a number is not a damage figure; DamageAudit never records one, and there is
            // no spelling of it that is both JSON and YAML.
            if (Amount != 0f && !float.IsNaN(Amount) && !float.IsInfinity(Amount)) {
                AppendKey(line, ref any, "amount");
                line.Append(Amount.ToString("R", CultureInfo.InvariantCulture));
            }
            AppendField(line, ref any, "target", Target);
            AppendField(line, ref any, "note", Note);
            line.Append('}');
        }

        private static void AppendKey(System.Text.StringBuilder line, ref bool any, string key) {
            if (any) { line.Append(", "); }
            any = true;
            line.Append('"').Append(key).Append("\": ");
        }

        private static void AppendField(System.Text.StringBuilder line, ref bool any, string key, long value) {
            if (value == 0L) { return; }
            AppendKey(line, ref any, key);
            line.Append(value);
        }

        private static void AppendField(System.Text.StringBuilder line, ref bool any, string key, string value) {
            if (value == null) { return; }
            AppendKey(line, ref any, key);
            line.Append('"');
            for (int i = 0; i < value.Length; i++) {
                char c = value[i];
                switch (c) {
                    case '"': line.Append("\\\""); continue;
                    case '\\': line.Append("\\\\"); continue;
                    case '\n': line.Append("\\n"); continue;
                    case '\r': line.Append("\\r"); continue;
                    case '\t': line.Append("\\t"); continue;
                }
                if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) {
                    line.Append(c).Append(value[++i]); // a whole pair is an ordinary character
                    continue;
                }
                if (char.IsSurrogate(c)) {
                    line.Append((char)0xFFFD); // half a pair cannot be written as UTF-8, nor escaped in YAML
                    continue;
                }
                // Control characters, the C1 block, YAML's extra line breaks (NEL, LS, PS) and the byte order
                // mark: all legal inside a quoted string only when escaped.
                if (c < ' ' || (c >= (char)0x7F && c <= (char)0x9F) || c == (char)0x2028 || c == (char)0x2029 || c == (char)0xFEFF) {
                    line.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    continue;
                }
                line.Append(c);
            }
            line.Append('"');
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
