using BepInEx;
using HarmonyLib;
using Jotunn;
using Jotunn.Extensions;
using Jotunn.Managers;
using Jotunn.Utils;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using ValheimEnforcer.common;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.modules {
    internal static class ModManager {

        internal static DataObjects.Mods ModSettings { get; set; }
        internal static Dictionary<string, BaseUnityPlugin> ActiveMods = new Dictionary<string, BaseUnityPlugin>();
        internal static JotunnDetailDisconnectExpansion DetailsUpdater { get; set; }

        internal static void SetModsActive() {
            ActiveMods.Clear();
            ActiveMods = BepInExUtils.GetPlugins(true);
            ModSettings = new DataObjects.Mods();
            Logger.LogDebug($"Detected {ActiveMods.Keys.Count} mods.");

            // Read the config file
            LoadConfig(File.ReadAllText(ValConfig.ModsConfigFilePath));
            ModSettings.ActiveMods.Clear();

            foreach (KeyValuePair<string, BaseUnityPlugin> plugin in ActiveMods) {
                if (ModSettings.ActiveMods.ContainsKey(plugin.Key) == false) {
                    Logger.LogDebug($"Adding Mod {plugin.Key} not found in modlist");
                    ModSettings.ActiveMods.Add(plugin.Key, new DataObjects.Mod() { EnforceVersion = true, Version = plugin.Value.Info.Metadata.Version.ToString(), PluginID = plugin.Value.Info.Metadata.GUID, Name = plugin.Value.Info.Metadata.Name });
                }

                Logger.LogDebug($"Found active mod: {plugin.Key} v{plugin.Value.Info.Metadata.Version}");
                string currentVersion = plugin.Value.Info.Metadata.Version.ToString();

                if (ModSettings.RequiredMods.ContainsKey(plugin.Key)) {
                    UpdateModVersionIfChanged(ModSettings.RequiredMods, plugin.Key, currentVersion);
                    continue;
                }
                if (ModSettings.AdminOnlyMods.ContainsKey(plugin.Key)) {
                    UpdateModVersionIfChanged(ModSettings.AdminOnlyMods, plugin.Key, currentVersion);
                    continue;
                }
                if (ModSettings.OptionalMods.ContainsKey(plugin.Key)) {
                    UpdateModVersionIfChanged(ModSettings.OptionalMods, plugin.Key, currentVersion);
                    continue;
                }
                if (ModSettings.ServerOnlyMods.ContainsKey(plugin.Key)) {
                    UpdateModVersionIfChanged(ModSettings.ServerOnlyMods, plugin.Key, currentVersion);
                    continue;
                } // Server only mods are basically the skip button for a mod

                if (ValConfig.AutoAddModsToRequired.Value == true) {
                    Logger.LogDebug($"Automatically adding {plugin.Key} as a required mod.");
                    ModSettings.RequiredMods.Add(plugin.Key, new DataObjects.Mod() { EnforceVersion = false, Version = plugin.Value.Info.Metadata.Version.ToString(), PluginID = plugin.Value.Info.Metadata.GUID, Name = plugin.Value.Info.Metadata.Name });
                }
            }

            // Write out updates to the loaded mods, if enabled
            if (ValConfig.UpdateLoadedModsOnStartup.Value) {
                Logger.LogDebug("Updated Mods.yaml.");
                File.WriteAllText(ValConfig.ModsConfigFilePath, DataObjects.yamlserializer.Serialize(ModSettings));
            }
        }

        private static void UpdateModVersionIfChanged(Dictionary<string, DataObjects.Mod> modList, string key, string currentVersion) {
            if (modList[key].Version != currentVersion) {
                Logger.LogInfo($"Updating version for {key}: {modList[key].Version} -> {currentVersion}");
                modList[key].Version = currentVersion;
            }
        }

        internal static void UpdateModSettingConfigs(string yamlstring) {
            try {
                ModSettings = DataObjects.yamldeserializer.Deserialize<DataObjects.Mods>(yamlstring);
            } catch {
                Logger.LogWarning("Failed to deserialize mod configurations.");
            }
        }

        internal static bool ValidateModlist(Mods CheckingMods, Mods AuthoratativeMods, bool isAdmin, out string summay, out string details) {
            summay = "";
            details = "";
            List<string> extraMods = new List<string>();
            List<string> versionMismatch = new List<string>();
            List<string> requiredModsMissing = AuthoratativeMods.RequiredMods.Keys.Distinct().ToList();

            Logger.LogDebug($"Validating modlist of {CheckingMods.ActiveMods.Count} mods isAdmin? {isAdmin}");

            foreach (KeyValuePair<string, DataObjects.Mod> mod in CheckingMods.ActiveMods) {
                requiredModsMissing.Remove(mod.Key);
                // Compare required mods
                if (AuthoratativeMods.RequiredMods.ContainsKey(mod.Key)) {
                    if (AuthoratativeMods.RequiredMods[mod.Key].EnforceVersion) {
                        if (AuthoratativeMods.RequiredMods[mod.Key].Version == mod.Value.Version) {
                            continue;
                        } else {
                            versionMismatch.Add(mod.Key);
                        }
                    } else {
                        continue;
                    }
                }

                // Compare admin mods - always recognize admin-only mods so non-admins
                // aren't kicked for having them, but only enforce versions for admins
                if (AuthoratativeMods.AdminOnlyMods.ContainsKey(mod.Key)) {
                    if (isAdmin && AuthoratativeMods.AdminOnlyMods[mod.Key].EnforceVersion) {
                        if (AuthoratativeMods.AdminOnlyMods[mod.Key].Version == mod.Value.Version) {
                            continue;
                        } else {
                            versionMismatch.Add(mod.Key);
                        }
                    } else {
                        continue;
                    }
                }

                // Compare optional mods
                if (AuthoratativeMods.OptionalMods.ContainsKey(mod.Key)) {
                    if (AuthoratativeMods.OptionalMods[mod.Key].EnforceVersion) {
                        if (AuthoratativeMods.OptionalMods[mod.Key].Version == mod.Value.Version) {
                            continue;
                        } else {
                            versionMismatch.Add(mod.Key);
                        }
                    } else {
                        continue;
                    }
                }

                // Mod didn't match one of the existing mods, its an extra
                extraMods.Add(mod.Key);
            }


            if (versionMismatch.Count > 0) {
                Logger.LogWarning($"Mods version mismatch with the server found:");
                summay = "A Mod mismatch was detected. Ensure you have the correct versions and are only using allowed mods.";
            }
            if (requiredModsMissing.Count > 0) {
                string requiredMissing = $"\nMissing required mods: {string.Join(", ", requiredModsMissing)}";
                summay += requiredMissing;
                Logger.LogWarning(requiredMissing);
            }
            if (extraMods.Count > 0) {
                string unallowedMods = $"\nNon-allowed mods found: {string.Join(", ", extraMods)}";
                summay += unallowedMods;
                Logger.LogWarning(unallowedMods);
            }
            if (versionMismatch.Count > 0 || requiredModsMissing.Count > 0 || extraMods.Count > 0) {
                // Build detailed error message for display in Jotunn's CompatibilityWindow
                StringBuilder errorBuilder = new StringBuilder();
                errorBuilder.AppendLine("\n<b>ValheimEnforcer - Mod Validation Failed</b>");

                if (versionMismatch.Count > 0) {
                    errorBuilder.AppendLine("\n<b>Version Mismatches:</b>");
                    foreach (var modKey in versionMismatch) {
                        errorBuilder.AppendLine($"  • {modKey}");
                    }
                }

                if (requiredModsMissing.Count > 0) {
                    errorBuilder.AppendLine("\n<b>Missing Required Mods:</b>");
                    foreach (var modKey in requiredModsMissing) {
                        errorBuilder.AppendLine($"  • {modKey}");
                    }
                }

                if (extraMods.Count > 0) {
                    errorBuilder.AppendLine("\n<b>Non-Allowed Mods:</b>");
                    foreach (var modKey in extraMods) {
                        errorBuilder.AppendLine($"  • {modKey}");
                    }
                }
                string fullError = errorBuilder.ToString();
                details = fullError;
                //Logger.LogWarning(LastValidationError);
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


        internal static class ValidateMods {
            // Register new RPC
            [HarmonyPatch(typeof(ZNet), nameof(ZNet.OnNewConnection))]
            public static class ZNet_OnNewConnection_Patch {
                [HarmonyPrefix]
                [HarmonyPriority(Priority.First)]
                private static void Prefix(ZNet __instance, ZNetPeer peer) {
                    Logger.LogDebug($"New Connection, register VE Mod Sync RPC.");
                    // Register our RPC handler
                    peer.m_rpc.Register<ZPackage>(nameof(RPC_ReceiveModVersionData), RPC_ReceiveModVersionData);
                }
            }
        }


        // Send Client list during handshake
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_ClientHandshake))]
        public static class ZNet_RPC_ClientHandshake_Patch {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static void Prefix(ZNet __instance, ZRpc rpc) {
                if (__instance.IsClientInstance()) {
                    Logger.LogDebug("Client sending mod version data to server");
                    // var modData = new ModVersionData(GetEnforcableMods());
                    rpc.Invoke(nameof(RPC_ReceiveModVersionData), ModSettings.ToZPackage());
                }
            }
        }

        // Send server list during handshake
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_ServerHandshake))]
        public static class ZNet_RPC_ServerHandshake_Patch {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static void Prefix(ZNet __instance, ZRpc rpc) {
                if (__instance.IsServer()) {
                    Logger.LogDebug("Server sending mod version data to client");
                    rpc.Invoke(nameof(RPC_ReceiveModVersionData), ModSettings.ToZPackage());
                }
            }
        }

        /// <summary>
        /// RPC handler to receive and store mod version data
        /// </summary>
        private static void RPC_ReceiveModVersionData(ZRpc sender, ZPackage data) {
            Logger.LogDebug($"Received mod version data from {sender.m_socket.GetEndPointString()}");
            string peerAddress = sender.m_socket.GetEndPointString();
            if (!ZNet.instance.IsServer()) {
                // Client received data from server
                Mods serverMods = new Mods().FromZPackage(data);
                Logger.LogDebug($"Client received server mod data: Required: {serverMods.RequiredMods.Count}, Optional: {serverMods.OptionalMods.Count}, AdminOnly: {serverMods.AdminOnlyMods.Count} mods");
                // Admin check on the client side is going to be iffy
                bool modsvalid = ValidateModlist(ModSettings, serverMods, SynchronizationManager.Instance.PlayerIsAdmin, out string summary, out string details);

                if (modsvalid == false) {
                    DetailsUpdater.UpdateErrorText(summary, details);
                    // Client does not kick, but it does set the error message, the server ultimately does the actual validation-
                    // this client side comparison is just to provide feedback to the user
                    Logger.LogWarning($"Mod compatibility check failed for client.");
                }
            } else {
                // Server received data from client
                Mods clientMods = new Mods().FromZPackage(data);
                bool isadmin = ZNet.instance.IsAdmin(sender.m_socket.GetHostName());
                Logger.LogDebug($"Server received server mod data from {peerAddress} Admin?{isadmin}: Required: {clientMods.RequiredMods.Count}, Optional: {clientMods.OptionalMods.Count}, AdminOnly: {clientMods.AdminOnlyMods.Count} mods");;
                bool modsvalid = ValidateModlist(clientMods, ModSettings, isadmin, out string summary, out string details);
                if (modsvalid == false) {
                    Logger.LogWarning($"Mod compatibility check failed for client at {peerAddress}\n{summary}");
                    // Kick the player
                    sender.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorVersion);
                }
            }
        }

        internal static void AddErrorMessageDetailsForMenu() {
            // We only want to monitor the start scene for the disconnect dialogue box
            if (SceneManager.GetActiveScene().name.Equals("start") == false) { return; }

            DetailsUpdater = GUIManager.CustomGUIFront.AddComponent<JotunnDetailDisconnectExpansion>();
        }

        public class JotunnDetailDisconnectExpansion : MonoBehaviour {
            GameObject ContentView;
            Text HeaderText;
            Text FooterText;
            static string HeaderMessage = "";
            static string FooterMessage = "";
            bool textset = false;

            public void UpdateErrorText(string header, string footer) {
                Logger.LogDebug($"Set Error results {header} {footer}");
                HeaderMessage = header;
                FooterMessage = footer;
                textset = false;
            }

            public void Update() {
                if (GUIManager.CustomGUIFront == null) { return; }
                Transform contentTForm = GUIManager.CustomGUIFront.transform.Find("CompatibilityWindow(Clone)/Scroll View/Viewport/Content");
                if (contentTForm == null) { 
                    textset = false;
                    return;
                }

                //List<string> children = new List<string>();
                //int count = contentTForm.childCount;
                //for (int i = 0; i < count; i++) {
                //    Transform child = contentTForm.GetChild(i);
                //    children.Add(child.name);
                //}
                //Logger.LogDebug($"Object Children: {string.Join(",", children) }");
                //return;

                if (textset == true) { return; }

                // Fix the scrollbars sensitivity
                GUIManager.CustomGUIFront.transform.Find("CompatibilityWindow(Clone)/Scroll View").GetComponent<ScrollRect>().scrollSensitivity = 1000f;


                ContentView = contentTForm.gameObject;
                // Assign references
                Transform headerTform = ContentView.transform.Find("Failed Connection Text");
                if (headerTform != null) { HeaderText = headerTform.GetComponent<Text>(); } else { Logger.LogDebug("Could not find HeaderText"); }
                Transform footerTForm = ContentView.transform.Find("Error Messages Text");
                if (footerTForm != null) { FooterText = footerTForm.GetComponent<Text>(); } else { Logger.LogDebug("Could not find FooterText"); }

                // Only override when VE has an actual message to add.
                // Otherwise preserve whatever Jotunn (or another mod) already wrote to the compatibility window.
                if (HeaderText != null && !string.IsNullOrEmpty(HeaderMessage)) {
                    HeaderText.text = $"<color=#FFA13C>Failed Connection:</color>\n{HeaderMessage}";
                }
                if (FooterText != null && !string.IsNullOrEmpty(FooterMessage)) {
                    FooterText.text = $"<color=#FFA13C>Further Steps:</color>\n{FooterMessage}";
                }
                Logger.LogDebug($"Set error results. H:{HeaderMessage} F:{FooterMessage}");
                textset = true;
            }
        }
    }
}
