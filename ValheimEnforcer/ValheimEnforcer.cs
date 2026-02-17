using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Entities;
using Jotunn.Managers;
using Jotunn.Utils;
using System;
using System.Collections.Generic;
using System.Reflection;
using ValheimEnforcer.modules;

namespace ValheimEnforcer
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    [NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod, VersionStrictness.Minor)]
    internal class ValheimEnforcer : BaseUnityPlugin
    {
        public const string PluginGUID = "MidnightsFX.ValheimEnforcer";
        public const string PluginName = "ValheimEnforcer";
        public const string PluginVersion = "0.0.1";

        internal static ManualLogSource Log;
        internal ValConfig cfg;

        public static CustomLocalization Localization = LocalizationManager.Instance.GetLocalization();
        
        public void Awake()
        {
            Log = this.Logger;
            cfg = new ValConfig(Config);
            // Just needs to run AFTER all mods are loaded
            ItemManager.OnItemsRegisteredFejd += ModManager.SetModsActive;

            Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly());
        }

    }
}