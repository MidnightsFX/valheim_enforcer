using BepInEx;
using BepInEx.Bootstrap;
using System;
using System.Collections.Generic;
using System.Linq;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.mods {

    /// <summary>
    /// Handles the plugins BepInEx skipped on this machine because they only run in the game client.
    ///
    /// A plugin marked [BepInProcess("valheim.exe")] never loads on valheim_server.exe, so Enforcer never sees it
    /// and never lists it. A player running it is then refused for an "extra" mod the admin did install on the
    /// server, which reads as Enforcer getting the comparison wrong. Such plugins are either added to optionalMods
    /// (AutoAddClientOnlyModsToOptional) or named in a warning.
    ///
    /// Read from BepInEx's own plugin cache, which the chainloader rewrote moments earlier, instead of opening
    /// every DLL in the plugins folder again. With the cache turned off there is nothing to read and this does
    /// nothing.
    /// </summary>
    internal static class ClientOnlyPlugins {

        private const string ClientProcess = "valheim";

        private static List<PluginInfo> found;
        private static bool warned;

        /// <summary>
        /// Runs on every SetModsActive, which reloads the lists from Mods.yaml each time: with
        /// UpdateLoadedModsOnStartup off, an entry added by the first call is not in the file the second call reads.
        /// </summary>
        internal static void Apply(DataObjects.Mods settings) {
            if (settings == null) { return; }
            try {
                List<PluginInfo> unlisted = Found().Where(p => !IsListed(settings, p.Metadata.GUID)).ToList();
                if (unlisted.Count == 0) { return; }
                string names = string.Join(", ", unlisted.Select(p => $"{p.Metadata.Name} ({p.Metadata.GUID})").ToArray());

                if (ValConfig.AutoAddClientOnlyModsToOptional.Value) {
                    foreach (PluginInfo plugin in unlisted) {
                        settings.OptionalMods[plugin.Metadata.GUID] = new DataObjects.Mod() { PluginID = plugin.Metadata.GUID, Name = plugin.Metadata.Name };
                    }
                    Logger.LogInfo($"Added to optionalMods because they only run in the game client: {names}. Any version is allowed until you give an entry a version.");
                    return;
                }

                if (warned) { return; }
                warned = true;
                Logger.LogWarning($"{unlisted.Count} plugin(s) installed here only run in the game client, so BepInEx skipped them and this server cannot list them: {names}. "
                    + $"Players who run them will be refused. To allow them, list each one under optionalMods in {ValConfig.ModsFileName}, or turn on AutoAddClientOnlyModsToOptional.");
            } catch (Exception e) {
                Logger.LogDebug($"Could not check for client-only plugins: {e.Message}");
            }
        }

        private static List<PluginInfo> Found() {
            if (found != null) { return found; }
            Dictionary<string, CachedAssembly<PluginInfo>> cache = TypeLoader.LoadAssemblyCache<PluginInfo>("chainloader");
            found = cache == null ? new List<PluginInfo>() : cache.Values
                .SelectMany(assembly => assembly.CacheItems)
                .Where(p => !Chainloader.PluginInfos.ContainsKey(p.Metadata.GUID) && SkippedHereButRunsInClient(p))
                .GroupBy(p => p.Metadata.GUID)
                .Select(g => g.First())
                .ToList();
            return found;
        }

        // The same process test Chainloader.Start applies, narrowed to plugins that name the game client.
        private static bool SkippedHereButRunsInClient(PluginInfo plugin) {
            List<string> processes = (plugin.Processes ?? Enumerable.Empty<BepInProcess>())
                .Select(p => p.ProcessName.Replace(".exe", "")).ToList();
            if (processes.Count == 0) { return false; }
            if (processes.Any(p => string.Equals(p, Paths.ProcessName, StringComparison.InvariantCultureIgnoreCase))) { return false; }
            return processes.Any(p => string.Equals(p, ClientProcess, StringComparison.InvariantCultureIgnoreCase));
        }

        // Any list counts, serverOnlyMods included: an admin who put the mod somewhere has made their choice.
        private static bool IsListed(DataObjects.Mods settings, string guid) {
            return (settings.RequiredMods?.ContainsKey(guid) ?? false)
                || (settings.OptionalMods?.ContainsKey(guid) ?? false)
                || (settings.AdminOnlyMods?.ContainsKey(guid) ?? false)
                || (settings.ServerOnlyMods?.ContainsKey(guid) ?? false);
        }
    }
}
