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

        [HarmonyPatch(typeof(Player), nameof(Player.Save))]
        public static class SaveSync {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.Last)]
            private static void PlayerSave(Player __instance) {
                CharacterManager.SavePlayerCharacter(__instance);
            }
        }

        //[HarmonyPatch(typeof(ZNet), nameof(ZNet.ShutdownWithoutSave))]
        //public static class SaveSyncForShutdown {
        //    [HarmonyPrefix]
        //    [HarmonyPriority(Priority.Last)]
        //    private static void PlayerSave() {
        //        CharacterManager.SavePlayerCharacter(Player.m_localPlayer);
        //    }
        //}

        [HarmonyPatch(typeof(Game), nameof(Game.Logout))]
        public static class SaveSyncForLogout {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static void PlayerSave() {
                if (Player.m_localPlayer != null) {
                    CharacterManager.SavePlayerCharacter(Player.m_localPlayer);
                } else {
                    Logger.LogWarning("Player.m_localPlayer was null during logout. Skipping character sync.");
                }
                
            }
        }

        // Maybe add specific save handling around tombstones?
    }
}
