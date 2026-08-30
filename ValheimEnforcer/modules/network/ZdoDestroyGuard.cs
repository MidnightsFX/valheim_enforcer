using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.network {

    /// <summary>
    /// Filters the DestroyZDO message, which is the cheapest way there is to erase a Valheim world.
    ///
    /// ZDOMan.RPC_DestroyZDO reads a count and then that many object ids, and deletes each one
    /// (ZDOMan.cs:529). It checks nothing: not who sent it, not whether they own the object, not whether they
    /// are anywhere near it, not how many they asked for. Every id in the packet is destroyed and the deletion
    /// is broadcast to everybody, so a single message can take out a base, and an enumerated one can take out
    /// the world.
    ///
    /// The legitimate shape of this message is narrow and comes straight from vanilla's own send path:
    /// ZDOMan.DestroyZDO refuses to queue anything the caller does not own (ZDOMan.cs:510-515), and SendDestroyed
    /// flushes that queue once per frame. So an honest client only ever names objects it owns, and only a
    /// frame's worth at a time.
    ///
    /// This filters the packet rather than dropping it, because a legitimate batch and a hostile id can arrive
    /// together and cancelling the whole batch would leave destroyed objects alive on the server. The ids that
    /// survive are re-packed and handed to vanilla; the rest never happened.
    /// </summary>
    [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.RPC_DestroyZDO))]
    internal static class ZdoDestroyGuard {

        /// <summary>Individual rejected ids named in the log before the rest are summarised.</summary>
        private const int LogDetailCap = 5;

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(long sender, ref ZPackage pkg) {
            try {
                if (!RpcGuardPolicy.Active() || !ValConfig.GuardZdoDestruction.Value) { return true; }
                if (pkg == null || ZDOMan.instance == null) { return true; }

                // No peer behind this id means the server sent it to itself - the normal path for anything the
                // server destroys, since SendDestroyed addresses Everybody and the server handles its own copy.
                // `sender` is trustworthy here because RoutedRpcGuard corrects the routed sender before this runs.
                ZNetPeer peer = ZNet.instance.GetPeer(sender);
                if (peer == null) { return true; }
                if (RpcGuardPolicy.IsExempt(peer)) { return true; }

                return Filter(peer, ref pkg);
            } catch (Exception e) {
                // Never take the destroy stream down over a bug of ours. Letting it through matches how the
                // rest of the mod fails: a guard that throws must not also break the game.
                Logger.LogWarning($"ZDO destroy guard failed, letting the packet through: {e}");
                return true;
            }
        }

        private static bool Filter(ZNetPeer peer, ref ZPackage pkg) {
            int startPos = pkg.GetPos();
            int claimed;
            try {
                claimed = pkg.ReadInt();
            } catch {
                pkg.SetPos(startPos);
                return true; // malformed; vanilla's own read will deal with it
            }

            if (claimed <= 0) {
                pkg.SetPos(startPos);
                return true; // nothing to destroy, and a negative count makes vanilla's loop a no-op
            }

            // Read at most the cap, however large the count claims to be. A crafted packet can say two billion,
            // and looping that far to reject them all would itself be the denial of service.
            int cap = Math.Max(1, ValConfig.MaxZdoDestroysPerPacket.Value);
            int toRead = Math.Min(claimed, cap);
            int overCap = claimed - toRead;

            List<ZDOID> allowed = new List<ZDOID>(toRead);
            List<string> rejected = new List<string>();

            for (int i = 0; i < toRead; i++) {
                ZDOID id;
                try {
                    id = pkg.ReadZDOID();
                } catch {
                    // The packet lied about its count. Everything read so far is still good, so keep it and
                    // stop; vanilla would have thrown here too.
                    break;
                }

                string refusal = RefusalFor(peer, id);
                if (refusal == null) {
                    allowed.Add(id);
                } else if (rejected.Count < LogDetailCap) {
                    rejected.Add(refusal);
                }
            }

            int refusedCount = (toRead - allowed.Count) + overCap;
            if (refusedCount == 0) {
                pkg.SetPos(startPos); // every id was legitimate - forward the original bytes untouched
                return true;
            }

            Report(peer, refusedCount, overCap, claimed, rejected);

            ZPackage filtered = new ZPackage();
            filtered.Write(allowed.Count);
            foreach (ZDOID id in allowed) { filtered.Write(id); }
            filtered.SetPos(0);
            pkg = filtered;
            return true;
        }

        /// <summary>
        /// Why this peer may not destroy this object, or null when it may.
        ///
        /// Ownership is the real test and covers the normal case, because vanilla only lets a client queue a
        /// destroy for something it owns and the server records that ownership as the object's updates arrive
        /// (ZDOMan.RPC_ZDOData -> ZDO.SetOwnerInternal).
        ///
        /// Proximity is the tolerance around it. A client takes ownership of an object and destroys it in the
        /// same breath - picking up a dropped item, breaking a piece - and the destroy can reach the server
        /// ahead of the ownership update that would justify it. Rather than lose those, anything close enough
        /// to the sender to be interacted with is allowed.
        /// </summary>
        private static string RefusalFor(ZNetPeer peer, ZDOID id) {
            ZDO zdo = ZDOMan.instance.GetZDO(id);
            // The server does not have this object. There is nothing to protect and vanilla's handler does its
            // own id bookkeeping for ids it does not know, so this must go through rather than be filtered out.
            if (zdo == null) { return null; }

            if (zdo.GetOwner() == peer.m_uid) { return null; }

            float allowed = Mathf.Max(1f, ValConfig.ZdoDestroyProximityMetres.Value);
            Vector3 where = zdo.GetPosition();
            float distance = Vector3.Distance(peer.GetRefPos(), where);
            if (distance <= allowed) { return null; }

            return $"{NameOf(zdo)} at ({where.x:F0}, {where.y:F0}, {where.z:F0}), " +
                   $"owned by {zdo.GetOwner()} and {distance:F0}m away";
        }

        private static string NameOf(ZDO zdo) {
            int hash = zdo.GetPrefab();
            return worldintegrity.StructureIndex.IsBuilt()
                ? worldintegrity.StructureIndex.NameOf(hash)
                : hash.ToString();
        }

        private static void Report(ZNetPeer peer, int refusedCount, int overCap, int claimed, List<string> examples) {
            string detail = $"asked the server to destroy {claimed} object(s); {refusedCount} refused";
            if (overCap > 0) {
                detail += $", of which {overCap} were past the {ValConfig.MaxZdoDestroysPerPacket.Value}-per-packet limit";
            }
            if (examples.Count > 0) {
                detail += $" - {string.Join("; ", examples.ToArray())}";
            }
            RpcGuardPolicy.Refuse(peer, "zdo-destroy", detail);
        }
    }
}
