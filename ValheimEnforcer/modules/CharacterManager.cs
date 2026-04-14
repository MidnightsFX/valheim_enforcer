using HarmonyLib;
using Jotunn;
using Mono.Security.Interface;
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
using static ValheimEnforcer.common.DataObjects;

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
                Logger.LogInfo($"No existing character data found for player {PlayerName} with ID {playerID}. Attempting to load from local save.");
                savableChar = ValConfig.LoadCharacterFromSave(playerID, PlayerName);

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
                            if (item.m_quality > 1) {
                                Logger.LogInfo($"Removing high quality item {item.m_dropPrefab.name}x{item.m_stack} with quality {item.m_quality} from new player {savableChar.Name}");
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
                        if (ValConfig.ValidateItemCustomData.Value) {
                            item.m_customData.Clear(); // Clear custom data on new characters to prevent exploits of starting with custom data items
                        }
                    }
                }
            }

            if (ValConfig.RemoveNontrackedItemsFromJoiningPlayers.Value) {
                List<ItemDrop.ItemData> removeItems = new List<ItemDrop.ItemData>();
                Dictionary<ItemDrop.ItemData, ItemValidatorResult> ValidatorResults = ValidateItems(player.m_inventory.GetAllItems(), savableChar);

                foreach (KeyValuePair<ItemDrop.ItemData, ItemValidatorResult> eval in ValidatorResults) {
                    if (eval.Value.Validated == false) {
                        Logger.LogInfo($"Removing item {eval.Key.m_dropPrefab.name}x{eval.Key.m_stack} from player {savableChar.Name}. Validation message: {eval.Value.ValidationMessage}");
                        savableChar.AddConfiscatedItem(eval.Key);
                        player.UnequipItem(eval.Key);
                        player.GetInventory().RemoveItem(eval.Key);
                    }
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
                        item.AddToInventory(player, false);
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
            ValConfig.WritePlayerCharacterToSave(playerID, savableChar);

            if (ZNet.instance.GetServerPeer() != null) {
                ValConfig.CharacterSaveRPC.SendPackage(ZNet.instance.GetServerPeer().m_uid, ValConfig.SendCharacterAsZpackage(savableChar));
            }
        }

        // Validate Item, stacksize, custom data, and quality
        internal static Dictionary<ItemDrop.ItemData, ItemValidatorResult> ValidateItems(List<ItemDrop.ItemData> playerItems, DataObjects.Character savedChar) {
            Dictionary<ItemDrop.ItemData, ItemValidatorResult> validationResults = new Dictionary<ItemDrop.ItemData, ItemValidatorResult>();
            Logger.LogInfo($"Player Items: {playerItems.Count} | SavedCharacter Items: {savedChar.PlayerItems.Count}");
            foreach (ItemDrop.ItemData item in playerItems) {
                Logger.LogDebug($"Checking player item: {item.m_dropPrefab.name}");

                ValidationSummary ItemValidationSummary = new DataObjects.ValidationSummary();
                validationResults.Add(item, new ItemValidatorResult() {
                    CharacterItemRef = item,
                });

                foreach (DataObjects.PackedItem savedItem in savedChar.PlayerItems) {
                    //Logger.LogInfo($"Comparing {savedItem.prefabName} == {item.m_dropPrefab.name} && {savedItem.m_stack} == {item.m_stack}");
                    if (savedItem.prefabName == item.m_dropPrefab.name && savedItem.m_stack == item.m_stack) {
                        ItemValidationSummary.NameAndStackMatch = true;
                        //Logger.LogDebug($"Matched {savedItem.prefabName} s:{savedItem.m_stack} q:{savedItem.m_quality} d:{savedItem.m_durability}");

                        
                        int quality = savedItem.m_quality;
                        if (quality == 0) { quality = 1; }
                        //Logger.LogDebug($"Checking Quality: {quality} == {item.m_quality}");
                        if (quality == item.m_quality) {
                            ItemValidationSummary.QualityMatch = true;
                        }

                        // Validate item durability
                        if (ValConfig.ValidateItemDurability.Value && savedItem.m_durability != item.m_durability) {
                            ItemValidationSummary.DurabilityMatch = false;
                            Logger.LogDebug($"Item {item.m_dropPrefab.name} durability mismatch. Expected {savedItem.m_durability} got {item.m_durability}");
                        } else {
                            ItemValidationSummary.DurabilityMatch = true;
                        }

                        // Check all of the custom data
                        ItemValidationSummary.CustomDataMatch = true;
                        if (ValConfig.ValidateItemCustomData.Value) {
                            foreach (KeyValuePair<string, string> playerItemKVP in item.m_customData) {
                                if (savedItem.m_customdata.ContainsKey(playerItemKVP.Key) && savedItem.m_customdata[playerItemKVP.Key] != playerItemKVP.Value) {
                                    ItemValidationSummary.CustomDataMatch = false;
                                    Logger.LogDebug($"Item {item.m_dropPrefab.name} custom data mismatch on key {playerItemKVP.Key}. Expected {savedItem.m_customdata[playerItemKVP.Key]} got {playerItemKVP.Value}");
                                }
                            }
                        }

                        if (ItemValidationSummary.IsValid()) {
                            Logger.LogDebug($"Item {item.m_dropPrefab.name} passed validation checks against saved character data.");
                            validationResults[item].SavedItemRef = savedItem;
                            validationResults[item].Validated = true;
                            break; // if we found a match skip remaining iterations of saved items
                        }
                    }
                }

                validationResults[item].ValidationResult = ItemValidationSummary;
                if (ItemValidationSummary.IsValid() == false) {
                    validationResults[item].ValidationMessage = $"Item {item.m_dropPrefab.name} failed validation checks against saved character data. " +
                        $"Stack Match: {ItemValidationSummary.NameAndStackMatch}, " +
                        $"Quality Match: {ItemValidationSummary.QualityMatch}, " +
                        $"Custom Data Match: {ItemValidationSummary.CustomDataMatch}, " +
                        $"Durability Match: {ItemValidationSummary.DurabilityMatch}";
                }
            }

            return validationResults;
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
                    savableChar = ValConfig.LoadCharacterFromSave(playerID, PlayerName);
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

                ValConfig.WritePlayerCharacterToSave(playerID, savableChar);

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

        [HarmonyPatch(typeof(Game), nameof(Game.Logout))]
        public static class ClearPlayerCharacterOnLogout {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            private static void Postfix() {
                if (PlayerCharacter != null) {
                    Logger.LogDebug($"Clearing selected save profile for {PlayerCharacter.Name} on logout.");
                    PlayerCharacter = null;
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
                if (PlayerCharacter != null) {
                    savableChar = PlayerCharacter;
                    playerID = PlayerCharacter.HostID;
                    PlayerName = PlayerCharacter.Name;
                } else {
                    playerID = GetPlayerID(__instance);
                    PlayerName = __instance.GetPlayerName();
                }
                if (PlayerCharacter == null) {
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
    }
}
