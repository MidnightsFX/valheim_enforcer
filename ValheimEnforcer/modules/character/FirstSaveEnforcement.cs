using HarmonyLib;
using System.Collections.Generic;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.character {

    /// <summary>
    /// Server side: remembers which connected peers had no stored character when they joined, so the first
    /// save each of them uploads can be held to the new-character rules.
    ///
    /// The whole point is *where* that fact comes from. The obvious place to decide "this is a first save"
    /// would be in CharacterStore, where a missing save is right there in front of you - but the key that
    /// lookup uses is the HostID inside the payload, and the payload is written by the client. A modified
    /// client could put someone else's HostID in it and the check would find that person's save, conclude the
    /// character is not new, and skip enforcement entirely (while overwriting the save it just found).
    ///
    /// So the verdict is recorded at connect, by ValConfig.SendSavedCharacter, from the server's own lookup
    /// against the peer that is connecting - before that peer has sent anything at all - and is keyed by peer
    /// uid. Nothing a client says can change it.
    ///
    /// Two categories are excluded by construction rather than by a check:
    ///  - the listen host's own character: the host is not one of its own peers, so SendSavedCharacter never
    ///    runs for it, and its saves go to disk through WritePlayerCharacterToSave without passing here;
    ///  - characters brought in by the ServerCharacters migration: that import is a synchronous ZNet.Start
    ///    postfix, so it has finished writing before any peer can connect, and the connect-time lookup finds
    ///    the files it left.
    /// </summary>
    internal static class FirstSaveEnforcement {

        internal sealed class PendingPeer {
            internal string AccountId;
            internal string CharacterName;
        }

        private static readonly Dictionary<long, PendingPeer> pending = new Dictionary<long, PendingPeer>();

        /// <summary>Server, main thread. The connect-time lookup found nothing for this peer.</summary>
        internal static void MarkNoSaveOnConnect(ZNetPeer peer, string accountId, string characterName) {
            if (peer == null) { return; }
            pending[peer.m_uid] = new PendingPeer { AccountId = accountId, CharacterName = characterName };
            Logger.LogDebug($"First-save enforcement armed for {characterName} ({accountId}).");
        }

        /// <summary>The peer does have a stored character, or has gone away. Either way it is not new.</summary>
        internal static void ClearForPeer(ZNetPeer peer) {
            if (peer == null) { return; }
            pending.Remove(peer.m_uid);
        }

        internal static void ClearAll() {
            pending.Clear();
        }

        /// <summary>
        /// Whether the next full save from this sender should be run through the new-character rules. Only
        /// true when the server's own connect-time lookup found nothing AND an admin has asked for server-side
        /// enforcement AND there is at least one rule to apply.
        /// </summary>
        internal static bool ShouldSanitize(long sender, out PendingPeer info) {
            info = null;
            if (!pending.TryGetValue(sender, out info)) { return false; }
            if (ValConfig.ServerSideNewCharacterEnforcement == null || !ValConfig.ServerSideNewCharacterEnforcement.Value) { return false; }
            return true;
        }

        // A peer's entry outlives its first save on purpose. It is not cleared when a save arrives, because the
        // worker confirms independently that no save exists before it strips anything - so a second upload from
        // the same session lands on a save that now exists and is left alone. Clearing on disconnect is what
        // keeps the dictionary from growing, and stops a recycled uid from inheriting a verdict.
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
        public static class ZNet_Disconnect_ClearFirstSave {
            [HarmonyPrefix]
            private static void Prefix(ZNet __instance, ZNetPeer peer) {
                if (__instance == null || !__instance.IsServer()) { return; }
                ClearForPeer(peer);
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
        public static class ZNet_Shutdown_ClearFirstSave {
            [HarmonyPostfix]
            private static void Postfix(ZNet __instance) {
                if (__instance != null && __instance.IsServer()) { ClearAll(); }
            }
        }
    }
}
