using System;

namespace ValheimEnforcer.common {

    /// <summary>
    /// Whether Mono was started with its native-debugger support on: <c>gdb</c> or <c>lldb</c> among the
    /// MONO_DEBUG options, or MONO_XDEBUG or MONO_LLDB set at all. Server hosts expose MONO_DEBUG as an ordinary
    /// environment setting, and it gets switched on while somebody is chasing a crash.
    ///
    /// With it on, Mono writes debug information for every method it compiles, under runtime locks, and two
    /// threads compiling at the same moment can deadlock. A server hit exactly that: with <c>gdb</c> added to
    /// MONO_DEBUG it hung on start four times, each time while Enforcer's warm-up thread was compiling
    /// the YAML serializer and the main thread was still loading plugins, and never before the option was added.
    /// Nothing reported an error; the log simply stopped.
    ///
    /// Enforcer cannot make the runtime safe - the game and other mods run threads of their own - so while this
    /// is on it compiles its heaviest startup work on the main thread instead, and says why the server may hang.
    /// </summary>
    internal static class MonoDebugOptions {

        /// <summary>What switched native-debugger support on, for the warning; null when it is off.</summary>
        private static readonly string source = Detect();

        internal static bool NativeDebuggerSupport => source != null;

        /// <summary>One warning at startup when native-debugger support is on; nothing otherwise.</summary>
        internal static void WarnIfNativeDebuggerSupport() {
            if (source == null) { return; }
            Logger.LogWarning($"Mono's native-debugger support is switched on ({source}). With it on the game can hang for good when two threads " +
                              "compile code at once, most often while starting up. Enforcer runs its startup checks on the main thread while it is on, " +
                              "but the game and other mods still use threads of their own: unless a native debugger is attached, remove it from the server's environment.");
        }

        private static string Detect() {
            try {
                // Mono reads MONO_DEBUG as a comma-separated list and matches each option exactly.
                string options = Environment.GetEnvironmentVariable("MONO_DEBUG");
                if (!string.IsNullOrEmpty(options)) {
                    foreach (string option in options.Split(',')) {
                        string name = option.Trim();
                        if (name == "gdb" || name == "lldb") { return $"{name} in MONO_DEBUG"; }
                    }
                }
                // These two switch it on whatever they hold.
                if (Environment.GetEnvironmentVariable("MONO_XDEBUG") != null) { return "MONO_XDEBUG"; }
                if (Environment.GetEnvironmentVariable("MONO_LLDB") != null) { return "MONO_LLDB"; }
            } catch (Exception) {
                // An environment we may not read is one we cannot reason about; treat it as the ordinary case.
            }
            return null;
        }
    }
}
