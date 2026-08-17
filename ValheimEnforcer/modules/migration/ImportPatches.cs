using HarmonyLib;
using System;

namespace ValheimEnforcer.modules.migration {

    /// <summary>
    /// Runs the ServerCharacters import once, when a server starts, if the admin has switched it on.
    ///
    /// Same trigger as ThunderstoreResolver and FullSyncScheduler, but without their GameObject/MonoBehaviour
    /// driver: those exist because their work is network I/O and per-frame ticking. This is a bounded local
    /// disk scan that runs once, and keeping it on the main thread sidesteps the CharacterStore threading
    /// contract entirely. ZoneSystem.instance is set in Awake, so the in-world registry that internal storage
    /// mode needs is already reachable by the time Start runs.
    ///
    /// No "already migrated" marker is needed: characters that already have a save are skipped, so the pass is
    /// naturally idempotent and safe to leave enabled.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Start))]
    internal static class ZNet_Start_ServerCharactersImport {

        [HarmonyPostfix]
        private static void Postfix(ZNet __instance) {
            if (__instance == null || !__instance.IsServer()) { return; }
            if (ValConfig.ImportServerCharacters == null || !ValConfig.ImportServerCharacters.Value) { return; }

            try {
                ImportReport report = ServerCharactersImport.Run(dryRun: false, force: false);
                Logger.LogInfo(report.Summary());
            } catch (Exception e) {
                // A migration must never be able to stop the server coming up.
                Logger.LogError($"ServerCharacters import failed: {e}");
            }
        }
    }
}
