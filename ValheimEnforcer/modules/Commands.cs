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
        }

        internal class ListPlayers : ConsoleCommand {
            public override string Name => "List-Players";
            public override string Help => "List-Players - Provides a full list of all accounts and Player names stored.";

            public override void Run(string[] args) {
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
            public override string Name => "List-Confiscated";
            public override string Help => "Gets a list of confiscated items, specific to a player/character. Format: list-confiscated 99999999 TerryTheTerrible";

            public override void Run(string[] args) {
                if (args.Length != 2) {
                    Logger.LogInfo("Account ID and playername are required. Ensure your command follows the format: list-confiscated 99999999 TerryTheTerrible");
                    return;
                }
                string account = args[0];
                string username = args[1];
                string targetPath = Path.Combine(Paths.ConfigPath, ValConfig.ValheimEnforcer, ValConfig.CharacterFolder, account, username);
                Logger.LogInfo($"Checking for {targetPath}");
                // Help find the user/character
                if (Directory.Exists(targetPath) == false) {
                    string accountPath = Path.Combine(Paths.ConfigPath, ValConfig.ValheimEnforcer, ValConfig.CharacterFolder, account);
                    bool accountExists = Directory.Exists(accountPath);
                    Logger.LogWarning($"Not found as specified. Account found? {accountExists} Character found? false");
                    if (accountExists == true) {
                        Logger.LogWarning("Found the following characters with that specified account:");
                        List<string> characters = Directory.GetFiles(accountPath).ToList();
                        foreach (string characterFile in characters) {
                            Logger.LogWarning($"  {characterFile.Split('/').Last()}");
                        }
                    }
                    return;
                }

                DataObjects.Character character = DataObjects.yamldeserializer.Deserialize<DataObjects.Character>(File.ReadAllText(targetPath));
                if (character.ConfiscatedItems.Count == 0) {
                    Logger.LogInfo("Player does not have any confiscated items.");
                    return;
                }
                Logger.LogInfo($"Found {character.ConfiscatedItems.Count} confiscated items.");
                foreach(DataObjects.PackedItem confiscated in character.ConfiscatedItems) {
                    Logger.LogInfo($"{confiscated.prefabName} x {confiscated.m_stack}");
                }
            }
        }

        internal class RestorePlayerConfiscatedItems : ConsoleCommand {
            public override string Name => "Retrieve-Confiscated";
            public override string Help => "Gives you player confiscated items, use either item prefab or 'all'. Format: retrieve-confiscated 99999999 TerryTheTerrible all";

            public override void Run(string[] args) {
                if (args.Length != 3) {
                    Logger.LogInfo("Account ID and playername are required. Ensure your command follows the format: list-confiscated 99999999 TerryTheTerrible");
                    return;
                }
                string account = args[0];
                string username = args[1];
                string prefab = args[2];
                string targetPath = Path.Combine(Paths.ConfigPath, ValConfig.ValheimEnforcer, ValConfig.CharacterFolder, account, username);
                Logger.LogInfo($"Checking for {targetPath}");
                // Help find the user/character
                if (Directory.Exists(targetPath) == false) {
                    string accountPath = Path.Combine(Paths.ConfigPath, ValConfig.ValheimEnforcer, ValConfig.CharacterFolder, account);
                    bool accountExists = Directory.Exists(accountPath);
                    Logger.LogWarning($"Not found as specified. Account found? {accountExists} Character found? false");
                    if (accountExists == true) {
                        Logger.LogWarning("Found the following characters with that specified account:");
                        List<string> characters = Directory.GetFiles(accountPath).ToList();
                        foreach (string characterFile in characters) {
                            Logger.LogWarning($"  {characterFile.Split('/').Last()}");
                        }
                    }
                    return;
                }

                DataObjects.Character character = DataObjects.yamldeserializer.Deserialize<DataObjects.Character>(File.ReadAllText(targetPath));
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
            }
        }
    }
}
