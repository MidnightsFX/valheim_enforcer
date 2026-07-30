using BepInEx;
using BepInEx.Configuration;
using Jotunn.Entities;
using Jotunn.Managers;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using ValheimEnforcer.common;
using ValheimEnforcer.modules;
using ValheimEnforcer.modules.character;
using ValheimEnforcer.modules.cheatmonitor;
using ValheimEnforcer.modules.commands;
using ValheimEnforcer.modules.notifications;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer {
    internal class ValConfig {
        public static ConfigFile cfg;
        public static ConfigEntry<bool> EnableDebugMode;
        public static ConfigEntry<bool> UpdateLoadedModsOnStartup;
        public static ConfigEntry<bool> AutoAddModsToRequired;
        public static ConfigEntry<bool> RemoveNontrackedItemsFromJoiningPlayers;
        public static ConfigEntry<bool> AddMissingItemsFromPlayerServerSave;
        public static ConfigEntry<bool> PreventExternalSkillRaises;
        public static ConfigEntry<bool> NewCharactersRemoveExtraItems;
        public static ConfigEntry<bool> NewCharacterSetSkillsToZero;
        public static ConfigEntry<bool> newCharacterClearCustomData;
        public static ConfigEntry<bool> PreventExternalCustomDataChanges;
        public static ConfigEntry<bool> ValidateItemCustomData;
        public static ConfigEntry<bool> ValidateItemDurability;
        public static ConfigEntry<float> ItemValidationDurabilityAllowedVariance;
        public static ConfigEntry<bool> SavePlayerStatusEffectsOnLogout;
        public static ConfigEntry<bool> ItemRemovalForDirtyReconnection;
        public static ConfigEntry<bool> ItemReturnForDirtyReconnection;

        public static ConfigEntry<bool> InternalStorageMode;
        public static ConfigEntry<int> ConfigPollIntervalSeconds;
        public static ConfigEntry<int> DeltaSynchronizationFrequencyInSeconds;
        public static ConfigEntry<int> FullSyncPullIntervalMinutes;
        public static ConfigEntry<int> FullSyncMaxConcurrentPlayers;

        public static ConfigEntry<bool> EnableCheatDetection;
        public static ConfigEntry<bool> DetectCheatEngine;
        public static ConfigEntry<bool> DetectValheimTooler;
        //public static ConfigEntry<bool> DetectSpeedhack;
        public static ConfigEntry<string> CheatDetectionAction;
        public static ConfigEntry<int> CheatScanIntervalSeconds;

        public static ConfigEntry<string> DiscordWebhookUrl;
        public static ConfigEntry<bool> DiscordNotifyServerStartup;
        public static ConfigEntry<bool> DiscordNotifyServerShutdown;
        public static ConfigEntry<bool> DiscordNotifyPlayerJoined;
        public static ConfigEntry<bool> DiscordNotifyPlayerLeft;
        public static ConfigEntry<bool> DiscordNotifyWrongMods;
        public static ConfigEntry<bool> DiscordNotifyCheaterBanned;

        internal const string ModsFileName = "Mods.yaml";
        internal const string ValheimEnforcer = "ValheimEnforcer";
        internal const string CharacterFolder = "Characters";
        internal const string KnownCheatersFileName = "KnownCheaters.yaml";
        internal static String ModsConfigFilePath = Path.Combine(Paths.ConfigPath, ValheimEnforcer, ModsFileName);
        internal static String CharacterFilePath = Path.Combine(Paths.ConfigPath, ValheimEnforcer, CharacterFolder);
        internal static String KnownCheatersFilePath = Path.Combine(Paths.ConfigPath, ValheimEnforcer, KnownCheatersFileName);

        internal static CustomRPC CharacterSaveRPC;
        internal static CustomRPC ReturnConfiscatedItemsRPC;
        internal static CustomRPC CheatDetectionRPC;
        internal static CustomRPC ItemDeltaUpdateRPC;
        internal static CustomRPC ListPlayerRPC;
        internal static CustomRPC ClearConfiscatedRPC;
        internal static CustomRPC FullSyncRequestRPC;

        public ValConfig(ConfigFile cf) {
            // ensure all the config values are created
            cfg = cf;
            cfg.SaveOnConfigSet = true;
            CreateConfigValues(cf);
            Logger.SetDebugLogging(EnableDebugMode.Value);
            ConfigFileWatcher.Initialize();
            SetupMainFileWatcher();

            CharacterSaveRPC = NetworkManager.Instance.AddRPC("VENFORCE_CHAR", OnServerRecieveCharacter, OnClientReceiveCharacter);
            ReturnConfiscatedItemsRPC = NetworkManager.Instance.AddRPC("VENFORCE_RETURN_CONFISCATED", OnServerReturnConfiscatedReceive, OnClientReceiveConfiscatedItems);
            CheatDetectionRPC = NetworkManager.Instance.AddRPC("VENFORCE_CHEAT", OnServerReceiveCheatReport, OnClientReceiveCheatReport);
            ItemDeltaUpdateRPC = NetworkManager.Instance.AddRPC("VENFORCE_ITEMDELTA", OnServerRecieveDeltaItemUpdate, OnClientReceiveDeltaItemUpdate);
            ListPlayerRPC = NetworkManager.Instance.AddRPC("VENFORCE_LIST_PLAYER", OnServerReceiveListPlayer, OnClientReceiveListPlayer);
            ClearConfiscatedRPC = NetworkManager.Instance.AddRPC("VENFORCE_CLEAR_CONFISCATED", OnServerRecieveClearConfiscated, OnClientReceiveClearConfiscated);
            FullSyncRequestRPC = NetworkManager.Instance.AddRPC("VENFORCE_FULLSYNC_REQ", OnServerReceiveFullSyncRequest, OnClientReceiveFullSyncRequest);

            SynchronizationManager.Instance.AddInitialSynchronization(CharacterSaveRPC, SendSavedCharacter);

            LoadYamlConfigs(new Dictionary<string, Action<string>>() {
                { ModsConfigFilePath, CreateModsFile },
                { KnownCheatersFilePath, CreateKnownCheatersFile }
            });
            KnownCheaterTracker.Initialize();
        }

        private void CreateConfigValues(ConfigFile Config) {
            // Debugmode
            EnableDebugMode = Config.Bind("Client config", "EnableDebugMode", false,
                new ConfigDescription("Enables Debug logging.",
                null,
                new ConfigurationManagerAttributes { IsAdvanced = true }));
            EnableDebugMode.SettingChanged += Logger.EnableDebugLogging;
            Logger.CheckEnableDebugLogging();

            UpdateLoadedModsOnStartup = BindServerConfig("Mods", "UpdateLoadedModsOnStartup", true, "Whether or not the mod configuration file will update its loaded mods once they are detected.");
            AutoAddModsToRequired = BindServerConfig("Mods", "AutoAddModsToRequired", true, "If true, automatically adds mods not found in the optional, admin, or server-only mod lists.");
            RemoveNontrackedItemsFromJoiningPlayers = BindServerConfig("Player Sync", "RemoveNontrackedItemsFromJoiningPlayers", true, "If enabled, any items that are not tracked by the server will be removed from joining player's inventories.");
            AddMissingItemsFromPlayerServerSave = BindServerConfig("Player Sync", "AddMissingItemsFromPlayerServerSave", true, "If enabled, any items the player does not have that are listed on the server will be given to the player when joining");
            PreventExternalSkillRaises = BindServerConfig("Player Sync", "PreventExternalSkillRaises", true, "If enabled, player skill gains outside of the server are removed when connecting.");
            NewCharactersRemoveExtraItems = BindServerConfig("Player Sync", "NewCharactersRemoveExtraItems", false, "If enabled, new characters that have no existing character file will have all items removed except for starting items.");
            NewCharacterSetSkillsToZero = BindServerConfig("Player Sync", "NewCharacterSetSkillsToZero", false, "If enabled, new characters will have their skills set to zero. Prevents players from raising skills before connecting.");
            PreventExternalCustomDataChanges = BindServerConfig("Player Sync", "PreventExternalCustomDataChanges", true, "If enabled, tracks player custom data. Warning: custom data can be large and can impact how other mods function.");
            newCharacterClearCustomData = BindServerConfig("Player Sync", "newCharacterClearCustomData", true, "If enabled, new characters will have their custom data cleared.");
            ValidateItemCustomData = BindServerConfig("Player Sync", "ValidateItemCustomData", true, "If enabled, custom data on items will be validated.");
            ValidateItemDurability = BindServerConfig("Player Sync", "ValidateItemDurability", true, "If enabled, item durability will be validated");
            ItemValidationDurabilityAllowedVariance = BindServerConfig("Player Sync", "ItemValidationDurabilityAllowedVariance", 10f, "Allowed variance for item durability validation.", true, 0, 100f);
            SavePlayerStatusEffectsOnLogout = BindServerConfig("Player Sync", "SavePlayerStatusEffectsOnLogout", true, "Whether or not to save active character effects on logout and reapply on login");
            ItemRemovalForDirtyReconnection = BindServerConfig("Player Sync", "ItemRemovalForDirtyReconnection", false, "Leniency for dirty reconnects (crash/timeout, where the server save may be up to one delta window stale). RemoveNontrackedItemsFromJoiningPlayers always runs otherwise; if this is enabled, untracked items are NOT confiscated when the player's last disconnect was dirty, so crash victims keep items gained in the unsaved window.");
            ItemReturnForDirtyReconnection = BindServerConfig("Player Sync", "ItemReturnForDirtyReconnection", false, "Leniency for dirty reconnects. AddMissingItemsFromPlayerServerSave always restores missing tracked items on a clean join; on a dirty reconnect restoration is skipped by default (to avoid duping items consumed in the unsaved window) unless this is enabled.");

            // portable mode
            InternalStorageMode = BindServerConfig("Advanced", "InternalStorageMode", false, "If enabled, player character data will be stored within your world. Enables full portability of the world without having to synchronize configurations.", advanced: true);
            ConfigPollIntervalSeconds = BindServerConfig("Advanced", "ConfigPollIntervalSeconds", 30, "How frequently (in seconds) the mod polls config files on disk for changes.", advanced: true, valmin: 1, valmax: 300);
            DeltaSynchronizationFrequencyInSeconds = BindServerConfig("Advanced", "CharacterDeltaTracker", 15, "Minimum time (in seconds) between incremental inventory/skill/custom-data updates. Updates are only produced when the player's inventory actually changes, so an idle player sends nothing; this is a rate limit rather than a polling interval.", advanced: true, valmin: 5, valmax: 300);
            FullSyncPullIntervalMinutes = BindServerConfig("Advanced", "FullSyncPullIntervalMinutes", 25, "How often (in minutes) the server asks connected players to upload a full character save. Full saves are a periodic reconciliation layered on top of the incremental delta updates (CharacterDeltaTracker); they are no longer tied to the world/profile autosave.", advanced: true, valmin: 1, valmax: 1440);
            FullSyncMaxConcurrentPlayers = BindServerConfig("Advanced", "FullSyncMaxConcurrentPlayers", 5, "Maximum number of players the server asks to upload a full character save at the same time. Larger player counts are staggered into successive waves of this size to avoid a bandwidth spike. 10 is safe on a healthy server; lower it on constrained upload/VPS hosts.", advanced: true, valmin: 1, valmax: 50);

            EnableCheatDetection = BindServerConfig("Anti-Cheat", "EnableCheatDetection", true, "Enable client-side scanning for known cheat tools (Cheat Engine, ValheimTooler). Detections are reported to the server.");
            DetectValheimTooler = BindServerConfig("Anti-Cheat", "DetectValheimTooler", true, "Detect ValheimTooler by the namespace of the types it loads (rename-proof), including assemblies injected mid-session. A confirmed detection is always auto-banned regardless of ActionOnDetection. High confidence, very low cost.");
            DetectCheatEngine = BindServerConfig("Anti-Cheat", "DetectCheatEngine", true, "Scan for Cheat Engine (processes, windows, injected speedhack/DBK modules, debugger). Note: Cheat Engine has legitimate uses — prefer Log action over Kick/Ban.");
            //DetectSpeedhack = BindServerConfig("Anti-Cheat", "DetectSpeedhack", true, "Detect speedhack via Unity time vs. wall-clock drift.");
            CheatDetectionAction = BindServerConfig("Anti-Cheat", "ActionOnDetection", "Kick", "Server-side action taken when a Cheat Engine detection is reported (ValheimTooler is always auto-banned).", new AcceptableValueList<string>("Log", "Kick", "Ban"));
            CheatScanIntervalSeconds = BindServerConfig("Anti-Cheat", "ScanIntervalSeconds", 30, "Seconds between the periodic Cheat Engine process check on the client. ValheimTooler assembly detection is event-driven and not affected by this interval.", false, 5, 300);

            // Discord notifications. These are intentionally LOCAL (non-synced) configs: the webhook URL is a secret and must not be synced to clients
            DiscordWebhookUrl = BindLocalConfig("Discord", "WebhookUrl", "", "Discord webhook URL the server posts notifications to. This is a server-only secret and is never synced to clients. Leave empty to disable. Note: player names are sent to Discord when enabled.");
            DiscordNotifyServerStartup = BindLocalConfig("Discord", "NotifyServerStartup", true, "Post a message when the server comes online.");
            DiscordNotifyServerShutdown = BindLocalConfig("Discord", "NotifyServerShutdown", true, "Post a message when the server shuts down.");
            DiscordNotifyPlayerJoined = BindLocalConfig("Discord", "NotifyPlayerJoined", true, "Post a message when a player joins.");
            DiscordNotifyPlayerLeft = BindLocalConfig("Discord", "NotifyPlayerLeft", true, "Post a message when a player leaves, including whether their saved data is up to date.");
            DiscordNotifyWrongMods = BindLocalConfig("Discord", "NotifyWrongMods", true, "Post a message when a player is rejected for a mod mismatch, listing the offending mods.");
            DiscordNotifyCheaterBanned = BindLocalConfig("Discord", "NotifyCheaterBanned", true, "Post a message when a player is banned for cheat usage, including the detected cheat(s).");
        }

        // routine: set for the recurring background baseline write driven by CharacterDeltaTracker, which happens
        // often enough that logging every one at info level would drown the log. Notable saves (join, logout,
        // death, an incoming character from a client) leave it false and stay visible without debug logging.
        internal static void WritePlayerCharacterToSave(string id, DataObjects.Character character, bool routine = false) {
            if (ValConfig.InternalStorageMode.Value) {
                if (routine) { Logger.LogDebug("Saving character with internal storage mode."); } else { Logger.LogInfo("Saving character with internal storage mode."); }
                InternalDataStore.SaveAccountCharacter(character);
            }
            // Double write the data so that if the storage mode is switched the data will still be present.
            Directory.CreateDirectory(Path.Combine(Paths.ConfigPath, ValheimEnforcer, CharacterFolder));
            var saveDir = Directory.CreateDirectory(Path.Combine(Paths.ConfigPath, ValheimEnforcer, CharacterFolder, id));
            string path = Path.Combine(saveDir.FullName, $"{character.Name}.yaml");
            if (routine) { Logger.LogDebug($"Writing to {path}"); } else { Logger.LogInfo($"Writing to {path}"); }
            try {
                File.WriteAllText(path, DataObjects.yamlserializer.Serialize(character));
            } catch (Exception e) {
                Logger.LogWarning($"Failed to write character data to disk at {path}: {e.Message}");
            }
        }

        internal static DataObjects.Character LoadCharacterFromSave(string id, string name) {
            if (ValConfig.InternalStorageMode.Value) {
                Logger.LogInfo("Loading character from internal storage system.");
                DataObjects.Character savedChar = InternalDataStore.GetAccountCharacter(id, name);
                if (savedChar == null) {
                    Logger.LogDebug($"No character file found for player with {id}-{name} is this character new?");
                }
                return savedChar;
            }

            var charFile = Path.Combine(Paths.ConfigPath, ValheimEnforcer, CharacterFolder, id, $"{name}.yaml");
            if (!File.Exists(charFile)) {
                Logger.LogDebug($"No character file found for player with {id}-{name} is this character new?");
                return null;
            }
            var chartext = File.ReadAllText(charFile);
            return DataObjects.yamldeserializer.Deserialize<DataObjects.Character>(chartext);
        }

        public static string GetSecondaryConfigDirectoryPath() {
            string patchesFolderPath = Path.Combine(Paths.ConfigPath, ValheimEnforcer);
            if (!Directory.Exists(patchesFolderPath)) {
                Directory.CreateDirectory(patchesFolderPath);
            }
            
            return patchesFolderPath;
        }

        internal void LoadYamlConfigs(Dictionary<string, Action<string>> configFilesToFind) {
            string externalConfigFolder = ValConfig.GetSecondaryConfigDirectoryPath();
            string[] presentFiles = Directory.GetFiles(externalConfigFolder);
            List<string> foundConfigs = new List<string>();
            List<string> targetFiles = configFilesToFind.Keys.ToList();
            foreach (string configFile in presentFiles) {
                if (targetFiles.Contains(configFile)) {
                    foundConfigs.Add(configFile);
                    Logger.LogDebug($"Found config: {configFile}");
                }
            }

            // Create files that have not been found
            foreach(var cfg in configFilesToFind) {
                if (!foundConfigs.Contains(cfg.Key)) {
                    configFilesToFind[cfg.Key](cfg.Key);
                    foundConfigs.Add(cfg.Key);
                }
            }

            // Sets up file watcher for all of the required files
            foreach (string configFile in foundConfigs) {
                string file = Path.GetFileName(configFile);
                Logger.LogDebug($"Setting filewatcher for {file}");
                SetupFileWatcher(configFile);
            }
        }

        private void SetupFileWatcher(string fullPath) {
            ConfigFileWatcher.Register(fullPath, UpdateConfigFileOnChange);
        }

        private static void UpdateConfigFileOnChange(string filepath) {
            if (SynchronizationManager.Instance.PlayerIsAdmin == false) {
                Logger.LogInfo("Player is not an admin, and not allowed to change local configuration. Ignoring.");
                return;
            }
            if (File.Exists(filepath) == false) { return; }

            string filetext = File.ReadAllText(filepath);
            var fileInfo = new FileInfo(filepath);
            Logger.LogDebug($"Filewatch changes from: ({fileInfo.Name}) {fileInfo.FullName}");
            switch (fileInfo.Name) {
                case ModsFileName:
                    Logger.LogDebug("Triggering Mod Settings update.");
                    ModManager.UpdateModSettingConfigs(filetext);
                    break;
                case KnownCheatersFileName:
                    Logger.LogDebug("Triggering KnownCheaters list update.");
                    KnownCheaterTracker.LoadFromText(filetext);
                    break;
            }
        }

        private static void CreateModsFile(string filepath) {
            Logger.LogDebug("Loot config missing, recreating.");
            using (StreamWriter writetext = new StreamWriter(filepath)) {
                String header = @"#################################################
# Valheim Enforcer - Required, Admin and Optional Mods
#################################################
";
                writetext.WriteLine(header);
                writetext.WriteLine(ModManager.GetDefaultConfig());
            }
        }

        private static void CreateKnownCheatersFile(string filepath) {
            Logger.LogDebug("KnownCheaters file missing, recreating.");
            // Seeded with the embedded internal list by KnownCheaterTracker.Initialize(), which
            // runs immediately after this and rewrites the file with the merged entries.
            using (StreamWriter writetext = new StreamWriter(filepath)) {
                String header = @"#################################################
# Valheim Enforcer - Known Cheaters (server side)
# Auto-populated when cheaters are banned. Entries: { id, reason }
#################################################
";
                writetext.WriteLine(header);
            }
        }

        internal static ZPackage SendSavedCharacter(ZNetPeer peer) {
            string id = peer.m_socket.GetEndPointString();
            Logger.LogInfo($"Sending saved character data to player {peer.m_playerName} with ID: {id}");
            ZPackage package = new ZPackage();
            if (ValConfig.InternalStorageMode.Value) {
                Logger.LogInfo("Using internal storage mode to send character data.");
                DataObjects.Character chara = InternalDataStore.GetAccountCharacter(id, peer.m_playerName);
                if (chara == null) {
                    Logger.LogInfo($"No character data found for player {peer.m_playerName} with ID: {id}, no character data will be sent.");
                    return new ZPackage();
                }
                string serialChara = DataObjects.yamlserializer.Serialize(chara);
                package.Write(serialChara);
                return package;
            }

            // Disk mode. Prefer the in-memory store (kept current by the async writer, so it can be newer
            // than disk while a write is pending) and fall back to disk, warming the store so the player's
            // first deltas can be applied without re-reading the file. If the on-disk file has been edited
            // out-of-band (e.g. an admin edited the save while the player was offline) since we cached it, the
            // store reports a miss so the edited file is re-read and re-seeded below.
            var charFile = Path.Combine(Paths.ConfigPath, ValheimEnforcer, CharacterFolder, $"{id}");
            string fullpath = Path.Combine(charFile, $"{peer.m_playerName}.yaml");
            bool exists = File.Exists(fullpath);
            DateTime diskMtime = exists ? File.GetLastWriteTimeUtc(fullpath) : DateTime.MinValue;

            string cached = modules.character.CharacterStore.GetYamlIfCurrent(id, peer.m_playerName, diskMtime);
            if (cached != null) {
                package.Write(cached);
                return package;
            }

            if (!exists) {
                Logger.LogInfo($"path: {fullpath} does not exist, no character data will be sent.");
                return new ZPackage();
            }
            string filecontents = File.ReadAllText(fullpath);
            modules.character.CharacterStore.Seed(id, peer.m_playerName, filecontents, diskMtime);
            package.Write(filecontents);
            return package;
        }

        public static IEnumerator OnServerRecieveCharacter(long sender, ZPackage package) {
            string yaml = package.ReadString(); // must run on the main thread (consumes the ZPackage); cheap
            PersistReceivedCharacterYaml(sender, yaml);
            yield break;
        }

        // Shared server-side persistence for a full character save received from a client. Used by the
        // Jotunn CharacterSaveRPC handler (OnServerRecieveCharacter) and the synchronous end-of-session
        // FinalSaveRpc. The ZPackage must already be consumed on the main thread before calling this.
        internal static void PersistReceivedCharacterYaml(long sender, string yaml) {
            if (ValConfig.InternalStorageMode.Value) {
                // Internal storage writes touch a registry ZDO and must stay on the main thread.
                try {
                    DataObjects.Character chara = DataObjects.yamldeserializer.Deserialize<DataObjects.Character>(yaml);
                    Logger.LogInfo($"Recieved Player data update for {sender} - {chara.Name}|{chara.HostID}");
                    WritePlayerCharacterToSave(chara.HostID, chara);
                } catch (Exception e) {
                    Logger.LogWarning($"Failed to deserialize character data from {sender}: {e.Message}");
                }
                return;
            }

            // Disk mode: hand the raw YAML to the background store. All parsing, serialization and disk I/O
            // happen off the main thread, so a burst of saves (e.g. every client saving at once on a
            // "save player profiles" broadcast) cannot stall the server and time peers out.
            modules.character.CharacterStore.SubmitFullSave(yaml);
        }

        public static IEnumerator OnServerRecieveClearConfiscated(long sender, ZPackage package) {
            RPCServerUpdateData data = DataObjects.yamldeserializer.Deserialize<DataObjects.RPCServerUpdateData>(package.ReadString());

            ZNetPeer zpeer = GetPeerByPlatformID(data.PlatformID);
            if (zpeer == null) {
                Logger.LogWarning($"Could not find peer with PlatformID {data.PlatformID} to clear confiscated items.");
                yield break;
            }
            CommandHelpers.ClearSpecifiedPlayerConfiscatedItems(data.PlatformID, data.PlayerName, data.ItemPrefabFilter);
            ValConfig.ClearConfiscatedRPC.SendPackage(zpeer.m_uid, package);

            yield break;
        }

        public static IEnumerator OnClientReceiveClearConfiscated(long sender, ZPackage package) {
            RPCServerUpdateData data = DataObjects.yamldeserializer.Deserialize<DataObjects.RPCServerUpdateData>(package.ReadString());

            CommandHelpers.ClearSpecifiedPlayerConfiscatedItems(data.PlatformID, data.PlayerName, data.ItemPrefabFilter);
            yield break;
        }

        public static IEnumerator OnClientReceiveCharacter(long sender, ZPackage package) {
            DataObjects.Character chara = DataObjects.yamldeserializer.Deserialize<DataObjects.Character>(package.ReadString());
            Logger.LogDebug("Recieved Player character data from server.");
            CharacterManager.SetPlayerCharacter(chara);
            yield break;
        }

        public static IEnumerator OnServerReturnConfiscatedReceive(long sender, ZPackage package) {
            // Parse the target and the prefab filter
            DataObjects.RPCServerUpdateData returnAct = DataObjects.yamldeserializer.Deserialize<DataObjects.RPCServerUpdateData>(package.ReadString());

            List<PackedItem> itemsToReturn = CommandHelpers.LoadCharacterAndFindItemsToReturn(returnAct.PlatformID, returnAct.PlayerName, returnAct.ItemPrefabFilter);
            DataObjects.Character character = ValConfig.LoadCharacterFromSave(returnAct.PlatformID, returnAct.PlayerName);

            // Find the target peer by account ID and character name
            ZNetPeer targetPeer = ValConfig.GetPeerByPlatformID(returnAct.PlatformID);

            if (targetPeer == null) {
                Logger.LogInfo($"Player {returnAct.PlayerName} is not currently connected. Moving items to player inventory save so they are restored on next login.");
                foreach (DataObjects.PackedItem item in itemsToReturn) {
                    character.PlayerItems.Add(item);
                }
                ValConfig.WritePlayerCharacterToSave(returnAct.PlatformID, character);
                if (ValConfig.InternalStorageMode.Value) {
                    Logger.LogInfo("Also updating character data in internal storage.");
                    InternalDataStore.SaveAccountCharacter(character);
                }
                yield break;
            }
            Logger.LogInfo($"Sending {itemsToReturn.Count} confiscated item(s) to player {returnAct.PlayerName}.");
            // Update the character data on the server
            ValConfig.WritePlayerCharacterToSave(returnAct.PlatformID, character);
            // This write bypasses the async store, so drop any cached copy the store holds for this player;
            // the next access reloads the freshly written save instead of overwriting it with stale state.
            modules.character.CharacterStore.Invalidate(returnAct.PlatformID, returnAct.PlayerName);
            if (ValConfig.InternalStorageMode.Value) {
                Logger.LogInfo("Also updating character data in internal storage.");
                InternalDataStore.SaveAccountCharacter(character);
            }
            ZPackage returnableItems = new ZPackage();
            returnableItems.Write(DataObjects.yamlserializer.Serialize(itemsToReturn));
            ValConfig.ReturnConfiscatedItemsRPC.SendPackage(targetPeer.m_uid, returnableItems);
            // Send the updated player character to the client so that their client-side data is also updated with the returned items
            ValConfig.CharacterSaveRPC.SendPackage(targetPeer.m_uid, ValConfig.SendCharacterAsZpackage(character));
            yield break;
        }

        public static IEnumerator OnServerReceiveCheatReport(long sender, ZPackage package) {
            string yaml = package.ReadString();
            DataObjects.CheatSummaryReport summary;
            try {
                summary = DataObjects.yamldeserializer.Deserialize<DataObjects.CheatSummaryReport>(yaml);
            } catch (Exception e) {
                Logger.LogWarning($"Failed to deserialize cheat report from {sender}: {e.Message}");
                yield break;
            }

            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            string playerName = summary.PlayerName;
            if (peer == null) {
                Logger.LogWarning($"Received cheat report for {playerName} but could not find corresponding peer. No action will be taken.");
                yield break;
            }

            string endpoint = peer.m_socket.GetEndPointString();
            bool cheatEngineDetected = summary.CheatEngineStatus?.IsCheatEngineDetected() ?? false;
            Logger.LogWarning($"Cheat detection from {playerName} ({endpoint}): valheim-tooler: {summary.ValheimToolerStatus} cheatengine: {cheatEngineDetected}");

            // ValheimTooler is unambiguous cheat software; always ban regardless of ActionOnDetection.
            if (summary.ValheimToolerStatus) {
                Logger.LogWarning($"Banning {playerName} for ValheimTooler usage.");
                BanCheater(peer, playerName, summary);
                yield break;
            }

            // Cheat Engine (and any future non-ValheimTooler detection) honors the configured action.
            string action = CheatDetectionAction.Value ?? "Log";
            switch (action) {
                case "Kick":
                    Logger.LogWarning($"Kicking {playerName} for cheat usage.");
                    ZNet.instance.Kick(playerName);
                    break;
                case "Ban":
                    Logger.LogWarning($"Banning {playerName} for cheat usage.");
                    BanCheater(peer, playerName, summary);
                    break;
                case "Log":
                default:
                    break;
            }
            yield break;
        }

        // Persists the ban to the KnownCheaters list (the durable rejoin barrier), applies the
        // vanilla ban, and posts a Discord notification when enabled.
        private static void BanCheater(ZNetPeer peer, string playerName, DataObjects.CheatSummaryReport summary) {
            string hostId = peer.m_socket.GetHostName();
            string reason = BuildCheatReason(summary);
            KnownCheaterTracker.AddCheater(hostId, reason);
            ZNet.instance.Ban(playerName);

            if (ValConfig.DiscordNotifyCheaterBanned.Value) {
                DiscordEmbed embed = new DiscordEmbed("Cheater Banned", null, Red)
                    .AddField("Player", playerName, true)
                    .AddField("Detected", reason, true)
                    .AddField("Host ID", hostId, false);
                DiscordNotifier.SendAsync(embed.ToMessage());
            }
        }

        private static string BuildCheatReason(DataObjects.CheatSummaryReport summary) {
            List<string> detections = new List<string>();
            if (summary.ValheimToolerStatus) { detections.Add("ValheimTooler"); }
            if (summary.CheatEngineStatus != null && summary.CheatEngineStatus.IsCheatEngineDetected()) { detections.Add("CheatEngine"); }
            string detail = detections.Count > 0 ? string.Join(", ", detections) : "cheat detected";
            return $"Cheat detection: {detail}";
        }

        public static IEnumerator OnClientReceiveCheatReport(long sender, ZPackage package) {
            // Client -> server only; clients do not act on this RPC.
            yield break;
        }

        public static IEnumerator OnClientReceiveListPlayer(long sender, ZPackage package) {
            Dictionary<string, List<string>> accountNameMap = DataObjects.yamldeserializer.Deserialize<Dictionary<string, List<string>>>(package.ReadString());
            foreach(var kvp in accountNameMap) {
                Logger.LogInfo($"AccountID: {kvp.Key}");
                foreach (string chara in kvp.Value) {
                    Logger.LogInfo($"    Character: {chara}");
                }
            }
            yield break;
        }

        public static IEnumerator OnServerReceiveListPlayer(long sender, ZPackage package) {
            // AccountNameMap
            Dictionary<string, List<string>> accountNameMap = new Dictionary<string, List<string>>();

            if (ValConfig.InternalStorageMode.Value) {
                accountNameMap = InternalDataStore.GetAccountRegistry();
                ValConfig.ListPlayerRPC.SendPackage(sender, new ZPackage(DataObjects.yamlserializer.Serialize(accountNameMap)));
                // Send the RPC
                yield break;
            }

            List<string> storedAccounts = Directory.GetFiles(Path.Combine(Paths.ConfigPath, ValConfig.ValheimEnforcer, ValConfig.CharacterFolder)).ToList();
            foreach (string account in storedAccounts) {
                List<string> characters = Directory.GetFiles(account).ToList();
                string accountID = account.Split('/').Last();
                List<string> accountCharacters = new List<string>();
                foreach (string characterFile in characters) {
                    accountCharacters.Add(characterFile.Split('/').Last());
                }
                accountNameMap.Add(accountID, accountCharacters);
            }
            ValConfig.ListPlayerRPC.SendPackage(sender, new ZPackage(DataObjects.yamlserializer.Serialize(accountNameMap)));

            // Returns an RPC to the client that will send all of the account ID player maps
            yield break;
        }

        public static IEnumerator OnClientReceiveConfiscatedItems(long sender, ZPackage package) {
            List<DataObjects.PackedItem> items = DataObjects.yamldeserializer.Deserialize<List<DataObjects.PackedItem>>(package.ReadString());
            Logger.LogInfo($"Received {items.Count} confiscated item(s) returned from server.");
            foreach (DataObjects.PackedItem item in items) {
                Logger.LogInfo($"Adding returned confiscated item: {item.prefabName} x{item.m_stack}");
                item.AddToInventory(Player.m_localPlayer, false);
            }
            yield break;
        }

        internal static IEnumerator OnServerRecieveDeltaItemUpdate(long sender, ZPackage package) {
            string yaml = package.ReadString();
            DeltaSummaryUpdate deltaUpdate;
            try {
                deltaUpdate = DataObjects.yamldeserializer.Deserialize<DeltaSummaryUpdate>(yaml);
            } catch (Exception e) {
                Logger.LogWarning($"Failed to deserialize delta update from {sender}: {e.Message}");
                yield break;
            }
            if (string.IsNullOrEmpty(deltaUpdate.Name) || string.IsNullOrEmpty(deltaUpdate.HostID)) {
                Logger.LogWarning($"Malformed delta update from {sender}: missing CharacterName or HostName.");
                yield break;
            }

            if (ValConfig.InternalStorageMode.Value) {
                // Internal storage reads/writes touch a registry ZDO and must stay on the main thread.
                Logger.LogInfo("Loading character for delta update with internal storage mode.");
                DataObjects.Character character = InternalDataStore.GetAccountCharacter(deltaUpdate.HostID, deltaUpdate.Name);
                if (character == null) {
                    RequestFullSync(sender, deltaUpdate);
                    yield break;
                }
                Logger.LogInfo($"Received delta update from {deltaUpdate.Name} ({deltaUpdate.HostID}): {deltaUpdate.ItemModifications?.Count ?? 0} item delta(s).");
                UpdatePlayerSaveWithDeltaData(deltaUpdate, character);
                yield break;
            }

            // Disk mode: apply and persist on the background store. We can only decide "no save exists"
            // (which requires a full-sync request from the main thread) up front; if we hold no authoritative
            // state cached and none on disk, ask the client for a full save. Otherwise the worker loads,
            // applies and writes off the main thread. A present-but-corrupt save is handled by the worker,
            // which drops the delta rather than overwrite it.
            if (!modules.character.CharacterStore.IsCached(deltaUpdate.HostID, deltaUpdate.Name)) {
                string fullpath = Path.Combine(Paths.ConfigPath, ValheimEnforcer, CharacterFolder, deltaUpdate.HostID, $"{deltaUpdate.Name}.yaml");
                if (!File.Exists(fullpath)) {
                    RequestFullSync(sender, deltaUpdate);
                    yield break;
                }
            }

            Logger.LogInfo($"Received delta update from {deltaUpdate.Name} ({deltaUpdate.HostID}): {deltaUpdate.ItemModifications?.Count ?? 0} item delta(s).");
            modules.character.CharacterStore.SubmitDelta(deltaUpdate);
            yield break;
        }

        // No authoritative save exists yet (e.g. the connect-time full push was skipped or a delta beat it
        // to the server). Ask the client for a full character save instead of dropping into a partial state;
        // the incoming full save establishes the file and the next delta applies.
        private static void RequestFullSync(long sender, DeltaSummaryUpdate deltaUpdate) {
            Logger.LogInfo($"No saved data for {deltaUpdate.Name} ({deltaUpdate.HostID}); requesting a full character sync from the client. This delta is dropped and will be superseded by the full save.");
            ZPackage req = new ZPackage();
            req.Write(deltaUpdate.Name);
            ValConfig.FullSyncRequestRPC.SendPackage(sender, req);
        }

        public static IEnumerator OnClientReceiveDeltaItemUpdate(long sender, ZPackage package) {
            yield break;
        }

        // Server never receives this RPC; it only sends it to clients to ask for a full character save.
        public static IEnumerator OnServerReceiveFullSyncRequest(long sender, ZPackage package) {
            yield break;
        }

        // Client side: the server is asking for a full character save. Sent both on the periodic server pull
        // (FullSyncScheduler) and as recovery when a delta arrives with no authoritative save to apply onto.
        public static IEnumerator OnClientReceiveFullSyncRequest(long sender, ZPackage package) {
            if (Player.m_localPlayer == null) {
                Logger.LogWarning("Server requested a full character sync but the local player is null; cannot respond.");
                yield break;
            }
            Logger.LogInfo("Server requested a full character sync. Sending full character save.");
            CharacterManager.SavePlayerCharacter(Player.m_localPlayer);
            yield break;
        }

        /// <summary>
        /// Pure in-memory merge of a delta update into a character. Performs no I/O so it can run on the
        /// background <see cref="modules.character.CharacterStore"/> worker; it is also reused by the
        /// internal-storage path in <see cref="UpdatePlayerSaveWithDeltaData"/>.
        /// </summary>
        internal static void MergeDelta(DeltaSummaryUpdate deltaSummary, DataObjects.Character character) {
            // Apply item deltas
            foreach (ItemDelta delta in deltaSummary.ItemModifications) {
                switch (delta.Op) {
                    case ItemDeltaChangeType.Added:
                        character.PlayerItems.Add(delta.Item);
                        Logger.LogDebug($"Delta: added {delta.Item.prefabName} x{delta.Item.m_stack}.");
                        break;
                    case ItemDeltaChangeType.Removed:
                        character.RemoveFromPlayerItems(delta.Item);
                        break;
                }
            }
            Logger.LogDebug($"Applied {deltaSummary.ItemModifications.Count} item delta(s) for {character.Name}.");

            // Update custom data
            foreach (string key in deltaSummary.RemovedCustomDataKeys) {
                character.PlayerCustomData.Remove(key);
            }
            foreach (var kvp in deltaSummary.PlayerCustomDataModifications) {
                character.PlayerCustomData[kvp.Key] = kvp.Value;
            }
            Logger.LogDebug($"Updated custom data for {character.Name}.");

            // Update skills and active status effects
            character.SkillLevels = deltaSummary.SkillLevels;
            character.ActiveCharacterEffects = deltaSummary.ActiveCharacterEffects;

            // Set the connection state (applied before any persistence so internal-storage and disk copies agree)
            character.LastDisconnect = deltaSummary.DisconnectionState;
        }

        // Internal-storage delta persistence — runs on the main thread because it writes the registry ZDO.
        // Disk mode routes deltas through the async CharacterStore instead.
        internal static void UpdatePlayerSaveWithDeltaData(DeltaSummaryUpdate deltaSummary, DataObjects.Character character) {
            MergeDelta(deltaSummary, character);

            if (ValConfig.InternalStorageMode.Value) {
                Logger.LogInfo("Saving character with internal storage mode.");
                InternalDataStore.SaveAccountCharacter(character);
            }

            var charDir = Path.Combine(Paths.ConfigPath, ValheimEnforcer, CharacterFolder, deltaSummary.HostID);
            // Ensure the per-id folder exists (internal-storage mode loads from a ZDO and may not have
            // written the file yet). Mirrors WritePlayerCharacterToSave.
            Directory.CreateDirectory(charDir);
            string fullpath = Path.Combine(charDir, $"{deltaSummary.Name}.yaml");
            File.WriteAllText(fullpath, DataObjects.yamlserializer.Serialize(character));
            Logger.LogInfo($"Saved delta update for {character.Name}.");
        }

        internal static ZPackage SendCharacterAsZpackage(DataObjects.Character chara) {
            string serialChara = DataObjects.yamlserializer.Serialize(chara);
            ZPackage package = new ZPackage();
            package.Write(serialChara);
            return package;
        }

        public static ZNetPeer GetPeerByPlatformID(string platformID) {
            foreach (ZNetPeer peer in ZNet.instance.GetPeers()) {
                if (peer.IsReady() && peer.m_socket.GetHostName() == platformID) {
                    return peer;
                }
            }

            return null;
        }

        internal static void SetupMainFileWatcher() {
            ConfigFileWatcher.Register(cfg.ConfigFilePath, OnMainConfigFileChanged);
        }

        private static void OnMainConfigFileChanged(string _) {
            if (ZNet.instance == null || ZNet.instance.IsServer() == false) {
                return;
            }
            Logger.LogInfo("Configuration file has been changed, reloading settings.");
            cfg.Reload();
        }

        /// <summary>
        /// Binds a LOCAL (non-synced) string configuration entry. Unlike <see cref="BindServerConfig"/>, this does NOT
        /// set IsAdminOnly, so Jotunn's SynchronizationManager will not push the value to clients. Use for server-only
        /// secrets (e.g. the Discord webhook URL) that must never leave the host.
        /// </summary>
        public static ConfigEntry<string> BindLocalConfig(string catagory, string key, string value, string description, bool advanced = false) {
            return cfg.Bind(catagory, key, value,
                new ConfigDescription(description,
                null,
                new ConfigurationManagerAttributes { IsAdminOnly = false, IsAdvanced = advanced }));
        }

        /// <summary>
        /// Binds a LOCAL (non-synced) bool configuration entry. See the string overload of <see cref="BindLocalConfig"/>.
        /// </summary>
        public static ConfigEntry<bool> BindLocalConfig(string catagory, string key, bool value, string description, bool advanced = false) {
            return cfg.Bind(catagory, key, value,
                new ConfigDescription(description,
                null,
                new ConfigurationManagerAttributes { IsAdminOnly = false, IsAdvanced = advanced }));
        }

        /// <summary>
        /// Binds a server configuration entry for a list of strings with the specified category, key, default value,
        /// and description. This config will be server authoratative, editable by admins.
        /// </summary>
        /// <param name="catagory">The category under which the configuration entry is grouped. Cannot be null or empty.</param>
        /// <param name="key">The unique key identifying the configuration entry within the specified category. Cannot be null or empty.</param>
        /// <param name="value">The default list of strings to use for the configuration entry if no value is set.</param>
        /// <param name="description">A description of the configuration entry, used for documentation and display purposes.</param>
        /// <param name="advanced">Indicates whether the configuration entry is considered advanced. If <see langword="true"/>, the entry may
        /// be hidden from standard configuration views.</param>
        /// <returns>A <see cref="ConfigEntry{List{string}}"/> representing the bound server configuration entry.</returns>
        public static ConfigEntry<List<string>> BindServerConfig(string catagory, string key, List<string> value, string description, bool advanced = false) {
            return cfg.Bind(catagory, key, value,
                new ConfigDescription(description, 
                null,
                new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced })
                );
        }

        /// <summary>
        /// Helper to bind configs for float types
        /// </summary>
        /// <param name="config_file"></param>
        /// <param name="catagory"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="description"></param>
        /// <param name="advanced"></param>
        /// <param name="valmin"></param>
        /// <param name="valmax"></param>
        /// <returns></returns>
        public static ConfigEntry<float[]> BindServerConfig(string catagory, string key, float[] value, string description, bool advanced = false, float valmin = 0, float valmax = 150) {
            return cfg.Bind(catagory, key, value,
                new ConfigDescription(description,
                new AcceptableValueRange<float>(valmin, valmax),
                new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced })
                );
        }

        /// <summary>
        ///  Helper to bind configs for bool types
        /// </summary>
        /// <param name="config_file"></param>
        /// <param name="catagory"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="description"></param>
        /// <param name="acceptableValues"></param>>
        /// <param name="advanced"></param>
        /// <returns></returns>
        public static ConfigEntry<bool> BindServerConfig(string catagory, string key, bool value, string description, AcceptableValueBase acceptableValues = null, bool advanced = false) {
            return cfg.Bind(catagory, key, value,
                new ConfigDescription(description,
                    acceptableValues,
                new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced })
                );
        }

        /// <summary>
        /// Helper to bind configs for int types
        /// </summary>
        /// <param name="config_file"></param>
        /// <param name="catagory"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="description"></param>
        /// <param name="advanced"></param>
        /// <param name="valmin"></param>
        /// <param name="valmax"></param>
        /// <returns></returns>
        public static ConfigEntry<int> BindServerConfig(string catagory, string key, int value, string description, bool advanced = false, int valmin = 0, int valmax = 150) {
            return cfg.Bind(catagory, key, value,
                new ConfigDescription(description,
                new AcceptableValueRange<int>(valmin, valmax),
                new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced })
                );
        }

        /// <summary>
        /// Helper to bind configs for float types
        /// </summary>
        /// <param name="config_file"></param>
        /// <param name="catagory"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="description"></param>
        /// <param name="advanced"></param>
        /// <param name="valmin"></param>
        /// <param name="valmax"></param>
        /// <returns></returns>
        public static ConfigEntry<float> BindServerConfig(string catagory, string key, float value, string description, bool advanced = false, float valmin = 0, float valmax = 150) {
            return cfg.Bind(catagory, key, value,
                new ConfigDescription(description,
                new AcceptableValueRange<float>(valmin, valmax),
                new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced })
                );
        }

        /// <summary>
        /// Helper to bind configs for strings
        /// </summary>
        /// <param name="config_file"></param>
        /// <param name="catagory"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="description"></param>
        /// <param name="advanced"></param>
        /// <returns></returns>
        public static ConfigEntry<string> BindServerConfig(string catagory, string key, string value, string description, AcceptableValueList<string> acceptableValues = null, bool advanced = false) {
            return cfg.Bind(catagory, key, value,
                new ConfigDescription(
                    description,
                    acceptableValues,
                new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced })
                );
        }
    }
}
