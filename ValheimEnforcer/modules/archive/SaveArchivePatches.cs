using HarmonyLib;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.archive {

    /// <summary>
    /// The one hook the archiver needs: notice that a world save has begun.
    ///
    /// Everything else happens in <see cref="SaveArchiver.Tick"/>, which is already a server-only main-thread
    /// tick. That split is deliberate - the postfix below fires while the save is still being written, so it
    /// is not a point at which anything may be read.
    /// </summary>
    internal static class SaveArchivePatches {

        /// <summary>
        /// SaveWorld rather than the public Save it hangs off, for the same reason the Discord notification
        /// patches SaveWorld: Save returns early - without saving anything - on a load error or when the zone
        /// system asks to skip, and a postfix there would arm an archive of a save that never happened.
        /// </summary>
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.SaveWorld))]
        public static class ZNet_SaveWorld_Archive {
            [HarmonyPostfix]
            private static void Postfix(ZNet __instance) {
                if (__instance == null || !__instance.IsServer()) { return; }
                // Not gated on EnableSaveArchives: this only sets two fields, and Tick - which is where any
                // work would happen - checks the setting itself. Gating here as well would mean an archive
                // switched on mid-session waited for the save after next.
                SaveArchiver.NoteSaveStarted();
            }
        }
    }
}
