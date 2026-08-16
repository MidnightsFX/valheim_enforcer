using BepInEx;
using BepInEx.Configuration;
using Jotunn.Entities;
using Jotunn.Managers;
using System;
using System.Collections;
using System.Collections.Concurrent;
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
        public static ConfigEntry<string> HashEnforcement;
        public static ConfigEntry<bool> RecordHashesForLoadedMods;
        public static ConfigEntry<bool> ResolveThunderstoreHashes;
        public static ConfigEntry<int> HashComputeTimeoutSeconds;
        public static ConfigEntry<int> ThunderstoreMaxArchiveMB;
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

        public static ConfigEntry<bool> EnforceCharacterLimit;
        public static ConfigEntry<int> MaxCharactersPerAccount;
        // Comma-separated rather than List<string>: BepInEx's config system only supports primitives,
        // string and enums, so binding a List<string> throws at startup.
        public static ConfigEntry<string> CharacterLimitExemptAccounts;
        public static ConfigEntry<bool> CharacterLimitExemptAdmins;

        public static ConfigEntry<bool> ImportServerCharacters;
        public static ConfigEntry<string> ServerCharactersImportPath;

        public static ConfigEntry<bool> InternalStorageMode;
        public static ConfigEntry<int> ConfigPollIntervalSeconds;
        public static ConfigEntry<int> DeltaSynchronizationFrequencyInSeconds;
        public static ConfigEntry<int> FullSyncPullIntervalMinutes;
        public static ConfigEntry<int> FullSyncMaxConcurrentPlayers;

        public static ConfigEntry<bool> EnableCheatDetection;
        public static ConfigEntry<bool> DetectCheatEngine;
        public static ConfigEntry<bool> DetectValheimTooler;
        public static ConfigEntry<bool> DetectCheatTools;
        public static ConfigEntry<bool> DetectGenericTrainers;
        public static ConfigEntry<bool> ScanLoadedModules;
        public static ConfigEntry<bool> ScanWindowTitles;
        // Comma-separated rather than List<string>: BepInEx's config system only supports primitives,
        // string and enums, so binding a List<string> throws at startup.
        public static ConfigEntry<string> AdditionalCheatProcesses;
        public static ConfigEntry<string> IgnoredCheatProcesses;
        //public static ConfigEntry<bool> DetectSpeedhack;
        public static ConfigEntry<string> CheatDetectionAction;
        public static ConfigEntry<int> CheatScanIntervalSeconds;

        public static ConfigEntry<string> DiscordWebhookUrl;
        public static ConfigEntry<string> DiscordWebhookUrlPlayerActivity;
        public static ConfigEntry<string> DiscordWebhookUrlServerStatus;
        public static ConfigEntry<string> DiscordWebhookUrlModeration;
        public static ConfigEntry<string> DiscordWebhookUrlModMismatch;
        public static ConfigEntry<string> DiscordServerLabel;
        public static ConfigEntry<bool> DiscordNotifyServerStartup;
        public static ConfigEntry<bool> DiscordNotifyServerShutdown;
        public static ConfigEntry<bool> DiscordNotifyWorldSaved;
        public static ConfigEntry<bool> DiscordNotifyPlayerJoined;
        public static ConfigEntry<bool> DiscordNotifyPlayerLeft;
        public static ConfigEntry<bool> DiscordNotifyWrongMods;
        public static ConfigEntry<bool> DiscordNotifyCheaterBanned;
        public static ConfigEntry<bool> DiscordNotifyCharacterRejected;

        internal const string ModsFileName = "Mods.yaml";
        internal const string ValheimEnforcer = "ValheimEnforcer";
        internal const string CharacterFolder = "Characters";
        internal const string KnownCheatersFileName = "KnownCheaters.yaml";
        internal const string NotificationsFileName = "Notifications.yaml";
        internal static String ModsConfigFilePath = Path.Combine(Paths.ConfigPath, ValheimEnforcer, ModsFileName);
        internal static String CharacterFilePath = Path.Combine(Paths.ConfigPath, ValheimEnforcer, CharacterFolder);
        internal static String KnownCheatersFilePath = Path.Combine(Paths.ConfigPath, ValheimEnforcer, KnownCheatersFileName);
        internal static String NotificationsFilePath = Path.Combine(Paths.ConfigPath, ValheimEnforcer, NotificationsFileName);

        internal static CustomRPC CharacterSaveRPC;
        internal static CustomRPC ReturnConfiscatedItemsRPC;
        internal static CustomRPC CheatDetectionRPC;
        internal static CustomRPC ItemDeltaUpdateRPC;
        internal static CustomRPC ListPlayerRPC;
        internal static CustomRPC ClearConfiscatedRPC;
        internal static CustomRPC FullSyncRequestRPC;
        internal static CustomRPC ImportServerCharactersRPC;
        internal static CustomRPC TestNotificationRPC;

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
            ImportServerCharactersRPC = NetworkManager.Instance.AddRPC("VENFORCE_IMPORT_SC", OnServerReceiveImportRequest, OnClientReceiveImportReport);
            TestNotificationRPC = NetworkManager.Instance.AddRPC("VENFORCE_TEST_NOTIFY", OnServerReceiveTestNotification, OnClientReceiveTestNotificationReport);

            SynchronizationManager.Instance.AddInitialSynchronization(CharacterSaveRPC, SendSavedCharacter);

            LoadYamlConfigs(new Dictionary<string, Action<string>>() {
                { ModsConfigFilePath, CreateModsFile },
                { KnownCheatersFilePath, CreateKnownCheatersFile },
                { NotificationsFilePath, CreateNotificationsFile }
            });
            KnownCheaterTracker.Initialize();
            NotificationTemplates.Initialize();
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
            HashEnforcement = BindServerConfig("Mods", "HashEnforcement", "WhenKnown", "Controls SHA256 file verification of client plugin DLLs during the connect handshake, which catches a mod somebody recompiled with different numbers in it even though its version string is unchanged. 'Off' never checks. 'WhenKnown' (the default) enforces only the mods this server has a recorded hash for, so verification is opt-in per mod and enabling it breaks nothing. 'Strict' additionally rejects any client carrying a Required or AdminOnly mod the server has NO recorded hash for - a deliberately loud signal that the mod list is not fully pinned. Individual mods override this with a 'hashEnforcement' field in Mods.yaml. Note this raises the bar from 'edit one file and rebuild' to 'reverse engineer and patch the enforcer'; it is not a wall.", new AcceptableValueList<string>("Off", "WhenKnown", "Strict"));
            RecordHashesForLoadedMods = BindServerConfig("Mods", "RecordHashesForLoadedMods", true, "If enabled, the SHA256 of every plugin DLL loaded on this machine is recorded into Mods.yaml at startup, so the mods the server itself runs get pinned with no manual work. Hashes an admin pinned by hand, or that came from a thunderstorePackage, are never overwritten. Requires UpdateLoadedModsOnStartup for the result to reach disk.");
            ResolveThunderstoreHashes = BindServerConfig("Mods", "ResolveThunderstoreHashes", false, "If enabled, the server downloads any mod in Mods.yaml carrying a 'thunderstorePackage' field (format Owner-ModName or Owner-ModName-Version, the same format a Thunderstore manifest uses), hashes the DLLs inside the archive in memory, records them, and discards the download. This is how you pin a client-only mod the server never loads itself. Only thunderstore.io and its CDN are ever contacted; arbitrary download URLs are deliberately not supported. Off by default because it makes outbound network requests.");
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

            EnforceCharacterLimit = BindServerConfig("Player Sync", "EnforceCharacterLimit", false, "Master switch for the one-character-per-account rule. When enabled, an account may only join with a character the server already has a save for, up to MaxCharactersPerAccount; any other character is refused at the connect handshake and told which character to use instead. Characters that already have a save are always allowed, so turning this on never locks out an existing player - it only stops new characters being added. Freeing a slot means deleting that character's save file (BepInEx/config/ValheimEnforcer/Characters/<accountId>/<Name>.yaml), which is what a character reset already involves. Off by default.");
            MaxCharactersPerAccount = BindServerConfig("Player Sync", "MaxCharactersPerAccount", 1, "How many characters one account may have on this server when EnforceCharacterLimit is enabled. Accounts that already have more than this keep every character they have; the limit only blocks adding another.", valmin: 1, valmax: 20);
            CharacterLimitExemptAccounts = BindServerConfig("Player Sync", "CharacterLimitExemptAccounts", "", "Comma-separated list of account ids allowed to connect with any number of characters, regardless of EnforceCharacterLimit. Independent of admin status - an id listed here does not need to be an admin, and an admin is not exempt unless listed (or CharacterLimitExemptAdmins is enabled). Both the platform-prefixed form (Steam_76561198012345678) and the bare id (76561198012345678) are accepted. Note this setting is synced to connected clients, so the ids in it are visible to players.");
            CharacterLimitExemptAdmins = BindServerConfig("Player Sync", "CharacterLimitExemptAdmins", false, "If enabled, anyone on the server's adminlist is exempt from the character limit without needing an entry in CharacterLimitExemptAccounts. Off by default so the two permissions stay separate.");

            // Migration. Deliberately local (non-synced) configs: these are server-only operational settings
            // with no client-side behaviour, and the path in particular would otherwise be pushed to every
            // connected client, exposing the server's filesystem layout. Edit them in the config file; the
            // server-side main file watcher reloads it.
            ImportServerCharacters = BindLocalConfig("Migration", "ImportServerCharacters", false, "If enabled, the server imports character saves from the ServerCharacters mod once at startup, so players migrating from it keep their inventory and skills instead of having everything confiscated on their first join. Characters that already have a save here are left alone, so the pass is safe to leave on. IMPORTANT: uninstall ServerCharacters first - the two mods are declared incompatible and BepInEx will refuse to load ValheimEnforcer while both are present. The files ServerCharacters leaves behind in the character folder are what gets read; nothing is moved or deleted. Off by default.");
            ServerCharactersImportPath = BindLocalConfig("Migration", "ServerCharactersImportPath", "", "Where to look for ServerCharacters' character files. Leave empty to use the game's own local character folder, which is where ServerCharacters puts them and which follows Valheim's -savedir argument automatically. Only set this if you moved the files somewhere else.");

            // portable mode
            InternalStorageMode = BindServerConfig("Advanced", "InternalStorageMode", false, "If enabled, player character data will be stored within your world. Enables full portability of the world without having to synchronize configurations.", advanced: true);
            ConfigPollIntervalSeconds = BindServerConfig("Advanced", "ConfigPollIntervalSeconds", 30, "How frequently (in seconds) the mod polls config files on disk for changes.", advanced: true, valmin: 1, valmax: 300);
            DeltaSynchronizationFrequencyInSeconds = BindServerConfig("Advanced", "CharacterDeltaTracker", 15, "Minimum time (in seconds) between incremental inventory/skill/custom-data updates. Updates are only produced when the player's inventory actually changes, so an idle player sends nothing; this is a rate limit rather than a polling interval.", advanced: true, valmin: 5, valmax: 300);
            FullSyncPullIntervalMinutes = BindServerConfig("Advanced", "FullSyncPullIntervalMinutes", 25, "How often (in minutes) the server asks connected players to upload a full character save. Full saves are a periodic reconciliation layered on top of the incremental delta updates (CharacterDeltaTracker); they are no longer tied to the world/profile autosave.", advanced: true, valmin: 1, valmax: 1440);
            HashComputeTimeoutSeconds = BindServerConfig("Advanced", "HashComputeTimeoutSeconds", 30, "Maximum time spent hashing local plugin DLLs at startup before giving up and reporting the remainder as unverifiable. Hashing runs on background threads and usually takes well under a second; this is a safety valve for a stalled disk, not a tuning knob.", advanced: true, valmin: 5, valmax: 300);
            ThunderstoreMaxArchiveMB = BindServerConfig("Advanced", "ThunderstoreMaxArchiveMB", 128, "Largest Thunderstore archive, in megabytes, the server will download when resolving mod hashes. Archives are held in memory while their DLLs are hashed, so this is also the peak transient allocation; packages are resolved one at a time so it is never multiplied. Larger archives are skipped and logged.", advanced: true, valmin: 1, valmax: 512);
            FullSyncMaxConcurrentPlayers = BindServerConfig("Advanced", "FullSyncMaxConcurrentPlayers", 5, "Maximum number of players the server asks to upload a full character save at the same time. Larger player counts are staggered into successive waves of this size to avoid a bandwidth spike. 10 is safe on a healthy server; lower it on constrained upload/VPS hosts.", advanced: true, valmin: 1, valmax: 50);

            EnableCheatDetection = BindServerConfig("Anti-Cheat", "EnableCheatDetection", true, "Master switch for client-side cheat scanning. When enabled the client checks running processes, the DLLs loaded into the game, and open window titles against a catalog of known cheat tools. Only matched entries are reported to the server - the player's full process list is never transmitted.");
            DetectValheimTooler = BindServerConfig("Anti-Cheat", "DetectValheimTooler", true, "Detect ValheimTooler by the namespace of the types it loads (rename-proof), including assemblies injected mid-session. A confirmed detection is always auto-banned regardless of ActionOnDetection. High confidence, very low cost.");
            DetectCheatTools = BindServerConfig("Anti-Cheat", "DetectCheatTools", true, "Scan for the built-in catalog of known cheat tools: WeMod/Wand, ArtMoney, PLITCH, Speed Gear, Squalr, WPE Pro, and the injectors/loaders used to deliver Valheim cheats (SharpMonoInjector, Xenos, Extreme Injector, ValheimTooler launcher, ValHack, Valheim Mod Menu). Tools with no legitimate purpose are auto-banned; the rest follow ActionOnDetection.");
            DetectCheatEngine = BindServerConfig("Anti-Cheat", "DetectCheatEngine", true, "Include Cheat Engine in the catalog scan (process names, TfrmMain/TfrmMemView windows, and injected speedhack/DBK modules). Note: Cheat Engine has legitimate uses — prefer Log action over Kick/Ban. Requires DetectCheatTools.");
            DetectGenericTrainers = BindServerConfig("Anti-Cheat", "DetectGenericTrainers", true, "Flag any running process whose executable name contains the word 'trainer' (e.g. 'Valheim Trainer.exe', 'Hitman 3 Trainer - FLiNG.exe'). Catches FLiNG, MrAntiFun and Cheat Happens trainers without listing each one. Follows ActionOnDetection.");
            ScanLoadedModules = BindServerConfig("Anti-Cheat", "ScanLoadedModules", true, "Scan the native DLLs loaded into the game process itself. This is the only way to see a cheat that has already injected and then closed its launcher, and it survives renaming the tool's executable. Cheap - the module list is local to our own process.");
            ScanWindowTitles = BindServerConfig("Anti-Cheat", "ScanWindowTitles", true, "Scan open window classes and titles. Catches tools that have been renamed to evade the process-name check, most notably Cheat Engine (window class TfrmMain is not affected by renaming the exe).");
            AdditionalCheatProcesses = BindServerConfig("Anti-Cheat", "AdditionalCheatProcesses", "", "Comma-separated list of extra process names to treat as cheat tools, without the '.exe' suffix, matched exactly and case-insensitively. Empty by default. Suggested opt-in values for strict servers: x64dbg, x32dbg, x96dbg, ProcessHacker, SystemInformer, HxD, ReClass.NET, ollydbg, Scylla_x64, frida, Fiddler, Charles. WARNING: every one of those is a standard developer tool with heavy legitimate use by modders and streamers, which is why none of them ship enabled. Deliberately excluded from the built-in catalog and NOT recommended here: Aurora (collides with Aurora RGB lighting software), Process Lasso (a CPU priority optimiser, not a speedhack), AutoHotkey (compiled scripts take arbitrary names, so the check is worthless, and it is widely used for accessibility and key remapping), and MSI Afterburner/RivaTuner/OBS (their overlay DLLs look injector-shaped).");
            IgnoredCheatProcesses = BindServerConfig("Anti-Cheat", "IgnoredCheatProcesses", "", "Comma-separated allowlist of process, module or window names to never flag, matched as a case-insensitive substring. Applied last, so it overrides the built-in catalog and AdditionalCheatProcesses. Use this to keep playing when a legitimate program trips a signature.");
            //DetectSpeedhack = BindServerConfig("Anti-Cheat", "DetectSpeedhack", true, "Detect speedhack via Unity time vs. wall-clock drift.");
            CheatDetectionAction = BindServerConfig("Anti-Cheat", "ActionOnDetection", "Kick", "Server-side action taken when a cheat tool is reported. Note that dedicated game-cheating tools (injectors, ValheimTooler, ValHack, Valheim Mod Menu) are always auto-banned regardless of this setting.", new AcceptableValueList<string>("Log", "Kick", "Ban"));
            CheatScanIntervalSeconds = BindServerConfig("Anti-Cheat", "ScanIntervalSeconds", 30, "Seconds between periodic client scan ticks. The process, module and window scans are staggered across successive ticks so their cost never lands on the same frame, so each individual scan runs every three intervals. ValheimTooler assembly detection is event-driven and not affected by this interval.", false, 5, 300);

            // Discord notifications. These are intentionally LOCAL (non-synced) configs: the webhook URL is a secret and must not be synced to clients
            DiscordWebhookUrl = BindLocalConfig("Discord", "WebhookUrl", "", "Discord webhook URL the server posts notifications to. This is a server-only secret and is never synced to clients. Leave empty to disable. Note: player names are sent to Discord when enabled. Every category falls back to this URL unless it has one of its own, so a server that wants everything in one channel only needs this setting.");
            DiscordWebhookUrlPlayerActivity = BindLocalConfig("Discord", "WebhookUrlPlayerActivity", "", "Webhook URL for player joins and leaves. Leave empty to use WebhookUrl. Set this to keep routine join/leave traffic out of the channel you actually watch - it is by far the noisiest category on a busy server.");
            DiscordWebhookUrlServerStatus = BindLocalConfig("Discord", "WebhookUrlServerStatus", "", "Webhook URL for server startup, shutdown and world-save messages. Leave empty to use WebhookUrl.");
            DiscordWebhookUrlModeration = BindLocalConfig("Discord", "WebhookUrlModeration", "", "Webhook URL for cheat bans and character-limit rejections. Leave empty to use WebhookUrl. This is the one worth pointing at a private moderator channel: the messages name the account behind a ban.");
            DiscordWebhookUrlModMismatch = BindLocalConfig("Discord", "WebhookUrlModMismatch", "", "Webhook URL for connections refused over a mod mismatch. Leave empty to use WebhookUrl. Often worth a support channel of its own, since the message lists exactly which mods the player needs to fix.");
            DiscordServerLabel = BindLocalConfig("Discord", "ServerLabel", "", "Name for this server in notification messages, available to templates as the {server} placeholder. Empty by default, and no built-in template uses it - set it only if several servers post into the same channel and you need to tell them apart. Deliberately a setting rather than the server's advertised name, so it also works on a player-hosted world.");
            DiscordNotifyServerStartup = BindLocalConfig("Discord", "NotifyServerStartup", true, "Post a message when the server comes online.");
            DiscordNotifyServerShutdown = BindLocalConfig("Discord", "NotifyServerShutdown", true, "Post a message when the server shuts down.");
            DiscordNotifyWorldSaved = BindLocalConfig("Discord", "NotifyWorldSaved", false, "Post a message every time the world is saved, covering both the periodic autosave and a manual 'save' from the console. Off by default because the autosave fires roughly every twenty minutes, all day, whether or not anyone is playing - on most servers that buries everything else in the channel. Worth turning on temporarily when you are chasing a save problem, or permanently if it has its own channel via WebhookUrlServerStatus.");
            DiscordNotifyPlayerJoined = BindLocalConfig("Discord", "NotifyPlayerJoined", true, "Post a message when a player joins.");
            DiscordNotifyPlayerLeft = BindLocalConfig("Discord", "NotifyPlayerLeft", true, "Post a message when a player leaves, including whether their saved data is up to date.");
            DiscordNotifyWrongMods = BindLocalConfig("Discord", "NotifyWrongMods", true, "Post a message when a player is rejected for a mod mismatch, listing the offending mods.");
            DiscordNotifyCheaterBanned = BindLocalConfig("Discord", "NotifyCheaterBanned", true, "Post a message when a player is banned for cheat usage, including the detected cheat(s).");
            DiscordNotifyCharacterRejected = BindLocalConfig("Discord", "NotifyCharacterRejected", true, "Post a message when a connection is refused by EnforceCharacterLimit, naming the character that was turned away.");
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
                    // An admin may have just added or repinned a thunderstorePackage.
                    if (ZNet.instance != null && ZNet.instance.IsServer()) {
                        modules.mods.ThunderstoreResolver.RequestPass("Mods.yaml changed");
                    }
                    break;
                case KnownCheatersFileName:
                    Logger.LogDebug("Triggering KnownCheaters list update.");
                    KnownCheaterTracker.LoadFromText(filetext);
                    break;
                case NotificationsFileName:
                    Logger.LogDebug("Triggering notification template update.");
                    // Deliberately not persisting the filled-in defaults here, unlike the startup path: an admin
                    // mid-edit would get their file rewritten under them one poll after every save.
                    NotificationTemplates.LoadFromText(filetext);
                    break;
            }
        }

        private static void CreateModsFile(string filepath) {
            Logger.LogDebug("Mods config missing, recreating.");
            using (StreamWriter writetext = new StreamWriter(filepath)) {
                // Shared with the header restore in ModManager.PersistModSettings, so a file that is recreated
                // and one that is rewritten carry the same guide. It survives rewrites now that comments do.
                writetext.WriteLine(string.Join(Environment.NewLine, ModManager.ModsFileHeaderLines));
                writetext.WriteLine();
                writetext.WriteLine(ModManager.GetDefaultConfig());
            }
        }

        private static void CreateNotificationsFile(string filepath) {
            Logger.LogDebug("Notification templates file missing, recreating.");
            // The embedded copy verbatim - banner and templates together, exactly as it sits in the repo. Not
            // reserialized from the parsed object: the file is hand-authored JSON inside YAML, and a round trip
            // through the serializer would reflow it into something less pleasant to read than what was written.
            File.WriteAllText(filepath, NotificationTemplates.GetDefaultConfig());
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
                return SendCharacterToClientAsZpackage(chara);
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

            // Both branches below strip ConfiscatedItems before the payload goes out (see
            // SendCharacterToClientAsZpackage). That costs a parse + re-serialize on a path that otherwise just
            // forwards a cached string, which is accepted deliberately: this runs once per player connect, not on
            // the save-burst path the async store exists to protect, and the disk branch already does file I/O.
            // If connect latency ever becomes a concern, cache the stripped form on CharacterStore.Entry and have
            // the worker thread produce it.
            string cached = modules.character.CharacterStore.GetYamlIfCurrent(id, peer.m_playerName, diskMtime);
            if (cached != null) {
                package.Write(StripConfiscatedItemsFromYaml(cached));
                return package;
            }

            if (!exists) {
                Logger.LogInfo($"path: {fullpath} does not exist, no character data will be sent.");
                return new ZPackage();
            }
            string filecontents = File.ReadAllText(fullpath);
            // Seed the store with the FULL save - it is the server's authoritative copy. Only the outbound
            // payload is stripped.
            modules.character.CharacterStore.Seed(id, peer.m_playerName, filecontents, diskMtime);
            package.Write(StripConfiscatedItemsFromYaml(filecontents));
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
                    // The client's confiscated list is a report of what it confiscated this session, never a
                    // replacement for ours - see Character.MergeConfiscatedItems.
                    DataObjects.Character existing = InternalDataStore.GetAccountCharacter(chara.HostID, chara.Name);
                    List<PackedItem> reported = chara.ConfiscatedItems;
                    chara.ConfiscatedItems = existing?.ConfiscatedItems ?? new List<PackedItem>();
                    int appended = chara.MergeConfiscatedItems(reported);
                    if (appended > 0) {
                        Logger.LogInfo($"Recorded {appended} newly confiscated item(s) for {chara.Name}.");
                    }
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
            // The call above only touches a copy loaded from disk. The in-memory character is what gets pushed
            // back to the server, so it has to be cleared too - otherwise the entries the admin just removed are
            // still held here and would be re-appended by confiscationId on this session's next full push.
            ClearInMemoryConfiscatedItems(data.ItemPrefabFilter);
            yield break;
        }

        // Client side: drop confiscated entries matching an admin's /clear filter from the tracked character.
        // Mirrors the filter handling in CommandHelpers.ClearSpecifiedPlayerConfiscatedItems.
        private static void ClearInMemoryConfiscatedItems(string prefabFilter) {
            DataObjects.Character tracked = CharacterManager.PlayerCharacter;
            if (tracked?.ConfiscatedItems == null || tracked.ConfiscatedItems.Count == 0) { return; }

            int before = tracked.ConfiscatedItems.Count;
            if (string.Compare(prefabFilter, "all", true) == 0) {
                tracked.ConfiscatedItems.Clear();
            } else {
                List<string> targets = prefabFilter.Split(',').Select(s => s.Trim()).ToList();
                tracked.ConfiscatedItems.RemoveAll(i => i != null && targets.Contains(i.prefabName));
            }
            Logger.LogDebug($"Cleared {before - tracked.ConfiscatedItems.Count} tracked confiscated item(s) locally.");
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
            // Send the updated player character to the client so that their client-side data is also updated with
            // the returned items. Stripped of the confiscated list like every other client-bound character send -
            // which also resets the client's in-memory copy, so it cannot re-report the entries we just returned.
            ValConfig.CharacterSaveRPC.SendPackage(targetPeer.m_uid, ValConfig.SendCharacterToClientAsZpackage(character));
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
            Logger.LogWarning($"Cheat detection from {playerName} ({endpoint}): valheim-tooler: {summary.ValheimToolerStatus} tools: {DescribeDetectedTools(summary)}");

            // ValheimTooler is unambiguous cheat software; always ban regardless of ActionOnDetection.
            if (summary.ValheimToolerStatus) {
                Logger.LogWarning($"Banning {playerName} for ValheimTooler usage.");
                BanCheater(peer, playerName, summary);
                yield break;
            }

            // Tools with no purpose other than cheating also ban on sight. AutoBan is resolved from
            // the server's own catalog by label, never taken from the payload, so a tampered client
            // cannot escalate a report into a ban.
            if (summary.DetectedTools != null) {
                foreach (DataObjects.CheatToolDetection detection in summary.DetectedTools) {
                    if (CheatToolCatalog.IsAutoBan(detection.Tool)) {
                        Logger.LogWarning($"Banning {playerName} for {detection.Tool} usage.");
                        BanCheater(peer, playerName, summary);
                        yield break;
                    }
                }
            }

            // Everything else honors the configured action.
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
                DiscordNotifier.Notify(NotificationEvent.CheaterBanned, new Dictionary<string, string> {
                    { "player", playerName },
                    { "playerId", hostId },
                    { "reason", reason },
                    { "detections", DescribeDetectedTools(summary) },
                    { "action", "Ban" },
                });
            }
        }

        private static string BuildCheatReason(DataObjects.CheatSummaryReport summary) {
            List<string> detections = new List<string>();
            if (summary.ValheimToolerStatus) { detections.Add("ValheimTooler"); }
            if (summary.DetectedTools != null) {
                foreach (CheatToolDetection detection in summary.DetectedTools) {
                    detections.Add($"{detection.Tool} ({detection.Vector}: {detection.Detail})");
                }
            }
            string detail = detections.Count > 0 ? string.Join(", ", detections) : "cheat detected";
            return $"Cheat detection: {detail}";
        }

        // Compact one-line rendering of the reported tools for the server log.
        private static string DescribeDetectedTools(DataObjects.CheatSummaryReport summary) {
            if (summary.DetectedTools == null || summary.DetectedTools.Count == 0) { return "none"; }
            return string.Join(", ", summary.DetectedTools.Select(d => $"{d.Tool} [{d.Vector}: {d.Detail}]"));
        }

        public static IEnumerator OnClientReceiveCheatReport(long sender, ZPackage package) {
            // Client -> server only; clients do not act on this RPC.
            yield break;
        }

        public static IEnumerator OnClientReceiveImportReport(long sender, ZPackage package) {
            foreach (string line in package.ReadString().Split('\n')) {
                Logger.LogInfo(line.TrimEnd());
            }
            yield break;
        }

        public static IEnumerator OnServerReceiveImportRequest(long sender, ZPackage package) {
            // Unlike the older command RPCs this one is admin gated on the server. IsCheat on the console
            // command only gates the vanilla client; this handler can write character saves (and overwrite them
            // outright with 'force'), so it must not take a crafted client's word for who is asking.
            ZNetPeer peer = ZNet.instance?.GetPeer(sender);
            string hostId = peer?.m_socket?.GetHostName();
            if (string.IsNullOrEmpty(hostId) || !ZNet.instance.IsAdmin(hostId)) {
                Logger.LogWarning($"Ignoring a ServerCharacters import request from non-admin {hostId ?? sender.ToString()}.");
                yield break;
            }

            // Anything unrecognised falls through to a dry run - the safe reading of a malformed request.
            string mode = package.ReadString() ?? "";
            bool force = mode.IndexOf("force", StringComparison.OrdinalIgnoreCase) >= 0;
            bool dryRun = mode.IndexOf("import", StringComparison.OrdinalIgnoreCase) < 0;

            string summary;
            try {
                summary = modules.migration.ServerCharactersImport.Run(dryRun, force).Summary();
            } catch (Exception e) {
                summary = $"ServerCharacters import failed: {e.Message}";
                Logger.LogError($"ServerCharacters import failed: {e}");
            }
            Logger.LogInfo(summary);
            // Write the text into the package rather than using the ZPackage(string) constructor: that overload
            // is a base64 decoder (Convert.FromBase64String) and throws on arbitrary text.
            ZPackage reply = new ZPackage();
            reply.Write(summary);
            ValConfig.ImportServerCharactersRPC.SendPackage(sender, reply);
            yield break;
        }

        public static IEnumerator OnClientReceiveTestNotificationReport(long sender, ZPackage package) {
            foreach (string line in package.ReadString().Split('\n')) {
                Logger.LogInfo(line.TrimEnd());
            }
            yield break;
        }

        /// <summary>
        /// The last time a test notification was accepted, so a held-down key cannot walk the webhook into
        /// Discord's rate limiter. Getting a webhook temporarily throttled would silence the real notifications
        /// too, which is a bad trade for a preview command.
        /// </summary>
        private static DateTime lastTestNotification = DateTime.MinValue;
        private static readonly TimeSpan TestNotificationCooldown = TimeSpan.FromSeconds(3);

        /// <summary>
        /// Posts one sample notification on behalf of an admin running Enforcer-Test-Notification from a
        /// connected client, and reports the outcome back to them.
        ///
        /// Admin gated on the server, for the same reason the ServerCharacters import is: IsCheat on the console
        /// command only gates the vanilla client, and this handler makes the server send an outbound HTTP
        /// request to a webhook URL that clients are deliberately never told. Taking a crafted client's word for
        /// who is asking would hand every connected player a button that posts into the server's Discord.
        /// </summary>
        public static IEnumerator OnServerReceiveTestNotification(long sender, ZPackage package) {
            ZNetPeer peer = ZNet.instance?.GetPeer(sender);
            string hostId = peer?.m_socket?.GetHostName();
            if (string.IsNullOrEmpty(hostId) || !ZNet.instance.IsAdmin(hostId)) {
                Logger.LogWarning($"Ignoring a test notification request from non-admin {hostId ?? sender.ToString()}.");
                yield break;
            }

            string requested = package.ReadString() ?? "";
            string reply;
            if (!Enum.TryParse(requested, true, out NotificationEvent evt) || !Enum.IsDefined(typeof(NotificationEvent), evt)) {
                // IsDefined as well as TryParse: TryParse happily accepts a bare number for any enum, so "99"
                // would otherwise come through as a NotificationEvent nothing can render.
                reply = $"Unknown notification event '{requested}'. One of: {string.Join(", ", Enum.GetNames(typeof(NotificationEvent)))}";
            } else if (DateTime.UtcNow - lastTestNotification < TestNotificationCooldown) {
                reply = "A test notification was just sent - wait a moment before sending another.";
            } else if (!DiscordNotifier.IsValidWebhookUrl(DiscordNotifier.ResolveUrl(NotificationTemplates.CategoryOf(evt)))) {
                reply = $"No usable webhook URL for the {NotificationTemplates.CategoryOf(evt)} category. Set Discord.WebhookUrl on the server, or the URL for that category.";
            } else {
                lastTestNotification = DateTime.UtcNow;
                DiscordNotifier.Notify(evt, NotificationTemplates.SampleTokens());
                reply = $"Posted a sample {evt} notification to the {NotificationTemplates.CategoryOf(evt)} webhook.";
                Logger.LogInfo($"{reply} Requested by admin {hostId}.");
            }

            // Write the text into the package rather than using the ZPackage(string) constructor: that overload
            // is a base64 decoder (Convert.FromBase64String) and throws on arbitrary text.
            ZPackage response = new ZPackage();
            response.Write(reply);
            ValConfig.TestNotificationRPC.SendPackage(sender, response);
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
                if (UpdatePlayerSaveWithDeltaData(deltaUpdate, character)) {
                    // Our copy no longer matches the client's baseline, so no later delta can repair it.
                    RequestFullSyncForDrift(sender, deltaUpdate.HostID, deltaUpdate.Name);
                }
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
            modules.character.CharacterStore.SubmitDelta(deltaUpdate, sender);
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

        // A full save takes a moment to arrive and the client keeps streaming deltas in the meantime, every one of
        // which can re-detect the same drift. Without a cooldown a single divergence would pull a full save on
        // every flush for as long as it lasted. Not exposed as config, matching FullSyncScheduler.WaveStaggerSeconds.
        private const double DriftResyncCooldownSeconds = 60d;
        private static readonly ConcurrentDictionary<string, DateTime> lastDriftResync = new ConcurrentDictionary<string, DateTime>();

        /// <summary>
        /// Main thread only. Ask a client for a full character save because a delta merge found our copy had
        /// drifted, rate limited per character. Callers on the CharacterStore worker must not call this directly -
        /// they queue the request for the main thread instead (see CharacterStore.TryDequeueDriftResync).
        /// </summary>
        internal static void RequestFullSyncForDrift(long sender, string hostId, string name) {
            string key = modules.character.CharacterStore.KeyFor(hostId, name);
            DateTime now = DateTime.UtcNow;
            if (lastDriftResync.TryGetValue(key, out DateTime last)
                && (now - last).TotalSeconds < DriftResyncCooldownSeconds) {
                Logger.LogDebug($"Drift resync for {name} already requested recently; skipping.");
                return;
            }
            lastDriftResync[key] = now;

            // The peer may have gone since the delta was received (disk mode queues this across frames).
            if (ZNet.instance == null || ZNet.instance.GetPeer(sender) == null) {
                Logger.LogDebug($"Not requesting a drift resync for {name}: peer {sender} is no longer connected.");
                return;
            }

            Logger.LogInfo($"Requesting a full character sync from {name} ({hostId}) to repair drifted server state.");
            ZPackage req = new ZPackage();
            req.Write(name);
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
        /// <returns>
        /// True when the merge detected drift - a Removed delta that matched nothing at all, not even
        /// <see cref="DataObjects.Character.RemoveFromPlayerItems"/>'s fuzzy fallback. That means our copy no
        /// longer reflects the client's baseline, so no further delta can reconcile it and the caller should ask
        /// the client for a full save. Callers, not this method, issue that request: this runs off the main
        /// thread in disk mode and must not touch ZNet.
        /// </returns>
        internal static bool MergeDelta(DeltaSummaryUpdate deltaSummary, DataObjects.Character character) {
            bool drifted = false;
            // Apply item deltas
            foreach (ItemDelta delta in deltaSummary.ItemModifications) {
                switch (delta.Op) {
                    case ItemDeltaChangeType.Added:
                        character.PlayerItems.Add(delta.Item);
                        Logger.LogDebug($"Delta: added {delta.Item.prefabName} x{delta.Item.m_stack}.");
                        break;
                    case ItemDeltaChangeType.Removed:
                        if (!character.RemoveFromPlayerItems(delta.Item)) {
                            drifted = true;
                            Logger.LogWarning($"Delta removal for {character.Name} found no match for {delta.Item?.prefabName} x{delta.Item?.m_stack}; our copy has drifted from the client's baseline.");
                        }
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

            return drifted;
        }

        // Internal-storage delta persistence — runs on the main thread because it writes the registry ZDO.
        // Disk mode routes deltas through the async CharacterStore instead.
        // Returns true when the merge detected drift and the client should be asked for a full save.
        internal static bool UpdatePlayerSaveWithDeltaData(DeltaSummaryUpdate deltaSummary, DataObjects.Character character) {
            bool drifted = MergeDelta(deltaSummary, character);

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

            return drifted;
        }

        internal static ZPackage SendCharacterAsZpackage(DataObjects.Character chara) {
            string serialChara = DataObjects.yamlserializer.Serialize(chara);
            ZPackage package = new ZPackage();
            package.Write(serialChara);
            return package;
        }

        /// <summary>
        /// Server -> client character payload, with ConfiscatedItems withheld.
        ///
        /// The client has no use for the confiscated history (nothing client side reads it) and mirroring it back
        /// on every full push wasted a lot of bandwidth - a real test character carried 239 entries in a 309KB
        /// save, re-sent both directions on join, death, respawn, logout and every full-sync pull. Worse, the
        /// mirror went stale the moment an admin ran /clear or /return, and the client's next push resurrected
        /// what the admin had removed. With the list withheld, a client's ConfiscatedItems only ever holds what it
        /// confiscated this session, which is exactly what MergeConfiscatedItems expects to receive.
        ///
        /// Deliberately NOT folded into SendCharacterAsZpackage: that one also serves client -> server pushes,
        /// which must keep carrying the new confiscations.
        /// </summary>
        internal static ZPackage SendCharacterToClientAsZpackage(DataObjects.Character chara) {
            if (chara == null) { return new ZPackage(); }
            List<PackedItem> held = chara.ConfiscatedItems;
            try {
                chara.ConfiscatedItems = null;
                return SendCharacterAsZpackage(chara);
            } finally {
                // The caller's object is server-side authoritative state; never leave it stripped.
                chara.ConfiscatedItems = held;
            }
        }

        /// <summary>Same as <see cref="SendCharacterToClientAsZpackage"/> but for a raw YAML string that has not
        /// been parsed yet - used on the connect path, where the store hands back cached/on-disk YAML. Falls back
        /// to the original text if it cannot be parsed, so a corrupt save still reaches the client unchanged
        /// rather than becoming an empty payload.</summary>
        internal static string StripConfiscatedItemsFromYaml(string yaml) {
            if (string.IsNullOrEmpty(yaml)) { return yaml; }
            try {
                DataObjects.Character chara = DataObjects.yamldeserializer.Deserialize<DataObjects.Character>(yaml);
                if (chara == null) { return yaml; }
                chara.ConfiscatedItems = null;
                return DataObjects.yamlserializer.Serialize(chara);
            } catch (Exception e) {
                Logger.LogWarning($"Could not strip confiscated items from a character payload, sending it as-is: {e.Message}");
                return yaml;
            }
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
