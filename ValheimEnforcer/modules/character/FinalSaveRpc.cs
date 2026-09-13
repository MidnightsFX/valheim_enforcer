using HarmonyLib;
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.character {
    /// <summary>
    /// End-of-session character save over a plain vanilla <see cref="ZRpc"/> method (not Jotunn's
    /// <c>CustomRPC</c>).
    ///
    /// The routine full-save path (join, delta stream, periodic full-sync pull) rides Jotunn's CustomRPC,
    /// whose <c>SendToPeer</c> is a coroutine that compresses and paces the package across several frames.
    /// That is fine mid-session, but on logout / Alt+F4 vanilla <c>Game.Shutdown</c> tears down ZNet in the
    /// same frame the save is queued, so the coroutine's socket enqueue never runs and the final save is
    /// lost (the server keeps a delta-stale copy and falsely confiscates on rejoin).
    ///
    /// This send is instead fully synchronous: <see cref="ZRpc.Invoke"/> enqueues into the socket send
    /// queue immediately and an explicit <see cref="ISocket.Flush"/> pushes it to the wire before the
    /// vanilla shutdown closes the connection (whose own Close() flush + 100ms linger reinforces delivery).
    /// The YAML is GZip-compressed so even very large characters stay within a single reliable message.
    /// </summary>
    internal static class FinalSaveRpc {
        internal const string RPC_NAME = "VE_FINAL_CHAR_SAVE";

        // Register the server-side receiver on every incoming connection, mirroring how vanilla registers
        // per-peer methods in OnNewConnection (and how ModManager already patches this method). Clients do
        // not register it — they only Invoke it by name, so the server resolves it by name-hash on receipt.
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.OnNewConnection))]
        public static class ZNet_OnNewConnection_RegisterFinalSave {
            [HarmonyPostfix]
            private static void Postfix(ZNet __instance, ZNetPeer peer) {
                if (__instance == null || !__instance.IsServer() || peer == null) { return; }
                peer.m_rpc.Register<ZPackage>(RPC_NAME, new Action<ZRpc, ZPackage>(RPC_FinalCharSave));
            }
        }

        // Largest compressed final-save payload accepted, and the ceiling its decompression may expand to.
        // This RPC is registered in OnNewConnection, before the handshake, so it is the earliest thing a
        // hostile client can reach; an unbounded GZip here is a decompression bomb on the main thread.
        private const int MaxCompressedBytes = 2 * 1024 * 1024;
        private const int MaxDecompressedBytes = 8 * 1024 * 1024;

        // Server side: a client's end-of-session character save. Deserialization/persistence is shared with
        // the Jotunn handler via ValConfig.PersistReceivedCharacterYaml (disk mode hands off to the async
        // CharacterStore, so this stays cheap on the main thread).
        private static void RPC_FinalCharSave(ZRpc rpc, ZPackage pkg) {
            // Resolve the peer from the real socket, and require it to be READY (past RPC_PeerInfo). Before that
            // its uid is 0 and its name is empty, which is exactly the state PersistReceivedCharacterYaml's
            // identity check now fails closed on - but dropping here is clearer and avoids GetPeer(0) aliasing
            // onto some other mid-handshake peer.
            ZNetPeer peer = ZNet.instance?.GetPeer(rpc);
            if (peer == null || !peer.IsReady()) {
                Logger.LogWarning("Dropping a final character save from a peer that is not past the handshake yet.");
                return;
            }
            long sender = peer.m_uid;

            byte[] compressed;
            try {
                compressed = pkg.ReadByteArray();
            } catch (Exception e) {
                Logger.LogWarning($"Failed to read final character save from {sender}: {e.Message}");
                return;
            }
            if (compressed == null || compressed.Length > MaxCompressedBytes) {
                Logger.LogWarning($"Dropping an oversize final character save from {sender} ({compressed?.Length ?? 0} compressed bytes).");
                return;
            }

            string yaml;
            try {
                yaml = Decompress(compressed);
            } catch (Exception e) {
                Logger.LogWarning($"Failed to decompress final character save from {sender}: {e.Message}");
                return;
            }
            if (yaml == null) {
                Logger.LogWarning($"Dropping a final character save from {sender}: it decompressed past the {MaxDecompressedBytes} byte limit.");
                return;
            }
            Logger.LogDebug($"Received synchronous final character save from {sender}.");
            ValConfig.PersistReceivedCharacterYaml(sender, yaml);
        }

        // Client side: synchronously push the final character save to the server and flush the socket so the
        // bytes are on the wire before the caller (Game.Shutdown) tears the connection down.
        internal static void SendFinalSaveSync(ZNetPeer serverPeer, DataObjects.Character character) {
            if (serverPeer == null || character == null) { return; }
            ZPackage package = new ZPackage();
            package.Write(Compress(DataObjects.yamlserializer.Serialize(character)));
            if (serverPeer.m_socket is ZPlayFabSocket) {
                InvokeOnPlayFab(serverPeer, package);
                Logger.LogDebug($"Sent synchronous final character save for {character.Name} ({package.Size()} bytes) over PlayFab.");
                return;
            }
            serverPeer.m_rpc.Invoke(RPC_NAME, package);
            serverPeer.m_socket?.Flush();
            Logger.LogDebug($"Sent synchronous final character save for {character.Name} ({package.Size()} bytes) and flushed the socket.");
        }

        /// <summary>
        /// The crossplay half of <see cref="SendFinalSaveSync"/>. ZPlayFabSocket cannot be flushed - Flush throws
        /// NotImplementedException - and its Send does not reach the wire on its own either: while ZNet is running
        /// it hands the payload to a background zlib thread and sends the result from a later LateUpdate. That
        /// frame never comes here, because Game.Shutdown disposes the socket in the same call, so the save would
        /// be dropped silently.
        ///
        /// The one path that compresses and sends on the spot is the one vanilla takes for its own Disconnect
        /// message, which ZNet.StopAll sends after setting m_haveStoped. This takes that path for one message and
        /// puts the flag back. Nothing else can observe it in between: the only readers of the flag are StopAll,
        /// the HaveStopped property and ZPlayFabSocket.InternalSend, all on this thread.
        /// </summary>
        private static void InvokeOnPlayFab(ZNetPeer serverPeer, ZPackage package) {
            ZNet znet = ZNet.instance;
            if (znet == null) { return; }
            bool wasStopped = znet.m_haveStoped;
            znet.m_haveStoped = true;
            try {
                serverPeer.m_rpc.Invoke(RPC_NAME, package);
            } finally {
                znet.m_haveStoped = wasStopped;
            }
        }

        private static byte[] Compress(string text) {
            byte[] raw = Encoding.UTF8.GetBytes(text);
            using (MemoryStream output = new MemoryStream()) {
                using (GZipStream gz = new GZipStream(output, CompressionMode.Compress)) {
                    gz.Write(raw, 0, raw.Length);
                }
                return output.ToArray();
            }
        }

        // Bounded decompression: returns null rather than expanding past MaxDecompressedBytes, so a small
        // compressed payload cannot balloon into hundreds of megabytes on the main thread.
        private static string Decompress(byte[] data) {
            using (MemoryStream input = new MemoryStream(data))
            using (GZipStream gz = new GZipStream(input, CompressionMode.Decompress))
            using (MemoryStream output = new MemoryStream()) {
                byte[] buffer = new byte[8192];
                int total = 0;
                int read;
                while ((read = gz.Read(buffer, 0, buffer.Length)) > 0) {
                    total += read;
                    if (total > MaxDecompressedBytes) { return null; }
                    output.Write(buffer, 0, read);
                }
                return Encoding.UTF8.GetString(output.ToArray());
            }
        }
    }
}
