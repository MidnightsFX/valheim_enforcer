using HarmonyLib;
using System;
using System.IO;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.character {

    /// <summary>
    /// Server-stored map exploration: what a character has uncovered on this world, what a cartography table
    /// revealed to them, and their saved pins.
    ///
    /// Distinct from <see cref="MapExploration"/>, which only ever wipes. That exists because without a
    /// server-side copy there is nothing to check a map against, so the only enforcement available is to
    /// remove it; this is the copy, and with it a map uncovered somewhere else is simply replaced by what
    /// this server has seen, and a player whose character file is lost gets their exploration back.
    ///
    /// Why it has a channel and a file of its own rather than living on the character record:
    ///
    ///  - Size. Vanilla's blob is a version int followed by a gzipped package holding two bytes per map
    ///    pixel (explored, and explored-by-others) at 2048x2048 - 8.4 MB before compression. Compressed it is
    ///    typically tens of kilobytes and can reach a megabyte on a fragmented map. The character payload
    ///    ceiling is 8 MB, and base64 inside YAML would inflate it by a third on top.
    ///  - Shape. It is opaque bytes vanilla already compressed. Round-tripping it through YAML buys nothing.
    ///  - Storage mode. InternalStorageMode puts the character record in a ZDO string inside the world file,
    ///    which is no place for a megabyte per player.
    ///
    /// Cost on the player's machine is the reason for the cadence. Producing the blob means rebuilding and
    /// gzipping that 8.4 MB package on the main thread, so it happens at most once every
    /// MapSyncIntervalMinutes, once more at logout, and not at all when nothing has been explored since the
    /// last upload - which the hash answers without rebuilding anything the second time.
    /// </summary>
    internal static class MapSync {

        /// <summary>The oldest map format vanilla still reads. The version is the first int of the blob, and
        /// it is read only to refuse a payload that is not a map at all - a NEWER version than this client
        /// knows is left to vanilla, which has the back-compat branches for it.</summary>
        private static readonly int OldestReadableMapVersion = (int)Version.Map.Pins;

        /// <summary>The version at which vanilla started gzipping the inner package.</summary>
        private static readonly int FirstCompressedMapVersion = (int)Version.Map.Compressed;

        /// <summary>The file extension the blob is stored under, beside the character's .yaml.</summary>
        internal const string MapFileExtension = ".map";

        internal static bool Enabled {
            get {
                return ValConfig.EnableProgressionSync != null && ValConfig.EnableProgressionSync.Value
                       && ValConfig.SyncMapExploration != null && ValConfig.SyncMapExploration.Value;
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Client: capture
        // ---------------------------------------------------------------------------------------------

        // Hash of the blob this session last sent, and the session it belongs to. Both reset together: a hash
        // carried across a disconnect would have the next session skip its first upload against a server that
        // may hold something entirely different.
        private static string lastSentHash;
        private static int lastSentGeneration = -1;
        private static float nextUploadTime;

        /// <summary>
        /// While this is in the future, an upload is held back because the server's own copy is still on its
        /// way and has not been adopted yet.
        ///
        /// Without it there is a real race, just a narrow one. The join asks for the stored map and the
        /// answer arrives some frames later; a full-sync pull landing inside that window would upload the
        /// LOCAL map - the one that has not been reconciled with anything - and the server would store it as
        /// authoritative, which is the exact overwrite this feature exists to prevent.
        ///
        /// Bounded rather than open-ended, because an answer is not guaranteed: a server running an older
        /// build, or with the feature off, never replies at all, and a client that waited forever would
        /// simply never sync its map.
        /// </summary>
        private static float awaitingAnswerUntil;

        /// <summary>How long a client holds its upload waiting for the server's copy.</summary>
        private const float AnswerGraceSeconds = 30f;

        /// <summary>The session ended, or a new one began. Forget what was sent.</summary>
        internal static void ResetSession() {
            lastSentHash = null;
            lastSentGeneration = -1;
            nextUploadTime = 0f;
            awaitingAnswerUntil = 0f;
        }

        /// <summary>
        /// The server answered a request - with a map, or with "none held". Either way there is nothing left
        /// to wait for.
        ///
        /// "None held" deliberately does NOT wipe the local map. A server that has just had this switched on
        /// holds nothing for anybody, and treating that as "you have explored nothing" would erase every
        /// connected player's map at once. Wiping a new character's map is a separate, narrower decision that
        /// NewCharacterResetMapExploration already owns.
        /// </summary>
        internal static void NoteServerAnswered() {
            awaitingAnswerUntil = 0f;
        }

        /// <summary>
        /// Builds the blob if it is worth building, and returns it with its hash. Null when the feature is
        /// off, when there is no minimap yet, when the cadence has not come round, or when nothing has been
        /// explored since the last upload.
        ///
        /// <paramref name="force"/> skips only the cadence, never the unchanged check - a logout should push
        /// what the session actually uncovered, not re-send an identical eight-megabyte package.
        /// </summary>
        internal static byte[] CaptureIfChanged(bool force, out string hash) {
            hash = null;
            if (!Enabled) { return null; }
            if (Minimap.instance == null || !Minimap.instance.m_hasGenerated) { return null; }
            if (Game.instance == null) { return null; }
            // The profile keys its map by world UID and silently no-ops on 0, so there is nothing to read
            // before ZNet has a world.
            if (ZNet.instance == null) { return null; }

            if (CharacterManager.SessionGeneration != lastSentGeneration) { ResetSession(); }

            float now = UnityEngine.Time.unscaledTime;
            if (!force && now < nextUploadTime) { return null; }

            byte[] blob;
            StallWatch watch = StallWatch.Start("mapsync.capture");
            try {
                // SaveMapData is the only thing that pushes the live minimap into the profile - vanilla calls
                // it from Game.SavePlayerProfile and nowhere else - so without it this reads whatever the map
                // looked like at the last profile save.
                Minimap.instance.SaveMapData();
                blob = Game.instance.GetPlayerProfile().GetMapData();
            } catch (Exception e) {
                Logger.LogWarning($"Could not read this character's map to send to the server: {e.Message}");
                return null;
            } finally {
                watch.Stop();
            }

            if (blob == null || blob.Length == 0) { return null; }
            if (blob.Length > ValConfig.MaxMapPayloadBytes) {
                // Refuse here rather than have the server drop it: this is the one side that can say why.
                Logger.LogWarning($"Not sending this character's map: it is {blob.Length} bytes, past the {ValConfig.MaxMapPayloadBytes} byte limit.");
                return null;
            }

            string digest = Hash(blob);
            // Scheduled from the attempt rather than from the send, so an unchanged map does not have the
            // cadence re-evaluated every frame.
            nextUploadTime = now + IntervalSeconds();
            lastSentGeneration = CharacterManager.SessionGeneration;
            if (digest == lastSentHash) { return null; }

            hash = digest;
            return blob;
        }

        /// <summary>Records that a blob reached the server, so an identical one is not sent again.</summary>
        internal static void MarkSent(string hash) {
            lastSentHash = hash;
            lastSentGeneration = CharacterManager.SessionGeneration;
        }

        private static float IntervalSeconds() {
            int minutes = ValConfig.MapSyncIntervalMinutes != null ? ValConfig.MapSyncIntervalMinutes.Value : 15;
            return Math.Max(1, minutes) * 60f;
        }

        // ---------------------------------------------------------------------------------------------
        // Client: send and request
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Uploads this character map if there is anything new to upload. Safe to call often - the cadence
        /// and the unchanged check are both inside <see cref="CaptureIfChanged"/>, so a call that has nothing
        /// to do costs a hash at most, and usually not even that.
        /// </summary>
        internal static void SendToServer(bool force) {
            if (!Enabled) { return; }
            // Singleplayer and a listen host own the store already; the profile on this disk IS the copy.
            if (CharacterManager.ThisMachineIsAuthority()) { return; }
            ZNetPeer serverPeer = ZNet.instance?.GetServerPeer();
            if (serverPeer == null) { return; }
            // Nothing this session has been validated yet, so there is no basis for calling our map the one
            // the server should hold - the same gate SavePlayerCharacter applies for the same reason.
            if (!CharacterManager.JoinValidationComplete) { return; }
            if (UnityEngine.Time.unscaledTime < awaitingAnswerUntil) {
                Logger.LogDebug("Holding this character map back: the server's own copy has been asked for and has not arrived yet.");
                return;
            }

            byte[] blob = CaptureIfChanged(force, out string hash);
            if (blob == null) { return; }

            ValConfig.MapSyncRPC.SendPackage(serverPeer.m_uid, ValConfig.MapPayload(blob, hash, ValConfig.MapPayloadMap));
            MarkSent(hash);
            Logger.LogDebug($"Sent this character map to the server ({blob.Length} bytes).");
        }

        /// <summary>Asks the server for its copy. Answered with a blob, or with an explicit "none held".</summary>
        internal static void RequestFromServer() {
            if (!Enabled) { return; }
            if (CharacterManager.ThisMachineIsAuthority()) { return; }
            ZNetPeer serverPeer = ZNet.instance?.GetServerPeer();
            if (serverPeer == null) { return; }
            ValConfig.MapRequestRPC.SendPackage(serverPeer.m_uid, new ZPackage());
            awaitingAnswerUntil = UnityEngine.Time.unscaledTime + AnswerGraceSeconds;
            Logger.LogDebug("Asked the server for the map it holds for this character.");
        }

        // ---------------------------------------------------------------------------------------------
        // Client: apply
        // ---------------------------------------------------------------------------------------------

        // A blob that arrived before the minimap had read the profile, waiting for it to. Vanilla loads the
        // map lazily, on the minimap's first Update after the world generator exists, and that load would
        // quietly undo anything written into the profile beforehand. Same problem, and the same answer, as
        // MapExploration's pendingFor.
        private static byte[] pendingBlob;
        private static int pendingGeneration = -1;

        /// <summary>The session ended; a blob still waiting belongs to it and must not land on the next one.</summary>
        internal static void CancelPending() {
            pendingBlob = null;
            pendingGeneration = -1;
        }

        /// <summary>
        /// Adopts the server's copy of this character's map. Applied now if the minimap has read the profile,
        /// otherwise as soon as it does.
        /// </summary>
        internal static void ApplyFromServer(byte[] blob) {
            if (!Enabled || blob == null || blob.Length == 0) { return; }
            if (Game.instance == null || ZNet.instance == null) { return; }
            if (!IsReadableMap(blob, out string problem)) {
                Logger.LogWarning($"Refusing the map the server sent: {problem}");
                return;
            }

            if (Minimap.instance == null || !Minimap.instance.m_hasGenerated) {
                Logger.LogDebug("The map has not loaded yet; applying the server's copy once it does.");
                pendingBlob = blob;
                pendingGeneration = CharacterManager.SessionGeneration;
                return;
            }
            Adopt(blob);
        }

        private static void Adopt(byte[] blob) {
            pendingBlob = null;
            pendingGeneration = -1;
            PlayerProfile profile = Game.instance.GetPlayerProfile();
            // Kept so a failed load can put back what was there. The profile is written to before the minimap
            // reads it - it has to be, because the minimap reads the map FROM the profile - which means a
            // throw partway through would otherwise leave a blob this client cannot load sitting in the
            // profile, to be written into the local character file at the next save and to throw again on
            // every respawn.
            byte[] previous = profile.GetMapData();
            try {
                profile.SetMapData(blob);
                Minimap.instance.LoadMapData();
                // The blob we just adopted is now what this client holds, so a capture immediately afterwards
                // has nothing new to report and should not send it straight back.
                MarkSent(Hash(blob));
                Logger.LogInfo($"Adopted the map this server holds for this character ({blob.Length} bytes).");
            } catch (Exception e) {
                try {
                    profile.SetMapData(previous);
                } catch (Exception restore) {
                    Logger.LogError($"Could not put this character's own map back after a failed adopt: {restore.Message}");
                }
                Logger.LogWarning($"Could not apply the map the server sent, keeping the local one: {e.Message}");
            }
        }

        /// <summary>
        /// Whether a blob is shaped like a map this client can load, checked before it reaches vanilla.
        ///
        /// Minimap.SetMapData throws a bare Exception("Error: minimap mismatch") when the texture size baked
        /// into the blob differs from the running one, and that would surface as an unhandled throw inside a
        /// vanilla method rather than as something an admin can read. A mismatch is not hypothetical: the
        /// size is a field on the minimap prefab, so a mod that changes it turns every stored map into one.
        ///
        /// Reading the texture size means expanding the blob, which is the same 8.4 MB vanilla is about to
        /// expand again - so applying a map costs two decompressions rather than one. Worth it for once per
        /// join: the alternative is deciding whether a blob is usable from where vanilla happens to throw,
        /// which is vanilla internals and could move under us.
        /// </summary>
        internal static bool IsReadableMap(byte[] blob, out string problem) {
            problem = null;
            try {
                ZPackage outer = new ZPackage(blob);
                int version = outer.ReadInt();
                if (version < OldestReadableMapVersion) {
                    problem = $"it declares map version {version}, which is not a map.";
                    return false;
                }
                // Vanilla compresses the inner package from Version.Map.Compressed onward; an older blob is
                // read straight through. Mirrors Minimap.SetMapData's own branch exactly.
                ZPackage inner = version >= FirstCompressedMapVersion ? outer.ReadCompressedPackage() : outer;
                int textureSize = inner.ReadInt();
                int running = Minimap.instance != null ? Minimap.instance.m_textureSize : textureSize;
                if (textureSize != running) {
                    problem = $"it was made at map resolution {textureSize} and this client runs {running}.";
                    return false;
                }
                return true;
            } catch (Exception e) {
                problem = $"it could not be read ({e.GetType().Name}: {e.Message}).";
                return false;
            }
        }

        internal static string Hash(byte[] blob) {
            if (blob == null || blob.Length == 0) { return null; }
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create()) {
                return modules.mods.PluginHasher.ToHex(sha.ComputeHash(blob));
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Server: storage
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Where a character's map blob lives: beside their save, as <c>&lt;Name&gt;.map</c>.
        ///
        /// Both segments are validated by the caller with PeerIdentity.IsSafeToken before they reach this -
        /// they are client-supplied and they become path components.
        /// </summary>
        internal static string PathFor(string accountId, string characterName) {
            return Path.Combine(ValConfig.CharacterFilePath, accountId, characterName + MapFileExtension);
        }

        /// <summary>Reads a stored blob, or null when there is none or it could not be read.</summary>
        internal static byte[] Load(string accountId, string characterName) {
            if (string.IsNullOrEmpty(accountId) || string.IsNullOrEmpty(characterName)) { return null; }
            try {
                string path = PathFor(accountId, characterName);
                if (!File.Exists(path)) { return null; }
                return File.ReadAllBytes(path);
            } catch (Exception e) {
                Logger.LogWarning($"Could not read the stored map for {accountId}/{characterName}: {e.Message}");
                return null;
            }
        }

        /// <summary>Deletes a stored blob. Used by the admin clear command and by the new-character rules.</summary>
        internal static bool Delete(string accountId, string characterName) {
            try {
                string path = PathFor(accountId, characterName);
                if (!File.Exists(path)) { return false; }
                File.Delete(path);
                return true;
            } catch (Exception e) {
                Logger.LogWarning($"Could not delete the stored map for {accountId}/{characterName}: {e.Message}");
                return false;
            }
        }

        // ---------------------------------------------------------------------------------------------

        /// <summary>Applies a blob that arrived before the minimap had read the profile.</summary>
        [HarmonyPatch(typeof(Minimap), "LoadMapData")]
        public static class ApplyAfterMapLoad {
            [HarmonyPostfix]
            private static void Postfix(Minimap __instance) {
                if (pendingBlob == null || __instance == null) { return; }
                // A blob left over from a session that has since ended belongs to that session's character.
                if (pendingGeneration != CharacterManager.SessionGeneration) { CancelPending(); return; }
                if (!Enabled) { CancelPending(); return; }
                byte[] blob = pendingBlob;
                pendingBlob = null;
                Adopt(blob);
            }
        }
    }
}
