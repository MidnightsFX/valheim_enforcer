using HarmonyLib;
using Jotunn;
using Splatform;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
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
            Logger.LogDebug("Set character from Saved server data");
            PlayerCharacter = character;
        }

        internal static string GetPlayerID(Player player) {
            List<ZNet.PlayerInfo> zplayerInfo = ZNet.instance.GetPlayerList();
            string selectedID = "";
            foreach (ZNet.PlayerInfo playerInfo in zplayerInfo) {
                Logger.LogDebug($"Checking player {playerInfo.m_characterID} with ID {playerInfo.m_userInfo.m_id.m_userID} against local player {player.m_nview.GetZDO().m_uid}");
                if (playerInfo.m_characterID == player.m_nview.GetZDO().m_uid) {
                    selectedID = playerInfo.m_userInfo.m_id.m_userID;
                    break;
                }
            }

            if (selectedID.Length < 1) {
                string playerName = player.GetPlayerName();
                foreach (ZNet.PlayerInfo playerInfo in zplayerInfo) {
                    if (playerInfo.m_name == playerName) {
                        selectedID = playerInfo.m_userInfo.m_id.m_userID;
                        Logger.LogDebug($"Matched player {playerName} by name to ID {selectedID}");
                        break;
                    }
                }
            }

            if (selectedID.Length < 1) {
                Logger.LogWarning($"Failed to find matching player ID for local player {player.GetPlayerName()}. Defaulting to ZDO UID as player ID.");
                selectedID = player.m_nview.GetZDO().m_uid.ToString();
            }
            if (selectedID.Contains(":")) {
                Logger.LogDebug($"Player ID contained invalid character : removing.");
                selectedID = selectedID.Split(':')[0];
            }
            return selectedID;
        }

        private static void LoadAndValidatePlayer(Player player) {

            string playerID;
            string PlayerName;
            if (PlayerCharacter != null) {
                playerID = PlayerCharacter.HostID;
                PlayerName = PlayerCharacter.Name;
            } else {
                playerID = GetPlayerID(player);
                PlayerName = player.GetPlayerName();
            }
            

            Logger.LogInfo($"Player {PlayerName} with ID {playerID} validating character data.");
            // If the character has already been connected to the server its data was already transferred during the connection process.
            DataObjects.Character savableChar = PlayerCharacter;

            if (savableChar == null) {

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
                    if (ValConfig.NewCharacterSetSkillsToZero.Value) {
                        Logger.LogInfo("Setting new character skills to zero.");
                        
                        foreach (Skills.SkillType skillKey in savableChar.SkillLevels.Keys.ToList()) {
                            savableChar.SkillLevels[skillKey] = 0;
                        }
                    }

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
                            player.UnequipItem(item); // Ensure removed items are unequipped as they will ghost otherwise
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
                List<ItemDrop.ItemData> removeItems = new List<ItemDrop.ItemData>();
                player.m_inventory.GetAllItems().ForEach(item => {
                    Logger.LogDebug($"Checking player item: {item.m_dropPrefab.name}");
                    if (!savableChar.PlayerItems.Any(savedItem => savedItem.prefabName == item.m_dropPrefab.name && savedItem.m_stack == item.m_stack)) {
                        Logger.LogInfo($"Removing non-tracked item {item.m_dropPrefab.name}x{item.m_stack} from player {savableChar.Name}");
                        savableChar.AddConfiscatedItem(item);
                        removeItems.Add(item);
                    }
                });
                foreach (ItemDrop.ItemData item in removeItems) {
                    player.UnequipItem(item); // Ensure removed items are unequipped as they will ghost otherwise
                    player.GetInventory().RemoveItem(item);
                }
            }
            if (ValConfig.AddMissingItemsFromPlayerServerSave.Value) {
                Logger.LogDebug("Checking to restore player items.");
                List<Tuple<string, int>> prefablist = new List<Tuple<string, int>>();
                foreach(ItemDrop.ItemData item in player.m_inventory.GetAllItems()) {
                    prefablist.Add(new Tuple<string, int>(item.m_dropPrefab.name, item.m_stack));
                }
                foreach (DataObjects.PackedItem item in savableChar.PlayerItems) {
                    Tuple<string, int> searcher = new Tuple<string, int>(item.prefabName, item.m_stack);
                    if (!prefablist.Contains(searcher)) {
                        Logger.LogInfo($"Adding missing item to players inventory: {item.prefabName}x{item.m_stack}");
                        item.AddToInventory(player.GetInventory(), false);
                    }
                }
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
                if (__instance == null || SceneManager.GetActiveScene().name.Equals("main") == false) { return; }
                string playerID = "";
                string PlayerName = "";
                DataObjects.Character savableChar = null;
                if (PlayerCharacter != null) {
                    savableChar = PlayerCharacter;
                    playerID = PlayerCharacter.HostID;
                    PlayerName = PlayerCharacter.Name;
                } else {
                    playerID = GetPlayerID(__instance);
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
                    if (ValConfig.PreventExternalCustomDataChanges.Value) {
                        savableChar.PlayerCustomData = __instance.m_customData;
                    }
                } else {
                    Logger.LogDebug($"Existing character data found for player {PlayerName} with ID {playerID}. Updating character data with current player information.");
                    savableChar.SkillLevels = __instance.GetSkills().GetSkillList().ToDictionary(skill => skill.m_info.m_skill, skill => skill.m_level);
                    Logger.LogDebug($"Updated player skills for {PlayerName} with ID {playerID}.");
                    if (ValConfig.PreventExternalCustomDataChanges.Value) {
                        savableChar.PlayerCustomData = __instance.m_customData;
                        Logger.LogDebug("Updated player custom data.");
                    }
                    savableChar.PlayerItems.Clear();
                    // Add all of the players current items
                    foreach (ItemDrop.ItemData item in __instance.GetInventory().GetAllItems().ToList()) {
                        savableChar.AddItemToPlayerItems(item);
                    }
                    Logger.LogDebug($"Updated player Items for {PlayerName} with ID {playerID}.");
                }

                if (savableChar == null) {
                    Logger.LogWarning("Savable character was null, not sending network updates.");
                    return;
                }

                ValConfig.WriteCharacterToFile(playerID, savableChar);

                if (ZNet.instance != null && ZNet.instance.GetServerPeer() != null) {
                    Logger.LogDebug("Sending updated character data to server.");
                    ValConfig.CharacterSaveRPC.SendPackage(ZNet.instance.GetServerPeer().m_uid, ValConfig.SendCharacterAsZpackage(savableChar));
                }
            }
        }

        [HarmonyPatch(typeof(Game), nameof(Game.SpawnPlayer))]
        public static class LoadAndValidatePlayerPatch {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.First)]
            private static void PlayerSpawn(Game __instance) {
                LoadAndValidatePlayer(Player.m_localPlayer);
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
                if (PlayerCharacter != null) {
                    savableChar = PlayerCharacter;
                    playerID = PlayerCharacter.HostID;
                    PlayerName = PlayerCharacter.Name;
                } else {
                    playerID = GetPlayerID(__instance);
                    PlayerName = __instance.GetPlayerName();
                }
                if (PlayerCharacter == null) {
                    savableChar = ValConfig.LoadCharacterFromFile(playerID, PlayerName);
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
    }
}
