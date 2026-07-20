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
                    DiscordNotifier.SendAsync(
                        new DiscordEmbed("Server Online", "The server has started and is accepting connections.", Green).ToMessage()
                    );
                }
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
        public static class ZNet_Shutdown_Patch {
            [HarmonyPrefix]
            private static void Prefix(ZNet __instance) {
                if (!__instance.IsServer()) { return; }
                if (ValConfig.DiscordNotifyServerShutdown.Value) {
                    DiscordNotifier.SendSync(
                        new DiscordEmbed("Server Offline", "The server is shutting down.", Grey).ToMessage()
                    );
                }
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
                    DiscordEmbed embed = new DiscordEmbed("Player Joined", null, Green).AddField("Player", peer.m_playerName, true);
                    DiscordNotifier.SendAsync(embed.ToMessage());
                }
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
        public static class ZNet_Disconnect_Patch {
            [HarmonyPrefix]
            private static void Prefix(ZNet __instance, ZNetPeer peer) {
                if (!__instance.IsServer() || peer == null) { return; }
                // Only announce a leave for a peer we announced joining (skips rejected/handshake-failed peers).
                if (!AnnouncedPeers.Remove(peer.m_uid)) { return; }
                if (!ValConfig.DiscordNotifyPlayerLeft.Value) { return; }

                DisconnectionState state = ResolveSavedDataState(peer);
                int deltaWindow = ValConfig.DeltaSynchronizationFrequencyInSeconds.Value;

                bool clean = state == DisconnectionState.Clean;
                string disconnectText = clean ? "Clean logout" : "Disconnected";
                string savedDataText = clean
                    ? "✅ Player Data up to date."
                    : $"⚠️ Stale — Data outdated by {deltaWindow}s";

                DiscordEmbed embed = new DiscordEmbed("Player Left", null, clean ? Green : Amber)
                    .AddField("Player", peer.m_playerName, true)
                    .AddField("Disconnect", disconnectText, true)
                    .AddField("Saved data", savedDataText, false);
               

                DiscordNotifier.SendAsync(embed.ToMessage());
            }
        }

        // Read the players current save state data to give an estimate on if they could have their character rolled back
        private static DisconnectionState ResolveSavedDataState(ZNetPeer peer) {
            try {
                string id = peer.m_socket.GetHostName();
                if (!string.IsNullOrEmpty(id) && id.Contains(":")) { id = id.Split(':')[0]; }
                DataObjects.Character chara = ValConfig.LoadCharacterFromSave(id, peer.m_playerName);
                if (chara == null) {
                    Logger.LogDebug($"Discord notifications: no saved character for {peer.m_playerName} ({id}); reporting saved data as stale.");
                    return DisconnectionState.DirtyDisconnect;
                }
                return chara.LastDisconnect;
            } catch (System.Exception e) {
                Logger.LogDebug($"Discord notifications: failed to resolve saved-data state for {peer.m_playerName}: {e.Message}");
                return DisconnectionState.DirtyDisconnect;
            }
        }
    }
}
