using Splatform;
using System;
using System.Collections.Generic;

namespace ValheimEnforcer.common {

    /// <summary>
    /// The spelling Valheim's <c>adminlist.txt</c> requires, and the comparison the game makes against it.
    ///
    /// A game update moved these lists to a one-letter platform prefix - V_ Steam, N_ Nintendo, X_ Xbox,
    /// S_ PlayStation, A_ GameCenter - and, crucially, made it the ONLY spelling accepted for an account
    /// whose id is numeric. <c>ZNet.ListContainsId</c> computes the old answer first, from the bare id and
    /// the long "Steam_..." form, and then, whenever <c>PlatformUserID.FilterPlatformUserID</c> produces
    /// something different, *overwrites* that answer with a lookup of the filtered form alone. So every
    /// adminlist.txt written before the update - a file of bare SteamID64s, which is what the game itself
    /// used to ask for - silently stopped granting anybody admin, while continuing to look exactly as
    /// correct as it always did. Nothing in the game says so; the only visible symptom is that admin
    /// commands stop working.
    ///
    /// This mirrors that rule so the mod can tell an operator which case they are in and print the line to
    /// write. It is diagnosis, never a gate: the server decides with <c>ZNet.IsAdmin</c>, which is the game's
    /// own code, and this file only ever produces advice and a client-side hint.
    /// </summary>
    internal static class AdminIds {

        /// <summary>What ZNet falls back to for an id with no platform on it, matching its m_steamPlatform.</summary>
        private static readonly Platform Steam = new Platform("Steam");

        /// <summary>
        /// The exact line adminlist.txt needs for an account, given the id the game identifies it by - a
        /// socket's host name, or the local platform user id. Null when the id cannot be read.
        ///
        /// For a console account this is not a rewrite of the number but a different number: filtering
        /// multiplies the platform id by a constant, which is why an operator cannot work the value out by
        /// hand and why printing it is most of the point of enforcer-whoami.
        /// </summary>
        internal static string Canonical(string id) {
            try {
                if (string.IsNullOrEmpty(id)) { return null; }
                if (AlreadyPrefixed(id)) { return id; }
                if (Parse(id, out PlatformUserID parsed) == false || parsed.IsValid == false) { return null; }
                return PlatformUserID.FilterPlatformUserID(parsed).ToString();
            } catch (Exception e) {
                Logger.LogDebug($"Could not work out the admin list spelling for '{id}': {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Whether a list of admin entries grants this account, by the same steps and in the same order as
        /// <c>ZNet.ListContainsId</c> - including its final assignment, which replaces the earlier answer
        /// rather than adding to it. Reimplemented because the game's own method is private and takes the
        /// server-only SyncedList, while a client holds the pushed copy as a plain list.
        ///
        /// Entries are compared exactly as written, whitespace included, because that is what the game does.
        /// </summary>
        internal static bool Accepts(IList<string> entries, string id) {
            if (entries == null || entries.Count == 0) { return false; }
            try {
                if (Parse(id, out PlatformUserID parsed) == false) { return false; }

                bool found = parsed.m_platform == Steam
                    ? entries.Contains(parsed.ToString()) || entries.Contains(parsed.m_userID)
                    : entries.Contains(parsed.ToString());

                PlatformUserID filtered = PlatformUserID.FilterPlatformUserID(parsed);
                if (filtered != parsed) { found = entries.Contains(filtered.ToString()); }
                return found;
            } catch (Exception e) {
                Logger.LogDebug($"Could not test '{id}' against the admin list: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// True when a line in the file is written the way servers wrote them before the update, and is
        /// therefore now ignored however correct it looks. <paramref name="replacement"/> is what that line
        /// should say instead.
        /// </summary>
        internal static bool IsPreUpdateSpelling(string entry, out string replacement) {
            replacement = null;
            if (string.IsNullOrEmpty(entry)) { return false; }

            string trimmed = entry.Trim();
            if (trimmed.Length == 0 || AlreadyPrefixed(trimmed)) { return false; }

            replacement = Canonical(trimmed);
            // A real conversion always ends in a short prefix. Without this test any line the game cannot
            // read as a number - a stray word, a hand-written note somebody left in the file - comes back as
            // "Steam_<that word>", because the Steam fallback renders every id with its platform on the front
            // whether or not filtering had anything to say. That is not a line that used to work, so counting
            // it would overstate how much of the file the update broke.
            if (replacement == null || replacement == trimmed || AlreadyPrefixed(replacement) == false) {
                replacement = null;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Whether two ids name the same account once both are written the way the file wants them, so a
        /// stale line can be matched to the person it was meant to grant.
        /// </summary>
        internal static bool SameAccount(string left, string right) {
            string a = Canonical(left);
            string b = Canonical(right);
            return a != null && b != null && a == b;
        }

        /// <summary>Parses an id the way ZNet does, falling back to Steam for a bare number.</summary>
        private static bool Parse(string id, out PlatformUserID parsed) {
            parsed = default;
            if (string.IsNullOrEmpty(id)) { return false; }
            if (PlatformUserID.TryParse(id, out parsed)) { return true; }
            parsed = new PlatformUserID(Steam, id);
            return true;
        }

        /// <summary>
        /// True when the string already carries the short prefix the files now use.
        ///
        /// Detected rather than assumed, because filtering is not idempotent for a console account: it
        /// multiplies the numeric id, so running it over an id that has already been through it yields a
        /// second, wrong number instead of the same one back. TryParse maps a short prefix onto the
        /// platform's long name, so an entry reading "X_..." reports a platform of "Xbox" while its own
        /// prefix says "X" - and that disagreement is exactly what marks it as already converted, without
        /// this file having to keep its own copy of the prefix table.
        /// </summary>
        private static bool AlreadyPrefixed(string id) {
            int split = id.IndexOf('_');
            if (split <= 0 || split == id.Length - 1) { return false; }
            if (PlatformUserID.TryParse(id, out PlatformUserID parsed) == false) { return false; }
            return string.Equals(id.Substring(0, split), parsed.m_platform.ToString(), StringComparison.Ordinal) == false;
        }
    }
}
