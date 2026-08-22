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
using ValheimEnforcer.modules.notifications;
using ValheimEnforcer.modules.worldintegrity;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer {
    internal class ValConfig {
        public static ConfigFile cfg;
        public static ConfigEntry<bool> EnableDebugMode;
        public static ConfigEntry<bool> EnableTerminalColors;
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
        // Comma-separated rather than List<string>: BepInEx's config system only supports primitives,
        // string and enums, so binding a List<string> throws at startup.
        public static ConfigEntry<string> NewCharacterStartingItems;
        public static ConfigEntry<bool> ServerSideNewCharacterEnforcement;
        public static ConfigEntry<bool> ConfiscateUnidentifiableItems;
        public static ConfigEntry<int> InitialCharacterSyncWaitSeconds;
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

        public static ConfigEntry<bool> EnableStructureValidation;
        public static ConfigEntry<bool> DetectNonBuildableStructures;
        public static ConfigEntry<bool> DetectExcessiveStructureHealth;
        public static ConfigEntry<float> StructureHealthAllowedMultiplier;
        public static ConfigEntry<string> StructureValidationAction;
        public static ConfigEntry<bool> RemoveDetectedStructures;
        public static ConfigEntry<bool> StructureValidationExemptAdmins;
        // Comma-separated rather than List<string>: BepInEx's config system only supports primitives,
        // string and enums, so binding a List<string> throws at startup.
        public static ConfigEntry<string> IgnoredStructurePrefabs;

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
        public static ConfigEntry<bool> DiscordNotifyStructureFlagged;

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
        internal static CustomRPC CheatDetectionRPC;
        internal static CustomRPC ItemDeltaUpdateRPC;
        internal static CustomRPC FullSyncRequestRPC;

        // Server to the affected client only. Both used to double as the admin's request channel as well;
        // that half now goes through ClientCommandRequestRPC, leaving these to do one thing each.
        internal static CustomRPC ReturnConfiscatedItemsRPC;
        internal static CustomRPC ClearConfiscatedRPC;

        // One pair for every console command: the request going up, the output coming back. Replaces the
        // four per-command RPCs, each of which had to re-implement the admin check and invent its own reply
        // format - and two of which had no reply at all, so the admin saw nothing either way.
        internal static CustomRPC ClientCommandRequestRPC;
        internal static CustomRPC CommandOutputRPC;

        public ValConfig(ConfigFile cf) {
            // ensure all the config values are created
            cfg = cf;
            cfg.SaveOnConfigSet = true;
            CreateConfigValues(cf);
            Logger.SetDebugLogging(EnableDebugMode.Value);
            ConfigFileWatcher.Initialize();
            SetupMainFileWatcher();

            CharacterSaveRPC = NetworkManager.Instance.AddRPC("VENFORCE_CHAR", OnServerRecieveCharacter, OnClientReceiveCharacter);
            ReturnConfiscatedItemsRPC = NetworkManager.Instance.AddRPC("VENFORCE_RETURN_CONFISCATED", NoServerHandler, OnClientReceiveConfiscatedItems);
            CheatDetectionRPC = NetworkManager.Instance.AddRPC("VENFORCE_CHEAT", OnServerReceiveCheatReport, OnClientReceiveCheatReport);
            ItemDeltaUpdateRPC = NetworkManager.Instance.AddRPC("VENFORCE_ITEMDELTA", OnServerRecieveDeltaItemUpdate, OnClientReceiveDeltaItemUpdate);
            ClearConfiscatedRPC = NetworkManager.Instance.AddRPC("VENFORCE_CLEAR_CONFISCATED", NoServerHandler, OnClientReceiveClearConfiscated);
            FullSyncRequestRPC = NetworkManager.Instance.AddRPC("VENFORCE_FULLSYNC_REQ", OnServerReceiveFullSyncRequest, OnClientReceiveFullSyncRequest);
            ClientCommandRequestRPC = NetworkManager.Instance.AddRPC("VENFORCE_CMD_REQ", OnServerReceiveCommandRequest, NoClientHandler);
            CommandOutputRPC = NetworkManager.Instance.AddRPC("VENFORCE_CMD_OUT", NoServerHandler, OnClientReceiveCommandOutput);

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

            // Local rather than synced: this is how console output looks on the machine reading it, so each
            // person decides for themselves rather than inheriting the server's preference.
            EnableTerminalColors = BindLocalConfig("Client config", "EnableTerminalColors", true,
                "Colour the output of this mod's console commands by severity - green for a result, blue for detail lines, amber for a warning, red for a failure. Turn it off if your console theme makes the colours hard to read, or if you are copying output somewhere that would show the markup.");

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
            NewCharacterStartingItems = BindServerConfig("Player Sync", "NewCharacterStartingItems", "ArmorRagsChest,ArmorRagsLegs,Torch", "Comma separated prefab names a brand new character is allowed to arrive holding when NewCharactersRemoveExtraItems is enabled. Anything else in their inventory on their first join is confiscated, as is any item above quality 1. Names are matched exactly (case insensitively), not as substrings, so 'Torch' does not also permit 'TorchMist'. Change this if your modpack starts players with different gear; leave it empty to allow no starting items at all.");
            ServerSideNewCharacterEnforcement = BindServerConfig("Player Sync", "ServerSideNewCharacterEnforcement", true, "If enabled, the server applies the NewCharacter* rules itself to the first character save it ever stores for a player, instead of trusting the client to have done it. The client does this too, but a client is what you are defending against - this is the copy of the check that a modified client cannot skip. Inert unless at least one of NewCharactersRemoveExtraItems, NewCharacterSetSkillsToZero or newCharacterClearCustomData is on.");
            ConfiscateUnidentifiableItems = BindServerConfig("Player Sync", "ConfiscateUnidentifiableItems", false, "Controls what happens to an inventory item whose ItemDrop prefab does not resolve on the client - usually a modded item, or an entry another mod created directly. These cannot be tracked, matched or handed back, so by default they are left alone and logged. Enable to confiscate them instead; note that a confiscated item with no prefab name can never be returned with the confiscation commands.", null, true);
            InitialCharacterSyncWaitSeconds = BindServerConfig("Player Sync", "InitialCharacterSyncWaitSeconds", 10, "How long a joining client waits for the server's answer about its stored character before giving up and treating the character as new. The answer normally arrives during the connection handshake, well before the world finishes loading, so this only matters if that is delayed. Set to 0 to never wait. Either way the character is treated as NEW when no answer arrives - the local save file on the joining machine is never used as the baseline for a server.", true, 0, 60);
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
            DetectCheatEngine = BindServerConfig("Anti-Cheat", "DetectCheatEngine", true, "Include Cheat Engine in the catalog scan (process names, window titles, and injected speedhack/DBK modules). Its TfrmMain/TfrmMemView window classes are generic Delphi names shared by legitimate software, so a class-only sighting is logged but never kicked or banned. Note: Cheat Engine has legitimate uses — prefer Log action over Kick/Ban. Requires DetectCheatTools.");
            DetectGenericTrainers = BindServerConfig("Anti-Cheat", "DetectGenericTrainers", true, "Flag any running process whose executable name contains the word 'trainer' (e.g. 'Valheim Trainer.exe', 'Hitman 3 Trainer - FLiNG.exe'). Catches FLiNG, MrAntiFun and Cheat Happens trainers without listing each one. Follows ActionOnDetection.");
            ScanLoadedModules = BindServerConfig("Anti-Cheat", "ScanLoadedModules", true, "Scan the native DLLs loaded into the game process itself. This is the only way to see a cheat that has already injected and then closed its launcher, and it survives renaming the tool's executable. Cheap - the module list is local to our own process.");
            ScanWindowTitles = BindServerConfig("Anti-Cheat", "ScanWindowTitles", true, "Scan open window classes and titles. Catches tools that have been renamed to evade the process-name check, most notably Cheat Engine. Generic framework window classes (e.g. Delphi's TfrmMain) are treated as low confidence: the server logs the sighting but takes no action on it alone.");
            AdditionalCheatProcesses = BindServerConfig("Anti-Cheat", "AdditionalCheatProcesses", "", "Comma-separated list of extra process names to treat as cheat tools, without the '.exe' suffix, matched exactly and case-insensitively. Empty by default. Suggested opt-in values for strict servers: x64dbg, x32dbg, x96dbg, ProcessHacker, SystemInformer, HxD, ReClass.NET, ollydbg, Scylla_x64, frida, Fiddler, Charles. WARNING: every one of those is a standard developer tool with heavy legitimate use by modders and streamers, which is why none of them ship enabled. Deliberately excluded from the built-in catalog and NOT recommended here: Aurora (collides with Aurora RGB lighting software), Process Lasso (a CPU priority optimiser, not a speedhack), AutoHotkey (compiled scripts take arbitrary names, so the check is worthless, and it is widely used for accessibility and key remapping), and MSI Afterburner/RivaTuner/OBS (their overlay DLLs look injector-shaped).");
            IgnoredCheatProcesses = BindServerConfig("Anti-Cheat", "IgnoredCheatProcesses", "", "Comma-separated allowlist of process, module or window names to never flag, matched as a case-insensitive substring. Applied last, so it overrides the built-in catalog and AdditionalCheatProcesses. Use this to keep playing when a legitimate program trips a signature.");
            //DetectSpeedhack = BindServerConfig("Anti-Cheat", "DetectSpeedhack", true, "Detect speedhack via Unity time vs. wall-clock drift.");
            CheatDetectionAction = BindServerConfig("Anti-Cheat", "ActionOnDetection", "Kick", "Server-side action taken when a cheat tool is reported. Note that dedicated game-cheating tools (injectors, ValheimTooler, ValHack, Valheim Mod Menu) are always auto-banned regardless of this setting, and low-confidence sightings (generic window classes) are always logged only, regardless of this setting.", new AcceptableValueList<string>("Log", "Kick", "Ban"));
            CheatScanIntervalSeconds = BindServerConfig("Anti-Cheat", "ScanIntervalSeconds", 30, "Seconds between periodic client scan ticks. The process, module and window scans are staggered across successive ticks so their cost never lands on the same frame, so each individual scan runs every three intervals. ValheimTooler assembly detection is event-driven and not affected by this interval.", false, 5, 300);

            EnableStructureValidation = BindServerConfig("World Integrity", "EnableStructureValidation", false, "Master switch for server-side validation of the structures clients place. When enabled, the server inspects the objects arriving from each client and reports the ones no legitimate client can produce: geometry that is not in any build menu, and pieces whose health is above what the prefab was designed to hold. This is the check for somebody spawning dungeon rooms, dvergr towns and ruins into a world - the structures that show a nameplate with no crafter on it, cannot be destroyed, and flatten the ground where they land. Off by default; every part of the feature is inert until this is on.");
            DetectNonBuildableStructures = BindServerConfig("World Integrity", "DetectNonBuildableStructures", true, "Flag a client that creates a structure which is in no build menu. Membership of a piece table is what makes a prefab placeable at all - by the hammer, the hoe, the cultivator, and by every blueprint or bulk-building mod, which all place out of those same tables - so a mod's own pieces are covered automatically and a large blueprint cannot trip this. Also blocks ZNetScene's SpawnObject RPC, an unused routed call that otherwise lets any client have the server instantiate any prefab by hash.");
            DetectExcessiveStructureHealth = BindServerConfig("World Integrity", "DetectExcessiveStructureHealth", true, "Flag a client that writes a piece's health above the maximum its prefab allows, which is how an indestructible structure is actually made - there is no separate invulnerability flag in Valheim, just an absurd number in the health field. The ceiling accounts for world modifiers that raise building health, and repairing a piece to full is never flagged. Health that was already too high before the client touched it is attributed to nobody, so walking past a cheated structure cannot get an innocent player reported; use Enforcer-Scan-Structures to find those.");
            StructureHealthAllowedMultiplier = BindServerConfig("World Integrity", "StructureHealthAllowedMultiplier", 1f, "Headroom on the health ceiling, for servers running a mod that raises piece health at runtime rather than on the prefab (a building-strength skill, for example). 1 means the prefab's own maximum, which is correct for vanilla and for mods that edit the prefab. Raise it only if legitimate pieces are being flagged, and prefer IgnoredStructurePrefabs if only a few prefabs are affected.", advanced: true, valmin: 1f, valmax: 1000f);
            StructureValidationAction = BindServerConfig("World Integrity", "StructureValidationAction", "Log", "Server-side action taken against a player caught placing invalid structures. Detections are always written to the server log and posted to Discord regardless of this setting; this only controls what happens to the player. Defaults to Log so a server can watch the detector for a while before letting it remove anybody.", new AcceptableValueList<string>("Log", "Kick", "Ban"));
            RemoveDetectedStructures = BindServerConfig("World Integrity", "RemoveDetectedStructures", false, "Destroy a flagged structure instead of only reporting it. Deliberately separate from the action above, and off by default, because a false positive here deletes something rather than merely naming it - run with this off first and read the log. Note this removes the structure, not the terrain flattening that came with it; that arrives as separate objects and still needs re-terraforming by hand.");
            StructureValidationExemptAdmins = BindServerConfig("World Integrity", "StructureValidationExemptAdmins", true, "Whether anyone on the server's adminlist is exempt from structure validation. On by default, unlike the other admin exemptions in this mod: spawning a non-buildable prefab is an ordinary thing to do with devcommands, and an admin building with them should not have to notice this feature exists. Turn it off to hold admins to the same rules as everyone else.");
            IgnoredStructurePrefabs = BindServerConfig("World Integrity", "IgnoredStructurePrefabs", "", "Comma-separated allowlist of prefab names never flagged, matched as a case-insensitive substring so one entry can cover a family of prefabs (e.g. 'dvergrprops_' covers all of them). Applied last, so it overrides every check above. This is the escape hatch when a mod on your server legitimately creates an object this detector does not recognise - reach for it rather than turning the whole feature off, and reach for it before enabling RemoveDetectedStructures.");

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
            DiscordNotifyStructureFlagged = BindLocalConfig("Discord", "NotifyStructureFlagged", true, "Post a message when structure validation catches a player placing something no legitimate client can place, naming the prefab and where it landed. Inert unless EnableStructureValidation is on. One post per player per minute at most, however many objects were involved - a cheat tool drops a whole village at once, and a message per piece would walk the webhook straight into Discord's rate limiter.");
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

        /// <summary>
        /// Server side, once per connect: hand the joining player the character we hold for them, or tell them
        /// plainly that we hold none.
        ///
        /// This is also where the server records its own verdict for FirstSaveEnforcement. It has to be here:
        /// this is the moment the server does the lookup itself, against the peer that is connecting, before
        /// that peer has said anything. Deciding "is this character new" any later means deciding it from data
        /// the client supplied, which a modified client chooses.
        /// </summary>
        internal static ZPackage SendSavedCharacter(ZNetPeer peer) {
            string id = peer.m_socket.GetEndPointString();
            Logger.LogInfo($"Sending saved character data to player {peer.m_playerName} with ID: {id}");

            // The id a save was filed under is not always spelled the way the socket spells it (see
            // PlatformIds), so resolve before concluding anything. Getting this wrong used to mean "we sent you
            // nothing" - now it would mean "you are new here", and a new character gets stripped, so a lookup
            // that cannot answer must never be read as an answer.
            bool resolved = modules.character.CharacterSaves.TryResolveSave(
                id, peer.m_playerName, out string saveId, out string saveName, out bool lookupFailed);
            if (lookupFailed) {
                Logger.LogWarning($"Could not read the character store while {peer.m_playerName} ({id}) was connecting. Treating them as a returning player so nothing is confiscated on a store error.");
                modules.character.FirstSaveEnforcement.ClearForPeer(peer);
                return CharacterPayload("", CharPayloadNone);
            }
            if (!resolved) {
                Logger.LogInfo($"No stored character named '{peer.m_playerName}' for account {id}; this is a new character on this server.");
                modules.character.FirstSaveEnforcement.MarkNoSaveOnConnect(peer, id, peer.m_playerName);
                return CharacterPayload("", CharPayloadNone);
            }
            modules.character.FirstSaveEnforcement.ClearForPeer(peer);

            if (ValConfig.InternalStorageMode.Value) {
                Logger.LogInfo("Using internal storage mode to send character data.");
                DataObjects.Character chara = InternalDataStore.GetAccountCharacter(saveId, saveName);
                if (chara == null) {
                    // The registry listed it a moment ago and now cannot produce it. That is a store problem,
                    // not a new character, so say "none" without arming the first-save enforcement.
                    Logger.LogWarning($"Internal storage listed a character '{saveName}' for {saveId} but could not load it; sending no character data.");
                    return CharacterPayload("", CharPayloadNone);
                }
                return SendCharacterToClientAsZpackage(chara);
            }

            // Disk mode. Prefer the in-memory store (kept current by the async writer, so it can be newer
            // than disk while a write is pending) and fall back to disk, warming the store so the player's
            // first deltas can be applied without re-reading the file. If the on-disk file has been edited
            // out-of-band (e.g. an admin edited the save while the player was offline) since we cached it, the
            // store reports a miss so the edited file is re-read and re-seeded below.
            var charFile = Path.Combine(Paths.ConfigPath, ValheimEnforcer, CharacterFolder, $"{saveId}");
            string fullpath = Path.Combine(charFile, $"{saveName}.yaml");
            bool exists = File.Exists(fullpath);
            DateTime diskMtime = exists ? File.GetLastWriteTimeUtc(fullpath) : DateTime.MinValue;

            // Both branches below strip ConfiscatedItems before the payload goes out (see
            // SendCharacterToClientAsZpackage). That costs a parse + re-serialize on a path that otherwise just
            // forwards a cached string, which is accepted deliberately: this runs once per player connect, not on
            // the save-burst path the async store exists to protect, and the disk branch already does file I/O.
            // If connect latency ever becomes a concern, cache the stripped form on CharacterStore.Entry and have
            // the worker thread produce it.
            string cached = modules.character.CharacterStore.GetYamlIfCurrent(saveId, saveName, diskMtime);
            if (cached != null) {
                return CharacterPayload(StripConfiscatedItemsFromYaml(cached), CharPayloadCharacter);
            }

            if (!exists) {
                // TryResolveSave said the save was there, so this is a race with a delete rather than a new
                // character. Do not arm first-save enforcement on it.
                Logger.LogWarning($"path: {fullpath} vanished between lookup and read, no character data will be sent.");
                return CharacterPayload("", CharPayloadNone);
            }
            string filecontents = File.ReadAllText(fullpath);
            // Seed the store with the FULL save - it is the server's authoritative copy. Only the outbound
            // payload is stripped.
            modules.character.CharacterStore.Seed(saveId, saveName, filecontents, diskMtime);
            return CharacterPayload(StripConfiscatedItemsFromYaml(filecontents), CharPayloadCharacter);
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
            // Resolved here, on the main thread, for two reasons: FirstSaveEnforcement's verdict comes from the
            // server's own connect-time lookup (so it must be read where that lookup's bookkeeping lives), and
            // the policy snapshot has to be taken off the config before it is handed to the worker thread,
            // which must never read a ConfigEntry itself.
            NewCharacterRules.Policy newCharacterPolicy = null;
            if (modules.character.FirstSaveEnforcement.ShouldSanitize(sender, out _)) {
                NewCharacterRules.Policy candidate = NewCharacterRules.Current();
                if (candidate.AnyEnabled) { newCharacterPolicy = candidate; }
            }

            // Who the server says this peer is. The payload names its own HostID and Name, but those are
            // written by the client: without an independent identity a client can file its save under another
            // account's character - overwriting that character, and skipping the first-save check at the same
            // time, because the check asks "does a save already exist?" about the name the payload supplied.
            ZNetPeer senderPeer = ZNet.instance?.GetPeer(sender);
            string senderAccountId = senderPeer?.m_socket?.GetEndPointString();
            string senderCharacterName = senderPeer?.m_playerName;

            if (ValConfig.InternalStorageMode.Value) {
                // Internal storage writes touch a registry ZDO and must stay on the main thread.
                try {
                    DataObjects.Character chara = DataObjects.yamldeserializer.Deserialize<DataObjects.Character>(yaml);
                    Logger.LogInfo($"Recieved Player data update for {sender} - {chara.Name}|{chara.HostID}");
                    if (!SaveBelongsToSender(chara, sender, senderAccountId, senderCharacterName)) { return; }
                    // The client's confiscated list is a report of what it confiscated this session, never a
                    // replacement for ours - see Character.MergeConfiscatedItems.
                    DataObjects.Character existing = InternalDataStore.GetAccountCharacter(chara.HostID, chara.Name);
                    List<PackedItem> reported = chara.ConfiscatedItems;
                    chara.ConfiscatedItems = existing?.ConfiscatedItems ?? new List<PackedItem>();
                    int appended = chara.MergeConfiscatedItems(reported);
                    if (appended > 0) {
                        Logger.LogInfo($"Recorded {appended} newly confiscated item(s) for {chara.Name}.");
                    }

                    // Both stores have to be empty before this counts as a first save. WritePlayerCharacterToSave
                    // deliberately double-writes (registry AND disk) so that switching storage modes does not
                    // lose data, which means a character can be absent from one and present in the other.
                    if (newCharacterPolicy != null && existing == null && !modules.character.CharacterSaves.ExistsOnDisk(chara.HostID, chara.Name)) {
                        NewCharacterRules.Result sanitized = NewCharacterRules.Apply(chara, newCharacterPolicy, recordConfiscation: true);
                        if (sanitized.Changed) {
                            Logger.LogWarning($"First save for {chara.Name} ({chara.HostID}) held to the new-character rules: {sanitized.Describe()}");
                            WritePlayerCharacterToSave(chara.HostID, chara);
                            // Already on the main thread here, so no queue hop is needed.
                            SendSanitizedCharacterToClient(sender, chara);
                            return;
                        }
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
            modules.character.CharacterStore.SubmitFullSave(yaml, sender, senderAccountId, senderCharacterName, newCharacterPolicy);
        }

        /// <summary>
        /// Whether an uploaded character actually belongs to the peer that uploaded it.
        ///
        /// Account ids are compared with <see cref="PlatformIds.Matches"/> because one account legitimately
        /// reaches us under more than one spelling; the character name must be the one the peer connected as.
        /// When the server could not resolve an identity for the sender at all the save is accepted rather
        /// than refused - an unrecognised socket type must not stop every save on the server from being
        /// written.
        /// </summary>
        private static bool SaveBelongsToSender(DataObjects.Character chara, long sender, string senderAccountId, string senderCharacterName) {
            if (chara == null) { return false; }
            if (string.IsNullOrEmpty(senderAccountId) || string.IsNullOrEmpty(senderCharacterName)) {
                Logger.LogDebug($"No resolved identity for sender {sender}; accepting the save for {chara.Name} unchecked.");
                return true;
            }
            if (!PlatformIds.Matches(senderAccountId, chara.HostID)) {
                Logger.LogWarning($"Refusing a character save from {senderCharacterName} ({senderAccountId}): it claims to belong to account {chara.HostID}.");
                return false;
            }
            if (!string.Equals(senderCharacterName, chara.Name, StringComparison.OrdinalIgnoreCase)) {
                Logger.LogWarning($"Refusing a character save from {senderAccountId}: they connected as '{senderCharacterName}' but uploaded a save for '{chara.Name}'.");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Server -> client, main thread: here is the character as the server now holds it, reconcile yourself
        /// to it.
        ///
        /// Sanitizing the stored save is only half a fix on its own - the player is still standing there
        /// holding the items. Without this the client's next delta or full save would simply put them back.
        /// </summary>
        internal static void SendSanitizedCharacterToClient(long sender, DataObjects.Character chara) {
            if (chara == null) { return; }
            // Same withholding as every other server -> client character payload, but tagged SANITIZED so the
            // client reconciles its live inventory rather than just adopting the record.
            List<PackedItem> held = chara.ConfiscatedItems;
            ZPackage payload;
            try {
                chara.ConfiscatedItems = null;
                payload = CharacterPayload(DataObjects.yamlserializer.Serialize(chara), CharPayloadSanitized);
            } finally {
                // The caller's object is server-side authoritative state; never leave it stripped.
                chara.ConfiscatedItems = held;
            }
            SendSanitizedPayload(sender, chara.Name, payload);
        }

        /// <summary>Overload for the async store, which holds the authoritative copy as YAML rather than as a
        /// parsed object.</summary>
        internal static void SendSanitizedCharacterToClient(long sender, string hostId, string name) {
            string yaml = modules.character.CharacterStore.GetYaml(hostId, name);
            if (string.IsNullOrEmpty(yaml)) {
                Logger.LogWarning($"Sanitized character for {name} ({hostId}) is no longer cached; the client will pick it up on its next connect instead.");
                return;
            }
            SendSanitizedPayload(sender, name, CharacterPayload(StripConfiscatedItemsFromYaml(yaml), CharPayloadSanitized));
        }

        private static void SendSanitizedPayload(long sender, string name, ZPackage payload) {
            // The peer may be gone: a first save can arrive over the end-of-session FinalSaveRpc, in which case
            // the connection is already tearing down. The stored save is correct either way, and the next join
            // validates against it.
            ZNetPeer peer = ZNet.instance?.GetPeer(sender);
            if (peer == null) {
                Logger.LogInfo($"Not pushing the sanitized character for {name}: that peer has already disconnected.");
                return;
            }
            Logger.LogInfo($"Pushing the sanitized character back to {name} so their inventory matches the server.");
            CharacterSaveRPC.SendPackage(sender, payload);
        }

        // Client handler: an admin cleared confiscated entries for this player. The character save on the
        // server is already authoritative; this drops the same entries from the copy this session is
        // tracking, which is what would otherwise re-append them on the next full push.
        public static IEnumerator OnClientReceiveClearConfiscated(long sender, ZPackage package) {
            string filter = package.ReadString();
            int cleared = modules.character.ConfiscatedItems.ClearTrackedLocally(
                modules.character.ConfiscatedItems.ParseFilter(filter));
            Logger.LogDebug($"Cleared {cleared} tracked confiscated item(s) locally for filter '{filter}'.");
            yield break;
        }

        public static IEnumerator OnClientReceiveCharacter(long sender, ZPackage package) {
            IncomingCharacter incoming = ReadIncomingCharacter(package);

            switch (incoming.Outcome) {
                // The server explicitly said it holds no character. This is the definite answer that used to be
                // missing: without it the client stayed in "haven't heard yet" and went off to read its own
                // local save file, which on a first join is whatever the player did in a solo world.
                case CharacterPayloadOutcome.NoCharacter:
                    CharacterManager.SetServerHasNoCharacter();
                    yield break;

                // The server sent a character and we could not read it. Emphatically NOT the same answer: this
                // player is a returning one, so treating it as "no character" would confiscate their entire
                // inventory because a save on the server went bad.
                case CharacterPayloadOutcome.Unreadable:
                    CharacterManager.SetServerCharacterUnreadable();
                    yield break;

                case CharacterPayloadOutcome.Sanitized:
                    CharacterManager.ApplyServerSanitizedCharacter(incoming.Character);
                    yield break;

                default:
                    Logger.LogDebug("Recieved Player character data from server.");
                    CharacterManager.SetPlayerCharacter(incoming.Character);
                    yield break;
            }
        }

        private enum CharacterPayloadOutcome {
            /// <summary>A stored character arrived intact.</summary>
            Character,
            /// <summary>A stored character arrived that the server had just sanitized; the live player has to
            /// be reconciled to it, not merely told about it.</summary>
            Sanitized,
            /// <summary>The server holds nothing for this account and character name.</summary>
            NoCharacter,
            /// <summary>The server holds something and we could not read it.</summary>
            Unreadable,
        }

        private struct IncomingCharacter {
            internal CharacterPayloadOutcome Outcome;
            internal DataObjects.Character Character;
        }

        // Split out of the handler because a C# iterator cannot yield out of a try/catch, and this read has to
        // be inside one: the payload arrives over the network and a malformed one must not throw out of the
        // Jotunn coroutine.
        private static IncomingCharacter ReadIncomingCharacter(ZPackage package) {
            // An empty package is how a server running an older build says "I have nothing for you" - it
            // predates the explicit NONE tag. Read it as that answer, not as a damaged payload.
            if (package == null || package.Size() == 0) {
                return new IncomingCharacter { Outcome = CharacterPayloadOutcome.NoCharacter };
            }

            string yaml;
            string kind = CharPayloadCharacter;
            try {
                yaml = package.ReadString();
                // Older servers send the YAML with no tag after it.
                if (package.GetPos() < package.Size()) {
                    kind = package.ReadString();
                }
            } catch (Exception e) {
                Logger.LogWarning($"Could not read the character payload from the server: {e.Message}");
                return new IncomingCharacter { Outcome = CharacterPayloadOutcome.Unreadable };
            }

            if (kind == CharPayloadNone || string.IsNullOrWhiteSpace(yaml)) {
                return new IncomingCharacter { Outcome = CharacterPayloadOutcome.NoCharacter };
            }

            try {
                DataObjects.Character chara = DataObjects.yamldeserializer.Deserialize<DataObjects.Character>(yaml);
                if (chara == null) {
                    Logger.LogWarning("The server sent a character payload that deserialized to nothing.");
                    return new IncomingCharacter { Outcome = CharacterPayloadOutcome.Unreadable };
                }
                return new IncomingCharacter {
                    Outcome = kind == CharPayloadSanitized ? CharacterPayloadOutcome.Sanitized : CharacterPayloadOutcome.Character,
                    Character = chara,
                };
            } catch (Exception e) {
                Logger.LogWarning($"Could not parse the character the server sent: {e.Message}");
                return new IncomingCharacter { Outcome = CharacterPayloadOutcome.Unreadable };
            }
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

            // Enforcement targets the reporting peer's socket host id (SteamID/PlatformUserID),
            // never the client-supplied character name: names are spoofable and collide, so a
            // crafted report could otherwise kick or ban a different online player.
            string hostId = peer.m_socket.GetHostName();
            string endpoint = peer.m_socket.GetEndPointString();
            Logger.LogWarning($"Cheat detection from {playerName} ({endpoint}): valheim-tooler: {summary.ValheimToolerStatus} tools: {DescribeDetectedTools(summary)}");

            // ValheimTooler is unambiguous cheat software; always ban regardless of ActionOnDetection.
            if (summary.ValheimToolerStatus) {
                Logger.LogWarning($"Banning {playerName} for ValheimTooler usage.");
                BanCheater(peer, playerName, summary);
                yield break;
            }

            // Weak sightings (generic window classes shared by legitimate software) are visibility
            // only. The flag is only ever trusted downward: a tampered client marking a tool weak
            // gains nothing over not reporting it at all.
            List<DataObjects.CheatToolDetection> enforceable = new List<DataObjects.CheatToolDetection>();
            if (summary.DetectedTools != null) {
                foreach (DataObjects.CheatToolDetection detection in summary.DetectedTools) {
                    if (!detection.Weak) { enforceable.Add(detection); }
                }
            }

            // Tools with no purpose other than cheating also ban on sight. AutoBan is resolved from
            // the server's own catalog by label, never taken from the payload, so a tampered client
            // cannot escalate a report into a ban.
            foreach (DataObjects.CheatToolDetection detection in enforceable) {
                if (CheatToolCatalog.IsAutoBan(detection.Tool)) {
                    Logger.LogWarning($"Banning {playerName} for {detection.Tool} usage.");
                    BanCheater(peer, playerName, summary);
                    yield break;
                }
            }

            if (enforceable.Count == 0) {
                Logger.LogWarning($"Low-confidence sighting from {playerName} ({endpoint}), logged without action: {DescribeDetectedTools(summary)}");
                yield break;
            }

            // Everything else honors the configured action.
            string action = CheatDetectionAction.Value ?? "Log";
            switch (action) {
                case "Kick":
                    Logger.LogWarning($"Kicking {playerName} for cheat usage.");
                    ZNet.instance.Kick(hostId);
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
        /// <summary>
        /// Persists a ban to the KnownCheaters list (the durable rejoin barrier) and applies the vanilla one.
        ///
        /// Ban by host id: vanilla InternalBan only uses a name to look up the host id of an online peer, so
        /// passing the id directly bans that account and only that account. The notification is deliberately
        /// left to the caller, since what gets posted depends on what triggered the ban.
        /// </summary>
        internal static void BanHost(string hostId, string reason) {
            if (string.IsNullOrEmpty(hostId)) { return; }
            KnownCheaterTracker.AddCheater(hostId, reason);
            ZNet.instance.Ban(hostId);
        }

        private static void BanCheater(ZNetPeer peer, string playerName, DataObjects.CheatSummaryReport summary) {
            string hostId = peer.m_socket.GetHostName();
            string reason = BuildCheatReason(summary);
            BanHost(hostId, reason);

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
                    detections.Add($"{detection.Tool} ({detection.Vector}: {detection.Detail}){(detection.Weak ? " (weak)" : "")}");
                }
            }
            string detail = detections.Count > 0 ? string.Join(", ", detections) : "cheat detected";
            return $"Cheat detection: {detail}";
        }

        // Compact one-line rendering of the reported tools for the server log.
        private static string DescribeDetectedTools(DataObjects.CheatSummaryReport summary) {
            if (summary.DetectedTools == null || summary.DetectedTools.Count == 0) { return "none"; }
            return string.Join(", ", summary.DetectedTools.Select(d => $"{d.Tool} [{d.Vector}: {d.Detail}]{(d.Weak ? " (weak)" : "")}"));
        }

        public static IEnumerator OnClientReceiveCheatReport(long sender, ZPackage package) {
            // Client -> server only; clients do not act on this RPC.
            yield break;
        }

        // A Jotunn CustomRPC needs a handler for both directions even when only one is ever used. These two
        // are the unused halves: a command request only ever travels client to server, and its output only
        // ever travels back.
        private static IEnumerator NoServerHandler(long sender, ZPackage package) { yield break; }
        private static IEnumerator NoClientHandler(long sender, ZPackage package) { yield break; }

        /// <summary>
        /// Server handler: an admin's client asked to run a server-authoritative console command.
        ///
        /// Every one of these commands reads or writes something only the server has - character saves, the
        /// webhook URL, the world's objects - and a dedicated server has no console to type them into, so the
        /// request is routed here. Gate on admin because any peer could craft this RPC; the client-side check
        /// exists only to give a clearer message.
        /// </summary>
        public static IEnumerator OnServerReceiveCommandRequest(long sender, ZPackage package) {
            if (ZNet.instance == null || ZNet.instance.IsServer() == false) { yield break; }

            string command = package.ReadString();
            if (SenderIsAdmin(sender) == false) {
                Logger.LogWarning($"Rejecting '{command}' from non-admin peer {sender}.");
                // Answer rather than going quiet, so the sender sees a refusal instead of nothing at all.
                TerminalOutput refusal = TerminalOutput.Remote(sender);
                refusal.Error($"Only server admins can run {command}.", log: false);
                refusal.Flush();
                yield break;
            }

            int argCount = package.ReadInt();
            string[] args = new string[argCount];
            for (int i = 0; i < argCount; i++) { args[i] = package.ReadString(); }

            Logger.LogInfo($"Running '{command}' for admin {PeerHostId(sender)}.");
            TerminalManager.ExecuteFromNetwork(command, args, TerminalOutput.Remote(sender));
            yield break;
        }

        /// <summary>
        /// Client handler: a batch of output lines from a command this client asked the server to run.
        /// Severity travels as a byte and the colour is applied here, so the server's log never contains
        /// markup and each client honours its own EnableTerminalColors setting.
        /// </summary>
        public static IEnumerator OnClientReceiveCommandOutput(long sender, ZPackage package) {
            int count = package.ReadInt();
            for (int i = 0; i < count; i++) {
                OutputLevel level = (OutputLevel)package.ReadByte();
                TerminalManager.PrintResponse(level, package.ReadString());
            }
            yield break;
        }

        private static string PeerHostId(long sender) {
            ZNetPeer peer = ZNet.instance?.GetPeer(sender);
            return peer?.m_socket?.GetHostName() ?? sender.ToString();
        }

        /// <summary>
        /// True when the given peer uid belongs to a connected admin. The single gate for every client-issued
        /// server-side command; the integrated host never routes through an RPC so is not considered here.
        /// </summary>
        private static bool SenderIsAdmin(long sender) {
            ZNetPeer peer = ZNet.instance?.GetPeer(sender);
            if (peer == null || peer.m_socket == null) { return false; }
            return ZNet.instance.IsAdmin(peer.m_socket.GetHostName());
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

        // What a VENFORCE_CHAR payload from the server means. Previously the server said "I have no character
        // for you" by sending an empty ZPackage - which is to say, by saying nothing the client could act on:
        // OnClientReceiveCharacter read straight past the end of it, and SetPlayerCharacter dropped the null.
        // The client could not tell "the server has nothing" from "the answer has not arrived", so it fell back
        // to its own local save file, which on a first join is whatever the player did in a solo world. Naming
        // the three cases explicitly is what makes that distinction expressible.
        internal const string CharPayloadCharacter = "CHAR";
        internal const string CharPayloadNone = "NONE";
        internal const string CharPayloadSanitized = "SANITIZED";

        /// <summary>
        /// Builds a tagged server -> client character payload.
        ///
        /// The YAML goes first and the tag last, on purpose: a client running an older build reads only the
        /// first string and gets exactly what it used to get (a character, or "" which deserializes to null and
        /// leaves it behaving as before), while a new client talking to an older server finds no tag and
        /// defaults to CHAR. Neither combination breaks, which matters because the payload rides the connect
        /// handshake.
        /// </summary>
        internal static ZPackage CharacterPayload(string yaml, string kind) {
            ZPackage package = new ZPackage();
            package.Write(yaml ?? "");
            package.Write(kind);
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
                // Tagged so every server -> client character payload carries its kind explicitly. Not tagging
                // would still work (the client defaults an untagged payload to CHAR, for older servers), but
                // leaving one path implicit is how the "silence means no character" ambiguity started.
                return CharacterPayload(DataObjects.yamlserializer.Serialize(chara), CharPayloadCharacter);
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
