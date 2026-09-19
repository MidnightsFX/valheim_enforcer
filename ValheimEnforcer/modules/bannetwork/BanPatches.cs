using HarmonyLib;
using ValheimEnforcer.common;
using ValheimEnforcer.modules.character;

namespace ValheimEnforcer.modules.bannetwork {

    /// <summary>
    /// The join gate. Refuses a banned account at the connect handshake, exactly as a vanilla ban does, and
    /// tells them why.
    ///
    /// Replaces the old ZNet_RPC_PeerInfo_BanCheck in the cheat monitor, which could only consult the
    /// known-cheater list and sent a bare ErrorBanned with no explanation. Priority.First is kept from that
    /// patch: the first prefix to return false short-circuits the rest, and "you are banned" is the most
    /// useful thing to tell someone who is about to be refused several times over.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_PeerInfo))]
    internal static class ZNet_RPC_PeerInfo_BanGate {

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(ZNet __instance, ZRpc rpc) {
            if (!__instance.IsServer()) { return true; }

            string hostId = rpc.GetSocket()?.GetHostName();
            if (string.IsNullOrEmpty(hostId)) { return true; }

            // Hashed inline. A salted digest is microseconds, so the handshake gets a complete verdict -
            // local layers and network layer both - on the first pass, with nothing deferred.
            BanVerdict verdict = BanPolicy.Evaluate(hostId, BanSubjectCache.Hash(hostId));

            if (verdict.Advisory) {
                Logger.LogInfo($"{hostId} is listed on the ban network but not under a category this server enforces. Allowing. {verdict.Explanation}");
            }
            if (!verdict.Reject) { return true; }

            Logger.LogWarning($"Refusing {hostId}: {verdict.Explanation}");
            // Only network refusals are posted. A player bouncing off this server's own ban list is not news
            // to the admin who set it, whereas one refused on another server's word is exactly the thing
            // they want to know happened.
            if (verdict.Source == BanSources.Network) {
                BanNotifications.Enforced(null, hostId, verdict, "Refused at join");
            }
            CharacterLimitPatches.RefuseConnection(rpc, verdict.Reason, ZNet.ConnectionStatus.ErrorBanned);
            return false; // skip vanilla peer-info handling, exactly as vanilla's own rejections do
        }
    }

    /// <summary>
    /// Starts and stops the subject hashing thread with the server.
    ///
    /// ZNet.Start rather than plugin Awake, for the reason AuditLog gives: a client that never hosts anything
    /// should never start the thread, and a listen host that re-hosts should get a fresh one.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Start))]
    internal static class ZNet_Start_BanNetwork {

        [HarmonyPostfix]
        private static void Postfix(ZNet __instance) {
            if (__instance == null || !__instance.IsServer()) { return; }
            if (ValConfig.EnableBanNetwork == null || !ValConfig.EnableBanNetwork.Value) { return; }
            BanSubjectCache.Initialize();
            BanNetworkScheduler.Initialize();
        }
    }

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
    internal static class ZNet_Shutdown_BanNetwork {

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix() {
            BanNetworkScheduler.FlushOnShutdown();
            BanNetworkScheduler.Teardown();
            BanSubjectCache.Teardown();
        }
    }
}
