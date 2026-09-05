namespace ValheimEnforcer.common {

    /// <summary>
    /// Comparison helpers for the account identifiers the mod handles.
    ///
    /// The same account reaches us under more than one spelling. A connecting peer is identified by
    /// <c>ISocket.GetHostName()</c>, which on Steam sockets is the bare SteamID64 but on PlayFab sockets is
    /// the platform-prefixed form ("Steam_7656...", "XboxLive_..."). Ids that were written to disk came from
    /// whichever path produced them - CharacterManager.GetPlayerID reads a bare m_userID, ValConfig
    /// .SendSavedCharacter uses GetEndPointString() - and an admin typing an id into a config file may use
    /// either. Comparing on the platform-specific suffix makes all of those agree.
    /// </summary>
    internal static class PlatformIds {

        /// <summary>
        /// Strips a leading platform prefix ("Steam_", "PlayFab_", ...) so ids compare on their
        /// platform-specific suffix. For comparison only, prefer <see cref="Matches"/>, which answers the same
        /// question without building the stripped strings.
        /// </summary>
        internal static string Normalize(string id) {
            if (string.IsNullOrEmpty(id)) { return id; }
            return id.Substring(SuffixStart(id));
        }

        /// <summary>
        /// True when two ids refer to the same account, tolerating a platform prefix difference.
        /// Empty ids never match anything, including each other.
        ///
        /// The suffixes are compared where they sit rather than by normalising both sides first. The fast path
        /// - two identical spellings - was always free, but the slow path is the one this method exists for,
        /// so it was the common case in practice and it allocated two strings every time. The audit's history
        /// query runs this once per recorded event it examines, which is where that started to matter.
        /// </summary>
        internal static bool Matches(string left, string right) {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) { return false; }
            if (left == right) { return true; }

            int leftStart = SuffixStart(left);
            int rightStart = SuffixStart(right);
            int length = left.Length - leftStart;
            if (length != right.Length - rightStart) { return false; }
            return string.CompareOrdinal(left, leftStart, right, rightStart, length) == 0;
        }

        /// <summary>
        /// Index of the first character of the platform-specific suffix: just past the last underscore, or
        /// zero when there is none or it is the final character. Kept as the single definition of where a
        /// prefix ends, so <see cref="Normalize"/> and <see cref="Matches"/> cannot drift apart.
        /// </summary>
        private static int SuffixStart(string id) {
            int idx = id.LastIndexOf('_');
            return idx >= 0 && idx < id.Length - 1 ? idx + 1 : 0;
        }
    }
}
