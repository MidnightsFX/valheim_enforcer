using BepInEx;
using BepInEx.Configuration;
using Jotunn.Entities;
using Jotunn.Managers;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using ValheimEnforcer.common;
using ValheimEnforcer.modules;

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

        public static ConfigEntry<bool> InternalStorageMode;

        internal const string ModsFileName = "Mods.yaml";
        internal const string ValheimEnforcer = "ValheimEnforcer";
        internal const string CharacterFolder = "Characters";
        internal static String ModsConfigFilePath = Path.Combine(Paths.ConfigPath, ValheimEnforcer, ModsFileName);
        internal static String CharacterFilePath = Path.Combine(Paths.ConfigPath, ValheimEnforcer, CharacterFolder);

        internal static CustomRPC CharacterSaveRPC;
        internal static CustomRPC ReturnConfiscatedItemsRPC;

        public ValConfig(ConfigFile cf) {
            // ensure all the config values are created
            cfg = cf;
            cfg.SaveOnConfigSet = true;
            CreateConfigValues(cf);
            Logger.SetDebugLogging(EnableDebugMode.Value);
            SetupMainFileWatcher();

            CharacterSaveRPC = NetworkManager.Instance.AddRPC("VENFORCE_CHAR", OnServerRecieveCharacter, OnClientReceiveCharacter);
            ReturnConfiscatedItemsRPC = NetworkManager.Instance.AddRPC("VENFORCE_RETURN_CONFISCATED", OnServerReturnConfiscatedReceive, OnClientReceiveConfiscatedItems);

            SynchronizationManager.Instance.AddInitialSynchronization(CharacterSaveRPC, SendSavedCharacter);

            LoadYamlConfigs(new Dictionary<string, Action<string>>() {{ ModsConfigFilePath, CreateModsFile }});
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


            InternalStorageMode = BindServerConfig("Advanced", "InternalStorageMode", false, "WARNING: Limit 1 stored character per account. If enabled, player character data will be stored within your world. Enables full portability of the world without having to synchronize configurations.", advanced: true);
        }

        internal static void WritePlayerCharacterToSave(string id, DataObjects.Character character) {
            if (ValConfig.InternalStorageMode.Value) {
                Logger.LogInfo("Saving character with internal storage mode.");
                InternalDataStore.SaveAccountCharacter(character);
            }
            // Double write the data so that if the storage mode is switched the data will still be present.
            Directory.CreateDirectory(Path.Combine(Paths.ConfigPath, ValheimEnforcer, CharacterFolder));
            var saveDir = Directory.CreateDirectory(Path.Combine(Paths.ConfigPath, ValheimEnforcer, CharacterFolder, id));
            string path = Path.Combine(saveDir.FullName, $"{character.Name}.yaml");
            Logger.LogInfo($"Writing to {path}");
            File.WriteAllText(path, DataObjects.yamlserializer.Serialize(character));
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
            var patchesFolderPath = Path.Combine(Paths.ConfigPath, ValheimEnforcer);
            var dirInfo = Directory.CreateDirectory(patchesFolderPath);

            return dirInfo.FullName;
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
                SetupFileWatcher(file);
            }
        }

        private void SetupFileWatcher(string filtername) {
            FileSystemWatcher fw = new FileSystemWatcher();
            fw.Path = ValConfig.GetSecondaryConfigDirectoryPath();
            fw.NotifyFilter = NotifyFilters.LastWrite;
            fw.Filter = filtername;
            fw.Changed += new FileSystemEventHandler(UpdateConfigFileOnChange);
            fw.Created += new FileSystemEventHandler(UpdateConfigFileOnChange);
            fw.Renamed += new RenamedEventHandler(UpdateConfigFileOnChange);
            fw.SynchronizingObject = ThreadingHelper.SynchronizingObject;
            fw.EnableRaisingEvents = true;
        }

        private static void UpdateConfigFileOnChange(object sender, FileSystemEventArgs e) {
            if (SynchronizationManager.Instance.PlayerIsAdmin == false) {
                Logger.LogInfo("Player is not an admin, and not allowed to change local configuration. Ignoring.");
                return;
            }
            if (!File.Exists(e.FullPath)) { return; }

            string filetext = File.ReadAllText(e.FullPath);
            var fileInfo = new FileInfo(e.FullPath);
            Logger.LogDebug($"Filewatch changes from: ({fileInfo.Name}) {fileInfo.FullName}");
            switch (fileInfo.Name) {
                case ModsFileName:
                    Logger.LogDebug("Triggering Mod Settings update.");
                    ModManager.UpdateModSettingConfigs(filetext);
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

            var charFile = Path.Combine(Paths.ConfigPath, ValheimEnforcer, CharacterFolder, $"{id}");
            string fullpath = Path.Combine(charFile, $"{peer.m_playerName}.yaml");
            if (!File.Exists(fullpath)) {
                Logger.LogInfo($"path: {fullpath} does not exist, no character data will be sent.");
                return new ZPackage();
            }
            string filecontents = File.ReadAllText(fullpath);
            package.Write(filecontents);
            return package;
        }

        public static IEnumerator OnServerRecieveCharacter(long sender, ZPackage package) {
            DataObjects.Character chara = DataObjects.yamldeserializer.Deserialize<DataObjects.Character>(package.ReadString());
            Logger.LogInfo($"Recieved Player data update for {sender} - {chara.Name}|{chara.HostID}");
            WritePlayerCharacterToSave(chara.HostID, chara);
            yield break;
        }

        public static IEnumerator OnClientReceiveCharacter(long sender, ZPackage package) {
            DataObjects.Character chara = DataObjects.yamldeserializer.Deserialize<DataObjects.Character>(package.ReadString());
            Logger.LogDebug("Recieved Player character data from server.");
            CharacterManager.SetPlayerCharacter(chara);
            yield break;
        }

        public static IEnumerator OnServerReturnConfiscatedReceive(long sender, ZPackage package) {
            // Clients should not send this RPC to the server
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

        internal static ZPackage SendCharacterAsZpackage(DataObjects.Character chara) {
            string serialChara = DataObjects.yamlserializer.Serialize(chara);
            ZPackage package = new ZPackage();
            package.Write(serialChara);
            return package;
        }

        internal static void SetupMainFileWatcher() {
            // Setup a file watcher to detect changes to the config file
            FileSystemWatcher watcher = new FileSystemWatcher();
            watcher.NotifyFilter = NotifyFilters.LastWrite;
            watcher.Path = Path.GetDirectoryName(cfg.ConfigFilePath);
            // Ignore changes to other files
            watcher.Filter = "MidnightsFX.ImpactfulSkills.cfg";
            watcher.Changed += OnConfigFileChanged;
            watcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
            watcher.EnableRaisingEvents = true;
        }

        private static void OnConfigFileChanged(object sender, FileSystemEventArgs e) {
            // We only want the config changes being allowed if this is a server (ie in game in a hosted world or dedicated ideally)
            if (ZNet.instance.IsServer() == false) {
                return;
            }
            // Handle the config file change event
            Logger.LogInfo("Configuration file has been changed, reloading settings.");
            cfg.Reload();
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
