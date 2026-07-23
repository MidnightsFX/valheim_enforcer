using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine.SceneManagement;
using ValheimEnforcer.common;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.modules.character {
    internal static class CharacterPatches {

        [HarmonyPatch(typeof(Game), nameof(Game.SpawnPlayer))]
        public static class LoadAndValidatePlayerPatch {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.First)]
            private static void PlayerSpawn(Game __instance) {
                CharacterManager.LoadAndValidatePlayer(Player.m_localPlayer);
            }
        }

        [HarmonyPatch(typeof(Game), nameof(Game.Logout))]
        public static class ClearPlayerCharacterOnLogout {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            private static void Postfix() {
                if (CharacterManager.PlayerCharacter != null) {
                    Logger.LogDebug($"Clearing selected save profile for {CharacterManager.PlayerCharacter.Name} on logout.");
                    CharacterManager.PlayerCharacter = null;
                }
                CharacterManager.LogoutInProgress = false;
            }
        }

        [HarmonyPatch(typeof(Player))]
        public static class LoadPlayerCustomData {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.First)]
            [HarmonyPatch(nameof(Player.Load))]
            static void Postfix(Player __instance) {
                string playerID;
                string PlayerName;
                DataObjects.Character savableChar = null;
                if (CharacterManager.PlayerCharacter != null) {
                    savableChar = CharacterManager.PlayerCharacter;
                    playerID = CharacterManager.PlayerCharacter.HostID;
                    PlayerName = CharacterManager.PlayerCharacter.Name;
                } else {
                    playerID = CharacterManager.GetPlayerID(__instance);
                    PlayerName = __instance.GetPlayerName();
                }
                if (CharacterManager.PlayerCharacter == null) {
                    savableChar = ValConfig.LoadCharacterFromSave(playerID, PlayerName);
                }

                if (savableChar == null) {
                    if (ValConfig.PreventExternalCustomDataChanges.Value) {
                        if (ValConfig.newCharacterClearCustomData.Value) { __instance.m_customData.Clear(); }
                    }
                } else {
                    if (ValConfig.PreventExternalCustomDataChanges.Value) {
                        __instance.m_customData = savableChar.PlayerCustomData;
                        Logger.LogDebug("Set player custom data.");
                    }
                }
            }
        }

        // NOTE: full character saves are no longer triggered by the vanilla Player.Save (world/profile
        // autosave). Persistence map:
        //  - End-of-session: SaveSyncForShutdown (Game.Shutdown prefix) writes a full save recorded Clean.
        //    Game.Shutdown is the choke point both exit paths funnel through — menu logout
        //    (Game.Logout -> ContinueLogout -> Shutdown) AND quit-to-desktop / Alt+F4
        //    (Game.OnApplicationQuit -> Shutdown, which never calls Game.Logout).
        //  - Mid-session (networked clients only): routine changes stream up incrementally through
        //    CharacterDeltaTracker and the server pulls periodic full saves on its own schedule
        //    (FullSyncScheduler); both are recorded DirtyDisconnect.
        //  - Join: LoadAndValidatePlayer still pushes a full save directly.

        // Drain the async character persistence store before the server stops so no queued save is lost.
        // No-op on clients (the store is only used server-side, in disk storage mode).
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
        public static class FlushCharacterStoreOnShutdown {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static void Prefix(ZNet __instance) {
                if (__instance != null && __instance.IsServer()) {
                    CharacterStore.Shutdown();
                }
            }
        }

        [HarmonyPatch(typeof(Game), nameof(Game.Shutdown))]
        public static class SaveSyncForShutdown {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static void PlayerSave(Game __instance, bool saveWorld) {
                // Shutdown can run twice (e.g. Logout then OnApplicationQuit); vanilla ignores the second
                // call via m_shuttingDown and so do we.
                if (__instance.m_shuttingDown) { return; }
                // Stay in lockstep with vanilla: when it skips SavePlayerProfile (disk-space decline) the
                // enforcer save must stay equally stale, or the two stores diverge and cause false
                // confiscation/restoration on the next join.
                if (!saveWorld) { return; }
                if (Player.m_localPlayer == null) { return; } // dedicated server
                // Mark the shutdown so this save — and any save the vanilla Shutdown body triggers afterwards —
                // records the character as cleanly disconnected. Reset by ClearPlayerCharacterOnLogout on a
                // return-to-menu, and by LoadAndValidatePlayer on the next spawn.
                CharacterManager.LogoutInProgress = true;
                CharacterManager.SavePlayerCharacter(Player.m_localPlayer);
            }
        }

        // Maybe add specific save handling around tombstones?
    }
}
