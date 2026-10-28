using HarmonyLib;
using System;

namespace ValheimEnforcer.modules.worldprivacy {

    /// <summary>
    /// Keeps players out of vanilla's free fly camera.
    ///
    /// freefly is the photo mode camera. It is not a cheat command, so any player can run it from chat as
    /// /freefly, and the camera has no collision: it passes through walls and terrain into other players'
    /// bases, behind cover in PvP, and underground to dungeons and ore veins. It cannot scout further than the
    /// player could already see, because zones load around the character (Player sets the reference position)
    /// rather than around the camera.
    ///
    /// The camera is the client's own, so this is enforced on the client and only stops ordinary players. Two
    /// patches: the command is refused with a message, and anything else that switches free fly on - another
    /// mod calling GameCamera.ToggleFreeFly, or the setting being turned on while a player is already flying -
    /// is undone on the next frame.
    ///
    /// Anyone on the adminlist, and the host of a listen server, can still use it.
    /// </summary>
    internal static class FreeFlyCamera {

        internal const string Refusal = "Free fly is disabled on this server.";

        /// <summary>
        /// Whether this client may not use free fly. Singleplayer and a listen host own the world, so there is
        /// nobody to keep anything from there.
        /// </summary>
        internal static bool BlockedOnThisClient() {
            if (!ValConfig.BlockFreeFly.Value) { return false; }
            ZNet net = ZNet.instance;
            if (net == null || net.IsServer()) { return false; }
            return !net.LocalPlayerIsAdminOrHost();
        }
    }

    /// <summary>
    /// Refuses the freefly command. On ConsoleCommand.RunAction for the same reason as printseeds: the console,
    /// chat and a bound key all end there. ffsmooth is left alone, since it only sets the smoothing of a camera
    /// that cannot be switched on.
    /// </summary>
    [HarmonyPatch(typeof(global::Terminal.ConsoleCommand), nameof(global::Terminal.ConsoleCommand.RunAction))]
    internal static class ConsoleCommand_RunAction_BlockFreeFly {

        [HarmonyPrefix]
        private static bool Prefix(global::Terminal.ConsoleCommand __instance, global::Terminal.ConsoleEventArgs args) {
            if (!string.Equals(__instance.Command, "freefly", StringComparison.OrdinalIgnoreCase)) { return true; }
            if (!FreeFlyCamera.BlockedOnThisClient()) { return true; }

            args?.Context?.AddString(FreeFlyCamera.Refusal);
            return false;
        }
    }

    /// <summary>
    /// Drops the camera out of free fly whenever it is in it and may not be. UpdateFreeFly only runs while free
    /// fly is on, so this costs nothing the rest of the time. Skipping it leaves the camera where it was for that
    /// one frame; the next frame takes the ordinary path, which puts the camera and field of view back.
    /// </summary>
    [HarmonyPatch(typeof(GameCamera), "UpdateFreeFly")]
    internal static class GameCamera_UpdateFreeFly_BlockFreeFly {

        [HarmonyPrefix]
        private static bool Prefix(GameCamera __instance) {
            if (!FreeFlyCamera.BlockedOnThisClient()) { return true; }

            __instance.m_freeFly = false;
            if (MessageHud.instance != null) {
                MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, FreeFlyCamera.Refusal);
            }
            return false;
        }
    }
}
