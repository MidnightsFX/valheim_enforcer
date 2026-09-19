using System;
using System.IO;
using System.Runtime.InteropServices;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.bannetwork {

    /// <summary>
    /// Reads the ban network API key from BanNetwork/api.key.
    ///
    /// <para><b>Why a file and not a config entry.</b> This is the RecoveryKey precedent verbatim. A
    /// BindServerConfig value is pushed to every connecting client by Jotunn, which would hand the key to
    /// anyone who joins. Even BindLocalConfig would put it in ValheimEnforcer.cfg - the file people paste
    /// wholesale into support threads, and the one Configuration Manager renders on screen to any in-game
    /// admin. A secret gets one home.</para>
    ///
    /// <para>The key is never logged, whole or partial. Only the server id - the middle segment, which
    /// identifies the registration and authorises nothing - ever appears in a log line or a command.</para>
    /// </summary>
    internal static class BanApiKey {

        internal const string FileName = "api.key";

        private const string Prefix = "vebn_";
        private const int ServerIdLength = 8;
        private const int SecretLength = 43;

        internal static string FilePath {
            get { return Path.Combine(BanOverrides.FolderPath, FileName); }
        }

        internal enum Shape {
            /// <summary>No file, or an empty one. The ordinary state before registration.</summary>
            Missing,
            /// <summary>A file with something in it that is not a key.</summary>
            Malformed,
            Valid,
        }

        /// <summary>
        /// Reads and shape-checks the key. <paramref name="key"/> is only set when the shape is Valid, so a
        /// malformed value can never be sent anywhere.
        /// </summary>
        internal static Shape Read(out string key, out string serverId) {
            key = null;
            serverId = null;

            string path = FilePath;
            string text;
            try {
                if (!File.Exists(path)) { return Shape.Missing; }
                text = File.ReadAllText(path).Trim();
            } catch (Exception e) {
                Logger.LogWarning($"Ban network: could not read {FileName}: {e.Message}");
                return Shape.Malformed;
            }

            if (string.IsNullOrEmpty(text)) { return Shape.Missing; }
            // Only the first line: pasting from a web page tends to bring a trailing newline and sometimes a
            // stray second line, and neither is worth refusing a valid key over.
            int newline = text.IndexOfAny(new[] { '\r', '\n' });
            if (newline >= 0) { text = text.Substring(0, newline).Trim(); }

            if (!text.StartsWith(Prefix, StringComparison.Ordinal)) { return Shape.Malformed; }
            string rest = text.Substring(Prefix.Length);
            int split = rest.IndexOf('_');
            if (split != ServerIdLength) { return Shape.Malformed; }

            string id = rest.Substring(0, split);
            string secret = rest.Substring(split + 1);
            if (secret.Length != SecretLength) { return Shape.Malformed; }
            if (!IsLowerAlphanumeric(id) || !IsBase64Url(secret)) { return Shape.Malformed; }

            key = text;
            serverId = id;
            return Shape.Valid;
        }

        /// <summary>
        /// Creates the file with an explanatory header if it is absent, and tightens its permissions.
        ///
        /// Written empty on purpose: there is no way for this mod to obtain a key by asking, because
        /// registration is reviewed by a person. The file exists to tell the owner where to put theirs.
        /// </summary>
        internal static void EnsureFile() {
            string path = FilePath;
            try {
                if (File.Exists(path)) {
                    Protect(path);
                    return;
                }
                File.WriteAllText(path,
                    "# Paste your ValheimEnforcer ban network API key on the line below, on its own, and save.\n"
                  + "# It looks like: vebn_ab12cd34_<43 characters>\n"
                  + "#\n"
                  + "# Keys are issued by a person after reviewing a registration - there is no way to request\n"
                  + "# one from in-game. Until a key is here the ban network stays idle and nothing is sent.\n"
                  + "#\n"
                  + "# Treat this like a password: it is not stored anywhere else, it is never written to the\n"
                  + "# log, and it is deliberately NOT in ValheimEnforcer.cfg so it cannot be pasted into a\n"
                  + "# support thread along with the rest of your settings.\n"
                  + "#\n"
                  + "# Run enforcer-ban-network-status after saving to check the server accepted it.\n");
                Protect(path);
            } catch (Exception e) {
                Logger.LogWarning($"Ban network: could not create {FileName}: {e.Message}");
            }
        }

        /// <summary>Owner-only on POSIX, matching how RecoveryKey treats its own file.</summary>
        private static void Protect(string path) {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) { return; }
            try {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                    FileName = "chmod", Arguments = $"600 \"{path}\"",
                    UseShellExecute = false, CreateNoWindow = true,
                })?.WaitForExit(2000);
            } catch (Exception) {
                // Best effort. A permissive key file is worth a shrug, not a failed startup.
            }
        }

        private static bool IsLowerAlphanumeric(string value) {
            foreach (char c in value) {
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))) { return false; }
            }
            return true;
        }

        private static bool IsBase64Url(string value) {
            foreach (char c in value) {
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                          || c == '-' || c == '_';
                if (!ok) { return false; }
            }
            return true;
        }
    }
}
