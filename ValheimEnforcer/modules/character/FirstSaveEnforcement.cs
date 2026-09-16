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

        // Guards both dictionaries. Almost all access is on the main thread (SendSavedCharacter,
        // PersistReceivedCharacterYaml), but ClearReturning is called from the CharacterStore worker thread
        // once it has reconciled a returning save, so the two must not be touched without the lock.
        private static readonly object gate = new object();
        // Peers that connected with NO stored character - the first save each uploads is held to the
        // new-character rules.
        private static readonly Dictionary<long, PendingPeer> pending = new Dictionary<long, PendingPeer>();
        // Peers that connected WITH a stored character - the first save each uploads is reconciled against it,
        // once per session (the entry is dropped after the first save reconciles it).
        private static readonly Dictionary<long, PendingPeer> returning = new Dictionary<long, PendingPeer>();

        /// <summary>Server, main thread. The connect-time lookup found nothing for this peer.</summary>
        internal static void MarkNoSaveOnConnect(ZNetPeer peer, string accountId, string characterName) {
            if (peer == null) { return; }
            lock (gate) {
                pending[peer.m_uid] = new PendingPeer { AccountId = accountId, CharacterName = characterName };
                returning.Remove(peer.m_uid);
            }
            Logger.LogDebug($"First-save enforcement armed for {characterName} ({accountId}).");
        }

        /// <summary>Server, main thread. The connect-time lookup DID find a stored character for this peer, so
        /// the first full save of the session is reconciled against it rather than trusted outright.</summary>
        internal static void MarkHasSaveOnConnect(ZNetPeer peer, string accountId, string characterName) {
            if (peer == null) { return; }
            lock (gate) {
                returning[peer.m_uid] = new PendingPeer { AccountId = accountId, CharacterName = characterName };
                pending.Remove(peer.m_uid);
            }
            Logger.LogDebug($"Returning-character enforcement armed for {characterName} ({accountId}).");
        }

        /// <summary>The peer has gone away (disconnect). Drop it from both tracks.</summary>
        internal static void ClearForPeer(ZNetPeer peer) {
            if (peer == null) { return; }
            lock (gate) {
                pending.Remove(peer.m_uid);
                returning.Remove(peer.m_uid);
            }
        }

        /// <summary>The returning save for this peer has been reconciled; do not reconcile it again this
        /// session. Safe to call from the CharacterStore worker thread.</summary>
        internal static void ClearReturning(long sender) {
            lock (gate) { returning.Remove(sender); }
        }

        internal static void ClearAll() {
            lock (gate) {
                pending.Clear();
                returning.Clear();
            }
        }

        /// <summary>How many peers are armed on each track, for enforcer-memory.</summary>
        internal static void Counts(out int armedNew, out int armedReturning) {
            lock (gate) {
                armedNew = pending.Count;
                armedReturning = returning.Count;
            }
        }

        /// <summary>
        /// Whether the next full save from this sender should be run through the new-character rules. Only
        /// true when the server's own connect-time lookup found nothing AND an admin has asked for server-side
        /// enforcement AND there is at least one rule to apply.
        /// </summary>
        internal static bool ShouldSanitize(long sender, out PendingPeer info) {
            lock (gate) {
                return pending.TryGetValue(sender, out info);
            }
        }

        /// <summary>Whether the first full save from this sender should be reconciled against the stored
        /// character it connected with.</summary>
        internal static bool ShouldReconcileReturning(long sender) {
            lock (gate) {
                return returning.ContainsKey(sender);
            }
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
