using BepInEx.Logging;
using System;


namespace ValheimEnforcer {
    internal class Logger {
        public static LogLevel Level = LogLevel.Info;

        // Every LogDebug call site builds its interpolated string before the call, so the level check
        // inside LogDebug only saves the BepInEx dispatch, not the formatting. Hot paths test this
        // first and format inside the branch instead.
        public static bool DebugEnabled { get { return Level >= LogLevel.Debug; } }

        public static void EnableDebugLogging(object sender, EventArgs e) {
            CheckEnableDebugLogging();
        }

        public static void CheckEnableDebugLogging() {
            if (ValConfig.EnableDebugMode.Value) {
                Level = LogLevel.Debug;
            } else {
                Level = LogLevel.Info;
            }
        }

        public static void SetDebugLogging(bool state) {
            if (state) {
                Level = LogLevel.Debug;
            } else {
                Level = LogLevel.Info;
            }
        }

        public static void LogDebug(string message) {
            if (Level >= LogLevel.Debug) {
                ValheimEnforcer.Log.LogInfo(message);
            }
        }
        public static void LogInfo(string message) {
            if (Level >= LogLevel.Info) {
                ValheimEnforcer.Log.LogInfo(message);
            }
        }

        public static void LogWarning(string message) {
            if (Level >= LogLevel.Warning) {
                ValheimEnforcer.Log.LogWarning(message);
            }
        }

        public static void LogError(string message) {
            if (Level >= LogLevel.Error) {
                ValheimEnforcer.Log.LogError(message);
            }
        }
    }
}
