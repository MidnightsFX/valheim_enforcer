using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UnityEngine;
using ValheimEnforcer.modules.mods;

namespace ValheimEnforcer.modules.modsync {

    /// <summary>
    /// The native helper that applies staged mod changes after the game has exited, carried inside this DLL.
    ///
    /// Source and build are in tools/modsync-helper (Rust, no dependencies). It is embedded rather than shipped
    /// as a loose file so the package layout is unchanged and the helper always matches the mod that wrote its
    /// plan. Three builds: Windows x64, which also serves Proton (Valheim reports itself as Windows there and runs
    /// the helper under Wine), a static Linux x64 one, and macOS x64. Doorstop only exists for x86_64 on a Mac, so
    /// a Mac running BepInEx runs Valheim as x86_64 - under Rosetta on Apple Silicon - and an x86_64 helper runs
    /// wherever this mod does.
    /// </summary>
    internal static class ModSyncHelper {

        private const string WindowsResource = "ValheimEnforcer.modsync.windows-x64";
        private const string LinuxResource = "ValheimEnforcer.modsync.linux-x64";
        private const string MacResource = "ValheimEnforcer.modsync.macos-x64";

        internal static bool IsLinux { get { return Application.platform == RuntimePlatform.LinuxPlayer; } }
        internal static bool IsMac { get { return Application.platform == RuntimePlatform.OSXPlayer; } }

        /// <summary>The helper build for this machine, or null when there is none.</summary>
        private static string ResourceName() {
            if (IntPtr.Size != 8) { return null; }
            switch (Application.platform) {
                case RuntimePlatform.WindowsPlayer: return WindowsResource;
                case RuntimePlatform.LinuxPlayer: return LinuxResource;
                case RuntimePlatform.OSXPlayer: return MacResource;
                default: return null;
            }
        }

        internal static bool Available {
            get {
                string name = ResourceName();
                return name != null && typeof(ModSyncHelper).Assembly.GetManifestResourceNames().Contains(name);
            }
        }

        /// <summary>
        /// Writes the helper into <paramref name="dir"/> and returns its path, or null with a reason.
        ///
        /// Named after its own hash, so a copy already there from an earlier run is reused rather than rewritten -
        /// and a helper still running from one could never be in the way of the next. Older builds are removed.
        /// </summary>
        internal static string Extract(string dir, out string failure) {
            failure = null;
            string resource = ResourceName();
            if (resource == null) {
                failure = "there is no helper for this platform";
                return null;
            }
            try {
                byte[] bytes;
                using (Stream stream = typeof(ModSyncHelper).Assembly.GetManifestResourceStream(resource)) {
                    if (stream == null) {
                        failure = "the helper is missing from this build of the mod";
                        return null;
                    }
                    using (MemoryStream copy = new MemoryStream()) {
                        stream.CopyTo(copy);
                        bytes = copy.ToArray();
                    }
                }

                string hash;
                using (SHA256 sha = SHA256.Create()) { hash = PluginHasher.ToHex(sha.ComputeHash(bytes)).Substring(0, 12); }
                Directory.CreateDirectory(dir);
                bool unix = IsLinux || IsMac;
                string path = Path.Combine(dir, "ve-modsync-" + hash + (unix ? "" : ".exe"));
                if (!File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(bytes)) {
                    File.WriteAllBytes(path, bytes);
                }
                if (unix) { MakeExecutable(path); }

                foreach (string old in Directory.GetFiles(dir, "ve-modsync-*")) {
                    if (string.Equals(old, path, StringComparison.OrdinalIgnoreCase)) { continue; }
                    try { File.Delete(old); } catch (Exception) { /* still running, or locked; next time */ }
                }
                return path;
            } catch (Exception e) {
                failure = e.Message;
                return null;
            }
        }

        [DllImport("libc", EntryPoint = "chmod", SetLastError = true)]
        private static extern int Chmod(string path, uint mode);

        /// <summary>
        /// rwxr-xr-x. Through libc when the runtime maps it, and through the chmod program when it does not - both
        /// are on every Linux and macOS the game runs on. A file this process writes is not marked as downloaded,
        /// so Gatekeeper has no reason to stop the Mac build either.
        /// </summary>
        private static void MakeExecutable(string path) {
            try {
                if (Chmod(path, Convert.ToUInt32("755", 8)) == 0) { return; }
            } catch (Exception e) {
                Logger.LogDebug($"Mod sync: libc chmod is not available ({e.Message}); using the chmod program.");
            }
            using (Process chmod = Process.Start(new ProcessStartInfo("/bin/chmod", "755 \"" + path + "\"") { UseShellExecute = false, CreateNoWindow = true })) {
                chmod?.WaitForExit(5000);
            }
        }
    }
}
