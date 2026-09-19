using System;
using System.Collections.Generic;
using System.Text;

namespace ValheimEnforcer.modules.bannetwork {

    /// <summary>
    /// What a ban is for. Four fixed values, because the set is a wire contract shared with every other server
    /// on the ban network - a server inventing a fifth would publish something nobody else can act on.
    /// </summary>
    internal enum BanCategory {
        Cheating,
        Rulebreaking,
        Griefing,
        Toxic,
    }

    /// <summary>
    /// Parsing and formatting for <see cref="BanCategory"/>.
    ///
    /// Everything on disk and on the wire is the lowercase spelling ("cheating"), and everything read is
    /// case-insensitive and whitespace-tolerant. Parsing never throws: Bans.yaml is hand-edited and the ban
    /// network is a third party, so an unrecognised category is dropped with a warning rather than taking the
    /// surrounding list - or the whole file - down with it.
    /// </summary>
    internal static class BanCategories {

        internal static readonly BanCategory[] All = {
            BanCategory.Cheating, BanCategory.Rulebreaking, BanCategory.Griefing, BanCategory.Toxic,
        };

        /// <summary>The canonical wire/disk spelling. Never ToString() a BanCategory directly.</summary>
        internal static string Name(BanCategory category) {
            switch (category) {
                case BanCategory.Cheating:     return "cheating";
                case BanCategory.Rulebreaking: return "rulebreaking";
                case BanCategory.Griefing:     return "griefing";
                case BanCategory.Toxic:        return "toxic";
                default:                       return "cheating";
            }
        }

        internal static List<string> Names() {
            List<string> names = new List<string>(All.Length);
            foreach (BanCategory category in All) { names.Add(Name(category)); }
            return names;
        }

        internal static bool TryParse(string value, out BanCategory category) {
            category = BanCategory.Cheating;
            if (string.IsNullOrEmpty(value)) { return false; }
            switch (value.Trim().ToLowerInvariant()) {
                case "cheating":     category = BanCategory.Cheating;     return true;
                case "rulebreaking": category = BanCategory.Rulebreaking; return true;
                case "griefing":     category = BanCategory.Griefing;     return true;
                case "toxic":        category = BanCategory.Toxic;        return true;
                default:             return false;
            }
        }

        /// <summary>
        /// Parses a stored list, dropping anything unrecognised. <paramref name="context"/> names the source in
        /// the warning so an admin can find the typo without guessing which file it was in.
        /// </summary>
        internal static List<BanCategory> Parse(List<string> values, string context) {
            List<BanCategory> parsed = new List<BanCategory>();
            if (values == null) { return parsed; }
            foreach (string value in values) {
                if (TryParse(value, out BanCategory category)) {
                    if (!parsed.Contains(category)) { parsed.Add(category); }
                } else {
                    Logger.LogWarning($"Ignoring unknown ban category '{value}' in {context}. Valid categories: {string.Join(", ", Names().ToArray())}.");
                }
            }
            return parsed;
        }

        /// <summary>Parses a console argument such as "griefing,toxic". Returns false if nothing parsed.</summary>
        internal static bool TryParseList(string commaSeparated, out List<BanCategory> categories, out string invalid) {
            categories = new List<BanCategory>();
            invalid = null;
            if (string.IsNullOrEmpty(commaSeparated)) { return false; }

            List<string> rejected = new List<string>();
            foreach (string part in commaSeparated.Split(',')) {
                if (string.IsNullOrEmpty(part.Trim())) { continue; }
                if (TryParse(part, out BanCategory category)) {
                    if (!categories.Contains(category)) { categories.Add(category); }
                } else {
                    rejected.Add(part.Trim());
                }
            }
            if (rejected.Count > 0) { invalid = string.Join(", ", rejected.ToArray()); }
            return categories.Count > 0;
        }

        internal static List<string> ToNames(List<BanCategory> categories) {
            List<string> names = new List<string>();
            if (categories == null) { return names; }
            foreach (BanCategory category in categories) { names.Add(Name(category)); }
            return names;
        }

        /// <summary>Sorted, comma-joined - the spelling the ban network stores and compares.</summary>
        internal static string Canonical(List<BanCategory> categories) {
            StringBuilder sb = new StringBuilder();
            foreach (BanCategory category in All) {
                if (categories == null || !categories.Contains(category)) { continue; }
                if (sb.Length > 0) { sb.Append(','); }
                sb.Append(Name(category));
            }
            return sb.ToString();
        }

        /// <summary>True when any of <paramref name="categories"/> is in <paramref name="enforced"/>.</summary>
        internal static bool AnyIn(List<BanCategory> categories, List<BanCategory> enforced) {
            if (categories == null || enforced == null) { return false; }
            foreach (BanCategory category in categories) {
                if (enforced.Contains(category)) { return true; }
            }
            return false;
        }
    }
}
