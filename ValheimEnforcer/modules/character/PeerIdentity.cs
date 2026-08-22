using System.Collections.Generic;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.character {

    /// <summary>
    /// The single place the server answers "who is this peer, really, and does this character belong to them".
    ///
    /// Every server-authoritative decision in this mod that used to resolve identity ad hoc goes through here,
    /// so the rule is written once: identity comes from the connection (the socket behind the peer), never
    /// from anything the client put in a payload, and a save/delta is only accepted for the exact account and
    /// character name that connection joined as.
    ///
    /// Two deliberate differences from the code this replaces:
    ///  - it resolves the peer from the RoutedRpc sender uid only after that uid has been verified against the
    ///    socket (see RoutedRpcGuard); on its own the uid is client-written and spoofable.
    ///  - it fails CLOSED. The old checks accepted a save "unchecked" whenever the peer's account id or name
    ///    came back empty, which is exactly the pre-handshake state a save could be smuggled in during. On
    ///    every supported backend a ready peer has both, so an empty one is a reason to refuse, not to trust.
    /// </summary>
    internal static class PeerIdentity {

        /// <summary>
        /// The peer a connection belongs to, matched on the live ZRpc. Vanilla's ZNet.GetPeer(ZRpc) does the
        /// same but is private; this mirrors StructureValidator.PeerFor so both share one definition of "the
        /// peer behind this socket". Server side; returns null when nothing matches.
        /// </summary>
        internal static ZNetPeer PeerFor(ZRpc rpc) {
            if (rpc == null || ZNet.instance == null) { return null; }
            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            for (int i = 0; i < peers.Count; i++) {
                if (peers[i] != null && peers[i].m_rpc == rpc) { return peers[i]; }
            }
            return null;
        }

        /// <summary>
        /// Resolves the account id and character name the server believes a connected peer to be, from the peer
        /// uid. Returns false when there is no ready peer for that uid, or the connection carries no usable
        /// identity - in which case the caller must refuse whatever it was about to do rather than fall back to
        /// trusting the payload.
        ///
        /// The account id is the socket's endpoint string, matching what SendSavedCharacter files saves under
        /// and what the previous inline checks compared against; identity is normalised for comparison in Owns.
        /// </summary>
        internal static bool TryResolve(long sender, out string accountId, out string characterName) {
            accountId = null;
            characterName = null;
            ZNetPeer peer = ZNet.instance?.GetPeer(sender);
            if (peer == null || !peer.IsReady()) { return false; }
            accountId = peer.m_socket?.GetEndPointString();
            characterName = peer.m_playerName;
            return !string.IsNullOrEmpty(accountId) && !string.IsNullOrEmpty(characterName);
        }

        /// <summary>
        /// Whether a save/delta naming <paramref name="hostId"/>/<paramref name="name"/> may be written by a
        /// connection the server resolved as <paramref name="accountId"/>/<paramref name="characterName"/>.
        /// Account ids are compared with PlatformIds.Matches because one account reaches us under more than one
        /// spelling; the character name must be the one the peer connected as.
        /// </summary>
        internal static bool Owns(string accountId, string characterName, string hostId, string name) {
            if (string.IsNullOrEmpty(accountId) || string.IsNullOrEmpty(characterName)) { return false; }
            if (string.IsNullOrEmpty(hostId) || string.IsNullOrEmpty(name)) { return false; }
            return PlatformIds.Matches(accountId, hostId)
                && string.Equals(characterName, name, System.StringComparison.OrdinalIgnoreCase);
        }

        // ---- Filesystem safety ---------------------------------------------------------------------------

        /// <summary>
        /// Longest account id or character name the store will file to disk. Steam ids are ~17 digits and
        /// Valheim caps character names well under this; the limit exists to bound a hostile name, not to
        /// constrain a real one.
        /// </summary>
        private const int MaxTokenLength = 64;

        private static readonly char[] ForbiddenChars = { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };

        /// <summary>
        /// True when a value is safe to use as a path segment. Both the account id and the character name reach
        /// Path.Combine on the server (Characters/&lt;id&gt;/&lt;name&gt;.yaml), and both originate from the client:
        /// the account id from the socket on a supported backend (safe in practice, checked anyway), the
        /// character name straight out of the client's peer-info package (whatever it typed). A name like
        /// "..\..\plugins\x" would otherwise write a .yaml file wherever the traversal points, with
        /// client-influenced contents. Rejects path separators, drive/ADS colons, wildcard/redirect characters,
        /// any "..", control characters, the reserved "." / "..", the empty string, and anything over the
        /// length cap.
        /// </summary>
        internal static bool IsSafeToken(string value) {
            if (string.IsNullOrEmpty(value)) { return false; }
            if (value.Length > MaxTokenLength) { return false; }
            if (value == "." || value == "..") { return false; }
            if (value.IndexOf("..", System.StringComparison.Ordinal) >= 0) { return false; }
            foreach (char c in value) {
                if (c < ' ') { return false; }             // control characters, incl. NUL, CR, LF, tab
                for (int i = 0; i < ForbiddenChars.Length; i++) {
                    if (c == ForbiddenChars[i]) { return false; }
                }
            }
            return true;
        }
    }
}
