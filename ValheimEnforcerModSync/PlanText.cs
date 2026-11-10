using System.IO;
using System.Text;
using ValheimEnforcer.API;

namespace ValheimEnforcerModSync {

    /// <summary>The plan as the player reads it before agreeing to it.</summary>
    internal static class PlanText {

        /// <summary>A path shown to the player relative to BepInEx/plugins, which is the folder they recognise.</summary>
        internal static string Shown(PlanInfo plan, string path) {
            if (PathUtil.IsUnder(path, plan.PluginsRoot)) {
                return "plugins/" + Path.GetFullPath(path).Substring(Path.GetFullPath(plan.PluginsRoot).TrimEnd('\\', '/').Length + 1).Replace('\\', '/');
            }
            return Path.GetFileName(path);
        }

        /// <summary>Unity rich text.</summary>
        internal static string Describe(PlanInfo plan) {
            StringBuilder text = new StringBuilder();
            if (plan.Installs.Count > 0) {
                text.AppendLine("<color=#FFA13C>Install or update from Thunderstore:</color>");
                foreach (PackageInfo package in plan.Installs) {
                    text.AppendLine($"  • {package.Owner}-{package.Name} {package.Version}");
                    foreach (string reason in package.Reasons) { text.AppendLine($"      {reason}"); }
                    foreach (string replaced in package.ReplacedDlls) { text.AppendLine($"      replaces {Shown(plan, replaced)}"); }
                }
                text.AppendLine();
            }
            if (plan.Disables.Count > 0) {
                text.AppendLine("<color=#FFA13C>Turn off (renamed to .old, not deleted):</color>");
                foreach (DisableInfo disable in plan.Disables) {
                    text.AppendLine($"  • {disable.PluginId} - {Shown(plan, disable.DllPath)}, {disable.Reason}");
                }
                text.AppendLine();
            }
            if (plan.Unfixable.Count > 0) {
                text.AppendLine("<color=#FFA13C>Not fixed automatically:</color>");
                foreach (string line in plan.Unfixable) { text.AppendLine($"  • {line}"); }
                text.AppendLine();
            }
            if (plan.Notes.Count > 0) {
                text.AppendLine("<color=#FFA13C>Left alone:</color>");
                foreach (string line in plan.Notes) { text.AppendLine($"  • {line}"); }
                text.AppendLine();
            }
            return text.ToString();
        }
    }
}
