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
        /// The connection-to-peer map behind <see cref="PeerFor"/>, maintained from ZNet's own connect and
        /// disconnect seams (see PeerIdentityPatches).
        ///
        /// ZRpc declares no Equals or GetHashCode, so this keys on reference identity - the exact comparison
        /// the scan it replaces was doing, just resolved once per connection instead of once per packet.
        ///
        /// No lock. Everything that touches this - the ZNet patches that fill it and every RPC handler that
        /// reads it - runs on Unity's main thread inside ZNet.Update, and putting a monitor on the hottest
        /// path in the game to serialise a single thread against itself would give back the win.
        /// </summary>
        private static readonly Dictionary<ZRpc, ZNetPeer> PeersByRpc = new Dictionary<ZRpc, ZNetPeer>();

        /// <summary>
        /// The account id behind each tracked connection, resolved once instead of per packet - see
        /// <see cref="AccountFor"/>. Keyed on the peer by reference (ZNetPeer declares no equality of its own),
        /// and under the same no-lock rule as the map above.
        ///
        /// Entries are created ONLY by <see cref="Remember"/> and destroyed only by <see cref="Forget"/> and
        /// <see cref="Reset"/>. That is deliberate: AccountFor is called from a ZNet.Disconnect prefix, which
        /// races Forget's own prefix, and a lookup that inserted on a miss would re-add an entry for a peer
        /// that has just left and keep it - and its socket - alive until the session ended.
        /// </summary>
        private static readonly Dictionary<ZNetPeer, string> AccountsByPeer = new Dictionary<ZNetPeer, string>();

        /// <summary>
        /// The peer a connection belongs to, matched on the live ZRpc. Vanilla's ZNet.GetPeer(ZRpc) does the
        /// same but is private, and is a linear walk of the peer list; this is the one definition of "the peer
        /// behind this socket" for the whole mod. Server side; returns null when nothing matches.
        ///
        /// This is called for every routed RPC the server relays (RoutedRpcGuard) and every ZDOData packet it
        /// receives (StructureValidator, ContainerAudit), so a scan here costs packets x connected players -
        /// and both of those numbers rise together in exactly the dense fight where the main thread can least
        /// afford it. The dictionary makes it a hash lookup that does not care how full the server is.
        /// </summary>
        internal static ZNetPeer PeerFor(ZRpc rpc) {
            if (rpc == null || ZNet.instance == null) { return null; }

            if (PeersByRpc.TryGetValue(rpc, out ZNetPeer cached)) {
                // Re-checked rather than trusted. Nothing here owns ZNetPeer.m_rpc, and attributing a packet
                // to the wrong player is the one failure this whole file exists to prevent - so a cached
                // answer that no longer matches the connection is thrown away rather than returned. One
                // reference compare, on a branch that is true for every honest packet.
                if (cached != null && cached.m_rpc == rpc) { return cached; }
                PeersByRpc.Remove(rpc);
            }

            // A miss means the cache never saw this connection (Enforcer patched in mid-session, or another
            // mod skipped ZNet.OnNewConnection) or the entry was just evicted above. Fall back to what this
            // method always did, then remember it: an unresolvable rpc costs exactly what it did before, and a
            // real one pays for the scan once instead of on every packet it ever sends.
            ZNetPeer found = Scan(rpc);
            if (found != null) { Remember(found); }
            return found;
        }

        private static ZNetPeer Scan(ZRpc rpc) {
            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            for (int i = 0; i < peers.Count; i++) {
                if (peers[i] != null && peers[i].m_rpc == rpc) { return peers[i]; }
            }
            return null;
        }

        /// <summary>
        /// The account id behind a connection: the socket's host name, resolved once and kept for the life of
        /// the connection.
        ///
        /// The caching is the whole point. ISocket.GetHostName builds a fresh string on every call - on a Steam
        /// socket it is literally SteamID.ToString() - and this value is wanted once per ZDOData packet
        /// (ContainerAudit) and once per relayed hit (DamageAudit), which on a full server is thousands of
        /// throwaway strings a second for an answer that cannot change while the socket is open.
        ///
        /// A peer this has never been told about - Enforcer patched in mid-session, or one already forgotten -
        /// is answered from the socket without filing anything, so the table never outlives the connections it
        /// describes. An id that is not readable yet (PlayFab learns it from the first data message, unlike
        /// Steam which has it at construction) is not cached, so the next call tries again.
        /// </summary>
        internal static string AccountFor(ZNetPeer peer) {
            if (peer == null) { return null; }

            if (!AccountsByPeer.TryGetValue(peer, out string cached)) {
                return peer.m_socket != null ? peer.m_socket.GetHostName() : null;
            }
            if (!string.IsNullOrEmpty(cached)) { return cached; }

            string host = peer.m_socket != null ? peer.m_socket.GetHostName() : null;
            if (!string.IsNullOrEmpty(host)) { AccountsByPeer[peer] = host; }
            return host;
        }

        /// <summary>Records a connection as it joins. Safe to call twice for the same peer.</summary>
        internal static void Remember(ZNetPeer peer) {
            ZRpc rpc = peer?.m_rpc;
            if (rpc == null) { return; }
            PeersByRpc[rpc] = peer;
            // Only ever adds the key. Overwriting would throw away an account id already resolved for a
            // connection that is simply being re-filed after a scan.
            if (!AccountsByPeer.ContainsKey(peer)) { AccountsByPeer[peer] = null; }
        }

        /// <summary>
        /// Drops a connection as it leaves. Must run before ZNet.Disconnect disposes the peer, while m_rpc is
        /// still the key it was filed under.
        /// </summary>
        internal static void Forget(ZNetPeer peer) {
            if (peer == null) { return; }
            AccountsByPeer.Remove(peer);
            ZRpc rpc = peer.m_rpc;
            if (rpc == null) { return; }
            PeersByRpc.Remove(rpc);
        }

        /// <summary>Empties the maps between sessions, so nothing survives a shutdown into the next world.</summary>
        internal static void Reset() {
            PeersByRpc.Clear();
            AccountsByPeer.Clear();
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
