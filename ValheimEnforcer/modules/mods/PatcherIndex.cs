using BepInEx;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.mods {

    /// <summary>
    /// Enumerates and hashes the BepInEx preloader patchers on this machine, so a server can hold them to a
    /// list the same way it holds plugins to one.
    ///
    /// Patchers are the blind spot this closes. They are not plugins - BepInEx loads them from
    /// <c>BepInEx/patchers/</c> before any plugin exists, hands each one the target assemblies as Mono.Cecil
    /// <c>AssemblyDefinition</c>s, and lets it rewrite them on the way in. That is a strictly stronger position
    /// than the plugins the mod list already checks, and until now nothing in this mod so much as looked at the
    /// directory: dropping a DLL there bypassed mod validation completely.
    ///
    /// Identity here is the file, not a GUID. A patcher is a bare assembly with no BepInEx metadata attached -
    /// no plugin id, no version - so there is nothing to key on but its path and its contents. That is enough
    /// for an allowlist, and it is what a rejection message can usefully name.
    ///
    /// Same honest limit as everything else on the client side: a hostile patcher runs before this code and can
    /// remove it. What it buys is that <c>BepInEx/patchers/</c> stops being a free bypass.
    /// </summary>
    internal static class PatcherIndex {

        /// <summary>Relative path (forward slashes) -> fingerprint. The authoritative result of a pass.</summary>
        private static readonly ConcurrentDictionary<string, DataObjects.PatcherEntry> results =
            new ConcurrentDictionary<string, DataObjects.PatcherEntry>();

        private static Task pass;
        private static readonly object passLock = new object();

        /// <summary>
        /// Starts a background pass over the patcher directory. Idempotent in the same way as
        /// <see cref="PluginHasher.BeginPass"/>, because the two are started from the same place and a listen
        /// host runs it more than once.
        /// </summary>
        internal static void BeginPass() {
            lock (passLock) {
                if (pass != null && !pass.IsCompleted) { return; }

                pass = Task.Run(() => {
                    Stopwatch sw = Stopwatch.StartNew();
                    try {
                        Scan();
                        Logger.LogDebug($"Hashed {results.Count} patcher file(s) in {sw.ElapsedMilliseconds}ms.");
                    } catch (Exception e) {
                        // A patcher directory we cannot read must not take the handshake down with it. An
                        // empty result reads to the server as "this client has no patchers", which is both the
                        // common case and the safe direction: it can only fail an allowlist, never pass one it
                        // should not.
                        Logger.LogWarning($"Patcher hashing pass failed: {e.Message}");
                    }
                });
            }
        }

        private static void Scan() {
            results.Clear();

            string root = RootPath();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { return; }

            // Recursive: Gale, r2modman and Thunderstore MM all give each package its own subfolder under
            // patchers/, so a top-level-only scan would see nothing on exactly the installs that have any.
            foreach (string file in Directory.GetFiles(root, "*.dll", SearchOption.AllDirectories)) {
                string key = RelativeKey(root, file);
                if (string.IsNullOrEmpty(key)) { continue; }

                string hash = PluginHasher.HashFile(file, out string status);
                results[key] = new DataObjects.PatcherEntry {
                    Name = key,
                    Hash = hash,
                    HashStatus = hash == null ? (status ?? PluginHasher.StatusUnreadable) : null,
                };
            }
        }

        /// <summary>
        /// The patcher directory, or null when BepInEx does not expose one. Wrapped because
        /// <see cref="Paths"/> is static state initialised by the preloader, and a context that never ran it
        /// (a unit test, an editor session) throws rather than returning empty.
        /// </summary>
        private static string RootPath() {
            try {
                return Paths.PatcherPluginPath;
            } catch (Exception e) {
                Logger.LogDebug($"Could not resolve the BepInEx patcher directory: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// A stable, portable key for one patcher file: its path relative to the patcher root, with forward
        /// slashes. The bare filename is deliberately not used - two packages may each ship "Patcher.dll" in
        /// their own subfolder, and collapsing those onto one key would let an allowlisted patcher vouch for an
        /// unrelated file of the same name.
        /// </summary>
        private static string RelativeKey(string root, string file) {
            try {
                string full = Path.GetFullPath(file);
                string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
                    full = full.Substring(prefix.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
                return full.Replace('\\', '/');
            } catch (Exception e) {
                Logger.LogDebug($"Could not build a patcher key for {file}: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Blocks until the current pass finishes. Unlike the plugin pass a timeout is not degraded to a
        /// "timed out" per-entry status: there is no entry to stamp, because the scan produces the entries. A
        /// timed-out pass reports whatever it had finished, and logs that it did.
        /// </summary>
        internal static bool WaitForPass(int timeoutMs) {
            Task current;
            lock (passLock) { current = pass; }
            if (current == null) { return true; }
            if (current.Wait(timeoutMs)) { return true; }

            Logger.LogWarning($"Patcher hashing did not finish within {timeoutMs}ms; reporting the {results.Count} patcher(s) hashed so far.");
            return false;
        }

        /// <summary>A detached copy of the current results, safe to serialize.</summary>
        internal static Dictionary<string, DataObjects.PatcherEntry> Snapshot() {
            Dictionary<string, DataObjects.PatcherEntry> copy = new Dictionary<string, DataObjects.PatcherEntry>();
            foreach (KeyValuePair<string, DataObjects.PatcherEntry> entry in results) {
                copy[entry.Key] = new DataObjects.PatcherEntry {
                    Name = entry.Value.Name,
                    Hash = entry.Value.Hash,
                    HashStatus = entry.Value.HashStatus,
                };
            }
            return copy;
        }

        internal static int Count() { return results.Count; }
    }
}
