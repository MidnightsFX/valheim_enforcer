using HarmonyLib;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.cheatmonitor {

    /// <summary>
    /// Extends Valheim's connection ban check so that any id in the server-side KnownCheaters
    /// list is rejected at join time, exactly like a vanilla-banned player.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_PeerInfo))]
    internal static class ZNet_RPC_PeerInfo_BanCheck {

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(ZNet __instance, ZRpc rpc) {
            if (!__instance.IsServer()) { return true; }

            string hostId = rpc.GetSocket()?.GetHostName();
            if (string.IsNullOrEmpty(hostId) || !KnownCheaterTracker.IsListed(hostId)) { return true; }

            Logger.LogWarning($"Rejecting known cheater {hostId}: {KnownCheaterTracker.GetReason(hostId)}");
            rpc.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorBanned); // mirror vanilla banned rejection
            return false; // skip vanilla peer-info handling
        }
    }
}
