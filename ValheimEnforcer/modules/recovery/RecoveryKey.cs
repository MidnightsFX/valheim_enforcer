using System;
using System.IO;
using System.Security.Cryptography;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.recovery {

    /// <summary>
    /// The server's recovery secret: generated once, kept in a file beside the configuration, and never sent
    /// anywhere.
    ///
    /// A file rather than a setting, and the distinction is not cosmetic. Every ordinary setting in this mod
    /// is bound with BindServerConfig, which marks it admin-only and has Jotunn's SynchronizationManager push
    /// it to every client that connects. A key pushed to the clients it is meant to be opaque to would make
    /// the whole feature decorative. Same reasoning as the Discord webhook, which is a password in URL form
    /// and is bound locally for it - but a config file is still readable by any admin in-game, so this is not
    /// in the config at all.
    ///
    /// Losing the file is survivable: every outstanding snapshot becomes unreadable and is refused as
    /// "sealed under a key that is not ours", which is the safe direction. Nothing is lost that the server
    /// itself still holds.
    /// </summary>
    internal static class RecoveryKey {

        internal const string FileName = "recovery.key";

        /// <summary>32 bytes. AES-256 and HMAC-SHA256 both take a 256-bit key, and both subkeys are derived
        /// from this rather than using it directly.</summary>
        private const int MasterBytes = 32;

        // Written once on load and replaced wholesale on a rotation, never mutated. Volatile because the
        // CharacterStore worker reads it to seal a snapshot while the main thread may be rotating it - and a
        // reference swap is the only thing that can be seen, so a worker mid-seal finishes with the key it
        // started with rather than half of each.
        private static volatile RecoverySeal.Keys keys;

        /// <summary>The current key material, or null when there is none and none could be made.</summary>
        internal static RecoverySeal.Keys Current {
            get { return keys; }
        }

        /// <summary>A publishable fingerprint of the key in use, for the status command. Null when unloaded.</summary>
        internal static string KeyId {
            get {
                RecoverySeal.Keys snapshot = keys;
                return snapshot == null ? null : snapshot.Id;
            }
        }

        internal static string Path() {
            return System.IO.Path.Combine(ValConfig.GetSecondaryConfigDirectoryPath(), FileName);
        }

        /// <summary>
        /// Loads the key, generating one the first time. Main thread, server side, called when the feature is
        /// switched on rather than at startup - a server that never enables this never gets a key file.
        /// Returns false when there is no usable key, which leaves the whole feature inert.
        /// </summary>
        internal static bool EnsureLoaded() {
            if (keys != null) { return true; }
            string path = Path();
            try {
                if (File.Exists(path)) {
                    string text = File.ReadAllText(path).Trim();
                    byte[] master = Convert.FromBase64String(text);
                    keys = new RecoverySeal.Keys(master);
                    Logger.LogInfo($"Loaded the crash-recovery key ({keys.Id}).");
                    return true;
                }
            } catch (Exception e) {
                // Not replaced automatically. A key file that is present but unreadable may be a disk problem
                // or a bad edit, and generating a new one over it would permanently orphan every snapshot the
                // clients are holding - exactly at the moment they might be needed.
                Logger.LogError($"The crash-recovery key at {path} could not be read ({e.Message}). Crash recovery is off until it is fixed or deleted; every snapshot clients hold is sealed under it.");
                return false;
            }

            try {
                byte[] master = new byte[MasterBytes];
                using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider()) {
                    rng.GetBytes(master);
                }
                Write(path, master);
                keys = new RecoverySeal.Keys(master);
                Logger.LogInfo($"Generated a crash-recovery key ({keys.Id}) at {path}. Keep it with your backups - without it the snapshots clients hold cannot be opened.");
                return true;
            } catch (Exception e) {
                Logger.LogError($"Could not create a crash-recovery key at {path}: {e.Message}. Crash recovery is off.");
                return false;
            }
        }

        /// <summary>
        /// Replaces the key. Every snapshot any client is currently holding becomes unopenable, which is the
        /// whole point of rotating - and is why the command that calls this makes an admin say so twice.
        /// </summary>
        internal static bool Rotate(out string newId) {
            newId = null;
            string path = Path();
            try {
                byte[] master = new byte[MasterBytes];
                using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider()) {
                    rng.GetBytes(master);
                }
                Write(path, master);
                RecoverySeal.Keys fresh = new RecoverySeal.Keys(master);
                keys = fresh;
                newId = fresh.Id;
                return true;
            } catch (Exception e) {
                Logger.LogError($"Could not rotate the crash-recovery key: {e.Message}");
                return false;
            }
        }

        // Base64 rather than raw bytes so the file survives being opened in an editor, copied into a backup
        // or pasted into a support ticket without being mangled. Written through AtomicFile so an interrupted
        // write cannot leave a truncated key - which would read as "unreadable" and disable the feature.
        private static void Write(string path, byte[] master) {
            AtomicFile.WriteBytes(path, System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(master)));
            Restrict(path);
        }

        // Best effort, and deliberately quiet about failing. On Linux this takes the file down to
        // owner-only; on Windows the config folder's own inheritance is what protects it and there is
        // nothing useful to do here. Either way a failure is not a reason to refuse to run.
        private static void Restrict(string path) {
            try {
                if (System.IO.Path.DirectorySeparatorChar != '/') { return; }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                    FileName = "chmod",
                    Arguments = "600 \"" + path + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                })?.WaitForExit(2000);
            } catch (Exception e) {
                Logger.LogDebug($"Could not restrict permissions on the recovery key: {e.Message}");
            }
        }
    }
}
