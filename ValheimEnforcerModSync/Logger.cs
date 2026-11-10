namespace ValheimEnforcerModSync {

    /// <summary>This add-on's log lines, through its own BepInEx log source.</summary>
    internal static class Logger {
        internal static void LogDebug(string message) { ValheimEnforcerModSync.Log?.LogDebug(message); }
        internal static void LogInfo(string message) { ValheimEnforcerModSync.Log?.LogInfo(message); }
        internal static void LogWarning(string message) { ValheimEnforcerModSync.Log?.LogWarning(message); }
    }
}
