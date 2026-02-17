using HarmonyLib;
using Jotunn;
using Splatform;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules {
    internal static class CharacterManager {
        internal static DataObjects.Character PlayerCharacter = null;
        internal static List<string> staringAllowedPrefabs = new List<string>() {
            "ArmorRagsChest",
            "ArmorRagsLegs",
            "Torch",
        };

        internal static void SetPlayerCharacter(DataObjects.Character character) {
            if (character == null) { return; }
            PlayerCharacter = character;
        }

        private static void LoadAndValidatePlayer(Player player) {
            string playerID = $"{player.GetPlayerID()}";
            string PlayerName = player.GetPlayerName();

            Logger.LogInfo($"Player {PlayerName} with ID {playerID} validating character data.");
            // If the character has already been connected to the server its data was already transferred during the connection process.
            DataObjects.Character savableChar = PlayerCharacter;

            if (PlayerCharacter == null) {
                // Gate loading local character based on if there is no dedicated server?
                Logger.LogInfo($"No existing character data found for player {PlayerName} with ID {playerID}. Attempting to load from file.");
                savableChar = ValConfig.LoadCharacterFromFile(playerID, PlayerName);

                // Nothing to load from file, create a new character and assign current player data
                if (savableChar == null) {
                    if (ValConfig.NewCharactersSkillsCleared.Value) {
                        Logger.LogInfo($"New character save for player {PlayerName} with ID {playerID}, skills set to zero.");
                        player.GetSkills().CheatResetSkill("all");
                    }
                    savableChar = new DataObjects.Character() {
                        Name = player.GetPlayerName(),
                        HostID = playerID,
                        SkillLevels = player.GetSkills().GetSkillList().ToDictionary(skill => skill.m_info.m_skill, skill => skill.m_level),
                    };
                    if (ValConfig.NewCharactersRemoveExtraItems.Value) {
                        Logger.LogInfo($"New character save for player {PlayerName} with ID {playerID}, checking to removing non-starter items.");
                        List<ItemDrop.ItemData> removeItems = new List<ItemDrop.ItemData>();
                        player.m_inventory.GetAllItems().ForEach(item => {
                            if (!staringAllowedPrefabs.Contains(item.m_dropPrefab.name)) {
                                Logger.LogInfo($"Removing non-starter item {item.m_dropPrefab.name}x{item.m_stack} from new player {savableChar.Name}");
                                savableChar.AddConfiscatedItem(item);
                                removeItems.Add(item);
                            }
                        });
                        foreach (ItemDrop.ItemData item in removeItems) {
                            player.GetInventory().RemoveItem(item);
                        }
                    }

                    // Add all of the players current items
                    foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItems().ToList()) {
                        savableChar.AddItemToPlayerItems(item);
                    }
                }
            }

            if (ValConfig.RemoveNontrackedItemsFromJoiningPlayers.Value) {
                player.m_inventory.GetAllItems().ForEach(item => {
                    if (!savableChar.PlayerItems.Any(savedItem => savedItem.prefabName == item.m_dropPrefab.name && savedItem.m_stack == item.m_stack)) {
                        Logger.LogInfo($"Removing non-tracked item {item.m_dropPrefab.name}x{item.m_stack} from player {savableChar.Name}");
                        savableChar.AddConfiscatedItem(item);
                        player.GetInventory().RemoveItem(item);
                    }
                });
            }
            Logger.LogDebug($"Validated player items.");

            if (ValConfig.PreventExternalSkillRaises.Value) {
                player.GetSkills().GetSkillList().ForEach(skill => {
                    if (savableChar.SkillLevels.TryGetValue(skill.m_info.m_skill, out float savedLevel)) {
                        if (skill.m_level > savedLevel) {
                            Logger.LogInfo($"Removing external skill gains for {skill.m_info.m_skill} from {savedLevel} to {skill.m_level} from player {savableChar.Name}");
                            skill.m_level = savedLevel;
                        }
                    }
                });
            }
            Logger.LogDebug($"Validated player skills.");

            PlayerCharacter = savableChar;
            ValConfig.WriteCharacterToFile(playerID, savableChar);

            if (ZNet.instance.GetServerPeer() != null) {
                ValConfig.CharacterSaveRPC.SendPackage(ZNet.instance.GetServerPeer().m_uid, ValConfig.SendCharacterAsZpackage(savableChar));
            }
        }

        // Move this off to its own repeating process? Recieve a unique seed from the server to offset save timer to prevent congestion?
        [HarmonyPatch(typeof(Player), nameof(Player.Save))]
        public static class SaveSync {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.High)]
            private static void PlayerSave(Player __instance) {
                if (__instance == null) { return; }
                string playerID = "";
                string PlayerName = "";
                DataObjects.Character savableChar = null;
                if (PlayerCharacter != null) {
                    savableChar = PlayerCharacter;
                    playerID = PlayerCharacter.HostID;
                    PlayerName = PlayerCharacter.Name;
                } else {
                    playerID = ZNet.instance.m_hostSocket.GetHostName();
                    PlayerName = __instance.GetPlayerName();
                }
                Logger.LogDebug($"Saving character for player {PlayerName} with id {playerID}");

                if (PlayerCharacter == null) {
                    savableChar = ValConfig.LoadCharacterFromFile(playerID, PlayerName);
                }
                 
                if (savableChar == null) {
                    Logger.LogWarning($"Attempted to save character for player {PlayerName} with ID {playerID} but no existing character data was found. Creating new character data.");
                    savableChar = new DataObjects.Character() {
                        Name = PlayerName,
                        HostID = playerID,
                        SkillLevels = __instance.GetSkills().GetSkillList().ToDictionary(skill => skill.m_info.m_skill, skill => skill.m_level),
                        ConfiscatedItems = null,
                    };
                    // Add all of the players current items
                    foreach (ItemDrop.ItemData item in __instance.GetInventory().GetAllItems().ToList()) {
                        savableChar.AddItemToPlayerItems(item);
                    }
                } else {
                    Logger.LogDebug($"Existing character data found for player {PlayerName} with ID {playerID}. Updating character data with current player information.");
                    savableChar.SkillLevels = __instance.GetSkills().GetSkillList().ToDictionary(skill => skill.m_info.m_skill, skill => skill.m_level);
                    Logger.LogDebug($"Updated player skills for {PlayerName} with ID {playerID}.");
                    savableChar.PlayerItems.Clear();
                    // Add all of the players current items
                    foreach (ItemDrop.ItemData item in __instance.GetInventory().GetAllItems().ToList()) {
                        savableChar.AddItemToPlayerItems(item);
                    }
                    Logger.LogDebug($"Updated player Items for {PlayerName} with ID {playerID}.");
                }
                ValConfig.WriteCharacterToFile(playerID, savableChar);

                if (ZNet.instance.GetServerPeer() != null) {
                    ValConfig.CharacterSaveRPC.SendPackage(ZNet.instance.GetServerPeer().m_uid, ValConfig.SendCharacterAsZpackage(savableChar));
                }
            }
        }

        [HarmonyPatch(typeof(Game), nameof(Game.SpawnPlayer))]
        public static class LoadAndValidatePlayerPatch {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.High)]
            private static void PlayerSpawn(Game __instance) {
                LoadAndValidatePlayer(Player.m_localPlayer);
            }
        }
    }
}
