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

        // Server side: a client's end-of-session character save. Deserialization/persistence is shared with
        // the Jotunn handler via ValConfig.PersistReceivedCharacterYaml (disk mode hands off to the async
        // CharacterStore, so this stays cheap on the main thread).
        private static void RPC_FinalCharSave(ZRpc rpc, ZPackage pkg) {
            long sender = ZNet.instance?.GetPeer(rpc)?.m_uid ?? 0L;
            string yaml;
            try {
                yaml = Decompress(pkg.ReadByteArray());
            } catch (Exception e) {
                Logger.LogWarning($"Failed to decompress final character save from {sender}: {e.Message}");
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
            serverPeer.m_rpc.Invoke(RPC_NAME, package);
            serverPeer.m_socket?.Flush();
            Logger.LogDebug($"Sent synchronous final character save for {character.Name} ({package.Size()} bytes) and flushed the socket.");
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

        private static string Decompress(byte[] data) {
            using (MemoryStream input = new MemoryStream(data))
            using (GZipStream gz = new GZipStream(input, CompressionMode.Decompress))
            using (MemoryStream output = new MemoryStream()) {
                gz.CopyTo(output);
                return Encoding.UTF8.GetString(output.ToArray());
            }
        }
    }
}
