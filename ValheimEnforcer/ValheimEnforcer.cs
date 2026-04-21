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

namespace ValheimEnforcer
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    [NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod, VersionStrictness.Minor)]
    [BepInDependency("shudnal.ExtraSlots", BepInDependency.DependencyFlags.SoftDependency)]
    internal class ValheimEnforcer : BaseUnityPlugin
    {
        public const string PluginGUID = "MidnightsFX.ValheimEnforcer";
        public const string PluginName = "ValheimEnforcer";
        public const string PluginVersion = "0.7.1";

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
            PrefabManager.OnPrefabsRegistered += InternalDataStore.InstanciateOrLinkMetadataRegistry;
            // For server
            PrefabManager.OnVanillaPrefabsAvailable += ModManager.SetModsActive;
            GUIManager.OnCustomGUIAvailable += ModManager.AddErrorMessageDetailsForMenu;
            InternalDataStore.RegisterMetadataHolder();
            TerminalCommands.AddCommands();
            MinimapManager.OnVanillaMapDataLoaded += CheatDetector.Initialize;

            Harmony harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly());
        }

    }
}