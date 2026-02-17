using BepInEx;
using HarmonyLib;
using Jotunn;
using Jotunn.Managers;
using Jotunn.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules {
    internal static class ModManager {

        internal static DataObjects.Mods ModSettings { get; set; }
        internal static Dictionary<string, BaseUnityPlugin> ActiveMods = new Dictionary<string, BaseUnityPlugin>();
        internal static void SetModsActive() {
            ActiveMods.Clear();
            ActiveMods = BepInExUtils.GetPlugins(true);
            ModSettings = new DataObjects.Mods();
    
            // Read the config file
            LoadConfig(File.ReadAllText(ValConfig.ModsConfigFilePath));

            foreach (KeyValuePair<string, BaseUnityPlugin> plugin in ActiveMods) {
                if (ModSettings.RequiredMods.ContainsKey(plugin.Key)) { continue; }
                if (ModSettings.AdminOnlyMods.ContainsKey(plugin.Key)) { continue; }
                if (ModSettings.OptionalMods.ContainsKey(plugin.Key)) { continue; }

                if (ActiveMods.ContainsKey(plugin.Key) == false) {
                    Logger.LogDebug($"Adding Mod {plugin.Key} not found in modlist");
                    ModSettings.ActiveMods.Add(plugin.Key, new DataObjects.Mod() { EnforceVersion = false, Version = plugin.Value.Info.Metadata.Version.ToString(), PluginID = plugin.Value.Info.Metadata.GUID, Name = plugin.Value.Info.Metadata.Name });
                }
            }

            // Write out updates to the loaded mods, if enabled
            if (ValConfig.UpdateLoadedModsOnStartup.Value) {
                File.WriteAllText(ValConfig.ModsConfigFilePath, DataObjects.yamlserializer.Serialize(ModSettings));
            }
        }

        internal static void UpdateModSettingConfigs(string yamlstring) {
            try {
                ModSettings = DataObjects.yamldeserializer.Deserialize<DataObjects.Mods>(yamlstring);
            } catch {
                Logger.LogWarning("Failed to deserialize mod configurations.");
            }
        }

        internal static bool ServerValidateClientModlist(string yamlstring, bool isAdmin) {
            DataObjects.Mods clientMods = new DataObjects.Mods();
            try {
                clientMods = DataObjects.yamldeserializer.Deserialize<DataObjects.Mods>(yamlstring);
            } catch {
                Logger.LogWarning("Failed to deserialize mod configurations.");
                return false;
            }

            List<string> extraMods = new List<string>();
            List<string> versionMismatch = new List<string>();
            List<string> requiredModsMissing = ModSettings.RequiredMods.Keys.ToList();

            foreach (KeyValuePair<string, DataObjects.Mod> mod in clientMods.ActiveMods) {
                if (ModSettings.RequiredMods.ContainsKey(mod.Key)) {
                    requiredModsMissing.Remove(mod.Key);
                    if (ModSettings.RequiredMods[mod.Key].EnforceVersion) {
                        if (ModSettings.RequiredMods[mod.Key].Version == mod.Value.Version) {
                            continue;
                        } else {
                            versionMismatch.Add(mod.Key);
                        }
                    } else {
                        continue;
                    }
                }
                if (isAdmin && ModSettings.AdminOnlyMods.ContainsKey(mod.Key)) {
                    if (ModSettings.AdminOnlyMods[mod.Key].EnforceVersion) {
                        if (ModSettings.AdminOnlyMods[mod.Key].Version == mod.Value.Version) {
                            continue;
                        } else {
                            versionMismatch.Add(mod.Key);
                        }
                    } else {
                        continue;
                    }
                }
                if (ModSettings.OptionalMods.ContainsKey(mod.Key)) {
                    if (ModSettings.OptionalMods[mod.Key].EnforceVersion) {
                        if (ModSettings.OptionalMods[mod.Key].Version == mod.Value.Version) {
                            continue;
                        } else {
                            versionMismatch.Add(mod.Key);
                        }
                    } else {
                        continue;
                    }
                }

                extraMods.Add(mod.Key);
            }


            if (versionMismatch.Count > 0) {
                Logger.LogWarning($"Mods version mismatch with the server found: {string.Join(",", versionMismatch)}");
            }
            if (requiredModsMissing.Count > 0) {
                Logger.LogWarning($"Missing required mods: {string.Join(",", requiredModsMissing)}");
            }
            if (extraMods.Count > 0) {
                Logger.LogWarning($"Non-allowed mods found: {string.Join(",", extraMods)}");
            }
            if (versionMismatch.Count > 0 || requiredModsMissing.Count > 0 || extraMods.Count > 0) {
                return false;
            }
            Logger.LogInfo("Client mod list validated successfully.");
            return true;
        }

        internal static void LoadConfig(string yaml) {
            ModSettings = DataObjects.yamldeserializer.Deserialize<DataObjects.Mods>(yaml);
        }

        internal static string GetDefaultConfig() {
            if (ModSettings != null) {
                return DataObjects.yamlserializer.Serialize(ModSettings);
            }
            return DataObjects.yamlserializer.Serialize(new DataObjects.Mods());
        }

        internal static bool ValidateClientMods() {
            bool isAdmin = SynchronizationManager.Instance.PlayerIsAdmin;

            if (ModSettings == null) {
                Logger.LogWarning("Could not validate mod list.");
                return false;
            }

            List<string> missingMods = new List<string>();
            List<string> unallowedMods = new List<string>();
            List<string> foundMods = new List<string>();

            foreach (KeyValuePair<string, BaseUnityPlugin> plugin in ActiveMods) {
                Logger.LogDebug($"Found active mod: {plugin.Key}, checking...");
                foundMods.Add(plugin.Key);
                if (ModSettings.RequiredMods.ContainsKey(plugin.Key)) { continue; }
                if (isAdmin == true && ModSettings.AdminOnlyMods.ContainsKey(plugin.Key)) { continue; }
                if (ModSettings.OptionalMods.ContainsKey(plugin.Key)) { continue; }

                unallowedMods.Add(plugin.Key);
            }
            missingMods = ModSettings.RequiredMods.Keys.ToList();
            missingMods.RemoveAll(v => foundMods.Contains(v));

            if (missingMods.Count > 0) {
                Logger.LogWarning($"Missing required mods: {string.Join(",", missingMods)}");
            }
            if (unallowedMods.Count > 0) {
                Logger.LogWarning($"Non-allowed mods found: {string.Join(",", unallowedMods)}");
            }
            if (missingMods.Count > 0 || unallowedMods.Count > 0) {
                return false;
            }
            Logger.LogInfo("Client mod list validated successfully.");
            return true;
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.SendPeerInfo))]
        public static class ZnetCharacterSyncPatch {
            [HarmonyPostfix]
            private static void ZNet_SendPeerInfo(ZNet __instance, ZRpc rpc, string password) {
                ZDOMan.ZDOPeer peer = ZDOMan.instance.FindPeer(rpc);
                if (peer != null && ZNet.instance.IsClientInstance()) {
                    Logger.LogDebug($"Client player found, sending mod list. to {peer} {rpc.m_socket.GetHostName()}");
                    ValConfig.ModEnforcmentRPC.SendPackage(peer.m_peer.m_uid, ValConfig.SendModConfigs());
                }
            }
        }

    }
}
