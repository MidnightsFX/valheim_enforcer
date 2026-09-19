using HarmonyLib;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.cheatmonitor {

    // The join-time ban check used to live here, reading the KnownCheaters list directly. It now lives in
    // modules/bannetwork/BanPatches.cs, which consults every ban source through one policy - see BanPolicy.

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
