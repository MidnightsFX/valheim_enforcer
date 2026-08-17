using HarmonyLib;
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using ValheimEnforcer.common;
using ValheimEnforcer.modules.notifications;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.modules.character {

    /// <summary>
    /// Applies the one-character-per-account rule at the connect handshake, and gets the reason in front of
    /// the player who was refused.
    ///
    /// Valheim has no free-text rejection channel: the server can only send one of thirteen
    /// <see cref="ZNet.ConnectionStatus"/> values and the client renders a canned localized string for it.
    /// So the reason travels separately over a plain vanilla <see cref="ZRpc"/> method, registered on the
    /// client in OnNewConnection alongside vanilla's own Error/Kicked handlers, and is written into the
    /// vanilla "Connection failed" panel once the client bounces back to the start scene.
    /// </summary>
    internal static class CharacterLimitPatches {

        internal const string RPC_NAME = "VE_CHARLIMIT_MSG";

        // Client side: the reason for the refusal we are currently bouncing back from, held across the scene
        // change into the start menu (where FejdStartup shows the connection-failed panel) and cleared on the
        // next connection attempt so it can never be shown against an unrelated failure.
        private static string PendingRejectReason;

        // ---- Server ---------------------------------------------------------------------------------------

        /// <summary>
        /// Deliberately NOT Priority.First, unlike the mod-mismatch and known-cheater gates: the first prefix
        /// to return false short-circuits the rest, and "your mods are wrong" or "you are a known cheater" is
        /// the more useful thing to tell a player than "wrong character".
        /// </summary>
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_PeerInfo))]
        internal static class ZNet_RPC_PeerInfo_CharacterLimit {

            [HarmonyPrefix]
            private static bool Prefix(ZNet __instance, ZRpc rpc, ZPackage pkg) {
                if (!__instance.IsServer() || !AccountCharacterLimit.Enabled) { return true; }

                string hostId = rpc.GetSocket()?.GetHostName();
                if (string.IsNullOrEmpty(hostId)) { return true; }
                if (!TryPeekPlayerName(pkg, out string playerName)) { return true; }

                string reason = AccountCharacterLimit.EvaluateJoin(hostId, playerName);
                if (reason == null) { return true; }

                Logger.LogWarning($"Refusing '{playerName}' from {hostId}: account character limit reached.");
                NotifyRejection(playerName, hostId, reason);

                // Reason first so it is ahead of the error in the send queue, then flush before anything tears
                // the connection down - same reason FinalSaveRpc flushes.
                rpc.Invoke(RPC_NAME, reason);
                rpc.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorKicked);
                rpc.GetSocket()?.Flush();
                return false; // skip vanilla peer-info handling, exactly as vanilla's own rejections do
            }
        }

        /// <summary>
        /// Reads the character name out of the peer-info package without consuming it, mirroring vanilla's own
        /// read order in ZNet.RPC_PeerInfo. The position is always restored: the original method reads the same
        /// package immediately afterwards and would desync otherwise.
        ///
        /// Returns false - allow the connection - for anything unexpected, because the alternative is refusing
        /// a player over a misread name. A client too old to carry a network version writes a different layout
        /// and is about to be rejected on version anyway.
        ///
        /// The network version check is the guard against a future Valheim changing this package's layout under
        /// us. Version.m_networkVersion is a const, so it is baked in at compile time rather than read from the
        /// game at runtime - that is deliberate and is the whole point: if the wire format changes the number
        /// changes with it, our copy no longer matches, and the rule quietly stops enforcing until the mod is
        /// rebuilt against the new game. Reading the game's live value would defeat this and hand our old
        /// parsing code a new layout.
        /// </summary>
        private static bool TryPeekPlayerName(ZPackage pkg, out string playerName) {
            playerName = null;
            if (pkg == null) { return false; }

            int position = pkg.GetPos();
            try {
                pkg.ReadLong();                                     // session uid
                string versionString = pkg.ReadString();
                if (!GameVersion.TryParseGameVersion(versionString, out GameVersion version)
                    || version < Version.FirstVersionWithNetworkVersion) {
                    return false;
                }
                if (pkg.ReadUInt() != Version.m_networkVersion) {
                    return false;                                   // let vanilla report the version mismatch
                }
                pkg.ReadVector3();                                  // reference position
                playerName = pkg.ReadString();
                return !string.IsNullOrEmpty(playerName);
            } catch (Exception e) {
                Logger.LogWarning($"Character limit: could not read the character name from the peer info package ({e.Message}). Allowing the connection.");
                playerName = null;
                return false;
            } finally {
                pkg.SetPos(position);
            }
        }

        /// <summary>
        /// <paramref name="reason"/> is the same sentence the player is shown, naming the character they should
        /// come back as. No built-in template prints it - keeping the shipped message identical to what this
        /// mod always sent - but it is offered as {reason} for admins who want the detail in their channel.
        /// </summary>
        private static void NotifyRejection(string playerName, string hostId, string reason) {
            if (!ValConfig.DiscordNotifyCharacterRejected.Value) { return; }
            DiscordNotifier.Notify(NotificationEvent.CharacterRejected, new Dictionary<string, string> {
                { "character", playerName },
                { "playerId", hostId },
                { "reason", reason ?? "" },
                { "maxCharacters", ValConfig.MaxCharactersPerAccount.Value.ToString() },
            });
        }

        // ---- Client ---------------------------------------------------------------------------------------

        /// <summary>
        /// Client side: register the reason receiver on the server peer. Vanilla registers the client's own
        /// Error and Kicked handlers here, so this is the seam where a client-side handler belongs; the
        /// server-side twin of this is FinalSaveRpc.ZNet_OnNewConnection_RegisterFinalSave.
        /// </summary>
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.OnNewConnection))]
        internal static class ZNet_OnNewConnection_RegisterCharacterLimitReason {
            [HarmonyPostfix]
            private static void Postfix(ZNet __instance, ZNetPeer peer) {
                if (__instance == null || __instance.IsServer() || peer == null) { return; }
                PendingRejectReason = null; // a fresh attempt never inherits the previous one's reason
                peer.m_rpc.Register<string>(RPC_NAME, new Action<ZRpc, string>(RPC_CharacterLimitReason));
            }
        }

        private static void RPC_CharacterLimitReason(ZRpc rpc, string reason) {
            if (string.IsNullOrEmpty(reason)) { return; }
            Logger.LogInfo($"Server refused this character: {reason}");
            PendingRejectReason = reason;
            // Also feed Jotunn's compatibility window, in case something else causes it to be the dialog shown.
            ModManager.DetailsUpdater?.UpdateErrorText(reason, "");
        }

        /// <summary>
        /// Client side: replace the canned error text with the server's reason. ShowConnectError is private
        /// with one optional argument, hence the explicit signature. It runs from FejdStartup.Start(), i.e.
        /// once the client is back on the start scene after the failed connect.
        /// </summary>
        [HarmonyPatch(typeof(FejdStartup), "ShowConnectError", new Type[] { typeof(ZNet.ConnectionStatus) })]
        internal static class FejdStartup_ShowConnectError_CharacterLimitReason {
            [HarmonyPostfix]
            private static void Postfix(FejdStartup __instance) {
                if (string.IsNullOrEmpty(PendingRejectReason) || __instance == null) { return; }

                // Only speak when the connection actually failed. Leave the reason pending otherwise rather
                // than clearing it, so an early no-op call cannot swallow the message before it is shown.
                ZNet.ConnectionStatus status = ZNet.GetConnectionStatus();
                if (status == ZNet.ConnectionStatus.None
                    || status == ZNet.ConnectionStatus.Connecting
                    || status == ZNet.ConnectionStatus.Connected) {
                    return;
                }

                string reason = PendingRejectReason;
                PendingRejectReason = null;

                GameObject panel = __instance.m_connectionFailedPanel;
                TMP_Text label = __instance.m_connectionFailedError;
                if (label == null) {
                    Logger.LogDebug("Character limit: no connection failed label to write the rejection reason to.");
                    return;
                }
                if (panel != null) { panel.SetActive(true); }
                label.text = reason;
            }
        }
    }
}
