using BepInEx;
using Jotunn.Entities;
using Jotunn.Managers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules {
    internal static class TerminalCommands {
        internal static void AddCommands() {
            CommandManager.Instance.AddConsoleCommand(new ListPlayers());
            CommandManager.Instance.AddConsoleCommand(new ListPlayerConfiscatedItems());
            CommandManager.Instance.AddConsoleCommand(new RestorePlayerConfiscatedItems());
            CommandManager.Instance.AddConsoleCommand(new ReturnPlayerConfiscatedItems());
        }

        internal class ListPlayers : ConsoleCommand {
            public override string Name => "Enforcer-List-Players";
            public override bool IsCheat => true;
            public override string Help => "Enforcer-List-Players - Provides a full list of all accounts and Player names stored.";

            public override void Run(string[] args) {
                if (ValConfig.InternalStorageMode.Value) {
                    Dictionary<string, List<string>> accountCharacters = InternalDataStore.GetAccountRegistry();
                    foreach(KeyValuePair<string, List<string>> account in accountCharacters) {
                        Logger.LogInfo($"Account:{account.Key}");
                        foreach (string character in account.Value) {
                            Logger.LogInfo($"   {character}");
                        }
                    }
                    return;
                }

                List<string> storedAccounts = Directory.GetFiles(Path.Combine(Paths.ConfigPath, ValConfig.ValheimEnforcer, ValConfig.CharacterFolder)).ToList();
                foreach(string account in storedAccounts) {
                    List<string> characters = Directory.GetFiles(account).ToList();
                    Logger.LogInfo($"Account:{account.Split('/').Last()}");
                    foreach(string characterFile in characters) {
                        Logger.LogInfo($"   {characterFile.Split('/').Last()}");
                    }
                }
            }
        }

        internal class ListPlayerConfiscatedItems : ConsoleCommand {
            public override string Name => "Enforcer-List-Confiscated";
            public override bool IsCheat => true;
            public override string Help => "Gets a list of confiscated items, specific to a player/character. Format: enforcer-list-confiscated 99999999 TerryTheTerrible";

            public override void Run(string[] args) {
                if (args.Length != 2) {
                    Logger.LogInfo("Account ID and playername are required. Ensure your command follows the format: enforcer-list-confiscated 99999999 TerryTheTerrible");
                    return;
                }
                string account = args[0];
                string username = args[1];

                DataObjects.Character character = ValConfig.LoadCharacterFromSave(account, username);
                if (character.ConfiscatedItems.Count == 0) {
                    Logger.LogInfo("Player does not have any confiscated items.");
                    return;
                }
                Logger.LogInfo($"Found {character.ConfiscatedItems.Count} confiscated items.");
                foreach(DataObjects.PackedItem confiscated in character.ConfiscatedItems) {
                    Logger.LogInfo($"  {confiscated.prefabName} x {confiscated.m_stack}");
                }
            }
        }

        internal class ClearPlayerConfiscatedItems : ConsoleCommand {
            public override string Name => "Enforcer-Clear-Confiscated";
            public override bool IsCheat => true;
            public override string Help => "Clears any confiscated items listed for the specified player Format: enforcer-retrieve-confiscated 99999999 TerryTheTerrible all";

            public override void Run(string[] args) {
                if (args.Length != 3) {
                    Logger.LogInfo("Account ID and playername are required. Ensure your command follows the format: enforcer-retrieve-confiscated 99999999 TerryTheTerrible all");
                    return;
                }
                string account = args[0];
                string username = args[1];
                string prefab = args[2];

                DataObjects.Character character = ValConfig.LoadCharacterFromSave(account, username);
                // Help find the user/character
                if (character == null) {
                    Logger.LogInfo("Character was not found for the specified account.");
                    return;
                }
                if (character.ConfiscatedItems.Count == 0) {
                    Logger.LogInfo("Player does not have any confiscated items.");
                    return;
                }
                character.ConfiscatedItems.Clear();
                ValConfig.WritePlayerCharacterToSave(account, character);

                Logger.LogInfo($"Found {character.ConfiscatedItems.Count} confiscated items.");
                if (string.Compare(prefab, "all", true) == 0) {
                    Logger.LogInfo("Providing all confiscated items.");
                    foreach (var confiscatedItem in character.ConfiscatedItems) {
                        confiscatedItem.AddToInventory(Player.m_localPlayer.m_inventory, false);
                    }
                    character.ConfiscatedItems.Clear();
                    return;
                }

                foreach (DataObjects.PackedItem confiscated in character.ConfiscatedItems) {
                    List<string> targetItems = prefab.Split(',').ToList();
                    foreach (var confiscatedItem in character.ConfiscatedItems) {
                        if (targetItems.Contains(confiscatedItem.prefabName) == false) { continue; }
                        Logger.LogInfo($"Providing {confiscatedItem.prefabName}");
                        confiscatedItem.AddToInventory(Player.m_localPlayer.m_inventory, false);
                    }
                }
            }
        }

        internal class RestorePlayerConfiscatedItems : ConsoleCommand {
            public override string Name => "Enforcer-Admin-Take-Confiscated";
            public override bool IsCheat => true;
            public override string Help => "Gives you player confiscated items, use either item prefab or 'all'. Format: enforcer-admin-take-confiscated 99999999 TerryTheTerrible all";

            public override void Run(string[] args) {
                if (args.Length != 3) {
                    Logger.LogInfo("Account ID and playername are required. Ensure your command follows the format: enforcer-admin-take-confiscated 99999999 TerryTheTerrible all");
                    return;
                }
                string account = args[0];
                string username = args[1];
                string prefab = args[2];

                // TODO: this likely needs to PULL the value from the server for flatfiles
                DataObjects.Character character = ValConfig.LoadCharacterFromSave(account, username);
                // Help find the user/character
                if (character == null) {
                    Logger.LogInfo("Character was not found for the specified account.");
                    return;
                }
                if (character.ConfiscatedItems.Count == 0) {
                    Logger.LogInfo("Player does not have any confiscated items.");
                    return;
                }
                Logger.LogInfo($"Found {character.ConfiscatedItems.Count} confiscated items.");
                if (string.Compare(prefab, "all", true) == 0) {
                    Logger.LogInfo("Providing all confiscated items.");
                    foreach (var confiscatedItem in character.ConfiscatedItems) {
                        confiscatedItem.AddToInventory(Player.m_localPlayer.m_inventory, false);
                    }
                    character.ConfiscatedItems.Clear();
                    return;
                }

                foreach (DataObjects.PackedItem confiscated in character.ConfiscatedItems) {
                    List<string> targetItems = prefab.Split(',').ToList();
                    foreach (var confiscatedItem in character.ConfiscatedItems) {
                        if (targetItems.Contains(confiscatedItem.prefabName) == false) { continue; }
                        Logger.LogInfo($"Providing {confiscatedItem.prefabName}");
                        confiscatedItem.AddToInventory(Player.m_localPlayer.m_inventory, false);
                    }
                }
                ValConfig.WritePlayerCharacterToSave(character.HostID, character);
            }
        }

        internal class ReturnPlayerConfiscatedItems : ConsoleCommand {
            public override string Name => "Enforcer-Return-Confiscated";
            public override bool IsCheat => true;
            public override string Help => "Sends confiscated items to a connected player via RPC. Use 'all' or comma-separated prefab names. Format: Enforcer-Return-Confiscated 99999999 TerryTheTerrible all";

            public override void Run(string[] args) {
                if (args.Length != 3) {
                    Logger.LogInfo("Account ID, player name, and item filter are required. Ensure your command follows the format: Enforcer-Return-Confiscated 99999999 TerryTheTerrible all");
                    return;
                }
                string account = args[0];
                string username = args[1];
                string prefabFilter = args[2];

                DataObjects.Character character = ValConfig.LoadCharacterFromSave(account, username);
                if (character == null) {
                    Logger.LogInfo("Character was not found for the specified account.");
                    return;
                }
                if (character.ConfiscatedItems.Count == 0) {
                    Logger.LogInfo("Player does not have any confiscated items.");
                    return;
                }

                List<DataObjects.PackedItem> itemsToReturn;
                if (string.Compare(prefabFilter, "all", true) == 0) {
                    itemsToReturn = new List<DataObjects.PackedItem>(character.ConfiscatedItems);
                    character.ConfiscatedItems.Clear();
                } else {
                    List<string> targetPrefabs = prefabFilter.Split(',').Select(s => s.Trim()).ToList();
                    itemsToReturn = character.ConfiscatedItems.Where(i => targetPrefabs.Contains(i.prefabName)).ToList();
                    character.ConfiscatedItems.RemoveAll(i => targetPrefabs.Contains(i.prefabName));
                }

                if (itemsToReturn.Count == 0) {
                    Logger.LogInfo("No matching confiscated items found for the specified filter.");
                    return;
                }

                if (Player.m_localPlayer != null && Player.m_localPlayer.GetPlayerName() == username) {
                    Logger.LogInfo("Local player is the target, returning player items.");
                    foreach (DataObjects.PackedItem confiscated in itemsToReturn) {
                        Logger.LogInfo($"Providing {confiscated.prefabName}");
                        confiscated.AddToInventory(Player.m_localPlayer.m_inventory, false);
                    }
                    ValConfig.WritePlayerCharacterToSave(account, character);
                    return;
                }

                // Find the target peer by account ID and character name
                ZNetPeer targetPeer = null;
                if (ZNet.instance != null) {
                    foreach (ZNetPeer peer in ZNet.instance.GetPeers()) {
                        if (peer.m_playerName != username) { continue; }
                        string peerAccount = peer.m_socket.GetEndPointString();
                        if (peerAccount.Contains(":")) { peerAccount = peerAccount.Split(':')[0]; }
                        if (peerAccount == account) {
                            targetPeer = peer;
                            break;
                        }
                    }
                    // Fallback: match by character name alone
                    if (targetPeer == null) {
                        foreach (ZNetPeer peer in ZNet.instance.GetPeers()) {
                            if (peer.m_playerName == username) {
                                targetPeer = peer;
                                break;
                            }
                        }
                    }
                }

                if (targetPeer == null) {
                    Logger.LogInfo($"Player {username} is not currently connected. Moving items to player inventory save so they are restored on next login.");
                    foreach (DataObjects.PackedItem item in itemsToReturn) {
                        character.PlayerItems.Add(item);
                    }
                    ValConfig.WritePlayerCharacterToSave(account, character);
                    return;
                }

                ValConfig.WritePlayerCharacterToSave(account, character);
                Logger.LogInfo($"Sending {itemsToReturn.Count} confiscated item(s) to player {username}.");
                ZPackage package = new ZPackage();
                package.Write(DataObjects.yamlserializer.Serialize(itemsToReturn));
                ValConfig.ReturnConfiscatedItemsRPC.SendPackage(targetPeer.m_uid, package);
            }
        }
    }
}
