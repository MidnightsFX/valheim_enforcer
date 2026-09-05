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
            if (rpc == null || pkg == null) { return true; }

            bool enforceSender = ValConfig.EnforceRoutedRpcSender != null && ValConfig.EnforceRoutedRpcSender.Value;
            // RoutedRpcFilter inspects the payload of a handful of specific methods (see that file). It shares
            // this prefix rather than adding one of its own so a packet is parsed and re-serialized at most
            // once, and so the two features cannot end up depending on which patch Harmony runs first.
            bool filter = RoutedRpcFilter.AnyEnabled();
            if (!enforceSender && !filter) { return true; }

            ZNetPeer peer = PeerIdentity.PeerFor(rpc);
            if (peer == null || !peer.IsReady()) {
                // No ready connection to attribute this to. Dropping is the safe choice: the only routed
                // traffic before a peer is ready is either noise or an attempt to act before we know who they
                // are. Vanilla itself does nothing useful with a pre-handshake routed call.
                //
                // Only sender enforcement drops here. The filters are about the contents of a packet, not who
                // owns it, so turning one of them on must not change how unattributable traffic is handled.
                if (!enforceSender) { return true; }
                Logger.LogDebug("Dropping a routed RPC from an unresolved or not-ready peer.");
                return false;
            }

            int startPos = pkg.GetPos();
            long senderId;
            ZDOID targetZdo;
            int methodHash;
            try {
                // RoutedRPCData layout: m_msgID (long), m_senderPeerID (long), m_targetPeerID (long),
                // m_targetZDO (ZDOID), m_methodHash (int), m_parameters (package). Reading up to the method
                // hash is enough to answer both questions and allocates nothing - the parameters package, which
                // is the only allocating read, is left alone. This runs on every routed packet the server
                // handles, so it must stay cheap.
                pkg.ReadLong();                 // m_msgID
                senderId = pkg.ReadLong();      // m_senderPeerID
                pkg.ReadLong();                 // m_targetPeerID
                targetZdo = pkg.ReadZDOID();    // m_targetZDO
                methodHash = pkg.ReadInt();     // m_methodHash
            } catch {
                pkg.SetPos(startPos);
                return true; // malformed; let vanilla's own parsing deal with it
            }

            bool forgedSender = enforceSender && senderId != peer.m_uid;
            bool watched = filter && RoutedRpcFilter.IsWatched(methodHash);
            if (!forgedSender && !watched) {
                pkg.SetPos(startPos); // untouched - reset the read cursor for the original method
                return true;
            }

            // Nothing here can refuse or rewrite this packet - the only thing left is to write down that it
            // happened - so it is armed and read in the finalizer instead, which runs after the original
            // method has forwarded it. That ordering is worth the bracket: RouteRPC hands the bytes to the
            // socket synchronously (ZSteamSocket.Send pushes to Steam then and there, it does not wait for
            // the next frame), so everything this prefix does sits directly in front of a hit reaching the
            // player it lands on, and a recording has no business being there.
            if (!forgedSender && RoutedRpcFilter.RecordsOnly(peer, methodHash)) {
                RoutedRpcFilter.ArmDeferredRead(peer, targetZdo, pkg, pkg.GetPos());
                pkg.SetPos(startPos); // untouched - the original bytes are forwarded exactly as they arrived
                return true;
            }

            // Nothing here will edit the packet: the sender is honest and this is one of the filters that only
            // reads, but its answer is needed before the packet moves. The cursor is already sitting on the
            // parameters, so the filter works straight off it and no RoutedRPCData is built at all - which
            // matters because vanilla's RPC_RoutedRPC parses this same package into one of its own the moment
            // we return.
            if (!forgedSender && !RoutedRpcFilter.MayRewrite(methodHash)) {
                RpcVerdict readOnly = RoutedRpcFilter.InspectInPlace(peer, methodHash, targetZdo, pkg);
                // Skipping the original method means the server neither handles nor relays it, so the message
                // reaches nobody; otherwise the original bytes are forwarded exactly as they arrived.
                if (readOnly == RpcVerdict.Drop) { return false; }
                pkg.SetPos(startPos);
                return true;
            }

            // A rewrite is possible, so the payload has to be materialised. Re-serialize from a full parse
            // rather than patching bytes, so this is robust to the exact wire layout; only packets that are
            // actually forged, or carry a method that can be rewritten, pay the cost.
            pkg.SetPos(startPos);
            ZRoutedRpc.RoutedRPCData data = new ZRoutedRpc.RoutedRPCData();
            try {
                data.Deserialize(pkg);
            } catch {
                pkg.SetPos(startPos);
                return true;
            }

            bool changed = false;
            if (forgedSender) {
                Logger.LogWarning($"Routed RPC from {Describe(peer)} claimed sender id {data.m_senderPeerID} " +
                                  $"(method {data.m_methodHash}); correcting it to the real peer {peer.m_uid}.");
                data.m_senderPeerID = peer.m_uid;
                changed = true;
            }

            if (watched) {
                // Runs after the sender correction so a filter that cares about identity sees the true one.
                RpcVerdict verdict = RoutedRpcFilter.Inspect(peer, data);
                if (verdict == RpcVerdict.Drop) {
                    // Skipping the original method means the server neither handles nor relays it, so the
                    // message reaches nobody. Nothing to reset: vanilla never reads this package again.
                    return false;
                }
                if (verdict == RpcVerdict.Rewritten) { changed = true; }
            }

            if (!changed) {
                pkg.SetPos(startPos); // inspected and found honest; forward the original bytes
                return true;
            }

            ZPackage corrected = new ZPackage();
            data.Serialize(corrected);
            corrected.SetPos(0);
            pkg = corrected;
            return true;
        }

        /// <summary>
        /// Reads whatever the prefix armed, after the original method has relayed the packet.
        ///
        /// A finalizer rather than a postfix so it runs on the exception path too. That is not about
        /// recording the hit - it is about the armed slot never surviving into the next packet, which would
        /// attribute one player's hit to whoever sent the message after it. Costs a null check on every
        /// routed packet that armed nothing, which is all of them unless the audit is on.
        /// </summary>
        [HarmonyFinalizer]
        private static void Finalizer() {
            RoutedRpcFilter.RunDeferredRead();
        }

        private static string Describe(ZNetPeer peer) {
            string host = peer.m_socket != null ? peer.m_socket.GetHostName() : null;
            return string.IsNullOrEmpty(host) ? peer.m_uid.ToString() : $"{peer.m_playerName} ({host})";
        }
    }
}
