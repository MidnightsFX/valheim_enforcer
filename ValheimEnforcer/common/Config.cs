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
        public static ConfigEntry<bool> AllowPublicDiagnosticCommands;
        public static ConfigEntry<bool> UpdateLoadedModsOnStartup;
        public static ConfigEntry<bool> AutoAddModsToRequired;
        public static ConfigEntry<bool> RemoveUnloadedModsFromRequired;
        public static ConfigEntry<bool> ModValidationExemptAdmins;
        public static ConfigEntry<string> HashEnforcement;
        public static ConfigEntry<bool> RecordHashesForLoadedMods;
        public static ConfigEntry<bool> ValidatePatchers;
        public static ConfigEntry<bool> AutoAddPatchersToAllowed;
        public static ConfigEntry<string> AttestationPolicy;
        public static ConfigEntry<bool> ResolveThunderstoreHashes;
        public static ConfigEntry<int> HashComputeTimeoutSeconds;
        public static ConfigEntry<int> ThunderstoreMaxArchiveMB;
        public static ConfigEntry<bool> RemoveNontrackedItemsFromJoiningPlayers;
        public static ConfigEntry<bool> AddMissingItemsFromPlayerServerSave;
        public static ConfigEntry<bool> RestoreSkillsFromPlayerServerSave;
        public static ConfigEntry<bool> PreventExternalSkillRaises;
        public static ConfigEntry<bool> NewCharactersRemoveExtraItems;
        public static ConfigEntry<bool> NewCharacterSetSkillsToZero;
        public static ConfigEntry<bool> newCharacterClearCustomData;
        public static ConfigEntry<bool> PassthroughCompatModCustomData;
        // Comma-separated rather than List<string>: BepInEx's config system only supports primitives,
        // string and enums, so binding a List<string> throws at startup.
        public static ConfigEntry<string> NewCharacterStartingItems;
        public static ConfigEntry<bool> ConfiscateUnidentifiableItems;
        public static ConfigEntry<int> InitialCharacterSyncWaitSeconds;
        public static ConfigEntry<bool> PreventExternalCustomDataChanges;
        public static ConfigEntry<bool> ValidateItemCustomData;
        public static ConfigEntry<bool> ValidateItemDurability;
        public static ConfigEntry<float> ItemValidationDurabilityAllowedVariance;
        public static ConfigEntry<bool> SavePlayerStatusEffectsOnLogout;
        public static ConfigEntry<bool> ItemRemovalForDirtyReconnection;
        public static ConfigEntry<bool> ItemReturnForDirtyReconnection;
        public static ConfigEntry<bool> ServerSideJoinEnforcement;
        public static ConfigEntry<bool> PreventExternalForsakenPowerChanges;
        public static ConfigEntry<bool> NewCharacterClearForsakenPower;
        public static ConfigEntry<bool> NewCharacterResetMapExploration;
        public static ConfigEntry<bool> PreventExternalFoodChanges;
        public static ConfigEntry<bool> NewCharacterClearKnownRecipes;
        public static ConfigEntry<bool> RecordSkillReductions;

        // Progression sync: the half of a character's progress that lives in the player profile rather than in
        // the character record. Every one of these is off by default - see the master switch's description.
        public static ConfigEntry<bool> EnableProgressionSync;
        public static ConfigEntry<bool> SyncMapExploration;
        public static ConfigEntry<bool> SyncKnownItems;
        public static ConfigEntry<bool> SyncTrophies;
        public static ConfigEntry<bool> SyncPlayerStats;
        public static ConfigEntry<bool> SyncSpawnPoint;
        public static ConfigEntry<int> MapSyncIntervalMinutes;
        public static ConfigEntry<bool> NewCharacterClearPlayerStats;
        public static ConfigEntry<bool> EnableStarterLoadouts;
        public static ConfigEntry<string> DefaultStarterLoadout;

        // Rolling save archives. Off by default, and expensive when on - see the master switch's description.
        public static ConfigEntry<bool> EnableSaveArchives;
        public static ConfigEntry<int> SaveArchiveIntervalMinutes;
        public static ConfigEntry<int> SaveArchiveKeepCount;
        public static ConfigEntry<int> SaveArchiveMaxTotalMB;
        public static ConfigEntry<bool> SaveArchiveIncludeCharacters;
        public static ConfigEntry<bool> SaveArchiveIncludeConfig;
        public static ConfigEntry<string> SaveArchiveCompression;
        public static ConfigEntry<string> SaveArchivePath;

        // Emergency crash recovery. Off by default; see the master switch's description for what it cannot do.
        public static ConfigEntry<bool> EnableCrashRecovery;
        public static ConfigEntry<bool> CrashRecoveryAutoRestore;
        public static ConfigEntry<int> CrashRecoveryWindowMinutes;
        public static ConfigEntry<int> CrashRecoveryPushIntervalMinutes;
        public static ConfigEntry<int> CrashRecoveryKeepBlobs;

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
        public static ConfigEntry<bool> EnforceRoutedRpcSender;
        public static ConfigEntry<int> StallWarningThresholdMs;
        public static ConfigEntry<int> CharacterCacheIdleMinutes;
        public static ConfigEntry<int> MemoryReportIntervalMinutes;

        public static ConfigEntry<bool> EnableCheatDetection;
        public static ConfigEntry<bool> DetectCheatEngine;
        public static ConfigEntry<bool> DetectValheimTooler;
        public static ConfigEntry<bool> DetectCheatTools;
        public static ConfigEntry<bool> DetectGenericTrainers;
        public static ConfigEntry<bool> ScanLoadedModules;
        public static ConfigEntry<bool> ScanWindowTitles;
        public static ConfigEntry<bool> ScanElevatedProcesses;
        // Comma-separated rather than List<string>: BepInEx's config system only supports primitives,
        // string and enums, so binding a List<string> throws at startup.
        public static ConfigEntry<string> AdditionalCheatProcesses;
        public static ConfigEntry<string> IgnoredCheatProcesses;
        //public static ConfigEntry<bool> DetectSpeedhack;
        public static ConfigEntry<string> CheatDetectionAction;
        public static ConfigEntry<int> CheatScanIntervalSeconds;

        public static ConfigEntry<bool> EnableStructureValidation;
        public static ConfigEntry<bool> DetectNonBuildableStructures;
        public static ConfigEntry<bool> BlockSpawnObjectRPC;
        public static ConfigEntry<bool> DetectExcessiveStructureHealth;
        public static ConfigEntry<float> StructureHealthAllowedMultiplier;
        public static ConfigEntry<string> StructureValidationAction;
        public static ConfigEntry<bool> RemoveDetectedStructures;
        public static ConfigEntry<bool> StructureValidationExemptAdmins;
        // Comma-separated rather than List<string>: BepInEx's config system only supports primitives,
        // string and enums, so binding a List<string> throws at startup.
        public static ConfigEntry<string> IgnoredStructurePrefabs;
        public static ConfigEntry<bool> DetectItemOrigins;
        public static ConfigEntry<bool> DetectUncraftedEquipment;
        public static ConfigEntry<bool> DetectUnknownCrafterIds;
        public static ConfigEntry<bool> ItemOriginExemptAdmins;
        public static ConfigEntry<string> IgnoredItemOriginPrefabs;

        public static ConfigEntry<bool> EnableRpcGuards;
        public static ConfigEntry<bool> GuardChatSenderName;
        public static ConfigEntry<bool> GuardPlayerTeleportRpc;
        public static ConfigEntry<bool> GuardZdoDestruction;
        public static ConfigEntry<int> MaxZdoDestroysPerPacket;
        public static ConfigEntry<float> ZdoDestroyProximityMetres;
        public static ConfigEntry<bool> GuardDamageRpc;
        public static ConfigEntry<float> MaxAllowedHitDamage;
        public static ConfigEntry<bool> GuardPvpDamage;
        public static ConfigEntry<bool> GuardGlobalKeys;
        public static ConfigEntry<bool> BlockClientGlobalKeyRemoval;
        // Comma-separated rather than List<string>: BepInEx's config system only supports primitives,
        // string and enums, so binding a List<string> throws at startup.
        public static ConfigEntry<string> AllowedClientGlobalKeys;
        public static ConfigEntry<bool> RpcGuardExemptAdmins;
        public static ConfigEntry<string> RpcGuardAction;
        public static ConfigEntry<bool> ReportClientContradictions;
        public static ConfigEntry<int> ContradictionThreshold;
        public static ConfigEntry<string> ContradictionAction;

        // Audit. Records what players do so a report can be investigated afterwards; never enforces anything.
        public static ConfigEntry<bool> EnableAuditLog;
        public static ConfigEntry<int> AuditRetentionDays;
        public static ConfigEntry<int> AuditFlushIntervalSeconds;
        public static ConfigEntry<int> AuditDamageWindowSeconds;
        public static ConfigEntry<float> AuditHighDamageThreshold;
        public static ConfigEntry<float> AuditHighDamagePerWindow;
        public static ConfigEntry<int> AuditContainerTrackingLimit;
        public static ConfigEntry<int> AuditMaxDownloadDays;
        public static ConfigEntry<bool> AuditExemptAdmins;

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
        public static ConfigEntry<bool> DiscordNotifyClientContradiction;
        public static ConfigEntry<bool> DiscordNotifyItemOrigin;

        internal const string ModsFileName = "Mods.yaml";
        internal const string ServerActiveModsFileName = "ServerActiveMods.yaml";
        internal const string ValheimEnforcer = "ValheimEnforcer";
        internal const string CharacterFolder = "Characters";
        internal const string KnownCheatersFileName = "KnownCheaters.yaml";
        internal const string NotificationsFileName = "Notifications.yaml";
        internal static String ModsConfigFilePath = Path.Combine(Paths.ConfigPath, ValheimEnforcer, ModsFileName);
        internal static String ServerActiveModsFilePath = Path.Combine(Paths.ConfigPath, ValheimEnforcer, ServerActiveModsFileName);
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
        internal static CustomRPC SkillRestoreRPC;

        // One pair for every console command: the request going up, the output coming back. Replaces the
        // four per-command RPCs, each of which had to re-implement the admin check and invent its own reply
        // format - and two of which had no reply at all, so the admin saw nothing either way.
        internal static CustomRPC ClientCommandRequestRPC;
        internal static CustomRPC CommandOutputRPC;

        // The map channel. A pair, and a channel of its own rather than more fields on the character payload:
        // a fully explored map is 8.4 MB before compression, the character payload ceiling is 8 MB, and the
        // character record also has to fit in a ZDO string under InternalStorageMode. Request goes up or down,
        // the blob answers it.
        internal static CustomRPC MapSyncRPC;
        internal static CustomRPC MapRequestRPC;

        // The crash-recovery channel. The server pushes sealed snapshots down and asks for them back on the
        // same RPC (the payload says which); the offer coming up is a channel of its own so the server side
        // of it has one handler and one set of checks.
        internal static CustomRPC RecoveryRPC;
        internal static CustomRPC RecoveryOfferRPC;

        // The audit history channel. A pair rather than two, on the same reasoning as the command pair above:
        // listing and downloading share one server handler and therefore one admin gate. It cannot reuse the
        // command pair - that one carries console lines with a 256 line ceiling, and a week of one player's
        // activity is a file.
        internal static CustomRPC AuditRequestRPC;
        internal static CustomRPC AuditDataRPC;

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
            SkillRestoreRPC = NetworkManager.Instance.AddRPC("VENFORCE_SKILL_RESTORE", NoServerHandler, OnClientReceiveSkillRestore);
            FullSyncRequestRPC = NetworkManager.Instance.AddRPC("VENFORCE_FULLSYNC_REQ", OnServerReceiveFullSyncRequest, OnClientReceiveFullSyncRequest);
            ClientCommandRequestRPC = NetworkManager.Instance.AddRPC("VENFORCE_CMD_REQ", OnServerReceiveCommandRequest, NoClientHandler);
            CommandOutputRPC = NetworkManager.Instance.AddRPC("VENFORCE_CMD_OUT", NoServerHandler, OnClientReceiveCommandOutput);
            MapSyncRPC = NetworkManager.Instance.AddRPC("VENFORCE_MAP", OnServerReceiveMap, OnClientReceiveMap);
            // One direction only: a client asks, the server answers on VENFORCE_MAP. The server never has to
            // ask - a full-sync pull already has the client offer its map - so there is no client handler
            // rather than one nothing would ever call.
            MapRequestRPC = NetworkManager.Instance.AddRPC("VENFORCE_MAP_REQ", OnServerReceiveMapRequest, NoClientHandler);
            RecoveryRPC = NetworkManager.Instance.AddRPC("VENFORCE_RECOVERY", NoServerHandler, OnClientReceiveRecovery);
            RecoveryOfferRPC = NetworkManager.Instance.AddRPC("VENFORCE_RECOVERY_OFFER", OnServerReceiveRecoveryOffer, NoClientHandler);
            AuditRequestRPC = NetworkManager.Instance.AddRPC("VENFORCE_AUDIT_REQ", OnServerReceiveAuditRequest, NoClientHandler);
            AuditDataRPC = NetworkManager.Instance.AddRPC("VENFORCE_AUDIT_OUT", NoServerHandler, OnClientReceiveAuditData);

            SynchronizationManager.Instance.AddInitialSynchronization(CharacterSaveRPC, SendSavedCharacter);

            // Deliberately not in the list below: ServerActiveMods.yaml is output, not config, so it is neither
            // watched nor created empty. Cleared here and rewritten by ModManager.SetModsActive once every plugin
            // has loaded.
            ModManager.DeleteActiveModsFile();
            LoadYamlConfigs(new Dictionary<string, Action<string>>() {
                { ModsConfigFilePath, CreateModsFile },
                { KnownCheatersFilePath, CreateKnownCheatersFile },
                { NotificationsFilePath, CreateNotificationsFile },
                { modules.character.StarterLoadouts.FilePath, CreateLoadoutsFile }
            });
            modules.character.StarterLoadouts.Initialize();
            KnownCheaterTracker.Initialize();
            modules.worldintegrity.KnownPlayerIds.Initialize();
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

            AllowPublicDiagnosticCommands = BindServerConfig("Advanced", "AllowPublicDiagnosticCommands", true,
                "If enabled (the default), the two commands that tell somebody nothing but what they could already work out - 'enforcer-help', which lists the command names, and 'enforcer-whoami', which reports what this server makes of the connection the caller is sitting on - run for any player rather than admins only. This exists because the person who most needs to ask 'why am I not an admin here?' is by definition the person the admin check is refusing, and with the answer locked behind that same check an operator whose id is misspelled in adminlist.txt has no way to find out from in-game. Neither command reads or changes anything belonging to the server or to another player, and enforcer-whoami never names another admin. Turn it off if you would rather players could not see the command list at all; every other command stays admin-only regardless.", advanced: true);

            UpdateLoadedModsOnStartup = BindServerConfig("Mods", "UpdateLoadedModsOnStartup", true, "Whether or not the mod configuration file will update its loaded mods once they are detected.");
            AutoAddModsToRequired = BindServerConfig("Mods", "AutoAddModsToRequired", true, "If true, automatically adds mods not found in the optional, admin, or server-only mod lists.");
            RemoveUnloadedModsFromRequired = BindServerConfig("Mods", "RemoveUnloadedModsFromRequired", true, "If enabled, any mod in requiredMods that this server does not have loaded is removed from the list at startup, so a mod taken off the server stops being demanded of every client without anyone editing Mods.yaml. Only requiredMods is touched: optionalMods, adminOnlyMods and serverOnlyMods routinely hold mods the server never runs, and are left alone. This also removes a mod you required by hand that the server does not run itself, including one pinned with a thunderstorePackage - put 'keepWhenUnloaded: true' on those entries and they are left alone without having to turn this off for every other mod. Needs UpdateLoadedModsOnStartup for the removal to reach disk. On by default.");
            ModValidationExemptAdmins = BindServerConfig("Mods", "ModValidationExemptAdmins", false, "If enabled, anyone on the server's adminlist may connect with any mods at all - missing required mods, mods this server does not allow, mismatched versions, modified mod files, unlisted BepInEx patchers, and a missing or wrong attestation. It also covers an admin running no ValheimEnforcer at all, who is otherwise refused for never sending a mod list. The check still runs and its result is still written to the server log, so you can see what your admin was carrying; only the rejection is skipped, and the Discord mod-mismatch notification is not sent for them. This exists so you can test a mod against the live server without editing Mods.yaml first. OFF by default, and be deliberate about turning it on: it makes a place on adminlist.txt the only thing between an account and the entire mod gate, so every admin id in that file needs to be somebody you would trust with an arbitrary client. Admin status is read from the server's own admin list against the connection the request arrived on, so no client can claim it.");
            HashEnforcement = BindServerConfig("Mods", "HashEnforcement", "WhenKnown", "Controls SHA256 file verification of client plugin DLLs during the connect handshake, which catches a mod somebody recompiled with different numbers in it even though its version string is unchanged. 'Off' never checks. 'WhenKnown' (the default) enforces only the mods this server has a recorded hash for, so verification is opt-in per mod and enabling it breaks nothing. 'Strict' additionally rejects any client carrying a Required or AdminOnly mod the server has NO recorded hash for - a deliberately loud signal that the mod list is not fully pinned. Individual mods override this with a 'hashEnforcement' field in Mods.yaml. Note this raises the bar from 'edit one file and rebuild' to 'reverse engineer and patch the enforcer'; it is not a wall.", new AcceptableValueList<string>("Off", "WhenKnown", "Strict"));
            AttestationPolicy = BindServerConfig("Mods", "AttestationPolicy", "Off", "Make each client prove its mod report was generated for THIS connection. The server hands every connection a random value during the handshake and the client returns a digest over that value and the exact mod and patcher list it is sending; the server recomputes it and compares. Without this, a client reports the same list and the same file hashes every session forever, so a client patched to skip the work can replay one captured answer indefinitely. Be clear on what a pass means: it proves the report was produced now, by code that actually ran - it does NOT prove the report is true, because a client that keeps the original DLLs and hashes those still passes. What it costs an attacker is the difference between returning a constant and keeping a working hashing path alive. Off does nothing at all. Report logs a failure and lets the player in. Require rejects them - and note that a player on an older ValheimEnforcer sends no attestation at all and is therefore rejected too, so only set Require once your pack has rolled out.", new AcceptableValueList<string>("Off", "Report", "Require"));
            ValidatePatchers = BindServerConfig("Mods", "ValidatePatchers", false, "Hold clients to a list of allowed BepInEx patchers, the same way they are held to a list of allowed mods. Patchers are the DLLs in BepInEx/patchers - not plugins. BepInEx loads them before any plugin exists and hands each one the game assemblies to rewrite on the way in, which is a more powerful position than any plugin has, and until now nothing here looked at that folder at all. The list is an allowlist, not a required list: a client with no patchers always passes, and only a patcher the server has not allowed is refused. Off by default. Patchers are enumerated and logged either way, so leave this off for a while first and read the log to see what your players actually carry.");
            AutoAddPatchersToAllowed = BindServerConfig("Mods", "AutoAddPatchersToAllowed", true, "Adds the patchers this server itself has to the allowedPatchers list in Mods.yaml at startup, with their file hashes, so a server running patchers of its own does not have to write them out by hand. Mirrors AutoAddModsToRequired. Hashes are only ever added, never replaced, so an entry an admin pinned by hand survives. Turn it off to curate the list yourself - patchers the server runs are then refused for clients like any other.");
            RecordHashesForLoadedMods = BindServerConfig("Mods", "RecordHashesForLoadedMods", true, "If enabled, the SHA256 of every plugin DLL loaded on this machine is recorded into Mods.yaml at startup, so the mods the server itself runs get pinned with no manual work. Hashes an admin pinned by hand, or that came from a thunderstorePackage, are never overwritten. Requires UpdateLoadedModsOnStartup for the result to reach disk.");
            ResolveThunderstoreHashes = BindServerConfig("Mods", "ResolveThunderstoreHashes", false, "If enabled, the server downloads any mod in Mods.yaml carrying a 'thunderstorePackage' field (format Owner-ModName or Owner-ModName-Version, the same format a Thunderstore manifest uses), hashes the DLLs inside the archive in memory, records them, and discards the download. This is how you pin a client-only mod the server never loads itself. Only thunderstore.io and its CDN are ever contacted; arbitrary download URLs are deliberately not supported. Off by default because it makes outbound network requests.");
            RemoveNontrackedItemsFromJoiningPlayers = BindServerConfig("Player Sync", "RemoveNontrackedItemsFromJoiningPlayers", true, "If enabled, any items that are not tracked by the server will be removed from joining player's inventories.");
            AddMissingItemsFromPlayerServerSave = BindServerConfig("Player Sync", "AddMissingItemsFromPlayerServerSave", true, "If enabled, any items the player does not have that are listed on the server will be given to the player when joining");
            RestoreSkillsFromPlayerServerSave = BindServerConfig("Player Sync", "RestoreSkillsFromPlayerServerSave", true, "If enabled, a returning character whose skills are below the levels the server holds for them has them raised back to those levels when they join - the skill counterpart of AddMissingItemsFromPlayerServerSave. This is what brings a character back after its local save was deleted and the character recreated under the same name: the server still holds their progress, and without this the recreated character's blank skills would be uploaded as the new record on their first save. Never lowers a skill; PreventExternalSkillRaises covers the other direction. Skipped on a dirty reconnect unless ItemReturnForDirtyReconnection is on, for the same reason item restoration is: the stored copy can be a delta window stale, and the one way it can hold a higher skill than the player has is a death the server never heard about. On by default.");
            PreventExternalSkillRaises = BindServerConfig("Player Sync", "PreventExternalSkillRaises", true, "If enabled, player skill gains outside of the server are removed when connecting.");
            NewCharactersRemoveExtraItems = BindServerConfig("Player Sync", "NewCharactersRemoveExtraItems", false, "If enabled, new characters that have no existing character file will have all items removed except for starting items.");
            NewCharacterSetSkillsToZero = BindServerConfig("Player Sync", "NewCharacterSetSkillsToZero", false, "If enabled, new characters will have their skills set to zero. Prevents players from raising skills before connecting.");
            PreventExternalCustomDataChanges = BindServerConfig("Player Sync", "PreventExternalCustomDataChanges", true, "If enabled, tracks player custom data. Warning: custom data can be large and can impact how other mods function.");
            newCharacterClearCustomData = BindServerConfig("Player Sync", "newCharacterClearCustomData", true, "If enabled, new characters will have their custom data cleared.");
            PassthroughCompatModCustomData = BindServerConfig("Player Sync", "PassthroughCompatModCustomData", true, "Leaves inventory-describing custom data owned by slot mods (ExtraSlots and its CustomSlots addon, EquipmentAndQuickSlots including its 2.x legacy keys, InventorySlots) to those mods instead of tracking and enforcing it. These mods keep a serialized backup of every slot item in player custom data and restore those items whenever a character loads with empty slots; when the enforcer tracked that backup and re-applied its (stale) copy on every spawn, each death resurrected and duplicated the player's slot gear. With this enabled the affected keys are never stored in server saves, never streamed as deltas, and never overwrite the live player's own value, and the mods' per-item slot-memory keys are ignored when deciding whether an item matches the save. Disable only to restore the old behaviour.", advanced: true);
            // The flag is read from worker threads through a volatile snapshot (see CompatCustomData); keep
            // that snapshot current from the main thread, including on config reload and server sync.
            PassthroughCompatModCustomData.SettingChanged += (sender, args) => modules.compat.CompatCustomData.RefreshEnabled();
            modules.compat.CompatCustomData.RefreshEnabled();
            NewCharacterStartingItems = BindServerConfig("Player Sync", "NewCharacterStartingItems", "ArmorRagsChest,ArmorRagsLegs,Torch", "Comma separated prefab names a brand new character is allowed to arrive holding when NewCharactersRemoveExtraItems is enabled. Anything else in their inventory on their first join is confiscated, as is any item above quality 1. Names are matched exactly (case insensitively), not as substrings, so 'Torch' does not also permit 'TorchMist'. Change this if your modpack starts players with different gear; leave it empty to allow no starting items at all.");
            ConfiscateUnidentifiableItems = BindServerConfig("Player Sync", "ConfiscateUnidentifiableItems", false, "Controls what happens to an inventory item whose ItemDrop prefab does not resolve on the client - usually a modded item, or an entry another mod created directly. These cannot be tracked, matched or handed back, so by default they are left alone and logged. Enable to confiscate them instead; note that a confiscated item with no prefab name can never be returned with the confiscation commands.", null, true);
            InitialCharacterSyncWaitSeconds = BindServerConfig("Player Sync", "InitialCharacterSyncWaitSeconds", 10, "How long a joining client waits for the server's answer about its stored character before giving up and treating the character as new. The answer normally arrives during the connection handshake, well before the world finishes loading, so this only matters if that is delayed. Set to 0 to never wait. Either way the character is treated as NEW when no answer arrives - the local save file on the joining machine is never used as the baseline for a server.", true, 0, 60);
            ValidateItemCustomData = BindServerConfig("Player Sync", "ValidateItemCustomData", true, "If enabled, custom data on items will be validated.");
            ValidateItemDurability = BindServerConfig("Player Sync", "ValidateItemDurability", true, "If enabled, item durability will be validated");
            ItemValidationDurabilityAllowedVariance = BindServerConfig("Player Sync", "ItemValidationDurabilityAllowedVariance", 10f, "Allowed variance for item durability validation.", true, 0, 100f);
            SavePlayerStatusEffectsOnLogout = BindServerConfig("Player Sync", "SavePlayerStatusEffectsOnLogout", true, "Whether or not to save active character effects on logout and reapply on login");
            ItemRemovalForDirtyReconnection = BindServerConfig("Player Sync", "ItemRemovalForDirtyReconnection", false, "Leniency for dirty reconnects (crash/timeout, where the server save may be up to one delta window stale). RemoveNontrackedItemsFromJoiningPlayers always runs otherwise; if this is enabled, untracked items are NOT confiscated when the player's last disconnect was dirty, so crash victims keep items gained in the unsaved window.");
            ItemReturnForDirtyReconnection = BindServerConfig("Player Sync", "ItemReturnForDirtyReconnection", false, "Leniency for dirty reconnects. AddMissingItemsFromPlayerServerSave always restores missing tracked items on a clean join, and RestoreSkillsFromPlayerServerSave raises lowered skills the same way; on a dirty reconnect both are skipped by default (to avoid duping items consumed, or handing back skill lost to a death, in the unsaved window) unless this is enabled.");
            ServerSideJoinEnforcement = BindServerConfig("Player Sync", "ServerSideJoinEnforcement", true, "If enabled, the server re-applies the join rules (item confiscation, skill clamping, custom-data, Forsaken Power and food reset) to the first full character save a RETURNING player uploads each session, instead of trusting the client to have done it. This is the returning-character counterpart to the first-save enforcement the server already runs for brand new characters: the client runs the same checks, but the client is what you are defending against, so this is the copy a modified client cannot skip. Honours RemoveNontrackedItemsFromJoiningPlayers, PreventExternalSkillRaises, PreventExternalCustomDataChanges, PreventExternalForsakenPowerChanges, PreventExternalFoodChanges and the dirty-reconnect leniency settings, so turning those off turns off the matching server-side check too. Inert if none of them are on.");

            PreventExternalForsakenPowerChanges = BindServerConfig("Player Sync", "PreventExternalForsakenPowerChanges", false, "If enabled, each character's save records the Forsaken Power they have selected, and it is put back when they join - so a power picked up in a solo world or on another server, one this server may never have unlocked at its boss stones, cannot be brought in. Selecting a power at a boss stone while playing here is saved as normal. A character whose save was written before this was enabled has no power recorded yet: they keep the one they arrive with on their next join and are tracked from then on, so switching this on strips nobody. Also checked server side when ServerSideJoinEnforcement is on. Pair with NewCharacterClearForsakenPower, which covers a character's first join. Off by default.");
            NewCharacterClearForsakenPower = BindServerConfig("Player Sync", "NewCharacterClearForsakenPower", false, "If enabled, a character joining this server for the first time has their Forsaken Power cleared, so one selected in a solo world or on another server does not come with them. They can select one at a boss stone here as normal. Pair with PreventExternalForsakenPowerChanges, which stops a returning character bringing a different one in later. Off by default.");
            NewCharacterResetMapExploration = BindServerConfig("Player Sync", "NewCharacterResetMapExploration", false, "If enabled, a character joining this server for the first time has their map of this world wiped: explored areas, anything a cartography table revealed, and their saved pins. Valheim keeps a separate map for every world, so arriving with this one already uncovered means having played a copy of this world somewhere else - or being a character whose save an admin deleted to reset them, who now gets a fresh map along with everything else. The wipe only happens once the server has confirmed it holds no save for the character; if that answer has not arrived by the time the join is validated the map is left alone, because unlike an item a wiped map cannot be given back. The map lives only in the player's own character file and never reaches the server, so this is carried out by the client and cannot be checked server side. Off by default.");
            PreventExternalFoodChanges = BindServerConfig("Player Sync", "PreventExternalFoodChanges", false, "If enabled, each character's save records the foods they have eaten and how long each has left, and those exact foods are put back when they join. Vanilla keeps eaten food in the player's own character file, so without this a player can log off, eat food this server has not reached yet in a solo world, and come straight back with it - or just top their food back up for free. A returning character gets back what they left with, whatever they ate or let run out in between; a character joining for the first time has all their food cleared, and can eat as normal once here. A character whose save was written before this was enabled has no foods recorded yet: they keep what they arrive with on their next join and are tracked from then on, so switching this on strips nobody. The record is only as fresh as the character's last save, so after a crash or dropped connection a player can get back the burn time used since then - at most FullSyncPullIntervalMinutes' worth. Also checked server side when ServerSideJoinEnforcement is on. Off by default.");
            EnableProgressionSync = BindServerConfig("Player Sync", "EnableProgressionSync", false, "Master switch for storing a character's map, known recipes, trophies, statistics and spawn point on the server the way their items and skills already are. Everything in this group lives in the player's own character file, which means a player can edit it offline and a corrupted local save loses it for good; with this on the server keeps its own copy, hands it back on join, and its copy is the one that counts. Nothing is stored until at least one of the Sync* settings below is also on, and no extra data crosses the wire or reaches the disk while this is off. Note what this cannot do: the server can restore a statistic and refuse a value higher than the one it holds, but it has no way to prove any individual increment was earned honestly, so treat the statistics as progress that survives a lost save rather than as a cheat-proof record. Steam and Xbox achievement unlocks are held by the platform, not in the save, and are not touched at all. Off by default; every part of the feature is inert until this is on.");
            SyncMapExploration = BindServerConfig("Player Sync", "SyncMapExploration", false, "If enabled, the server stores each character's map of this world - explored ground, what a cartography table revealed, and their saved pins - and pushes its copy back when they join. A map uncovered in a copy of this world elsewhere is replaced by what this server has seen, and a player whose local character file is lost gets their exploration back. The map is stored beside the character save as an opaque .map file rather than inside it, because a fully explored map is 8.4 megabytes before compression; a real one costs tens of kilobytes to a megabyte of disk per character per world. The client only rebuilds and uploads it when something has actually been explored since the last upload, at most once every MapSyncIntervalMinutes and once more at logout, because building it is not free on the player's machine. Requires EnableProgressionSync. Not stored in InternalStorageMode - a megabyte of opaque bytes per character does not belong in the world file - so the two together leave the map untracked and say so in the log. Off by default.");
            SyncKnownItems = BindServerConfig("Player Sync", "SyncKnownItems", false, "If enabled, the server stores what each character knows - recipes, build pieces, materials, crafting stations, discovered biomes, runestone texts and permanent unlocks - and restores its copy on join. This is the opposite end of NewCharacterClearKnownRecipes: instead of wiping what a character arrived knowing because there is nothing to check it against, the server now has something to check it against, and anything learned somewhere it did not see is replaced. Vanilla rediscovers a recipe the moment the player holds its materials and has seen its crafting station, so the materials and stations are what is really being held and the recipe list is rebuilt from them on arrival. Requires EnableProgressionSync. Off by default.");
            SyncTrophies = BindServerConfig("Player Sync", "SyncTrophies", false, "If enabled, the server stores the trophy list vanilla keeps in each character file - every trophy that character has ever picked up, which is what the compendium reads - and restores it on join. Kept separate from SyncKnownItems because vanilla files trophies by prefab name while everything else is a localization token, and because an admin may reasonably want one without the other. Requires EnableProgressionSync. Off by default.");
            SyncPlayerStats = BindServerConfig("Player Sync", "SyncPlayerStats", false, "If enabled, the server stores each character's statistics - the counters behind the in-game Statistics panel and the post-1.0 achievement system: kills, deaths, distance travelled, items crafted, pieces placed and the rest - and restores them on join. A counter the client reports above the value the server holds was raised somewhere this server did not see, and is put back, the same way PreventExternalSkillRaises handles skills. Be clear about the limit: the server can hold a ceiling and restore a lost save, but it cannot verify that any single increment was honest. Achievement unlocks themselves are held by Steam or Xbox rather than in the save and are not affected. Requires EnableProgressionSync. Off by default.");
            SyncSpawnPoint = BindServerConfig("Player Sync", "SyncSpawnPoint", false, "If enabled, the server stores where each character respawns on this world - the bed they have claimed, and the point the game falls back to when that bed is gone - and restores it on join, so a lost or corrupted local character file no longer means waking up at the start stone with a base on the other side of the map. Only the spawn point is stored; where a character logged out and where they died are deliberately not, because putting a player back at a stale logout point would relocate somebody who has since walked away. Requires EnableProgressionSync. Off by default.");
            MapSyncIntervalMinutes = BindServerConfig("Advanced", "MapSyncIntervalMinutes", 15, "How often, in minutes, a client may upload its map. Building the upload means rebuilding and compressing an 8.4 megabyte package on the player's own machine, so this is deliberately coarse; the client also skips the upload entirely when nothing has been explored since the last one, and sends once more at logout regardless. Only used when SyncMapExploration is on.", advanced: true, valmin: 1, valmax: 240);
            NewCharacterClearPlayerStats = BindServerConfig("Player Sync", "NewCharacterClearPlayerStats", false, "If enabled, a character joining this server for the first time starts with their statistics at zero rather than carrying in the totals from wherever they were played before. The same reasoning as NewCharacterSetSkillsToZero, and worth knowing before turning it on: these counters are per character and not per world, so this discards a record of everything that character has ever done anywhere, and it cannot be undone. Only used when SyncPlayerStats is on. Off by default.");
            EnableSaveArchives = BindServerConfig("Backups", "EnableSaveArchives", false, "Master switch for keeping rolling, compressed archives of this server's world save together with its character saves. The game already rotates backups of its own - two of them, uncompressed, of the world only - so what this adds is a longer history, compression, and the character store in the same archive. That last part is the reason it exists: restoring one of the game's backups puts the world back and leaves every character exactly as they were, which is its own kind of rollback. Be aware of the cost before switching it on. A real world file reaches 200 megabytes, the game's own rotation already keeps around a gigabyte beside it, and each archive here is another copy of that on top - compressed, but still measured in hundreds of megabytes. Archives are written after a world save finishes, never during one, so an archive is always of a complete save. Off by default; every part of the feature is inert until this is on.");
            SaveArchiveIntervalMinutes = BindServerConfig("Backups", "SaveArchiveIntervalMinutes", 120, "The shortest gap, in minutes, between two archives. An archive is only ever taken just after a world save completes, so the real cadence is this rounded up to the next save; a value below the server's own save interval simply means every save. enforcer-archive-now ignores this for one archive.", valmin: 5, valmax: 1440);
            SaveArchiveKeepCount = BindServerConfig("Backups", "SaveArchiveKeepCount", 5, "How many archives to keep. The oldest beyond this are deleted right after a new one is written, and lowering it takes effect at the next archive rather than one file at a time - this is a setting about disk space, so it has to give the disk back when you ask.", valmin: 1, valmax: 100);
            SaveArchiveMaxTotalMB = BindServerConfig("Backups", "SaveArchiveMaxTotalMB", 0, "A ceiling, in megabytes, on what all archives together may occupy. Oldest first are deleted until the total fits. 0 means no ceiling, and SaveArchiveKeepCount alone decides. The newest archive is never deleted to satisfy this, so setting it below the size of a single archive keeps one rather than none.", valmin: 0, valmax: 1000000);
            SaveArchiveIncludeCharacters = BindServerConfig("Backups", "SaveArchiveIncludeCharacters", true, "Whether to include the Characters folder - every player's items, skills, confiscation record and, if it is on, their synced progression. On by default, because a world restored without it is a world where everybody's character is from a different moment in time.");
            SaveArchiveIncludeConfig = BindServerConfig("Backups", "SaveArchiveIncludeConfig", true, "Whether to include this mod's own configuration: the .cfg and the YAML files beside it (Mods.yaml, Notifications.yaml, KnownCheaters.yaml, PlayerIds.yaml). Small, and it is what makes an archive enough to rebuild a server from. Other mods' configuration is deliberately not included - it is not this mod's to copy around.");
            SaveArchiveCompression = BindServerConfig("Backups", "SaveArchiveCompression", "Fastest", "How hard to compress. 'Fastest' is the default and the right answer for a world file, which is already dense; 'Optimal' buys a little more at a large cost in CPU time on a file this size; 'NoCompression' just packs the files together. The work happens on a background thread either way, so this trades disk against a background core rather than against frame time.", new AcceptableValueList<string>(new string[] { "Fastest", "Optimal", "NoCompression" }));
            SaveArchivePath = BindServerConfig("Backups", "SaveArchivePath", "", "Where archives are written. Empty means BepInEx/config/ValheimEnforcer/Archives. Point it at another drive if you would rather not keep a second copy of the world on the same one that holds the first.");
            EnableCrashRecovery = BindServerConfig("Crash Recovery", "EnableCrashRecovery", false, "Master switch for emergency crash recovery. Every few minutes the server hands each connected player a sealed snapshot of their own character - encrypted and signed with a key that never leaves the server, so it is opaque to whoever is holding it and useless anywhere else. If this server then crashes and comes back from an older save, it asks for those snapshots back and adopts the ones that verify and are genuinely newer than what it holds. Understand the trade before switching it on. The world and the character store are separate saves and roll back independently, so restoring a character to a state newer than the world can re-introduce items whose source in the world - the chest they came out of, the vein they were mined from - has itself rolled back. That is item duplication, and it is inherent to putting a character back rather than a fault in how it is done. It is also why restoration only ever happens inside a narrow window after an unclean shutdown. Note what this is not: the client cannot read its own snapshot, so this is no help against a lost or corrupted LOCAL character file - EnableProgressionSync is what covers that direction. Off by default; every part of the feature is inert until this is on.");
            CrashRecoveryAutoRestore = BindServerConfig("Crash Recovery", "CrashRecoveryAutoRestore", true, "Whether a verified, newer snapshot is adopted on its own while the recovery window is open, or whether the window only reports what could be restored. On by default, because the window is already narrow and only opens after an unclean shutdown - but it is a separate switch from EnableCrashRecovery so an admin can have the snapshots kept and distributed without the server ever acting on one unasked. With this off, nothing a client hands back is ever applied.");
            CrashRecoveryWindowMinutes = BindServerConfig("Crash Recovery", "CrashRecoveryWindowMinutes", 30, "How long after an unclean shutdown the server keeps accepting snapshots. The window opens only when the previous run left no clean-shutdown marker, and closes on its own after this many minutes; an admin can open one by hand with enforcer-recovery-open for a rollback the server could not have noticed, such as a restore from a backup.", valmin: 1, valmax: 720);
            CrashRecoveryPushIntervalMinutes = BindServerConfig("Crash Recovery", "CrashRecoveryPushIntervalMinutes", 10, "How often each connected player is sent a fresh snapshot. This is the bound on how much can be lost: a crash costs at most whatever happened since a player's last snapshot. It is also the bandwidth knob - a snapshot is a compressed character save, tens to hundreds of kilobytes, sent per player per interval - so a busy server should think about this number rather than only about the smallest one.", valmin: 1, valmax: 120);
            CrashRecoveryKeepBlobs = BindServerConfig("Crash Recovery", "CrashRecoveryKeepBlobs", 3, "How many characters' snapshots a client keeps per world. One file per character, each replaced by a newer one rather than accumulating, so this bounds how many different characters are remembered rather than how many copies of one.", valmin: 1, valmax: 20);
            EnableStarterLoadouts = BindServerConfig("Player Sync", "EnableStarterLoadouts", false, "Master switch for starter loadouts: a kit of items, skill levels, known materials and a spawn point handed to a character joining this server for the first time. The kits themselves are written in Loadouts.yaml beside this file, and DefaultStarterLoadout says which one new characters get. A loadout is granted AFTER the new-character rules have stripped whatever the character arrived with, so its items do not also need listing in NewCharacterStartingItems, and it may hand over an upgraded item even though the allowlist refuses one that turns up on its own. Skills are only ever raised, never lowered. Off by default.");
            DefaultStarterLoadout = BindServerConfig("Player Sync", "DefaultStarterLoadout", "", "The name of the loadout in Loadouts.yaml that a character joining for the first time gets. Empty means none, which leaves the feature doing nothing even with EnableStarterLoadouts on. A name that is not in the file is reported in the log and treated as none, rather than as a reason to refuse the join.");
            RecordSkillReductions = BindServerConfig("Player Sync", "RecordSkillReductions", true, "If enabled (the default), every time this mod lowers a character's skill - a returning character clamped back to the level the server holds for them under PreventExternalSkillRaises, or a first-time character's skills set to zero under NewCharacterSetSkillsToZero - the skill, the level it was lowered from and to, when, and why are written into that character's save, and enforcer-skills-list shows them. That record is what lets an admin undo one: enforcer-skills-restore puts each skill back to the highest level it was recorded being lowered from, straight away if the player is online and on their next join if not, and enforcer-skills-clear forgets a record without restoring anything. Only reductions this mod makes are recorded - the skill loss on death is the game's own, and a reported value the game could never produce (above 100, negative) is corrected without a record because there is nothing valid to put it back to. Turn this off to stop recording; records already made are kept until they are restored or cleared.");
            NewCharacterClearKnownRecipes = BindServerConfig("Player Sync", "NewCharacterClearKnownRecipes", true, "If enabled, a character joining this server for the first time forgets every recipe and build piece they discovered somewhere else. Valheim discovers a recipe as soon as the player knows its materials and crafting station, so the materials, crafting stations and trophies they know are cleared too - the same reset as the game's resetknownitems command - and discovery starts again from what they are carrying once the other new-character rules have run. Like NewCharacterResetMapExploration this only happens once the server has confirmed it holds no save for the character, because forgotten recipes cannot be given back, and it is carried out by the client because recipes never reach the server. Never applies in singleplayer or to a listen host's own character. Note that on a server which already has players, anyone joining for the first time since Valheim Enforcer was installed has no save yet and counts as new. On by default.");

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
            StallWarningThresholdMs = BindServerConfig("Advanced", "StallWarningThresholdMs", 100, "How long (in milliseconds) one of this mod's operations may take before it is reported as a frame hitch in the log. Covers the periodic cheat scans, the character delta flush and character saves. Purely diagnostic - it changes nothing about what the mod does, it only decides when a slow operation is worth a warning. Lower it while chasing a stutter report; raise it, or set 0 to disable the warning entirely, if a slow disk makes it noisy.", advanced: true, valmin: 0, valmax: 10000);
            ConfigPollIntervalSeconds = BindServerConfig("Advanced", "ConfigPollIntervalSeconds", 30, "How frequently (in seconds) the mod polls config files on disk for changes.", advanced: true, valmin: 1, valmax: 300);
            DeltaSynchronizationFrequencyInSeconds = BindServerConfig("Advanced", "CharacterDeltaTracker", 15, "Minimum time (in seconds) between incremental inventory/skill/custom-data updates. Updates are only produced when the player's inventory actually changes, so an idle player sends nothing; this is a rate limit rather than a polling interval.", advanced: true, valmin: 5, valmax: 300);
            FullSyncPullIntervalMinutes = BindServerConfig("Advanced", "FullSyncPullIntervalMinutes", 25, "How often (in minutes) the server asks connected players to upload a full character save. Full saves are a periodic reconciliation layered on top of the incremental delta updates (CharacterDeltaTracker); they are no longer tied to the world/profile autosave.", advanced: true, valmin: 1, valmax: 1440);
            HashComputeTimeoutSeconds = BindServerConfig("Advanced", "HashComputeTimeoutSeconds", 30, "Maximum time spent hashing local plugin DLLs at startup before giving up and reporting the remainder as unverifiable. Hashing runs on background threads and usually takes well under a second; this is a safety valve for a stalled disk, not a tuning knob.", advanced: true, valmin: 5, valmax: 300);
            ThunderstoreMaxArchiveMB = BindServerConfig("Advanced", "ThunderstoreMaxArchiveMB", 128, "Largest Thunderstore archive, in megabytes, the server will download when resolving mod hashes. Archives are held in memory while their DLLs are hashed, so this is also the peak transient allocation; packages are resolved one at a time so it is never multiplied. Larger archives are skipped and logged.", advanced: true, valmin: 1, valmax: 512);
            FullSyncMaxConcurrentPlayers = BindServerConfig("Advanced", "FullSyncMaxConcurrentPlayers", 5, "Maximum number of players the server asks to upload a full character save at the same time. Larger player counts are staggered into successive waves of this size to avoid a bandwidth spike. 10 is safe on a healthy server; lower it on constrained upload/VPS hosts.", advanced: true, valmin: 1, valmax: 50);
            EnforceRoutedRpcSender = BindServerConfig("Advanced", "EnforceRoutedRpcSender", true, "If enabled (the default), the server verifies the sender id on every routed network message against the connection it actually arrived on, and corrects it if they disagree. Valheim's routed RPC carries a sender id that the sending client writes and the server never checks, so without this a modified client can impersonate any other connected player - running admin commands, getting someone else banned, or overwriting another player's character save. This affects only forged packets; an honest client is never touched. Leave it on unless another mod is misbehaving because of it, in which case report the mod - turning this off re-opens sender spoofing for this mod AND every other routed RPC on the server.", advanced: true);

            CharacterCacheIdleMinutes = BindServerConfig("Advanced", "CharacterCacheIdleMinutes", 30, "How long, in minutes, the server keeps a character's save in memory after that character was last touched - a save, an incremental update, a death record, or a login. Saves on disk are always the authority, so an entry dropped from memory costs one file read the next time that player's data changes; this exists so a busy server's memory reflects who is playing now rather than everyone who has joined since the last restart. Keep it above FullSyncPullIntervalMinutes so players who are online but idle are not reloaded after every periodic pull. 0 keeps every character in memory until restart, which is what earlier versions did. Not used with InternalStorageMode.", advanced: true, valmin: 0, valmax: 1440);
            // Read from the character store's worker thread through a volatile snapshot; keep that snapshot
            // current from the main thread, including on config reload and server sync.
            CharacterCacheIdleMinutes.SettingChanged += (sender, args) => modules.character.CharacterStore.SetIdleEvictionMinutes(CharacterCacheIdleMinutes.Value);
            modules.character.CharacterStore.SetIdleEvictionMinutes(CharacterCacheIdleMinutes.Value);
            MemoryReportIntervalMinutes = BindServerConfig("Advanced", "MemoryReportIntervalMinutes", 0, "When above 0, writes the summary enforcer-memory prints to the server log every this-many minutes: process working set, managed heap, what this mod is holding (cached characters, audit buffers, per-player tables) and the world's object counts. Purely diagnostic. Off by default.", advanced: true, valmin: 0, valmax: 1440);

            EnableCheatDetection = BindServerConfig("Anti-Cheat", "EnableCheatDetection", true, "Master switch for client-side cheat scanning. When enabled the client checks running processes, the DLLs loaded into the game, and open window titles against a catalog of known cheat tools. Only matched entries are reported to the server - the player's full process list is never transmitted.");
            DetectValheimTooler = BindServerConfig("Anti-Cheat", "DetectValheimTooler", true, "Detect ValheimTooler by the namespace of the types it loads (rename-proof), including assemblies injected mid-session. A confirmed detection is always auto-banned regardless of ActionOnDetection. High confidence, very low cost.");
            DetectCheatTools = BindServerConfig("Anti-Cheat", "DetectCheatTools", true, "Scan for the built-in catalog of known cheat tools: WeMod/Wand, ArtMoney, PLITCH, Speed Gear, Squalr, WPE Pro, and the injectors/loaders used to deliver Valheim cheats (SharpMonoInjector, Xenos, Extreme Injector, ValheimTooler launcher, ValHack, Valheim Mod Menu). Tools with no legitimate purpose are auto-banned; the rest follow ActionOnDetection.");
            DetectCheatEngine = BindServerConfig("Anti-Cheat", "DetectCheatEngine", true, "Include Cheat Engine in the catalog scan (process names, window titles, and injected speedhack/DBK modules). Its TfrmMain/TfrmMemView window classes are generic Delphi names shared by legitimate software, so a class-only sighting is logged but never kicked or banned. Note: Cheat Engine has legitimate uses — prefer Log action over Kick/Ban. Requires DetectCheatTools.");
            DetectGenericTrainers = BindServerConfig("Anti-Cheat", "DetectGenericTrainers", true, "Flag any running process whose executable name contains the word 'trainer' (e.g. 'Valheim Trainer.exe', 'Hitman 3 Trainer - FLiNG.exe'). Catches FLiNG, MrAntiFun and Cheat Happens trainers without listing each one. Follows ActionOnDetection.");
            ScanLoadedModules = BindServerConfig("Anti-Cheat", "ScanLoadedModules", true, "Scan the native DLLs loaded into the game process itself. This is the only way to see a cheat that has already injected and then closed its launcher, and it survives renaming the tool's executable. Cheap - the module list is local to our own process.");
            ScanWindowTitles = BindServerConfig("Anti-Cheat", "ScanWindowTitles", true, "Scan open window classes and titles. Catches tools that have been renamed to evade the process-name check, most notably Cheat Engine. Generic framework window classes (e.g. Delphi's TfrmMain) are treated as low confidence: the server logs the sighting but takes no action on it alone.");
            ScanElevatedProcesses = BindServerConfig("Anti-Cheat", "ScanElevatedProcesses", true, "Let the process scan see programs run as administrator and background services. Valheim's runtime silently leaves those out of its own process list - roughly a third of what runs on a typical desktop - so without this any cheat tool started elevated, WeMod/Wand included, is invisible to the process check. Names are read from a Windows process snapshot without opening any process, and as before only matched names are reported to the server. Turn off only to go back to the old process list if this causes a problem. Windows only.", advanced: true);
            AdditionalCheatProcesses = BindServerConfig("Anti-Cheat", "AdditionalCheatProcesses", "", "Comma-separated list of extra process names to treat as cheat tools, without the '.exe' suffix, matched exactly and case-insensitively. Empty by default. Suggested opt-in values for strict servers: x64dbg, x32dbg, x96dbg, ProcessHacker, SystemInformer, HxD, ReClass.NET, ollydbg, Scylla_x64, frida, Fiddler, Charles. WARNING: every one of those is a standard developer tool with heavy legitimate use by modders and streamers, which is why none of them ship enabled. Deliberately excluded from the built-in catalog and NOT recommended here: Aurora (collides with Aurora RGB lighting software), Process Lasso (a CPU priority optimiser, not a speedhack), AutoHotkey (compiled scripts take arbitrary names, so the check is worthless, and it is widely used for accessibility and key remapping), and MSI Afterburner/RivaTuner/OBS (their overlay DLLs look injector-shaped).");
            IgnoredCheatProcesses = BindServerConfig("Anti-Cheat", "IgnoredCheatProcesses", "", "Comma-separated allowlist of process, module or window names to never flag, matched as a case-insensitive substring. Applied last, so it overrides the built-in catalog and AdditionalCheatProcesses. Use this to keep playing when a legitimate program trips a signature.");
            //DetectSpeedhack = BindServerConfig("Anti-Cheat", "DetectSpeedhack", true, "Detect speedhack via Unity time vs. wall-clock drift.");
            CheatDetectionAction = BindServerConfig("Anti-Cheat", "ActionOnDetection", "Kick", "Server-side action taken when a cheat tool is reported. Note that dedicated game-cheating tools (injectors, ValheimTooler, ValHack, Valheim Mod Menu) are always auto-banned regardless of this setting, and low-confidence sightings (generic window classes) are always logged only, regardless of this setting.", new AcceptableValueList<string>("Log", "Kick", "Ban"));
            CheatScanIntervalSeconds = BindServerConfig("Anti-Cheat", "ScanIntervalSeconds", 30, "Seconds between periodic client scan ticks. The process, module and window scans are staggered across successive ticks so their cost never lands on the same frame, so each individual scan runs every three intervals. ValheimTooler assembly detection is event-driven and not affected by this interval.", false, 5, 300);

            EnableStructureValidation = BindServerConfig("World Integrity", "EnableStructureValidation", false, "Master switch for server-side validation of the structures clients place. When enabled, the server inspects the objects arriving from each client and reports the ones no legitimate client can produce: geometry that is not in any build menu, and pieces whose health is above what the prefab was designed to hold. This is the check for somebody spawning dungeon rooms, dvergr towns and ruins into a world - the structures that show a nameplate with no crafter on it, cannot be destroyed, and flatten the ground where they land. Off by default; every part of the feature is inert until this is on.");
            DetectNonBuildableStructures = BindServerConfig("World Integrity", "DetectNonBuildableStructures", true, "Flag a client that creates a structure which is in no build menu. Membership of a piece table is what makes a prefab placeable at all - by the hammer, the hoe, the cultivator, and by every blueprint or bulk-building mod, which all place out of those same tables - so a mod's own pieces are covered automatically and a large blueprint cannot trip this.");
            BlockSpawnObjectRPC = BindServerConfig("World Integrity", "BlockSpawnObjectRPC", true, "Block ZNetScene's SpawnObject RPC, an unused routed call that nothing in the game legitimately sends but which lets any client have the server instantiate any prefab by hash - creatures and items, not just structures. When on (the default), every client-originated SpawnObject is refused, reported to the moderation Discord channel (the structureFlagged notification, controlled by Discord.NotifyStructureFlagged) and follows StructureValidationAction for what happens to the player - which defaults to Log, so the default is 'block it and post to Discord' without kicking or banning. A structure spawned this way is reported as a structure detection either way. Requires EnableStructureValidation. Turn this off to fall back to blocking only non-buildable structures via SpawnObject, in case a mod on your server legitimately uses this RPC.");
            DetectExcessiveStructureHealth = BindServerConfig("World Integrity", "DetectExcessiveStructureHealth", true, "Flag a client that writes a piece's health above the maximum its prefab allows, which is how an indestructible structure is actually made - there is no separate invulnerability flag in Valheim, just an absurd number in the health field. The ceiling accounts for world modifiers that raise building health, and repairing a piece to full is never flagged. Health that was already too high before the client touched it is attributed to nobody, so walking past a cheated structure cannot get an innocent player reported; use Enforcer-Scan-Structures to find those. This is the one check that needs a hook on every ZDO the server deserializes - it compares against the health the object held before the client's write, which no longer exists once the packet is done - so the hook is only installed when this and EnableStructureValidation are both on at startup, and turning either on in a running server takes a restart before this check begins working. The other checks, and the server-side death handling under ServerSideJoinEnforcement, are unaffected either way.");
            StructureHealthAllowedMultiplier = BindServerConfig("World Integrity", "StructureHealthAllowedMultiplier", 1f, "Headroom on the health ceiling, for servers running a mod that raises piece health at runtime rather than on the prefab (a building-strength skill, for example). 1 means the prefab's own maximum, which is correct for vanilla and for mods that edit the prefab. Raise it only if legitimate pieces are being flagged, and prefer IgnoredStructurePrefabs if only a few prefabs are affected.", advanced: true, valmin: 1f, valmax: 1000f);
            StructureValidationAction = BindServerConfig("World Integrity", "StructureValidationAction", "Log", "Server-side action taken against a player caught placing invalid structures. Detections are always written to the server log and posted to Discord regardless of this setting; this only controls what happens to the player. Defaults to Log so a server can watch the detector for a while before letting it remove anybody.", new AcceptableValueList<string>("Log", "Kick", "Ban"));
            RemoveDetectedStructures = BindServerConfig("World Integrity", "RemoveDetectedStructures", false, "Destroy a flagged structure instead of only reporting it. Deliberately separate from the action above, and off by default, because a false positive here deletes something rather than merely naming it - run with this off first and read the log. Note this removes the structure, not the terrain flattening that came with it; that arrives as separate objects and still needs re-terraforming by hand.");
            StructureValidationExemptAdmins = BindServerConfig("World Integrity", "StructureValidationExemptAdmins", true, "Whether anyone on the server's adminlist is exempt from structure validation. On by default, unlike the other admin exemptions in this mod: spawning a non-buildable prefab is an ordinary thing to do with devcommands, and an admin building with them should not have to notice this feature exists. Turn it off to hold admins to the same rules as everyone else.");
            DetectItemOrigins = BindServerConfig("World Integrity", "DetectItemOrigins", false, "Master switch for checking where a player's new equipment came from. A crafted item records who made it; an item conjured with devcommands or a mod menu records nobody, because only the crafting path ever writes that field. Note that having no crafter is perfectly normal on its own - every loot drop, chest item and trader purchase in the game has none - so the server works out from the loaded prefabs which equipment has no uncrafted route at all, and only reports that. Only items that have just appeared are examined, never whole inventories, so gear a character already had is never re-examined and no migration is needed. This warns and never confiscates. Off by default; every part of the feature is inert until this is on.");
            DetectUncraftedEquipment = BindServerConfig("World Integrity", "DetectUncraftedEquipment", true, "Report equipment that appears with no crafter recorded and that nothing in this world drops, sells or spawns. The list of things obtainable without crafting is read from the loaded prefabs - drop tables, creature drops, pickables and trader stock - so a modded boss with a custom weapon drop is covered without anyone listing it. Requires DetectItemOrigins.");
            DetectUnknownCrafterIds = BindServerConfig("World Integrity", "DetectUnknownCrafterIds", true, "Report equipment whose crafter is a player id nobody on this server has ever reported. Somebody who spawns gear and then stamps a crafter on it has to invent a number, and an invented one belongs to no player here. Trading is unaffected: the question is whether the id is known, not whether it is yours, so a sword one player made and gave to another is fine. Ids are collected from connected players and kept in PlayerIds.yaml. Expect some noise at first - an item crafted by somebody who has not joined since you enabled this has a crafter the registry does not know yet - and note that nothing is reported at all until the registry has somebody in it. Requires DetectItemOrigins.");
            ItemOriginExemptAdmins = BindServerConfig("World Integrity", "ItemOriginExemptAdmins", true, "Whether admins are exempt from item origin checks. On by default, matching StructureValidationExemptAdmins: spawning items is an ordinary thing to do with devcommands and an admin should not have to notice this feature exists.");
            IgnoredItemOriginPrefabs = BindServerConfig("World Integrity", "IgnoredItemOriginPrefabs", "", "Comma-separated allowlist of item prefab names never reported, matched as a case-insensitive substring so one entry can cover a family. Applied before every other check. This is the escape hatch for a mod that hands out gear by a route the prefab scan cannot see - a quest reward written in code, an item granted by a script - and it is the thing to reach for rather than turning the feature off.");
            IgnoredStructurePrefabs = BindServerConfig("World Integrity", "IgnoredStructurePrefabs", "", "Comma-separated allowlist of prefab names never flagged, matched as a case-insensitive substring so one entry can cover a family of prefabs (e.g. 'dvergrprops_' covers all of them). Applied last, so it overrides every check above. This is the escape hatch when a mod on your server legitimately creates an object this detector does not recognise - reach for it rather than turning the whole feature off, and reach for it before enabling RemoveDetectedStructures.");

            EnableRpcGuards = BindServerConfig("Network Integrity", "EnableRpcGuards", false, "Master switch for server-side validation of the vanilla routed RPCs a client can send. Valheim relays these without checking anything about who sent them or what they contain, so one modified client can teleport everybody into the ocean, delete a base, impersonate an admin in chat or one-shot another player. Each guard below inspects the packet on the server, where a client cannot lie about it, and drops the ones no legitimate client produces. Off by default; every guard is inert until this is on.");
            GuardChatSenderName = BindServerConfig("Network Integrity", "GuardChatSenderName", true, "Replace the display name on every relayed chat message with the name the SERVER holds for that connection. Valheim carries the sender's name inside the chat payload, written by the sending client and never checked, so any client can speak as any player or as an admin - a documented feature of the cheat tools in circulation. This rewrites the name rather than dropping the message, so an honest player never notices and an impersonator simply appears under their own name. No false positives are possible. Requires EnableRpcGuards.");
            GuardPlayerTeleportRpc = BindServerConfig("Network Integrity", "GuardPlayerTeleportRpc", true, "Refuse the RPC_TeleportPlayer message from non-admin clients. It teleports whoever RECEIVES it, and a client may address it to everybody at once, so it is the cheapest way to relocate a whole server into the ocean or the Ashlands. Vanilla sends it from exactly one place - the admin-only 'recall' console command - so an admin using recall still works while everyone else is refused. Honours RpcGuardExemptAdmins. Requires EnableRpcGuards.");
            GuardZdoDestruction = BindServerConfig("Network Integrity", "GuardZdoDestruction", true, "Filter the DestroyZDO message, which names a list of world objects for the server to delete and is checked for nothing at all - not ownership, not distance, not count. Vanilla only ever sends it for objects the sending client owns, so this drops the ids a client neither owns nor is standing near, and lets the rest through. Objects the SERVER destroys are never filtered. The packet is filtered rather than dropped whole, so one bad id cannot cancel a legitimate batch. Requires EnableRpcGuards.");
            MaxZdoDestroysPerPacket = BindServerConfig("Network Integrity", "MaxZdoDestroysPerPacket", 512, "Largest number of objects one client may ask the server to delete in a single DestroyZDO message. Ids beyond this are discarded and the sender is reported. Vanilla batches a client's destroys per frame, so legitimate packets are small even when a large structure collapses; the default leaves generous headroom. Requires GuardZdoDestruction.", advanced: true, valmin: 8, valmax: 100000);
            ZdoDestroyProximityMetres = BindServerConfig("Network Integrity", "ZdoDestroyProximityMetres", 96f, "How far from a client's last known position an object may be for that client to be allowed to destroy it, when the server does not have the client recorded as its owner. Ownership is the primary test and covers the normal case; this is the tolerance for the window where a client has taken ownership of something and the server has not caught up. A zone is 64 metres, so the default is a little over one zone. Requires GuardZdoDestruction.", advanced: true, valmin: 16f, valmax: 10000f);
            GuardDamageRpc = BindServerConfig("Network Integrity", "GuardDamageRpc", true, "Drop relayed damage messages carrying impossible values. Damage in Valheim is applied by the victim's own client from numbers the ATTACKER's client wrote, and nothing checks them, so a modified client can send any figure it likes at any player or creature. This rejects NaN, infinity, negative components and totals above MaxAllowedHitDamage. It is a sanity bound, not a damage model: it stops one-shot kills and health-bar corruption, it does not try to work out whether a plausible hit was earned. Requires EnableRpcGuards.");
            MaxAllowedHitDamage = BindServerConfig("Network Integrity", "MaxAllowedHitDamage", 5000f, "Largest total damage a single relayed hit may carry before it is dropped as impossible. The total is the sum of every damage component in the hit, before the receiver applies resistances and multipliers. Vanilla's heaviest hits land in the low hundreds, so the default is roughly an order of magnitude of headroom for modded weapons, damage-multiplier mods and world modifiers. Raise it if a mod on your server legitimately deals more; lower it only after watching the log. Requires GuardDamageRpc.", advanced: true, valmin: 100f, valmax: 1000000f);
            GuardPvpDamage = BindServerConfig("Network Integrity", "GuardPvpDamage", false, "Refuse player-to-player damage on the server when the victim has PvP switched off. Vanilla already refuses this on the victim's own client, but the check is skipped whenever the attacker sets the hit's ignore-PvP flag - and that flag is written by the attacker, so a modified client sets it and kills anybody it likes. This re-runs the decision on the server, where the flag carries no weight. Self-damage is always allowed: a player standing in their own area effect is the one case vanilla sets that flag for legitimately. Off by default even with the other guards on, because a few area-effect prefabs set the flag on purpose and a PvP arena mod may rely on it - watch the log before switching it on. Requires GuardDamageRpc.");
            GuardGlobalKeys = BindServerConfig("Network Integrity", "GuardGlobalKeys", false, "Restrict which global keys a client may set. Global keys hold world progression and the world modifiers - which bosses are dead, whether building costs resources, the damage rates - and any client can set or clear any of them with one message. The allowed set is worked out from the loaded prefabs, so every boss, offering bowl, trader and vegvisir that legitimately sets a key is covered automatically, modded content included; AllowedClientGlobalKeys adds anything the scan cannot see. Off by default even when EnableRpcGuards is on, because a key wrongly refused here silently stops progression registering - turn it on deliberately, watch the log for refusals, and add what you find to the allowlist. Keys the server sets itself are never filtered, and nothing is filtered until the prefab scan has succeeded.");
            BlockClientGlobalKeyRemoval = BindServerConfig("Network Integrity", "BlockClientGlobalKeyRemoval", true, "Refuse global key REMOVAL from non-admin clients outright, rather than checking it against the allowlist. Nothing in vanilla gameplay removes a global key - only console commands and world setup do - so a client asking to remove one is asking to erase progression or switch off a world modifier. Honours RpcGuardExemptAdmins. Requires GuardGlobalKeys.");
            AllowedClientGlobalKeys = BindServerConfig("Network Integrity", "AllowedClientGlobalKeys", "", "Comma-separated list of extra global keys clients may set, on top of the ones worked out from the loaded prefabs. Matched on the key name only, so an entry covers the valued form too ('activeBosses' also permits 'activeBosses 2'). Empty by default. Use this when the log shows a legitimate key being refused - a mod that sets a key from code rather than from a prefab field is the usual reason. Requires GuardGlobalKeys.");
            RpcGuardExemptAdmins = BindServerConfig("Network Integrity", "RpcGuardExemptAdmins", true, "Whether anyone on the server's adminlist is exempt from the RPC guards. On by default, matching StructureValidationExemptAdmins: admins legitimately teleport players with 'recall', set keys, and clean up objects they do not own, and none of that should require noticing this feature exists. The chat name binding is NOT covered by this exemption - it applies to everyone, because an admin has no reason to speak under another player's name. Turn this off to hold admins to the same rules as everyone else.");
            ReportClientContradictions = BindServerConfig("Network Integrity", "ReportClientContradictions", false, "Correlate what a client declared at join with what the guards later catch it doing. Every player who gets past the join gate is running only mods this server approved, so when a server-authoritative guard refuses something they sent, an approved mod set produced traffic the server does not sanction - and the guard is the half of that which cannot be forged. The report names the player, the guard and the mod list they claimed at join, which is what makes it actionable. It deliberately does NOT decide who lied: a guard trip cannot tell a client that lied about its mods apart from an approved mod that legitimately sends this, and nothing can work that out in general. Where the inference IS tight - the client declared only mods the server itself requires, and the server offers no optional mods - the report says so explicitly. Requires EnableRpcGuards for the RPC guards, and EnableStructureValidation for the structure half.");
            ContradictionThreshold = BindServerConfig("Network Integrity", "ContradictionThreshold", 2, "How many DISTINCT guards one player must trip in a session before ContradictionAction applies. Distinct rather than total on purpose: one player hitting the same guard four hundred times is a single behaviour the guard already stopped, while tripping the teleport, global-key and object-destroy guards once each is a toolkit. Every trip is reported regardless of this number; it only gates the action.", advanced: true, valmin: 1, valmax: 20);
            ContradictionAction = BindServerConfig("Network Integrity", "ContradictionAction", "Log", "What happens to a player who reaches ContradictionThreshold. Separate from RpcGuardAction on purpose, so a server can leave the individual guards on Log - blocking the packet and saying so - while still treating the pattern of several different guards from one player as something to act on. Applied at most once per connection.", new AcceptableValueList<string>("Log", "Kick", "Ban"));
            RpcGuardAction = BindServerConfig("Network Integrity", "RpcGuardAction", "Log", "What happens to a player whose packet a guard refused. The packet is always dropped or corrected and always written to the server log regardless of this setting; this only decides whether the player is also kicked or banned. Defaults to Log so a server can watch the guards for a while before letting them remove anybody - do that first, because a mod doing something unusual shows up here as a refusal.", new AcceptableValueList<string>("Log", "Kick", "Ban"));

            EnableAuditLog = BindServerConfig("Audit", "EnableAuditLog", true, "Master switch for the player activity audit, and the only switch it has: the audit records all three of the things below, or it records nothing. When enabled, the server writes three things to BepInEx/config/ValheimEnforcer/Audit, one file per day, deleted once they pass AuditRetentionDays. (1) Items gained and lost, read from the character delta stream the server already receives, so it costs nothing extra - but it inherits that stream's shape: the client coalesces changes over a couple of seconds before sending them, so a timestamp is when the server heard about it rather than when it happened, and it is a NET diff, so picking something up and dropping it again within one window produces no entry at all. It is also the one part that is client-reported. (2) What players take out of and put into chests, ships, carts and graves, read from the container's own world data rather than from any message the client sends, so it catches every route in - taking, storing, Take All, and a modified client writing the item list directly - and cannot be avoided by a client that stays quiet. Items are matched by prefab and quality with stacks summed, so tidying a chest is not reported as a flurry of takes. Graves are containers too, so this also records somebody looting a grave that is not theirs. (3) How much damage each player is dealing, and the spikes. The figure is the PRE-MITIGATION damage the attacking client claimed, before the victim applies armour, resistances and difficulty scaling - the right number for spotting the impossible, the wrong number for comparing builds. Only hits from a player's own character count; damage from their tamed creatures does not. When GuardDamageRpc is also on the damage message is read once and used for both, so running the two together is cheaper than it looks. This records and reports only - it never kicks, bans, confiscates or blocks anything, and it is not a detector. ON by default, which means a server that installs this mod and changes nothing is keeping a log of what its players do on disk. That is the point of the feature and it is why the default is on - an audit you have to have known to switch on beforehand is no use on the day you need it - but it is a decision to make deliberately, and to tell your players about if that is how you run your server. Set this to false to record nothing. Turning it OFF takes effect immediately; turning it back ON takes a server restart, because the container half has to watch every object a client replicates and its hook is only installed at startup.");
            AuditRetentionDays = BindServerConfig("Audit", "AuditRetentionDays", 7, "How many days of history to keep. Older day-files are deleted, one file per pass every five minutes, so a server that has been offline for a month drains its backlog gradually instead of stalling on a single large sweep. Anything an admin has downloaded with enforcer-audit-download is their own copy and is not covered by this.", false, 1, 90);
            AuditFlushIntervalSeconds = BindServerConfig("Audit", "AuditFlushIntervalSeconds", 30, "Seconds between writes of buffered events to disk. Writing happens on a background thread, so this trades how much is held in memory against how much a hard crash could lose - a clean shutdown always flushes first, and a report always reads the buffer as well as the files, so this never affects what a command shows you.", true, 5, 600);
            AuditDamageWindowSeconds = BindServerConfig("Audit", "AuditDamageWindowSeconds", 60, "Length of the rolling window enforcer-audit-damage reports over, in seconds. The default of one minute is long enough to show a sustained rate and short enough that a fight from ten minutes ago is not still in the numbers. Requires EnableAuditLog.", false, 10, 600);
            AuditHighDamageThreshold = BindServerConfig("Audit", "AuditHighDamageThreshold", 200f, "Damage on a SINGLE hit above which an entry is written to the audit log. Deliberately far below MaxAllowedHitDamage: that setting drops packets carrying impossible numbers, while this one only notes hits that are merely suspicious, which is the range a careful cheater actually operates in. Expect legitimate late-game hits to reach this; it is a thing to look at, not an accusation. Requires EnableAuditLog.", true, 1f, 1000000f);
            AuditHighDamagePerWindow = BindServerConfig("Audit", "AuditHighDamagePerWindow", 5000f, "Total damage across one window above which an entry is written, for the case that matters more than any single hit: a stream of individually plausible hits arriving far faster than a player can swing. At most one entry per player per window either way, so a long boss fight cannot flood the log. Requires EnableAuditLog.", true, 1f, 10000000f);
            AuditContainerTrackingLimit = BindServerConfig("Audit", "AuditContainerTrackingLimit", 5000, "How many containers the server remembers the previous contents of at once, so it can tell what changed. Beyond this the least recently touched are forgotten; a forgotten container is not lost from the audit, but the first change after it is forgotten re-establishes a baseline instead of being recorded. Raise it on a server with a very large number of chests in active use. Requires EnableAuditLog.", true, 64, 200000);
            AuditMaxDownloadDays = BindServerConfig("Audit", "AuditMaxDownloadDays", 7, "Largest number of days one enforcer-audit-download request may ask for. A request for more is clamped to this rather than refused, so an admin asking for everything gets everything there is.", true, 1, 90);
            // The writer is otherwise started from the ZNet.Start patch, which has already run by the time an
            // admin switches this on from a client. Without this hook the audit would record into a buffer
            // nothing ever flushed until the next server restart.
            EnableAuditLog.SettingChanged += (sender, eventArgs) => {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) { return; }
                if (EnableAuditLog.Value) {
                    modules.audit.AuditLog.Initialize();
                } else {
                    modules.audit.AuditLog.Shutdown();
                }
            };

            AuditExemptAdmins = BindServerConfig("Audit", "AuditExemptAdmins", false, "Whether anyone on the server's adminlist is left out of the audit. OFF by default, unlike every other admin exemption in this mod - and the difference is deliberate. The other exemptions exist because those features punish, and an admin using devcommands should not be kicked for it. This feature only records, so exempting admins buys nothing and puts a blind spot in exactly the accounts that can do the most damage and are the most worth impersonating. Turn it on only if you have a specific reason to stop recording your own staff.");

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
            DiscordNotifyItemOrigin = BindLocalConfig("Discord", "NotifyItemOrigin", true, "Post a message when a player gains equipment that has no crafter, or one no player here has ever reported. Inert unless DetectItemOrigins is on. One post per player per minute at most, however many items were involved - a cheat tool hands over a whole loadout at once, and a message per item would walk the webhook straight into Discord's rate limiter.");
            DiscordNotifyClientContradiction = BindLocalConfig("Discord", "NotifyClientContradiction", true, "Post to the moderation channel when a connected player trips a server-side guard, naming what they declared at join. Inert unless ReportClientContradictions is on. One post per player per guard - a repeat of the same guard is not posted again, so a player hammering one of them cannot walk the webhook into Discord's rate limiter.");
            DiscordNotifyStructureFlagged = BindLocalConfig("Discord", "NotifyStructureFlagged", true, "Post a message when structure validation catches a player placing something no legitimate client can place, naming the prefab and where it landed. Inert unless EnableStructureValidation is on. One post per player per minute at most, however many objects were involved - a cheat tool drops a whole village at once, and a message per piece would walk the webhook straight into Discord's rate limiter.");
        }

        // routine: set for the recurring background baseline write driven by CharacterDeltaTracker, which happens
        // often enough that logging every one at info level would drown the log. Notable saves (join, logout,
        // death, an incoming character from a client) leave it false and stay visible without debug logging.
        internal static void WritePlayerCharacterToSave(string id, DataObjects.Character character, bool routine = false) {
            // The id and the character name are both used as path segments below. On the server they arrive from
            // a client (the account id from the socket, the name from the peer-info package), so a
            // traversal-shaped one must never reach Path.Combine. Refuse rather than throw - a bad save is
            // dropped, the server keeps running.
            if (character == null || !modules.character.PeerIdentity.IsSafeToken(id) || !modules.character.PeerIdentity.IsSafeToken(character.Name)) {
                Logger.LogWarning($"Refusing to write a character save under an unsafe account id or name ('{id}' / '{character?.Name}').");
                return;
            }
            if (ValConfig.InternalStorageMode.Value) {
                if (routine) { Logger.LogDebug("Saving character with internal storage mode."); } else { Logger.LogInfo("Saving character with internal storage mode."); }
                InternalDataStore.SaveAccountCharacter(character);
            }
            // Double write the data so that if the storage mode is switched the data will still be present.
            Directory.CreateDirectory(Path.Combine(Paths.ConfigPath, ValheimEnforcer, CharacterFolder));
            var saveDir = Directory.CreateDirectory(Path.Combine(Paths.ConfigPath, ValheimEnforcer, CharacterFolder, id));
            string path = Path.Combine(saveDir.FullName, $"{character.Name}.yaml");
            if (routine) { Logger.LogDebug($"Writing to {path}"); } else { Logger.LogInfo($"Writing to {path}"); }
            // Serializing the whole character and writing it is synchronous and on the main thread on
            // every path except the server's disk-mode store, and a large save (the confiscated list can
            // run to hundreds of entries) makes that long enough to see. Timed so it shows up as itself
            // rather than as an unexplained hitch.
            StallWatch timer = StallWatch.Start("Character save (serialize + write)");
            try {
                File.WriteAllText(path, DataObjects.yamlserializer.Serialize(character));
            } catch (Exception e) {
                Logger.LogWarning($"Failed to write character data to disk at {path}: {e.Message}");
            } finally {
                timer.Stop();
            }
        }

        internal static DataObjects.Character LoadCharacterFromSave(string id, string name) {
            if (!modules.character.PeerIdentity.IsSafeToken(id) || !modules.character.PeerIdentity.IsSafeToken(name)) {
                Logger.LogWarning($"Refusing to load a character save for an unsafe account id or name ('{id}' / '{name}').");
                return null;
            }
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

        /// <summary>
        /// Creates any of these config files that is not already there, and watches all of them for edits.
        ///
        /// "Is it already there?" is File.Exists and nothing else. It used to be a case-sensitive ordinal
        /// comparison of full path strings against a Directory.GetFiles listing, which asks a case-insensitive
        /// file system a case-sensitive question: a file whose name on disk differed from the expected spelling
        /// by so much as a letter's case was reported absent, and the create callback - which opens a truncating
        /// writer - then emptied the very file the check had just failed to recognise. For Mods.yaml that meant
        /// an admin's mod lists were deleted at startup, silently, by the code meant to create the file for them.
        ///
        /// The same mismatch also registered a file twice with the watcher, once under each spelling, so every
        /// edit was parsed and applied twice.
        /// </summary>
        internal void LoadYamlConfigs(Dictionary<string, Action<string>> configFilesToFind) {
            ValConfig.GetSecondaryConfigDirectoryPath(); // called for its side effect: it creates the directory

            foreach (KeyValuePair<string, Action<string>> cfg in configFilesToFind) {
                if (File.Exists(cfg.Key)) {
                    Logger.LogDebug($"Found config: {cfg.Key}");
                } else {
                    cfg.Value(cfg.Key);
                }
                Logger.LogDebug($"Setting filewatcher for {Path.GetFileName(cfg.Key)}");
                SetupFileWatcher(cfg.Key);
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

        /// <summary>
        /// Writes a starter Mods.yaml the first time.
        ///
        /// The File.Exists guard is the one that matters most of the four. This method runs from the ValConfig
        /// constructor, long before ModManager has read anything, so GetDefaultConfig() here serializes a null
        /// ModSettings - an empty list under every heading. Reaching this with the file already present replaced
        /// an admin's whole mod policy with blanks, and the only survivor was requiredMods, because that one is
        /// rebuilt from the loaded plugins a moment later. That is exactly the "my optional and admin-only lists
        /// were cleared by a restart" report.
        /// </summary>
        private static void CreateModsFile(string filepath) {
            if (File.Exists(filepath)) {
                Logger.LogWarning($"Not recreating {Path.GetFileName(filepath)}: it is already there. Leaving it untouched.");
                return;
            }
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
            if (File.Exists(filepath)) { return; } // never overwrite an admin's edited templates
            Logger.LogDebug("Notification templates file missing, recreating.");
            // The embedded copy verbatim - banner and templates together, exactly as it sits in the repo. Not
            // reserialized from the parsed object: the file is hand-authored JSON inside YAML, and a round trip
            // through the serializer would reflow it into something less pleasant to read than what was written.
            File.WriteAllText(filepath, NotificationTemplates.GetDefaultConfig());
        }

        /// <summary>
        /// Writes an example Loadouts.yaml the first time.
        ///
        /// Guarded with File.Exists even though the caller is only supposed to call this when the file is
        /// missing. LoadYamlConfigs answers that question correctly now, but every one of these create methods
        /// opens a truncating writer, so the cost of a caller ever being wrong is an admin's file emptied. The
        /// guard is one line; being the second place that has to be right is not worth the saving.
        /// </summary>
        private static void CreateLoadoutsFile(string filepath) {
            if (File.Exists(filepath)) { return; }
            Logger.LogDebug("Loadouts file missing, creating an example.");
            using (StreamWriter writer = new StreamWriter(filepath)) {
                writer.Write(@"#################################################
# Starter loadouts
#
# A kit handed to a character joining this server for the FIRST time. Set
# EnableStarterLoadouts to true and point DefaultStarterLoadout at one of the
# names below.
#
# The kit is granted AFTER the new-character rules have stripped whatever the
# character arrived carrying, so nothing here needs listing in
# NewCharacterStartingItems as well.
#
# items       prefabName is the name in the game's object database, the same
#             spelling NewCharacterStartingItems takes. quality is the upgrade
#             level (1 = unupgraded). equipped is ignored for anything that
#             cannot be worn or held.
# skills      only ever RAISES a level, never lowers one.
# known*      only used when SyncKnownItems is on, which is what gives the
#             server somewhere to record them. Materials are usually what you
#             want: the game works most recipes out from the materials and
#             crafting stations a character knows.
# spawn       only used when SyncSpawnPoint is on. Leave haveSpawnPoint false
#             to use the world's own start location.
#
# This file is re-read while the server is running.
#################################################
loadouts: {}

# Remove the {} above and uncomment this to start from an example:
#
#loadouts:
#  starter:
#    description: A hood, a cloak and something to eat
#    items:
#      - prefabName: HelmetLeather
#        quality: 1
#        equipped: true
#      - prefabName: CapeDeerHide
#        quality: 1
#        equipped: true
#      - prefabName: CookedMeat
#        stack: 5
#    skills:
#      Run: 10
#      Swim: 10
#    knownMaterials:
#      - $item_wood
#      - $item_stone
#    haveSpawnPoint: false
");
            }
        }

        private static void CreateKnownCheatersFile(string filepath) {
            if (File.Exists(filepath)) { return; } // never overwrite a ban list that is already there
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
            // The account, not the endpoint. On a PlayFab (crossplay) socket GetEndPointString is
            // "playfab/<entity id>", which matches no save the client ever filed.
            string id = modules.character.PeerIdentity.AccountFor(peer);
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
            // This peer connected WITH a stored character: arm the returning-character reconciliation, so the
            // first full save it uploads this session is validated against that stored character server-side
            // rather than trusted. Keyed by peer uid from the server's own lookup, exactly like the no-save case.
            modules.character.FirstSaveEnforcement.MarkHasSaveOnConnect(peer, saveId, saveName);

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

            // Disk mode. The file is the authority: the async store holds parsed objects for the characters
            // being played and no serialized copy, so a login reads the file. The one way the file can trail
            // the store is a change the worker has applied but not yet written - a save or delta inside the
            // last second or so - and a login that close behind a save is a fast rejoin, whose previous
            // session's last save is exactly what must be sent. So wait for that write, bounded: a burst of
            // other players' saves must not hold a connection handshake for long.
            var charFile = Path.Combine(Paths.ConfigPath, ValheimEnforcer, CharacterFolder, $"{saveId}");
            string fullpath = Path.Combine(charFile, $"{saveName}.yaml");
            if (modules.character.CharacterStore.HasUnwrittenChanges(saveId, saveName)) {
                StallWatch wait = StallWatch.Start("Character login (waiting for a pending save write)");
                try {
                    if (!modules.character.CharacterStore.Flush(LoginFlushBound, pollMs: 5)) {
                        Logger.LogWarning($"A save was still being written when {saveName} ({saveId}) connected; the character sent to them may be one update behind.");
                    }
                } finally {
                    wait.Stop();
                }
            }

            if (!File.Exists(fullpath)) {
                // TryResolveSave said the save was there, so this is a race with a delete rather than a new
                // character. Do not arm first-save enforcement on it.
                Logger.LogWarning($"path: {fullpath} vanished between lookup and read, no character data will be sent.");
                return CharacterPayload("", CharPayloadNone);
            }
            // The whole file as one string is unavoidable here - it becomes the payload - but this runs once
            // per connect rather than on the save path, and the stripping below (see SendCharacterToClientAsZpackage)
            // costs a parse + re-serialize that is accepted for the same reason.
            string filecontents = File.ReadAllText(fullpath);
            DateTime diskMtime = File.GetLastWriteTimeUtc(fullpath);
            // Tell the store what is on disk, so a copy it parsed before an offline edit is re-read rather than
            // written back over the edit. Never replaces a copy with an unwritten change.
            modules.character.CharacterStore.Seed(saveId, saveName, diskMtime);
            return CharacterPayload(StripServerOwnedFromYaml(filecontents), CharPayloadCharacter);
        }

        // Coarse DoS guards on inbound client payloads. None of these are tight - they exist so a single
        // packet cannot exhaust memory or stall the main thread, not to constrain a legitimate one. A full
        // character save with a large modded inventory is generously bounded; a delta is small by design.
        // How long a connect may wait for the character store to finish a write already in flight, so the
        // save sent to a fast rejoin is the one their previous session ended on. See SendSavedCharacter.
        internal static readonly TimeSpan LoginFlushBound = TimeSpan.FromMilliseconds(750);

        internal const int MaxCharacterPayloadBytes = 8 * 1024 * 1024;
        // A minimap blob is 8.4 MB of mostly-identical bytes before compression and vanilla always stores it
        // compressed, so a real one is tens of kilobytes to a megabyte. 4 MB is far above anything an honest
        // client sends and far below what would hurt to receive.
        internal const int MaxMapPayloadBytes = 4 * 1024 * 1024;
        // A map request is an empty package; nothing legitimate in one is large.
        internal const int MaxMapRequestBytes = 1024;
        internal const int MaxDeltaPayloadBytes = 1 * 1024 * 1024;
        internal const int MaxCheatReportBytes = 64 * 1024;
        internal const int MaxModListBytes = 2 * 1024 * 1024;
        internal const int MaxCommandArgs = 32;
        // An audit request names a player and two dates; nothing legitimate in one is large. The download
        // ceiling doubles as the decompression bound on the receiving client.
        internal const int MaxAuditRequestBytes = 4 * 1024;
        internal const int MaxAuditDownloadBytes = 16 * 1024 * 1024;

        /// <summary>True when a received package is within a size limit; logs and returns false when it is not.</summary>
        internal static bool WithinLimit(ZPackage package, int limitBytes, long sender, string what) {
            int size = package?.Size() ?? 0;
            if (size > limitBytes) {
                Logger.LogWarning($"Dropping an oversize {what} from {sender}: {size} bytes exceeds the {limitBytes} byte limit.");
                return false;
            }
            return true;
        }

        // What a VENFORCE_MAP payload means, on the same reasoning - and with the same tag-goes-last layout -
        // as the character payload above: "the server holds no map for you" has to be expressible, or a client
        // cannot tell it apart from an answer that has not arrived and will keep asking.
        internal const string MapPayloadMap = "MAP";
        internal const string MapPayloadNone = "NONE";

        internal static ZPackage MapPayload(byte[] blob, string hash, string kind) {
            ZPackage package = new ZPackage();
            package.Write(blob ?? new byte[0]);
            package.Write(hash ?? "");
            package.Write(kind);
            return package;
        }

        // What a VENFORCE_RECOVERY payload is asking for. Tag last, same as every other payload here.
        internal const string RecoveryPayloadBlob = "BLOB";   // server -> client: hold this
        internal const string RecoveryPayloadWant = "WANT";   // server -> client: hand back what you hold

        internal static ZPackage RecoveryPayload(byte[] blob, string kind) {
            ZPackage package = new ZPackage();
            package.Write(blob ?? new byte[0]);
            package.Write(kind);
            return package;
        }

        internal static ZPackage RecoveryOfferPayload(byte[] blob) {
            ZPackage package = new ZPackage();
            package.Write(blob ?? new byte[0]);
            return package;
        }

        /// <summary>
        /// The server either handing this client a sealed snapshot to keep, or asking for the one it holds.
        ///
        /// Neither branch is a decision this client is making. It cannot read what it stores, and handing one
        /// back is not the same as it being used - the server verifies it and compares it against what it
        /// holds before anything happens.
        /// </summary>
        public static IEnumerator OnClientReceiveRecovery(long sender, ZPackage package) {
            if (!FromServer(sender)) {
                Logger.LogWarning($"Ignoring a recovery message from {sender}, which is not the server.");
                yield break;
            }
            if (!WithinLimit(package, modules.recovery.RecoveryManager.MaxRecoveryPayloadBytes, sender, "recovery snapshot")) { yield break; }

            byte[] blob;
            string kind;
            try {
                blob = package.ReadByteArray();
                kind = package.GetPos() < package.Size() ? package.ReadString() : RecoveryPayloadBlob;
            } catch (Exception e) {
                Logger.LogWarning($"Could not read a recovery message from the server: {e.Message}");
                yield break;
            }

            if (kind == RecoveryPayloadWant) {
                modules.recovery.RecoveryManager.Offer();
                yield break;
            }
            modules.recovery.RecoveryManager.Store(blob);
            yield break;
        }

        /// <summary>A client handing a snapshot back. Size, then identity, then content - the last two in
        /// RecoveryManager.OnOffer, which is also where the decision not to act on an unverified header is
        /// made explicit.</summary>
        public static IEnumerator OnServerReceiveRecoveryOffer(long sender, ZPackage package) {
            if (ZNet.instance == null || ZNet.instance.IsServer() == false) { yield break; }
            if (!WithinLimit(package, modules.recovery.RecoveryManager.MaxRecoveryPayloadBytes, sender, "recovery snapshot")) { yield break; }
            byte[] blob;
            try {
                blob = package.ReadByteArray();
            } catch (Exception e) {
                Logger.LogWarning($"Could not read a recovery snapshot offered by {sender}: {e.Message}");
                yield break;
            }
            modules.recovery.RecoveryManager.OnOffer(sender, blob);
            yield break;
        }

        /// <summary>
        /// A client uploading its map. Size, then identity, then content - and the identity is the server's
        /// own, never the payload's: the blob is written to a path built from the account id and character
        /// name, so a client that could name those could write a file wherever it liked.
        /// </summary>
        public static IEnumerator OnServerReceiveMap(long sender, ZPackage package) {
            if (ZNet.instance == null || ZNet.instance.IsServer() == false) { yield break; }
            if (!WithinLimit(package, MaxMapPayloadBytes, sender, "map")) { yield break; }

            byte[] blob;
            string hash;
            try {
                blob = package.ReadByteArray();
                hash = package.GetPos() < package.Size() ? package.ReadString() : null;
            } catch (Exception e) {
                Logger.LogWarning($"Could not read a map upload from {sender}: {e.Message}");
                yield break;
            }
            if (blob == null || blob.Length == 0) { yield break; }

            // Off means off: a client running an older or modified build may still send these, and a server
            // whose admin has not turned the feature on must not start writing files because of it.
            if (modules.character.MapSync.Enabled == false) {
                Logger.LogDebug($"Ignoring a map upload from {sender}: map exploration is not being synced.");
                yield break;
            }
            if (InternalStorageMode.Value) {
                Logger.LogDebug($"Ignoring a map upload from {sender}: maps are not stored in InternalStorageMode.");
                yield break;
            }

            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            string accountId = modules.character.PeerIdentity.AccountFor(peer);
            string characterName = peer?.m_playerName;
            if (string.IsNullOrEmpty(accountId) || string.IsNullOrEmpty(characterName)) {
                Logger.LogWarning($"Refusing a map upload from {sender}: the server could not resolve who they are.");
                yield break;
            }
            if (!modules.character.PeerIdentity.IsSafeToken(accountId) || !modules.character.PeerIdentity.IsSafeToken(characterName)) {
                Logger.LogWarning($"Refusing a map upload from {characterName} ({accountId}): the account id or character name is not a safe file name.");
                yield break;
            }

            // The .map has to land in the same folder as the .yaml, under the same spelling, or the store
            // finds no character to record its hash against and drops it. The two do not always agree on
            // their own: the save is filed under the account id the CLIENT put in its payload, which has been
            // written as both "Steam_7656..." and the bare "7656..." over this mod's life, while the id
            // resolved from the socket here is whichever form the platform hands us. On a case-sensitive
            // filesystem - which is what most servers run on - that is two different directories.
            //
            // A character with no save yet resolves to nothing, and the peer's own spelling is used instead;
            // the store then drops the upload for having no save to record it against, and the client sends
            // it again on its next cadence once the save exists.
            if (modules.character.CharacterSaves.TryResolveSave(accountId, characterName,
                                                                out string resolvedAccount, out string resolvedName, out _)) {
                accountId = resolvedAccount;
                characterName = resolvedName;
            }

            modules.character.CharacterStore.SubmitMap(accountId, characterName, blob, hash, sender);
            yield break;
        }

        /// <summary>The client adopting the server's copy.</summary>
        public static IEnumerator OnClientReceiveMap(long sender, ZPackage package) {
            if (!FromServer(sender)) {
                Logger.LogWarning($"Ignoring a map payload from {sender}, which is not the server.");
                yield break;
            }
            if (!WithinLimit(package, MaxMapPayloadBytes, sender, "map")) { yield break; }

            byte[] blob;
            string kind;
            try {
                blob = package.ReadByteArray();
                _ = package.GetPos() < package.Size() ? package.ReadString() : null; // hash, server-side bookkeeping
                kind = package.GetPos() < package.Size() ? package.ReadString() : MapPayloadMap;
            } catch (Exception e) {
                Logger.LogWarning($"Could not read the map the server sent: {e.Message}");
                yield break;
            }

            // Answered either way, before the branch: a client that stayed in "waiting for the server" would
            // never upload its own map to a server that holds none, which is exactly how the server acquires
            // one in the first place.
            modules.character.MapSync.NoteServerAnswered();
            if (kind == MapPayloadNone || blob == null || blob.Length == 0) {
                Logger.LogDebug("The server holds no map for this character; keeping the local one.");
                yield break;
            }
            modules.character.MapSync.ApplyFromServer(blob);
            yield break;
        }

        /// <summary>A client asking for its stored map, which is how a join gets one.</summary>
        public static IEnumerator OnServerReceiveMapRequest(long sender, ZPackage package) {
            if (ZNet.instance == null || ZNet.instance.IsServer() == false) { yield break; }
            // A request carries nothing but its own existence - it names no character, because the only
            // character a peer may ask about is its own and the server already knows which that is.
            if (!WithinLimit(package, MaxMapRequestBytes, sender, "map request")) { yield break; }
            if (modules.character.MapSync.Enabled == false || InternalStorageMode.Value) { yield break; }

            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            string accountId = modules.character.PeerIdentity.AccountFor(peer);
            string characterName = peer?.m_playerName;
            if (string.IsNullOrEmpty(accountId) || string.IsNullOrEmpty(characterName)) { yield break; }
            if (!modules.character.PeerIdentity.IsSafeToken(accountId) || !modules.character.PeerIdentity.IsSafeToken(characterName)) { yield break; }

            // The store may still be holding an unwritten map for this character from their previous session.
            // One short wait here beats sending a stale map that the client then adopts over a newer one.
            if (modules.character.CharacterStore.HasUnwrittenChanges(accountId, characterName)) {
                modules.character.CharacterStore.Flush(LoginFlushBound);
            }

            // Resolve the spelling actually on disk before composing a path: on a case-sensitive filesystem
            // the id a peer connects under does not always match the folder their save was written to.
            if (!modules.character.CharacterSaves.TryResolveSave(accountId, characterName, out string resolvedAccount, out string resolvedName, out _)) {
                MapSyncRPC.SendPackage(sender, MapPayload(null, null, MapPayloadNone));
                yield break;
            }

            byte[] blob = modules.character.MapSync.Load(resolvedAccount, resolvedName);
            if (blob == null || blob.Length == 0) {
                MapSyncRPC.SendPackage(sender, MapPayload(null, null, MapPayloadNone));
                yield break;
            }
            Logger.LogDebug($"Sending {characterName} the map this server holds for them ({blob.Length} bytes).");
            MapSyncRPC.SendPackage(sender, MapPayload(blob, null, MapPayloadMap));
            yield break;
        }

        public static IEnumerator OnServerRecieveCharacter(long sender, ZPackage package) {
            if (!WithinLimit(package, MaxCharacterPayloadBytes, sender, "character save")) { yield break; }
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

            // The returning-character counterpart: when this peer connected WITH a stored character and
            // ServerSideJoinEnforcement is on, the first full save of the session is re-validated against that
            // stored character. Mutually exclusive with newCharacterPolicy - a peer either had a save or did not.
            modules.character.ReturningCharacterRules.Policy returningPolicy = null;
            if (ValConfig.ServerSideJoinEnforcement != null && ValConfig.ServerSideJoinEnforcement.Value
                && modules.character.FirstSaveEnforcement.ShouldReconcileReturning(sender)) {
                modules.character.ReturningCharacterRules.Policy candidate = modules.character.ReturningCharacterRules.Current();
                if (candidate.AnyEnabled) { returningPolicy = candidate; }
            }

            // Who the server says this peer is. The payload names its own HostID and Name, but those are
            // written by the client: without an independent identity a client can file its save under another
            // account's character - overwriting that character, and skipping the first-save check at the same
            // time, because the check asks "does a save already exist?" about the name the payload supplied.
            ZNetPeer senderPeer = ZNet.instance?.GetPeer(sender);
            string senderAccountId = modules.character.PeerIdentity.AccountFor(senderPeer);
            string senderCharacterName = senderPeer?.m_playerName;

            if (ValConfig.InternalStorageMode.Value) {
                // Internal storage writes touch a registry ZDO and must stay on the main thread.
                try {
                    DataObjects.Character chara = DataObjects.yamldeserializer.Deserialize<DataObjects.Character>(yaml);
                    Logger.LogInfo($"Recieved Player data update for {sender} - {chara.Name}|{chara.HostID}");
                    if (!SaveBelongsToSender(chara, sender, senderAccountId, senderCharacterName)) { return; }
                    // Shed pass-through compat keys before this save is merged or written - also scrubs the
                    // stale copies saves written before pass-through handling still carry.
                    modules.compat.CompatCustomData.StripPassthroughKeys(chara.PlayerCustomData);
                    // The client's confiscated list is a report of what it confiscated this session, never a
                    // replacement for ours - see Character.MergeConfiscatedItems.
                    DataObjects.Character existing = InternalDataStore.GetAccountCharacter(chara.HostID, chara.Name);
                    List<PackedItem> reported = chara.ConfiscatedItems;
                    chara.ConfiscatedItems = existing?.ConfiscatedItems ?? new List<PackedItem>();
                    int appended = chara.MergeConfiscatedItems(reported);
                    if (appended > 0) {
                        Logger.LogInfo($"Recorded {appended} newly confiscated item(s) for {chara.Name}.");
                    }
                    // Skill reductions and pending restores are server-owned the same way - see CharacterStore.
                    List<SkillReduction> reportedReductions = chara.SkillReductions;
                    chara.SkillReductions = existing?.SkillReductions;
                    int reductions = chara.MergeSkillReductions(reportedReductions);
                    if (reductions > 0) {
                        Logger.LogInfo($"Recorded {reductions} skill reduction(s) reported by {chara.Name}.");
                    }
                    chara.PendingSkillRestores = existing?.PendingSkillRestores;
                    // Server-owned, exactly as in CharacterStore.Apply: the client is told these and must not
                    // be able to hand back a value of its own choosing for either.
                    chara.SaveSequence = (existing?.SaveSequence ?? 0L) + 1L;
                    if (chara.Progress != null) { chara.Progress.MapHash = existing?.Progress?.MapHash; }

                    // Both stores have to be empty before this counts as a first save. WritePlayerCharacterToSave
                    // deliberately double-writes (registry AND disk) so that switching storage modes does not
                    // lose data, which means a character can be absent from one and present in the other.
                    bool isFirstSave = existing == null && !modules.character.CharacterSaves.ExistsOnDisk(chara.HostID, chara.Name);
                    if (newCharacterPolicy != null && isFirstSave) {
                        NewCharacterRules.Result sanitized = NewCharacterRules.Apply(chara, newCharacterPolicy, record: true);
                        if (sanitized.Changed) {
                            Logger.LogWarning($"First save for {chara.Name} ({chara.HostID}) held to the new-character rules: {sanitized.Describe()}");
                            WritePlayerCharacterToSave(chara.HostID, chara);
                            // Already on the main thread here, so no queue hop is needed.
                            SendSanitizedCharacterToClient(sender, chara);
                            return;
                        }
                    }
                    // Returning-character reconciliation, first save of the session only.
                    else if (returningPolicy != null && existing != null) {
                        modules.character.ReturningCharacterRules.Result reconciled = modules.character.ReturningCharacterRules.Apply(chara, existing, returningPolicy);
                        modules.character.FirstSaveEnforcement.ClearReturning(sender);
                        if (reconciled.Changed) {
                            Logger.LogWarning($"Returning save for {chara.Name} ({chara.HostID}) reconciled to the stored character: {reconciled.Describe()}");
                            modules.character.SkillClamp.Apply(chara.SkillLevels, chara.Name);
                            chara.ConsumePendingSkillRestores();
                            WritePlayerCharacterToSave(chara.HostID, chara);
                            SendSanitizedCharacterToClient(sender, chara);
                            return;
                        }
                    }
                    modules.character.SkillClamp.Apply(chara.SkillLevels, chara.Name);
                    chara.ConsumePendingSkillRestores();
                    WritePlayerCharacterToSave(chara.HostID, chara);
                } catch (Exception e) {
                    Logger.LogWarning($"Failed to deserialize character data from {sender}: {e.Message}");
                }
                return;
            }

            // Disk mode: hand the raw YAML to the background store. All parsing, serialization and disk I/O
            // happen off the main thread, so a burst of saves (e.g. every client saving at once on a
            // "save player profiles" broadcast) cannot stall the server and time peers out.
            modules.character.CharacterStore.SubmitFullSave(yaml, sender, senderAccountId, senderCharacterName, newCharacterPolicy, returningPolicy);
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
            // Fail CLOSED when the connection carries no identity. This used to accept the save "unchecked",
            // which is exactly the pre-handshake state VE_FINAL_CHAR_SAVE could smuggle a save in during. On
            // every supported backend a ready peer has both an account id and a name, so an empty one is a
            // reason to refuse, not to trust.
            if (string.IsNullOrEmpty(senderAccountId) || string.IsNullOrEmpty(senderCharacterName)) {
                Logger.LogWarning($"Refusing a character save from sender {sender}: the server could not resolve who they are.");
                return false;
            }
            // The name and id are about to be used as filesystem path segments; a traversal-shaped one is
            // refused before it reaches Path.Combine.
            if (!modules.character.PeerIdentity.IsSafeToken(chara.HostID) || !modules.character.PeerIdentity.IsSafeToken(chara.Name)) {
                Logger.LogWarning($"Refusing a character save from {senderCharacterName} ({senderAccountId}): the account id or character name is not a safe file name.");
                return false;
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
            List<SkillReduction> heldReductions = chara.SkillReductions;
            ZPackage payload;
            try {
                chara.ConfiscatedItems = null;
                chara.SkillReductions = null;
                payload = CharacterPayload(DataObjects.yamlserializer.Serialize(chara), CharPayloadSanitized);
            } finally {
                // The caller's object is server-side authoritative state; never leave it stripped.
                chara.ConfiscatedItems = held;
                chara.SkillReductions = heldReductions;
            }
            SendSanitizedPayload(sender, chara.Name, payload);
        }

        /// <summary>Counterpart for the async store, whose worker has already serialized the character with the
        /// server-owned lists withheld (CharacterStore.SanitizedPush). Nothing is read back out of the store:
        /// by the time the main thread drains the push the entry may have moved on, or been dropped.</summary>
        internal static void SendSanitizedYamlToClient(long sender, string name, string strippedYaml) {
            if (string.IsNullOrEmpty(strippedYaml)) { return; }
            SendSanitizedPayload(sender, name, CharacterPayload(strippedYaml, CharPayloadSanitized));
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

        /// <summary>
        /// Whether a routed message a CLIENT received genuinely came from the server it is connected to.
        ///
        /// Every OnClientReceive* handler in this mod acts on the sender's behalf - adopting a character,
        /// planting items, clearing confiscations, uploading a full save on request. The server relays a
        /// routed RPC to whatever target the sender named (ZRoutedRpc.RouteRPC), so without this check one
        /// client can address another and drive these handlers directly. `sender` is trustworthy here because
        /// the server-side RoutedRpcGuard corrects it before the packet leaves the server; a client cannot
        /// forge the server's own uid past that.
        /// </summary>
        private static bool FromServer(long sender) {
            if (ZNet.instance == null) { return false; }
            if (ZNet.instance.IsServer()) { return true; } // integrated host: a local send is from us
            ZNetPeer server = ZNet.instance.GetServerPeer();
            return server != null && sender == server.m_uid;
        }

        // Client handler: an admin cleared confiscated entries for this player. The character save on the
        // server is already authoritative; this drops the same entries from the copy this session is
        // tracking, which is what would otherwise re-append them on the next full push.
        public static IEnumerator OnClientReceiveClearConfiscated(long sender, ZPackage package) {
            if (!FromServer(sender)) { Logger.LogWarning($"Ignoring a clear-confiscated message not from the server (sender {sender})."); yield break; }
            string filter = package.ReadString();
            int cleared = modules.character.ConfiscatedItems.ClearTrackedLocally(
                modules.character.ConfiscatedItems.ParseFilter(filter));
            Logger.LogDebug($"Cleared {cleared} tracked confiscated item(s) locally for filter '{filter}'.");
            yield break;
        }

        // Client handler: an admin restored or cleared skill reductions for this player. The save on the server
        // is already authoritative; this raises the live skills the admin restored, drops the matching records
        // from the copy this session is tracking (which would otherwise re-report them on the next full push),
        // and pushes a full save so the server sees the restored levels and can confirm them.
        public static IEnumerator OnClientReceiveSkillRestore(long sender, ZPackage package) {
            if (!FromServer(sender)) { Logger.LogWarning($"Ignoring a skill restore not from the server (sender {sender})."); yield break; }
            string filter = package.ReadString();
            Dictionary<Skills.SkillType, float> levels = ReadSkillLevels(package);

            int dropped = modules.character.SkillReductions.ClearTrackedLocally(modules.character.SkillReductions.ParseFilter(filter));
            Logger.LogDebug($"Dropped {dropped} tracked skill reduction record(s) locally for filter '{filter}'.");

            int raised = modules.character.SkillReductions.ApplyToLivePlayer(Player.m_localPlayer, levels, "Admin restore");
            if (raised > 0 && Player.m_localPlayer != null) {
                CharacterManager.SavePlayerCharacter(Player.m_localPlayer);
            }
            yield break;
        }

        // Split out because an iterator cannot yield inside a try/catch, and this read has to be in one: the
        // payload arrives over the network and a malformed one must not throw out of the Jotunn coroutine.
        private static Dictionary<Skills.SkillType, float> ReadSkillLevels(ZPackage package) {
            try {
                string yaml = package.GetPos() < package.Size() ? package.ReadString() : null;
                if (string.IsNullOrWhiteSpace(yaml)) { return null; }
                return DataObjects.yamldeserializer.Deserialize<Dictionary<Skills.SkillType, float>>(yaml);
            } catch (Exception e) {
                Logger.LogWarning($"Could not read the skill levels in a restore from the server: {e.Message}");
                return null;
            }
        }

        public static IEnumerator OnClientReceiveCharacter(long sender, ZPackage package) {
            if (!FromServer(sender)) { Logger.LogWarning($"Ignoring a character payload not from the server (sender {sender})."); yield break; }
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
            if (!WithinLimit(package, MaxCheatReportBytes, sender, "cheat report")) { yield break; }
            string yaml = package.ReadString();
            DataObjects.CheatSummaryReport summary;
            try {
                summary = DataObjects.yamldeserializer.Deserialize<DataObjects.CheatSummaryReport>(yaml);
            } catch (Exception e) {
                Logger.LogWarning($"Failed to deserialize cheat report from {sender}: {e.Message}");
                yield break;
            }

            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            if (peer == null) {
                Logger.LogWarning($"Received a cheat report from {sender} but could not find the corresponding peer. No action will be taken.");
                yield break;
            }
            // The report's PlayerName is client-supplied and used only for display; log/notify with the name the
            // SERVER knows for this connection instead, and bound anything free-text the report carries so a
            // hostile one cannot flood the log or a Discord embed.
            string playerName = string.IsNullOrEmpty(peer.m_playerName) ? summary.PlayerName : peer.m_playerName;
            CapDetections(summary);

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

        // Bounds a client-supplied cheat report so a hostile one cannot flood the log or a Discord embed: caps
        // the number of detections and truncates each free-text field. The Tool label is still resolved against
        // the server's own catalog for any enforcement decision, so truncating it here only affects display.
        private const int MaxCheatDetections = 32;
        private const int MaxCheatFieldLength = 256;
        private static void CapDetections(DataObjects.CheatSummaryReport summary) {
            if (summary?.DetectedTools == null) { return; }
            if (summary.DetectedTools.Count > MaxCheatDetections) {
                summary.DetectedTools = summary.DetectedTools.GetRange(0, MaxCheatDetections);
            }
            foreach (DataObjects.CheatToolDetection d in summary.DetectedTools) {
                if (d == null) { continue; }
                d.Tool = Truncate(d.Tool);
                d.Vector = Truncate(d.Vector);
                d.Detail = Truncate(d.Detail);
            }
        }

        private static string Truncate(string s) {
            if (string.IsNullOrEmpty(s) || s.Length <= MaxCheatFieldLength) { return s; }
            return s.Substring(0, MaxCheatFieldLength);
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
            // A handful of commands answer only about the caller's own standing and are open to anybody the
            // operator has not closed them off from - see TerminalManager.OpenToEveryone. Everything else is
            // admin-only, and this is the check that decides it: the client-side one is only for wording.
            if (SenderIsAdmin(sender) == false && TerminalManager.OpenToEveryone(command) == false) {
                Logger.LogWarning($"Rejecting '{command}' from non-admin peer {sender}.");
                // Answer rather than going quiet, so the sender sees a refusal instead of nothing at all.
                TerminalOutput refusal = TerminalOutput.Remote(sender);
                refusal.Error($"Only server admins can run {command}.", log: false);
                refusal.Flush();
                yield break;
            }

            int argCount = package.ReadInt();
            // Bound the count before allocating: even an admin (or a uid-spoofer, before RoutedRpcGuard is on)
            // must not be able to request an array of int.MaxValue strings. No enforcer command takes anywhere
            // near this many arguments.
            if (argCount < 0 || argCount > MaxCommandArgs) {
                Logger.LogWarning($"Rejecting '{command}' from {PeerHostId(sender)}: implausible argument count ({argCount}).");
                yield break;
            }
            string[] args = new string[argCount];
            for (int i = 0; i < argCount; i++) { args[i] = package.ReadString(); }

            Logger.LogInfo($"Running '{command}' for {PeerHostId(sender)}.");
            TerminalManager.ExecuteFromNetwork(command, args, TerminalOutput.Remote(sender), sender);
            yield break;
        }

        /// <summary>
        /// Client handler: a batch of output lines from a command this client asked the server to run.
        /// Severity travels as a byte and the colour is applied here, so the server's log never contains
        /// markup and each client honours its own EnableTerminalColors setting.
        /// </summary>
        public static IEnumerator OnClientReceiveCommandOutput(long sender, ZPackage package) {
            if (!FromServer(sender)) { Logger.LogWarning($"Ignoring command output not from the server (sender {sender})."); yield break; }
            int count = package.ReadInt();
            // The server batches at most TerminalOutput.BatchLines per packet; a huge count is a malformed or
            // hostile payload, so bound the loop rather than letting it drive an unbounded print into the console.
            if (count < 0 || count > MaxCommandOutputLines) {
                Logger.LogWarning($"Ignoring a command-output batch with an implausible line count ({count}).");
                yield break;
            }
            for (int i = 0; i < count; i++) {
                OutputLevel level = (OutputLevel)package.ReadByte();
                TerminalManager.PrintResponse(level, package.ReadString());
            }
            yield break;
        }

        private const int MaxCommandOutputLines = 256;

        /// <summary>
        /// Server handler: an admin is asking what audit history exists for a player, or for a copy of it.
        ///
        /// This is the one place in the mod where data leaves the server for an admin's own machine, so the
        /// gate order matters and is the same one the command relay uses: size, then identity, then content.
        /// Everything that validates the request body - the account, the character, the dates - lives in
        /// AuditTransfer.Serve, which composes its own filenames from parsed dates and never takes one from
        /// the wire.
        /// </summary>
        public static IEnumerator OnServerReceiveAuditRequest(long sender, ZPackage package) {
            if (ZNet.instance == null || ZNet.instance.IsServer() == false) { yield break; }
            if (!WithinLimit(package, MaxAuditRequestBytes, sender, "audit request")) { yield break; }

            if (SenderIsAdmin(sender) == false) {
                // Named and logged rather than silently dropped: somebody asking for another player's
                // history without being an admin is exactly the thing a moderator wants to know happened.
                Logger.LogWarning($"Rejecting an audit history request from non-admin peer {PeerHostId(sender)}.");
                yield break;
            }

            modules.audit.AuditTransfer.Serve(sender, package);
            yield break;
        }

        /// <summary>
        /// Client handler: the server's answer to this machine's audit request. Bounded on the way in and
        /// again when it is decompressed - see AuditTransfer.
        /// </summary>
        public static IEnumerator OnClientReceiveAuditData(long sender, ZPackage package) {
            if (!FromServer(sender)) { Logger.LogWarning($"Ignoring audit data not from the server (sender {sender})."); yield break; }
            if (!WithinLimit(package, MaxAuditDownloadBytes, sender, "audit history")) { yield break; }
            modules.audit.AuditTransfer.Receive(package);
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
            if (!FromServer(sender)) { Logger.LogWarning($"Ignoring a confiscated-item return not from the server (sender {sender})."); yield break; }
            List<DataObjects.PackedItem> items = DataObjects.yamldeserializer.Deserialize<List<DataObjects.PackedItem>>(package.ReadString());
            Logger.LogInfo($"Received {items.Count} confiscated item(s) returned from server.");
            foreach (DataObjects.PackedItem item in items) {
                Logger.LogInfo($"Adding returned confiscated item: {item.prefabName} x{item.m_stack}");
                item.AddToInventory(Player.m_localPlayer, false);
            }
            yield break;
        }

        internal static IEnumerator OnServerRecieveDeltaItemUpdate(long sender, ZPackage package) {
            if (!WithinLimit(package, MaxDeltaPayloadBytes, sender, "delta update")) { yield break; }
            string yaml = package.ReadString(); // must run on the main thread (consumes the ZPackage); cheap

            // Timed, because this is the most frequent piece of main-thread work the mod does on a server: one
            // per player per rate-limit window, and the payload carries the whole skill list and every active
            // status effect as well as the item deltas, all of it parsed here by YamlDotNet. The client's own
            // flush and the character save have been measured for a long time; the receiving half was the gap,
            // and it is the half whose cost rises with the player count.
            StallWatch timer = StallWatch.Start("Delta update (server receive)");
            try {
                HandleDeltaItemUpdate(sender, yaml);
            } finally {
                timer.Stop();
            }
            yield break;
        }

        /// <summary>
        /// Everything the delta handler does once the package has been read off the wire.
        ///
        /// Split out of the coroutine so it can be timed as a unit - an iterator cannot be wrapped in a
        /// try/finally around its own yields. Every step is synchronous; the coroutine above never suspends.
        /// </summary>
        private static void HandleDeltaItemUpdate(long sender, string yaml) {
            DeltaSummaryUpdate deltaUpdate;
            try {
                deltaUpdate = DataObjects.yamldeserializer.Deserialize<DeltaSummaryUpdate>(yaml);
            } catch (Exception e) {
                Logger.LogWarning($"Failed to deserialize delta update from {sender}: {e.Message}");
                return;
            }
            if (string.IsNullOrEmpty(deltaUpdate.Name) || string.IsNullOrEmpty(deltaUpdate.HostID)) {
                Logger.LogWarning($"Malformed delta update from {sender}: missing CharacterName or HostName.");
                return;
            }

            // A delta mutates the save named in its own payload. Bind that to the connection it arrived on -
            // the full-save path has always done this, the delta path historically did not, so any client
            // could edit any stored character. Identity is trustworthy because RoutedRpcGuard has already
            // corrected `sender`. Refuse (not silently drop) so the mismatch is visible in the log.
            if (!modules.character.PeerIdentity.TryResolve(sender, out string deltaAccount, out string deltaName)
                || !modules.character.PeerIdentity.Owns(deltaAccount, deltaName, deltaUpdate.HostID, deltaUpdate.Name)) {
                Logger.LogWarning($"Refusing a delta update from {sender}: it targets character '{deltaUpdate.Name}' ({deltaUpdate.HostID}), which is not the character that connection is playing.");
                return;
            }
            // Defence in depth: the name/id are used as path segments below. Owns already implies the sender's
            // own (safe) identity matches, but check the payload values directly before they reach the disk.
            if (!modules.character.PeerIdentity.IsSafeToken(deltaUpdate.HostID) || !modules.character.PeerIdentity.IsSafeToken(deltaUpdate.Name)) {
                Logger.LogWarning($"Refusing a delta update from {sender}: unsafe account id or character name.");
                return;
            }

            // Runs after the identity binding above, so the report names the character the connection is
            // actually playing rather than whatever the payload claimed, and before the delta is applied, so a
            // flagged item is reported whether or not the save that follows succeeds.
            modules.worldintegrity.ItemOriginValidator.InspectDelta(
                deltaUpdate, deltaUpdate.HostID, deltaUpdate.Name, SenderIsAdmin(sender));

            // Same placement, and for the same two reasons: after the identity binding above, so a recorded
            // event names the character this connection is really playing rather than whatever the payload
            // claimed; and before the merge below, so what a player gained is written down whether or not the
            // save that follows succeeds.
            modules.audit.ItemAudit.Record(sender, deltaUpdate);

            if (ValConfig.InternalStorageMode.Value) {
                // Internal storage reads/writes touch a registry ZDO and must stay on the main thread.
                Logger.LogInfo("Loading character for delta update with internal storage mode.");
                DataObjects.Character character = InternalDataStore.GetAccountCharacter(deltaUpdate.HostID, deltaUpdate.Name);
                if (character == null) {
                    RequestFullSync(sender, deltaUpdate);
                    return;
                }
                Logger.LogInfo($"Received delta update from {deltaUpdate.Name} ({deltaUpdate.HostID}): {deltaUpdate.ItemModifications?.Count ?? 0} item delta(s).");
                if (UpdatePlayerSaveWithDeltaData(deltaUpdate, character)) {
                    // Our copy no longer matches the client's baseline, so no later delta can repair it.
                    RequestFullSyncForDrift(sender, deltaUpdate.HostID, deltaUpdate.Name);
                }
                return;
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
                    return;
                }
            }

            Logger.LogInfo($"Received delta update from {deltaUpdate.Name} ({deltaUpdate.HostID}): {deltaUpdate.ItemModifications?.Count ?? 0} item delta(s).");
            modules.character.CharacterStore.SubmitDelta(deltaUpdate, sender);
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

        /// <summary>Main thread. Drops cooldown entries old enough that they can no longer suppress anything,
        /// so the table tracks characters recently asked for a resync rather than every character ever asked.
        /// Called from the scheduler's housekeeping tick.</summary>
        internal static void PruneDriftResyncTracking() {
            DateTime cutoff = DateTime.UtcNow.AddSeconds(-2 * DriftResyncCooldownSeconds);
            foreach (KeyValuePair<string, DateTime> entry in lastDriftResync) {
                if (entry.Value < cutoff) { lastDriftResync.TryRemove(entry.Key, out _); }
            }
        }

        internal static int DriftResyncTrackedCount => lastDriftResync.Count;

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
            if (!FromServer(sender)) { Logger.LogWarning($"Ignoring a full-sync request not from the server (sender {sender})."); yield break; }
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
                // A pass-through compat key never legitimately appears in a delta (the client's diff skips
                // it); drop rather than store one arriving from a modified client. Removals are deliberately
                // still applied - removing such a key only helps an older save shed it.
                if (modules.compat.CompatCustomData.IsPassthroughPlayerKey(kvp.Key)) { continue; }
                character.PlayerCustomData[kvp.Key] = kvp.Value;
            }
            Logger.LogDebug($"Updated custom data for {character.Name}.");

            // Update skills and active status effects. Clamp a fabricated jump against the levels we already
            // held for this character before overwriting them - the client reports its own skills, so a
            // modified one can claim any value.
            modules.character.SkillClamp.Apply(deltaSummary.SkillLevels, character.Name);
            character.SkillLevels = deltaSummary.SkillLevels;
            // The reported levels are the only confirmation a restore ever gets - see PendingSkillRestores.
            character.ConsumePendingSkillRestores();
            character.ActiveCharacterEffects = deltaSummary.ActiveCharacterEffects;
            // Null means the sender is not reporting a power (tracking is off, or it predates it) and must leave the
            // stored value alone. Not validated: selecting a power is purely client side, with no RPC to check it
            // against, so the stored value is only ever as good as the client that reported it.
            if (deltaSummary.GuardianPower != null) {
                character.GuardianPower = deltaSummary.GuardianPower;
            }
            // Same null rule. Not validated either: what a player eats is decided entirely on their own machine, and a
            // client that can lie here can eat whatever it likes anyway. What the record buys is the join restore.
            if (deltaSummary.Foods != null) {
                character.Foods = deltaSummary.Foods;
            }

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
        /// Server -> client character payload, with ConfiscatedItems and SkillReductions withheld.
        ///
        /// The client has no use for the confiscated history (nothing client side reads it) and mirroring it back
        /// on every full push wasted a lot of bandwidth - a real test character carried 239 entries in a 309KB
        /// save, re-sent both directions on join, death, respawn, logout and every full-sync pull. Worse, the
        /// mirror went stale the moment an admin ran /clear or /return, and the client's next push resurrected
        /// what the admin had removed. With the list withheld, a client's ConfiscatedItems only ever holds what it
        /// confiscated this session, which is exactly what MergeConfiscatedItems expects to receive. The
        /// skill-reduction record is withheld for the same reasons; PendingSkillRestores is deliberately NOT,
        /// because the client is what applies it.
        ///
        /// Deliberately NOT folded into SendCharacterAsZpackage: that one also serves client -> server pushes,
        /// which must keep carrying the new confiscations.
        /// </summary>
        internal static ZPackage SendCharacterToClientAsZpackage(DataObjects.Character chara) {
            if (chara == null) { return new ZPackage(); }
            List<PackedItem> held = chara.ConfiscatedItems;
            List<SkillReduction> heldReductions = chara.SkillReductions;
            try {
                chara.ConfiscatedItems = null;
                chara.SkillReductions = null;
                // Tagged so every server -> client character payload carries its kind explicitly. Not tagging
                // would still work (the client defaults an untagged payload to CHAR, for older servers), but
                // leaving one path implicit is how the "silence means no character" ambiguity started.
                return CharacterPayload(DataObjects.yamlserializer.Serialize(chara), CharPayloadCharacter);
            } finally {
                // The caller's object is server-side authoritative state; never leave it stripped.
                chara.ConfiscatedItems = held;
                chara.SkillReductions = heldReductions;
            }
        }

        /// <summary>Same as <see cref="SendCharacterToClientAsZpackage"/> but for a raw YAML string that has not
        /// been parsed yet - used on the connect path, where the store hands back cached/on-disk YAML. Falls back
        /// to the original text if it cannot be parsed, so a corrupt save still reaches the client unchanged
        /// rather than becoming an empty payload.</summary>
        internal static string StripServerOwnedFromYaml(string yaml) {
            if (string.IsNullOrEmpty(yaml)) { return yaml; }
            try {
                DataObjects.Character chara = DataObjects.yamldeserializer.Deserialize<DataObjects.Character>(yaml);
                if (chara == null) { return yaml; }
                chara.ConfiscatedItems = null;
                chara.SkillReductions = null;
                return DataObjects.yamlserializer.Serialize(chara);
            } catch (Exception e) {
                Logger.LogWarning($"Could not strip the server-owned lists from a character payload, sending it as-is: {e.Message}");
                return yaml;
            }
        }

        public static ZNetPeer GetPeerByPlatformID(string platformID) {
            foreach (ZNetPeer peer in ZNet.instance.GetPeers()) {
                // PlatformIds.Matches rather than ==: the same account reaches us as both "Steam_7656..." and
                // the bare "7656...", and an admin may type either into a command. An exact compare here silently
                // sent a returned item down the "they are offline" path when the spellings differed.
                if (peer.IsReady() && peer.m_socket != null && PlatformIds.Matches(peer.m_socket.GetHostName(), platformID)) {
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
