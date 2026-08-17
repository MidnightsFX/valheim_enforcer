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
        /// platform-specific suffix.
        /// </summary>
        internal static string Normalize(string id) {
            if (string.IsNullOrEmpty(id)) { return id; }
            int idx = id.LastIndexOf('_');
            return idx >= 0 && idx < id.Length - 1 ? id.Substring(idx + 1) : id;
        }

        /// <summary>
        /// True when two ids refer to the same account, tolerating a platform prefix difference.
        /// Empty ids never match anything, including each other.
        /// </summary>
        internal static bool Matches(string left, string right) {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) { return false; }
            if (left == right) { return true; }
            return Normalize(left) == Normalize(right);
        }
    }
}
