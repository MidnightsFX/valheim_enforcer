using HarmonyLib;
using ValheimEnforcer.common;
using ValheimEnforcer.modules.character;

namespace ValheimEnforcer.modules.network {

    /// <summary>
    /// Makes the sender of a routed network message trustworthy, which nothing in vanilla or Jotunn does.
    ///
    /// Valheim addresses every routed RPC with a RoutedRPCData whose m_senderPeerID is written by the sending
    /// client (ZRoutedRpc.RoutedRPCData.Serialize) and read straight back on the server
    /// (ZRoutedRpc.RPC_RoutedRPC -> HandleRoutedRPC), never once compared with the socket the packet actually
    /// arrived on. Jotunn's CustomRPC hands that same id to every OnServerReceive handler as its `long sender`.
    /// Peer uids are not secret - a peer writes its own into its PeerInfo and it is the owner id on every ZDO
    /// that peer holds - so a modified client can put any connected player's uid in the field and be treated
    /// as that player. In this mod that reaches the admin gate on relayed console commands, the ban target on a
    /// cheat report, the identity a character save is checked against, and Jotunn's own admin-only config sync.
    ///
    /// This prefix runs on the server before RPC_RoutedRPC does anything with the packet. It reads the sender
    /// id out of the package, compares it with the real peer behind the ZRpc, and - only when they disagree -
    /// rewrites the id to the true peer before the original method sees it. An honest packet is never touched
    /// (the common path reads two longs and returns), so this is a correction of forged input, not a new
    /// behaviour an honest client can observe. It fixes every routed RPC at once: this mod's CustomRPCs,
    /// Jotunn's config sync, and any other mod's routed calls.
    ///
    /// Off only if an admin sets EnforceRoutedRpcSender=false. A packet from a peer that cannot be resolved, or
    /// that is not yet ready (pre-handshake), is dropped: there is nobody to attribute it to.
    /// </summary>
    [HarmonyPatch(typeof(ZRoutedRpc), nameof(ZRoutedRpc.RPC_RoutedRPC))]
    internal static class RoutedRpcGuard {

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(ZRoutedRpc __instance, ZRpc rpc, ref ZPackage pkg) {
            // Only the server relays/attributes routed RPCs. A client receiving one has already had its sender
            // set by the server, and cannot verify anything anyway.
            if (ZNet.instance == null || !ZNet.instance.IsServer()) { return true; }
            if (ValConfig.EnforceRoutedRpcSender == null || !ValConfig.EnforceRoutedRpcSender.Value) { return true; }
            if (rpc == null || pkg == null) { return true; }

            ZNetPeer peer = PeerIdentity.PeerFor(rpc);
            if (peer == null || !peer.IsReady()) {
                // No ready connection to attribute this to. Dropping is the safe choice: the only routed
                // traffic before a peer is ready is either noise or an attempt to act before we know who they
                // are. Vanilla itself does nothing useful with a pre-handshake routed call.
                Logger.LogDebug("Dropping a routed RPC from an unresolved or not-ready peer.");
                return false;
            }

            int startPos = pkg.GetPos();
            long msgId;
            long senderId;
            try {
                // RoutedRPCData layout: m_msgID (long), m_senderPeerID (long), ... - reading just the first two
                // is enough to check the sender and keeps the honest path allocation-free. This runs on every
                // routed packet the server handles, so it must stay cheap.
                msgId = pkg.ReadLong();
                senderId = pkg.ReadLong();
            } catch {
                pkg.SetPos(startPos);
                return true; // malformed; let vanilla's own parsing deal with it
            }

            if (senderId == peer.m_uid) {
                pkg.SetPos(startPos); // untouched - reset the read cursor for the original method
                return true;
            }

            // Sender does not match the connection. Rewrite it. Re-serialize from a full parse rather than
            // patching bytes, so this is robust to the exact wire layout; only forged packets pay the cost.
            _ = msgId;
            pkg.SetPos(startPos);
            ZRoutedRpc.RoutedRPCData data = new ZRoutedRpc.RoutedRPCData();
            try {
                data.Deserialize(pkg);
            } catch {
                pkg.SetPos(startPos);
                return true;
            }

            Logger.LogWarning($"Routed RPC from {Describe(peer)} claimed sender id {data.m_senderPeerID} " +
                              $"(method {data.m_methodHash}); correcting it to the real peer {peer.m_uid}.");
            data.m_senderPeerID = peer.m_uid;

            ZPackage corrected = new ZPackage();
            data.Serialize(corrected);
            corrected.SetPos(0);
            pkg = corrected;
            return true;
        }

        private static string Describe(ZNetPeer peer) {
            string host = peer.m_socket != null ? peer.m_socket.GetHostName() : null;
            return string.IsNullOrEmpty(host) ? peer.m_uid.ToString() : $"{peer.m_playerName} ({host})";
        }
    }
}
