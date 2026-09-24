using System;
using System.Collections.Generic;
using System.IO;
using ValheimEnforcer.common;
using ValheimEnforcer.modules.character;

namespace ValheimEnforcer.modules.recovery {

    /// <summary>
    /// Emergency crash recovery, both ends.
    ///
    /// Server side: every few minutes each connected player is sent a sealed snapshot of their own character
    /// - opaque to them, and useless anywhere else. If this server then crashes and comes back from an older
    /// save, the window <see cref="CrashMarker"/> opens is its chance to ask for those snapshots back and
    /// adopt the ones that verify and are newer than what it holds.
    ///
    /// Client side: it stores what it is given and hands it back when asked. That is the whole of its job. It
    /// cannot read a snapshot, cannot make one, and cannot decide that one should be restored.
    ///
    /// What this does NOT protect against, and the README says so too: a lost or corrupted LOCAL character
    /// file. The client cannot open its own snapshot, so there is nothing in it for the client. Progression
    /// sync is what covers that direction.
    ///
    /// And the thing to understand before turning it on: the world and the character store are separate
    /// saves that roll back independently. Restoring a character to a state newer than the world can
    /// re-introduce items whose source in the world - the chest they came out of, the vein they were mined
    /// from - has itself rolled back. That is duplication, and it is inherent to putting a character back,
    /// not a defect in how it is done. It is why this is off by default and why the window is narrow.
    /// </summary>
    internal static class RecoveryManager {

        internal const string RecoveryFolder = "Recovery";
        internal const string BlobExtension = ".vebak";

        /// <summary>A sealed character snapshot is a compressed character save; a large modded one is
        /// generously bounded by this.</summary>
        internal const int MaxRecoveryPayloadBytes = 4 * 1024 * 1024;

        internal static bool Enabled {
            get { return ValConfig.EnableCrashRecovery != null && ValConfig.EnableCrashRecovery.Value; }
        }

        private static bool ServerSide {
            get { return Enabled && ZNet.instance != null && ZNet.instance.IsServer(); }
        }

        // ---------------------------------------------------------------------------------------------
        // Server: pushing snapshots out
        // ---------------------------------------------------------------------------------------------

        private static float nextPush;
        private static readonly HashSet<long> askedThisWindow = new HashSet<long>();

        /// <summary>Main thread, server side, every frame. Cheap unless something is due.</summary>
        internal static void Tick() {
            if (!ServerSide) { return; }
            if (!RecoveryKey.EnsureLoaded()) { return; }

            AskForSnapshots();

            float now = UnityEngine.Time.unscaledTime;
            if (now < nextPush) { return; }
            int minutes = ValConfig.CrashRecoveryPushIntervalMinutes != null ? ValConfig.CrashRecoveryPushIntervalMinutes.Value : 10;
            nextPush = now + Math.Max(1, minutes) * 60f;
            PushSnapshots();
        }

        // While the window is open, every ready peer is asked once for whatever it is holding. Tracked per
        // peer rather than broadcast repeatedly: a snapshot is a compressed character save, and asking the
        // same player for it every tick would be a self-inflicted upload storm at the worst possible moment.
        private static void AskForSnapshots() {
            if (!CrashMarker.WindowOpen) {
                if (askedThisWindow.Count > 0) { askedThisWindow.Clear(); }
                return;
            }
            foreach (ZNetPeer peer in ZNet.instance.GetPeers()) {
                if (peer == null || !peer.IsReady()) { continue; }
                if (!askedThisWindow.Add(peer.m_uid)) { continue; }
                ValConfig.RecoveryRPC.SendPackage(peer.m_uid, ValConfig.RecoveryPayload(null, ValConfig.RecoveryPayloadWant));
                Logger.LogInfo($"Asked {peer.m_playerName} for the recovery snapshot they are holding.");
            }
        }

        private static void PushSnapshots() {
            RecoverySeal.Keys keys = RecoveryKey.Current;
            if (keys == null) { return; }
            long worldUid = ZNet.instance.GetWorldUID();
            if (worldUid == 0L) { return; }

            int queued = 0;
            foreach (ZNetPeer peer in ZNet.instance.GetPeers()) {
                if (peer == null || !peer.IsReady()) { continue; }
                string account = PeerIdentity.AccountFor(peer);
                string name = peer.m_playerName;
                if (string.IsNullOrEmpty(account) || string.IsNullOrEmpty(name)) { continue; }
                if (!PeerIdentity.IsSafeToken(account) || !PeerIdentity.IsSafeToken(name)) { continue; }
                if (!CharacterSaves.TryResolveSave(account, name, out string resolvedAccount, out string resolvedName, out _)) {
                    continue; // nothing stored for them yet; nothing to seal
                }
                // Sealed on the store's worker, not here. Reading a character means a disk read and a YAML
                // parse of a few hundred kilobytes, and gzip and AES on top of it - all of which the worker
                // already owns the character for, and none of which belongs on a frame.
                CharacterStore.SubmitSeal(resolvedAccount, resolvedName, peer.m_uid, keys, worldUid);
                queued++;
            }
            if (queued > 0) { Logger.LogDebug($"Queued {queued} recovery snapshot(s) to be sealed."); }
        }

        /// <summary>Main thread. Sends snapshots the worker has finished sealing. Drained beside the drift
        /// resyncs and sanitized pushes, for the same reason: the worker must not touch ZNet.</summary>
        internal static void DrainSealed() {
            CharacterStore.SealedPush push;
            while ((push = CharacterStore.TryDequeueSealedPush()) != null) {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) { continue; }
                if (ZNet.instance.GetPeer(push.Sender) == null) { continue; } // they left while it was sealing
                ValConfig.RecoveryRPC.SendPackage(push.Sender, ValConfig.RecoveryPayload(push.Blob, ValConfig.RecoveryPayloadBlob));
                Logger.LogDebug($"Sent {push.Name} a sealed recovery snapshot ({push.Blob.Length} bytes).");
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Server: taking one back
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// A client has handed back a snapshot. Everything cheap is decided here; the expensive half -
        /// verifying, unsealing, parsing and comparing against the stored save - goes to the worker.
        ///
        /// Order matters and is the same as everywhere else in this mod: size, then identity, then content.
        /// The header is read without being trusted, purely to answer "is this even about the peer that sent
        /// it" before anything is spent on it.
        /// </summary>
        internal static void OnOffer(long sender, byte[] blob) {
            if (!ServerSide) { return; }
            if (blob == null || blob.Length == 0) { return; }
            RecoverySeal.Keys keys = RecoveryKey.Current;
            if (keys == null) { return; }

            if (!CrashMarker.WindowOpen) {
                Logger.LogInfo($"Ignoring a recovery snapshot offered by {sender}: the recovery window is closed.");
                return;
            }
            if (ValConfig.CrashRecoveryAutoRestore != null && !ValConfig.CrashRecoveryAutoRestore.Value) {
                Logger.LogInfo($"Ignoring a recovery snapshot offered by {sender}: CrashRecoveryAutoRestore is off.");
                return;
            }
            // A snapshot this player is still holding was sealed before the server stopped tracking them, which
            // makes it exactly as stale as the save the catch-up is replacing - adopting it would undo the catch-up.
            if (FirstSaveEnforcement.IsCatchUp(sender)) {
                Logger.LogInfo($"Ignoring a recovery snapshot offered by {sender}: their character is being caught up under CatchupOverwriteOnJoin.");
                return;
            }

            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            string account = PeerIdentity.AccountFor(peer);
            string name = peer?.m_playerName;
            if (string.IsNullOrEmpty(account) || string.IsNullOrEmpty(name)) {
                Logger.LogWarning($"Refusing a recovery snapshot from {sender}: the server could not resolve who they are.");
                return;
            }
            if (!PeerIdentity.IsSafeToken(account) || !PeerIdentity.IsSafeToken(name)) { return; }

            if (!RecoverySeal.TryPeek(blob, out RecoverySeal.Header header)) {
                Logger.LogWarning($"Refusing a recovery snapshot from {name}: it is not in a format this server wrote.");
                return;
            }
            // Read from the header, which is unauthenticated - so this is a filter, not a decision. The
            // authenticated versions of both are re-checked on the worker after the tag verifies.
            if (!PlatformIds.Matches(account, header.AccountId)
                || !string.Equals(name, header.CharacterName, StringComparison.OrdinalIgnoreCase)) {
                Logger.LogWarning($"Refusing a recovery snapshot from {name} ({account}): it claims to be for {header.CharacterName} ({header.AccountId}).");
                return;
            }
            long worldUid = ZNet.instance.GetWorldUID();
            if (header.WorldUid != worldUid) {
                Logger.LogWarning($"Refusing a recovery snapshot from {name}: it was sealed for a different world.");
                return;
            }

            if (!CharacterSaves.TryResolveSave(account, name, out string resolvedAccount, out string resolvedName, out bool lookupFailed)) {
                if (lookupFailed) {
                    Logger.LogWarning($"Not applying a recovery snapshot for {name}: the character store could not be read.");
                    return;
                }
                // No save at all. That is exactly what a rollback far enough back looks like, so the snapshot
                // is still worth taking - the worker treats a missing save as sequence zero.
                resolvedAccount = account;
                resolvedName = name;
            }

            CharacterStore.SubmitRecoveryRestore(resolvedAccount, resolvedName, blob, keys, worldUid, sender);
        }

        // ---------------------------------------------------------------------------------------------
        // Client: holding one
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Where a client keeps what it has been given: one folder per world, one file per character.
        ///
        /// Keyed by world rather than by server, because the world id is the one thing the client can name
        /// without being told - and it is also what the server checks the snapshot against on the way back.
        /// </summary>
        private static string ClientFolder(long worldUid) {
            return Path.Combine(ValConfig.GetSecondaryConfigDirectoryPath(), RecoveryFolder, worldUid.ToString("x16"));
        }

        /// <summary>Client. Stores a snapshot the server sent, and drops the oldest past the keep count.</summary>
        internal static void Store(byte[] blob) {
            if (!Enabled || blob == null || blob.Length == 0) { return; }
            if (ZNet.instance == null) { return; }
            if (!RecoverySeal.TryPeek(blob, out RecoverySeal.Header header)) {
                Logger.LogWarning("Ignoring a recovery snapshot from the server: it is not in a format this build reads.");
                return;
            }
            // Filed under the character name the SERVER put in the header, not the one this client believes
            // it is playing - the two are the same on an honest connection, and where they differ the
            // server's is the one the snapshot will be checked against.
            if (!PeerIdentity.IsSafeToken(header.CharacterName)) { return; }

            try {
                string folder = ClientFolder(header.WorldUid);
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, header.CharacterName + BlobExtension);
                AtomicFile.WriteBytes(path, blob);
                PruneOld(folder);
                Logger.LogDebug($"Stored a recovery snapshot from the server ({blob.Length} bytes, sequence {header.Sequence}).");
            } catch (Exception e) {
                Logger.LogWarning($"Could not store the recovery snapshot the server sent: {e.Message}");
            }
        }

        // One file per character, so the keep count bounds how many CHARACTERS are remembered for a world
        // rather than how many copies of one. A snapshot is only ever replaced by a newer one.
        private static void PruneOld(string folder) {
            int keep = ValConfig.CrashRecoveryKeepBlobs != null ? ValConfig.CrashRecoveryKeepBlobs.Value : 3;
            string[] files = Directory.GetFiles(folder, "*" + BlobExtension, SearchOption.TopDirectoryOnly);
            if (files.Length <= Math.Max(1, keep)) { return; }
            Array.Sort(files, (a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
            for (int i = Math.Max(1, keep); i < files.Length; i++) {
                try {
                    File.Delete(files[i]);
                } catch (Exception e) {
                    Logger.LogDebug($"Could not remove an old recovery snapshot: {e.Message}");
                }
            }
        }

        /// <summary>Client. The server has asked for what we hold; send it if there is one.</summary>
        internal static void Offer() {
            if (!Enabled) { return; }
            ZNetPeer serverPeer = ZNet.instance?.GetServerPeer();
            if (serverPeer == null) { return; }
            long worldUid = ZNet.instance.GetWorldUID();
            string name = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : null;
            if (string.IsNullOrEmpty(name) || !PeerIdentity.IsSafeToken(name)) { return; }

            try {
                string path = Path.Combine(ClientFolder(worldUid), name + BlobExtension);
                if (!File.Exists(path)) {
                    Logger.LogDebug("The server asked for a recovery snapshot; this machine holds none for this world.");
                    return;
                }
                byte[] blob = File.ReadAllBytes(path);
                if (blob.Length == 0 || blob.Length > MaxRecoveryPayloadBytes) { return; }
                ValConfig.RecoveryOfferRPC.SendPackage(serverPeer.m_uid, ValConfig.RecoveryOfferPayload(blob));
                Logger.LogInfo($"Handed the server back the recovery snapshot it left here ({blob.Length} bytes). The server decides whether to use it.");
            } catch (Exception e) {
                Logger.LogWarning($"Could not hand back a recovery snapshot: {e.Message}");
            }
        }
    }
}
