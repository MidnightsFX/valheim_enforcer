using HarmonyLib;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.recovery {

    /// <summary>
    /// The two hooks the crash marker needs: notice that this server started, and record that it stopped
    /// cleanly.
    ///
    /// Both sit behind the feature's own setting, so a server that never enables crash recovery never grows
    /// a marker file - the off-by-default rule applies to files on disk as much as to behaviour. The start
    /// hook holds the check; the shutdown hook writes nothing unless the start hook ran, so it inherits it.
    ///
    /// What that costs is one session. The first start after an admin switches this on finds no marker, and
    /// reads that as a first run rather than as a crash, so no window opens. Every start after that answers
    /// properly.
    /// </summary>
    internal static class RecoveryPatches {

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Start))]
        public static class ZNet_Start_CrashMarker {
            [HarmonyPostfix]
            private static void Postfix(ZNet __instance) {
                if (__instance == null || !__instance.IsServer()) { return; }
                try {
                    CrashMarker.OnServerStart();
                } catch (System.Exception e) {
                    // A marker problem must never stop a server from coming up.
                    Logger.LogWarning($"Could not check the crash-recovery marker: {e.Message}");
                }
            }
        }

        /// <summary>
        /// Written as a POSTFIX, which is what puts it after the character store's flush.
        ///
        /// CharacterStore.Shutdown runs from a ZNet.Shutdown prefix at Priority.First and blocks until every
        /// queued save is on disk. A marker written before that would claim a clean stop while a save was
        /// still pending - and the next start would then refuse to open the window over exactly the data the
        /// window exists to recover. A postfix cannot run before the prefix, whatever priorities anything
        /// else registers with.
        /// </summary>
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
        public static class ZNet_Shutdown_CrashMarker {
            [HarmonyPostfix]
            private static void Postfix() {
                try {
                    CrashMarker.OnServerShutdown();
                } catch (System.Exception e) {
                    Logger.LogWarning($"Could not record a clean shutdown: {e.Message}");
                }
            }
        }

        /// <summary>
        /// A completed world save closes the window early.
        ///
        /// Once this server has written a save of its own, its own copy is the current one and a snapshot
        /// from before the crash is no longer newer than reality - whatever its sequence number says. Hooked
        /// on the save STARTING rather than finishing, which errs on the side of closing sooner.
        /// </summary>
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.SaveWorld))]
        public static class ZNet_SaveWorld_CloseWindow {
            [HarmonyPostfix]
            private static void Postfix(ZNet __instance) {
                if (__instance == null || !__instance.IsServer()) { return; }
                CrashMarker.Close("this server has written a save of its own");
            }
        }
    }
}
