namespace ValheimEnforcer.modules.network {
    internal static class SocketFlush {

        /// <summary>
        /// Pushes queued messages out before a rejection tears the connection down.
        ///
        /// ZPlayFabSocket.Flush throws NotImplementedException, so a crossplay connection is skipped. Nothing is
        /// lost by that: a PlayFab Send is compressed on a background thread and goes out on a later LateUpdate,
        /// and every caller leaves the socket open for the client to disconnect itself.
        /// </summary>
        internal static void IfSupported(ISocket socket) {
            if (socket == null || socket is ZPlayFabSocket) { return; }
            socket.Flush();
        }
    }
}
