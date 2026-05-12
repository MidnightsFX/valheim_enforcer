using Jotunn.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace ValheimEnforcer.modules.compat {
    internal class ModCompatability {

        public static bool IsExtraSlotsEnabled = false;
        public static bool IsTrialsOfToilEnabled = false;

        internal static void CheckModCompat() {
            try {
                Dictionary<string, BepInEx.BaseUnityPlugin> plugins = BepInExUtils.GetPlugins();
                if (plugins == null) { return; }
                //Logger.LogDebug($"Checking for mod compatibility... {string.Join(",", plugins.Keys)}");
                if (plugins.Keys.Contains("shudnal.ExtraSlots")) {
                    //Logger.LogInfo("Extra Slots mod detected. Enabling compatibility.");
                    IsExtraSlotsEnabled = ExtraSlots.API.IsReady();
                }
                if (plugins.Keys.Contains("maxfoxgaming.environmentalawareness")) {
                    //Logger.LogInfo("Trials of Toil mod detected. Enabling compatibility.");
                    IsTrialsOfToilEnabled = true;
                }
            } catch {
                Logger.LogWarning("Unable to check mod compatibility. Ensure that Bepinex can load.");
            }
        }
    }
}
