using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Entities;
using Jotunn.Managers;
using Jotunn.Utils;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using ValheimEnforcer.modules;
using ValheimEnforcer.modules.character;
using ValheimEnforcer.modules.cheatmonitor;
using ValheimEnforcer.modules.commands;
using ValheimEnforcer.modules.compat;

namespace ValheimEnforcer
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    [NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod, VersionStrictness.Minor)]
    [BepInDependency("shudnal.ExtraSlots", BepInDependency.DependencyFlags.SoftDependency)]
    // ServerCharacters owns character saving the same way this mod does; running both would have them fight
    // over every profile. BepInEx enforces this by refusing to load US when it is present, so a server with
    // both installed runs with no enforcement at all - hence the loud ordering note in the README: uninstall
    // ServerCharacters first, then enable ImportServerCharacters to pick up the files it left behind.
    [BepInIncompatibility("org.bepinex.plugins.servercharacters")]
    internal class ValheimEnforcer : BaseUnityPlugin
    {
        public const string PluginGUID = "MidnightsFX.ValheimEnforcer";
        public const string PluginName = "ValheimEnforcer";
        public const string PluginVersion = "0.18.0";

        internal static ManualLogSource Log;
        internal ValConfig cfg;

        public static CustomLocalization Localization = LocalizationManager.Instance.GetLocalization();
        public static AssetBundle EmbeddedResourceBundle;

        public void Awake()
        {
            Log = this.Logger;
            cfg = new ValConfig(Config);
            EmbeddedResourceBundle = AssetUtils.LoadAssetBundleFromResources("ValheimEnforcer.assets.vebundle", typeof(ValheimEnforcer).Assembly);
            // Just needs to run AFTER all mods are loaded
            // For client
            PrefabManager.OnPrefabsRegistered += ModManager.SetModsActive;
            ZoneManager.OnLocationsRegistered += InternalDataStore.InstanciateOrLinkMetadataRegistry;
            // For server
            PrefabManager.OnVanillaPrefabsAvailable += ModManager.SetModsActive;
            GUIManager.OnCustomGUIAvailable += ModManager.AddErrorMessageDetailsForMenu;
            InternalDataStore.RegisterMetadataHolder();
            TerminalCommands.AddCommands();
            MinimapManager.OnVanillaMapDataLoaded += CheatDetector.Initialize;
            MinimapManager.OnVanillaMapDataLoaded += CharacterDeltaTracker.Initialize;

            ModCompatability.CheckModCompat();
            Harmony harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly());
        }

    }
}