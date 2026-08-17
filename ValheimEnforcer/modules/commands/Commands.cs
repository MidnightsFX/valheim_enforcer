using BepInEx;
using Jotunn.Entities;
using Jotunn.Managers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using ValheimEnforcer.common;
using ValheimEnforcer.modules.notifications;
using static Mono.Security.X509.X520;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.modules.commands {
    internal static class TerminalCommands {
        internal static void AddCommands() {
            CommandManager.Instance.AddConsoleCommand(new ListPlayers());
            //CommandManager.Instance.AddConsoleCommand(new ListPlayerConfiscatedItems());
            CommandManager.Instance.AddConsoleCommand(new ClearPlayerConfiscatedItems());
            CommandManager.Instance.AddConsoleCommand(new ReturnPlayerConfiscatedItems());
            CommandManager.Instance.AddConsoleCommand(new ImportServerCharacters());
            CommandManager.Instance.AddConsoleCommand(new TestNotification());
        }

        /// <summary>
        /// Posts one notification with stand-in data, so an admin editing Notifications.yaml can see the result
        /// without waiting for the real event. Without it, checking a mod-mismatch template means talking
        /// somebody into connecting with the wrong mods.
        ///
        /// Runs from a connected admin's client as well as the server console, forwarding over an RPC in that
        /// case. The webhook URL is never synced to clients, so the client cannot decide whether a post is
        /// possible or send one itself - the server does the work and reports back what happened. The RPC is
        /// admin gated server side; see ValConfig.OnServerReceiveTestNotification for why that matters.
        /// </summary>
        internal class TestNotification : ConsoleCommand {
            public override string Name => "Enforcer-Test-Notification";
            public override bool IsCheat => true;
            public override string Help => "Posts one Discord notification using sample data, to preview a template from Notifications.yaml. Ignores the Notify* on/off settings but still needs a webhook URL. Server admins only. Format: Enforcer-Test-Notification <event>, or 'list' for the event names.";

            public override List<string> CommandOptionList() {
                return Enum.GetNames(typeof(NotificationEvent)).ToList();
            }

            public override void Run(string[] args) {
                if (args.Length < 1) {
                    Logger.LogInfo($"An event is required. One of: {string.Join(", ", Enum.GetNames(typeof(NotificationEvent)))}");
                    return;
                }
                if (string.Equals(args[0], "list", StringComparison.OrdinalIgnoreCase)) {
                    Logger.LogInfo($"Notification events: {string.Join(", ", Enum.GetNames(typeof(NotificationEvent)))}");
                    return;
                }
                // Parsed here purely to catch a typo before it costs a round trip; the server parses the name
                // again rather than trusting this one.
                if (!Enum.TryParse(args[0], true, out NotificationEvent evt) || !Enum.IsDefined(typeof(NotificationEvent), evt)) {
                    Logger.LogInfo($"Unknown event '{args[0]}'. One of: {string.Join(", ", Enum.GetNames(typeof(NotificationEvent)))}");
                    return;
                }

                if (ZNet.instance == null) {
                    Logger.LogInfo("Not in a game. Join the server first.");
                    return;
                }

                // IsServer rather than IsCurrentServerDedicated, matching Enforcer-Import-ServerCharacters: a
                // client attached to somebody else's listen host must still forward, because the templates and
                // the webhook live on that host, not here.
                if (!ZNet.instance.IsServer()) {
                    ZPackage package = new ZPackage();
                    package.Write(evt.ToString());
                    ValConfig.TestNotificationRPC.SendPackage(ZRoutedRpc.instance.GetServerPeerID(), package);
                    Logger.LogInfo($"Asking the server to post a sample {evt} notification...");
                    return;
                }

                // This is the local path (listen host or singleplayer) - we are the server.
                if (!DiscordNotifier.IsValidWebhookUrl(DiscordNotifier.ResolveUrl(NotificationTemplates.CategoryOf(evt)))) {
                    Logger.LogInfo($"No usable webhook URL for the {NotificationTemplates.CategoryOf(evt)} category. Set Discord.WebhookUrl, or the URL for that category.");
                    return;
                }

                // The same bag the load-time validity check renders with, so what this previews is exactly what
                // that check accepted.
                DiscordNotifier.Notify(evt, NotificationTemplates.SampleTokens());
                Logger.LogInfo($"Posted a sample {evt} notification to the {NotificationTemplates.CategoryOf(evt)} webhook.");
            }
        }

        internal class ImportServerCharacters : ConsoleCommand {
            public override string Name => "Enforcer-Import-ServerCharacters";
            public override bool IsCheat => true;
            public override string Help => "Imports character saves left behind by the ServerCharacters mod. Run 'dryrun' first to see what would happen. Characters that already have a save here are skipped unless 'force' is given. Format: Enforcer-Import-ServerCharacters dryrun|import [force]";

            public override void Run(string[] args) {
                if (args.Length < 1) {
                    Logger.LogInfo("A mode is required. Format: Enforcer-Import-ServerCharacters dryrun|import [force]");
                    return;
                }
                bool dryRun;
                if (string.Equals(args[0], "dryrun", StringComparison.OrdinalIgnoreCase)) {
                    dryRun = true;
                } else if (string.Equals(args[0], "import", StringComparison.OrdinalIgnoreCase)) {
                    dryRun = false;
                } else {
                    Logger.LogInfo($"Unknown mode '{args[0]}'. Use 'dryrun' or 'import'.");
                    return;
                }
                bool force = args.Length > 1 && string.Equals(args[1], "force", StringComparison.OrdinalIgnoreCase);

                if (ZNet.instance == null) {
                    Logger.LogInfo("Not in a game. Start or join the server first.");
                    return;
                }

                // IsServer rather than IsCurrentServerDedicated, which the older commands use: a client attached
                // to somebody else's listen host must still forward, or it would import from its OWN character
                // folder into a save the server never sees.
                if (!ZNet.instance.IsServer()) {
                    ZPackage package = new ZPackage();
                    package.Write(force ? $"{args[0].ToLowerInvariant()} force" : args[0].ToLowerInvariant());
                    ValConfig.ImportServerCharactersRPC.SendPackage(ZRoutedRpc.instance.GetServerPeerID(), package);
                    Logger.LogInfo("Requesting the ServerCharacters import from the server...");
                    return;
                }

                // This is the local path (listen host or singleplayer) - we are the server.
                Logger.LogInfo(modules.migration.ServerCharactersImport.Run(dryRun, force).Summary());
            }
        }

        internal class ListPlayers : ConsoleCommand {
            public override string Name => "Enforcer-List-Players";
            public override bool IsCheat => true;
            public override string Help => "Enforcer-List-Players - Provides a full list of all accounts and Player names stored.";

            public override void Run(string[] args) {

                if (ZNet.instance.IsCurrentServerDedicated()) {
                    ValConfig.ListPlayerRPC.SendPackage(ZRoutedRpc.instance.GetServerPeerID(), new ZPackage());
                    Logger.LogInfo("Requesting player list from server...");
                    return;
                }
                // This is the non-dedicated path
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

                if (ZNet.instance.IsCurrentServerDedicated()) {
                    ZPackage package = new ZPackage();
                    RPCServerUpdateData clearData = new RPCServerUpdateData();
                    clearData.ItemPrefabFilter = prefab;
                    clearData.PlatformID = account;
                    clearData.PlayerName = username;
                    package.Write(DataObjects.yamlserializer.Serialize(clearData));
                    ValConfig.ClearConfiscatedRPC.SendPackage(ZRoutedRpc.instance.GetServerPeerID(), package);
                    Logger.LogInfo("Sending command to clear confiscated items on server...");
                    return;
                }


                // This is the local path
                CommandHelpers.ClearSpecifiedPlayerConfiscatedItems(account, username, prefab);
            }
        }


        // TODO finish rewriting
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
                        confiscatedItem.AddToInventory(Player.m_localPlayer, false);
                    }
                    character.ConfiscatedItems.Clear();
                    return;
                }

                foreach (DataObjects.PackedItem confiscated in character.ConfiscatedItems) {
                    List<string> targetItems = prefab.Split(',').ToList();
                    foreach (var confiscatedItem in character.ConfiscatedItems) {
                        if (targetItems.Contains(confiscatedItem.prefabName) == false) { continue; }
                        Logger.LogInfo($"Providing {confiscatedItem.prefabName}");
                        confiscatedItem.AddToInventory(Player.m_localPlayer, false);
                    }
                    foreach (string target in targetItems) {
                        character.ConfiscatedItems.RemoveAll(i => i.prefabName == target);
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

                if (Player.m_localPlayer != null && Player.m_localPlayer.GetPlayerName() == username && ZNet.instance.IsCurrentServerDedicated() == false) {
                    Logger.LogInfo("Local player is the target, returning player items.");
                    List<DataObjects.PackedItem> itemsToReturn = CommandHelpers.LoadCharacterAndFindItemsToReturn(account, username, prefabFilter);
                    DataObjects.Character character = ValConfig.LoadCharacterFromSave(account, username);
                    foreach (DataObjects.PackedItem confiscated in itemsToReturn) {
                        Logger.LogInfo($"Providing {confiscated.prefabName}");
                        confiscated.AddToInventory(Player.m_localPlayer, false);
                    }
                    ValConfig.WritePlayerCharacterToSave(account, character);
                    return;
                }

                ZPackage package = new ZPackage();
                RPCServerUpdateData returnCommand = new RPCServerUpdateData();
                returnCommand.PlatformID = account;
                returnCommand.ItemPrefabFilter = prefabFilter;
                returnCommand.PlayerName = username;
                package.Write(DataObjects.yamlserializer.Serialize(returnCommand));
                ValConfig.ReturnConfiscatedItemsRPC.SendPackage(ZRoutedRpc.instance.GetServerPeerID(), package);
            }
        }
    }
}
