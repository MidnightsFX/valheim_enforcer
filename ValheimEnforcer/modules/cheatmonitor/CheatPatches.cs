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

    /// <summary>
    /// Destroys the client-side detector at the end of a session.
    ///
    /// CheatDetector.Initialize runs on every world load, and its host is DontDestroyOnLoad, so
    /// without a teardown a player who returns to the menu and joins again ends up running two
    /// detectors, then three - each with its own timer, each doing its own blocking process and
    /// module enumeration. Initialize's own guard stops a single session duplicating it; this is what
    /// stops successive sessions accumulating.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
    internal static class ZNet_Shutdown_CheatDetectorTeardown {

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix() {
            CheatDetector.Teardown();
        }
    }
}
