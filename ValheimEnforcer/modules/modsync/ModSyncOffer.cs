using BepInEx;
using HarmonyLib;
using System.Collections.Generic;
using ValheimEnforcer.API;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.modsync {

    /// <summary>
    /// Client side: keeps the mod list of a server that offers to fix a refused player's mods, and works out what
    /// that fix would be. Nothing more - this mod never downloads, stages or installs anything for a player. The
    /// separate mod sync add-on does that, reading the plan through <see cref="ModSyncApi"/>.
    ///
    /// The offer comes only from a server whose admin turned on OfferModDownloads. The server never sends code:
    /// its list, which every client receives in the handshake anyway, names Thunderstore packages and the file
    /// hashes it will accept.
    /// </summary>
    internal static class ModSyncOffer {

        // The latest server list that offered downloads, for the connection attempt in progress. Cleared when the
        // next attempt starts, so a window from a different server can never act on this one's list.
        private static DataObjects.Mods offer;
        private static string rejoinAddress;

        internal static bool HasOffer { get { return offer != null; } }
        internal static string RejoinAddress { get { return rejoinAddress; } }

        internal static void ClearOffer() {
            offer = null;
            rejoinAddress = null;
        }

        /// <summary>Called with every server list a client receives. Keeps it only when the server offers downloads.</summary>
        internal static void NoteServerList(DataObjects.Mods serverMods) {
            if (serverMods == null || !serverMods.OffersModDownloads) {
                ClearOffer();
                return;
            }
            offer = serverMods;
            rejoinAddress = CurrentRejoinAddress();
            Logger.LogDebug($"This server offers mod downloads; rejoin address {(rejoinAddress ?? "not available")}.");
        }

        /// <summary>
        /// "host:port" for +connect, or null when this connection cannot be expressed that way. Only a direct
        /// connection to a dedicated server's address can; a crossplay join code or a Steam id join cannot, and
        /// the player is told to rejoin from the server list instead.
        /// </summary>
        private static string CurrentRejoinAddress() {
            if (ZNet.m_onlineBackend != OnlineBackendType.Steamworks) { return null; }
            if (string.IsNullOrEmpty(ZNet.m_serverHost) || ZNet.m_serverHostPort <= 0) { return null; }
            return $"{ZNet.m_serverHost}:{ZNet.m_serverHostPort}";
        }

        /// <summary>The plan for the server that offered, against what this game has loaded. Null without an offer.</summary>
        internal static ModSyncPlan BuildPlan() {
            if (offer == null || ModManager.ModSettings == null) { return null; }
            Dictionary<string, string> dllPaths = new Dictionary<string, string>();
            foreach (KeyValuePair<string, BaseUnityPlugin> plugin in ModManager.ActiveMods) {
                string location = plugin.Value?.Info?.Location;
                if (!string.IsNullOrEmpty(location)) { dllPaths[plugin.Key] = location; }
            }
            // Never turned off, whatever the server says: this mod, Jotunn it runs on, and the add-on doing the fix.
            string[] protectedIds = { global::ValheimEnforcer.ValheimEnforcer.PluginGUID, Jotunn.Main.ModGuid, ModSyncApi.AddonGuid };
            return ModSyncPlan.Build(ModManager.ModSettings, offer, dllPaths, Paths.PluginPath, protectedIds);
        }

        /// <summary>A new connection attempt starts with no offer; the next server has to make its own.</summary>
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Awake))]
        public static class ZNet_Awake_ClearOffer {
            [HarmonyPrefix]
            private static void Prefix() {
                ClearOffer();
            }
        }
    }
}
