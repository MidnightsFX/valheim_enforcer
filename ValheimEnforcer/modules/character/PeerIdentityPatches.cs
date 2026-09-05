using HarmonyLib;

namespace ValheimEnforcer.modules.character {

    /// <summary>
    /// Keeps <see cref="PeerIdentity"/>'s connection-to-peer map in step with ZNet's own peer list.
    ///
    /// These are the two seams ZNet uses itself: OnNewConnection is the single place a ZNetPeer is added to
    /// m_peers, and Disconnect is the single place one is removed. Hooking them rather than sampling the list
    /// is what makes the map exact - anything else would be a cache that drifts.
    ///
    /// None of this is conditional on a setting. It is bookkeeping for a lookup the mod already performs, not
    /// a behaviour: an admin who has every guard off pays four dictionary writes over the life of a
    /// connection, and the map is simply never read.
    ///
    /// Deliberately un-gated on IsServer() as well. The client side keeps exactly one entry - the server it
    /// is connected to - and gating would mean the client's own PeerFor calls fell through to the scan for no
    /// saving worth the branch.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.OnNewConnection))]
    internal static class ZNet_OnNewConnection_TrackPeerIdentity {

        // Postfix: vanilla constructs the ZRpc in the ZNetPeer constructor, so the key exists by the time this
        // runs, and a peer that never made it into m_peers is not one we want to answer with.
        [HarmonyPostfix]
        private static void Postfix(ZNetPeer peer) {
            PeerIdentity.Remember(peer);
        }
    }

    /// <summary>Forgets a connection as it leaves, before the peer is disposed.</summary>
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
    internal static class ZNet_Disconnect_TrackPeerIdentity {

        // A prefix, because ZNet.Disconnect disposes the peer as its last act; running afterwards would still
        // read the same ZRpc reference, but relying on a disposed object staying intact is not worth the risk.
        //
        // If another mod's prefix cancels Disconnect the entry is dropped while the peer stays connected. That
        // is the harmless direction: the next packet from it misses, rescans, and re-files the same answer.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void Prefix(ZNetPeer peer) {
            PeerIdentity.Forget(peer);
        }
    }

    /// <summary>
    /// Empties the map at the start of every session.
    ///
    /// A prefix, not a postfix: on a client ZNet.Start runs ClientConnect - and therefore OnNewConnection -
    /// before it returns, so clearing on the way out would throw away the entry for the server just connected
    /// to. Clearing on the way in also covers the shutdown paths that do not go through ZNet.Shutdown, so
    /// nothing from a previous world can ever be consulted in this one.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Start))]
    internal static class ZNet_Start_TrackPeerIdentity {

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void Prefix() {
            PeerIdentity.Reset();
        }
    }

    /// <summary>Releases the last references when the server stops, rather than holding them until the next start.</summary>
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
    internal static class ZNet_Shutdown_TrackPeerIdentity {

        [HarmonyPostfix]
        private static void Postfix() {
            PeerIdentity.Reset();
        }
    }
}
