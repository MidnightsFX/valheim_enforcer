using HarmonyLib;
using JetBrains.Annotations;
using System.Collections.Generic;
using ValheimEnforcer.common;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.modules.notifications {
    internal static class NotificationPatches {

        // Track players to avoid double announcements
        private static readonly HashSet<long> AnnouncedPeers = new HashSet<long>();

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Start))]
        public static class ZNet_Start_Patch {
            [HarmonyPostfix]
            private static void Postfix(ZNet __instance) {
                if (!__instance.IsServer()) { return; }
                AnnouncedPeers.Clear();
                DiscordNotifier.Initialize();
                if (ValConfig.DiscordNotifyServerStartup.Value) {
                    DiscordNotifier.Notify(NotificationEvent.ServerStartup);
                }
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
        public static class ZNet_Shutdown_Patch {
            [HarmonyPrefix]
            private static void Prefix(ZNet __instance) {
                if (!__instance.IsServer()) { return; }
                if (ValConfig.DiscordNotifyServerShutdown.Value) {
                    DiscordNotifier.NotifySync(NotificationEvent.ServerShutdown);
                }
            }
        }

        /// <summary>
        /// Covers both the periodic autosave and a manual 'save' from the console: RPC_Save routes through the
        /// same method. Also fires on the save a normal shutdown performs.
        ///
        /// SaveWorld rather than the public Save it hangs off, because Save returns early - without saving
        /// anything - on a load error or when the zone system asks to skip, and a postfix there would announce
        /// a save that Valheim had just logged as skipped.
        ///
        /// The message means the save *started*: an async save hands the write to a background thread, and this
        /// is the last point a patch can speak from without polling that thread. It is also what an admin
        /// watching for "did the autosave tick run at all" is looking for.
        /// </summary>
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.SaveWorld))]
        public static class ZNet_SaveWorld_Patch {
            [HarmonyPostfix]
            private static void Postfix(ZNet __instance) {
                if (!__instance.IsServer()) { return; }
                if (!ValConfig.DiscordNotifyWorldSaved.Value) { return; }
                DiscordNotifier.Notify(NotificationEvent.WorldSaved);
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_PeerInfo))]
        public static class ZNet_RPC_PeerInfo_Patch {
            [HarmonyPostfix]
            private static void Postfix(ZNet __instance, ZRpc rpc) {
                if (!__instance.IsServer()) { return; }

                ZNetPeer peer = __instance.GetPeer(rpc);
                // A rejected handshake returns before the player name is assigned, so a non-empty name means the
                // peer was accepted at the ZNet layer.
                if (peer == null || string.IsNullOrEmpty(peer.m_playerName)) { return; }
                if (!AnnouncedPeers.Add(peer.m_uid)) { return; }

                if (ValConfig.DiscordNotifyPlayerJoined.Value) {
                    string hostId = ResolveHostId(peer);
                    DiscordNotifier.Notify(NotificationEvent.PlayerJoined, new Dictionary<string, string> {
                        { "player", peer.m_playerName },
                        { "playerId", hostId },
                        { "isAdmin", IsAdmin(__instance, hostId) ? "yes" : "no" },
                    });
                }
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
        public static class ZNet_Disconnect_Patch {
            [HarmonyPrefix]
            private static void Prefix(ZNet __instance, ZNetPeer peer) {
                if (!__instance.IsServer() || peer == null) { return; }
                // Resolved here rather than beside the message it feeds, and ahead of both early returns: the
                // ledger entry has to be dropped whether or not this disconnect ends up announced, or a server
                // with the notification off never drains it. See FinalSaveRpc.ConsumeCleanLogout, which also
                // records why this is not read off the player's save file any more.
                bool clean = character.FinalSaveRpc.ConsumeCleanLogout(peer.m_uid);

                // Only announce a leave for a peer we announced joining (skips rejected/handshake-failed peers).
                if (!AnnouncedPeers.Remove(peer.m_uid)) { return; }
                if (!ValConfig.DiscordNotifyPlayerLeft.Value) { return; }

                int deltaWindow = ValConfig.DeltaSynchronizationFrequencyInSeconds.Value;
                string disconnectText = clean ? "Clean logout" : "Disconnected";
                string savedDataText = clean
                    ? "✅ Player Data up to date."
                    : $"⚠️ Stale — Data outdated by {deltaWindow}s";

                DiscordNotifier.Notify(NotificationEvent.PlayerLeft, new Dictionary<string, string> {
                    { "player", peer.m_playerName },
                    { "playerId", ResolveHostId(peer) },
                    { "disconnect", disconnectText },
                    { "savedData", savedDataText },
                    { "deltaWindow", deltaWindow.ToString() },
                    // The default template uses this as its colour, which is how one template keeps the
                    // green-on-clean / amber-on-dirty split the hard-coded embed had. It has to be the decimal
                    // Discord wants, not the name of the colour: the template writes it unquoted, so a word
                    // here renders `"color": Amber` and Discord refuses the whole document.
                    { "statusColor", (clean ? Green : Amber).ToString() },
                });
            }
        }

        /// <summary>
        /// The account id behind a peer, with the port stripped off the socket's host name. Returns an empty
        /// string when it cannot be read, which makes the field carrying it drop out of the message rather
        /// than printing something misleading.
        /// </summary>
        private static string ResolveHostId(ZNetPeer peer) {
            try {
                string id = peer.m_socket?.GetHostName();
                if (string.IsNullOrEmpty(id)) { return ""; }
                if (id.Contains(":")) { id = id.Split(':')[0]; }
                return id;
            } catch (System.Exception e) {
                Logger.LogDebug($"Discord notifications: could not read the host id for {peer.m_playerName}: {e.Message}");
                return "";
            }
        }

        private static bool IsAdmin(ZNet znet, string hostId) {
            if (string.IsNullOrEmpty(hostId)) { return false; }
            try {
                return znet.IsAdmin(hostId);
            } catch (System.Exception e) {
                Logger.LogDebug($"Discord notifications: could not read admin status for {hostId}: {e.Message}");
                return false;
            }
        }
    }
}
