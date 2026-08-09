using BepInEx;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.mods {

    /// <summary>
    /// Computes a SHA256 over the DLL file BepInEx loaded each plugin from, so a server can tell a plugin that
    /// came out of its official download from one somebody rebuilt with better numbers in it.
    ///
    /// The digest is computed and reported by the client, so this stops a recompiled mod, not a patched
    /// enforcer - see the README for the full statement of what this does and does not buy.
    /// </summary>
    internal static class PluginHasher {

        /// <summary>Assembly has no file on disk (loaded from a byte[]), so there is nothing to hash.</summary>
        internal const string StatusDynamic = "dynamic";
        /// <summary>A location was known but the file is not there any more.</summary>
        internal const string StatusMissing = "missing";
        /// <summary>The file could not be read (permissions, IO error).</summary>
        internal const string StatusUnreadable = "unreadable";
        /// <summary>The hashing pass did not finish in time and gave up on this plugin.</summary>
        internal const string StatusTimedOut = "timeout";

        internal sealed class PluginFingerprint {
            public string Hash;
            public string Status;
        }

        private sealed class CacheEntry {
            public long Length;
            public DateTime MTimeUtc;
            public string Hash;
        }

        // Keyed by full DLL path. Survives repeated passes: a listen host runs SetModsActive from both
        // OnPrefabsRegistered and OnVanillaPrefabsAvailable, and returning to the menu to re-host runs it again,
        // so a second pass costs a stat() per file instead of a re-read.
        private static readonly ConcurrentDictionary<string, CacheEntry> fileCache = new ConcurrentDictionary<string, CacheEntry>();

        // Plugin GUID -> fingerprint. The authoritative result the rest of the mod reads.
        private static readonly ConcurrentDictionary<string, PluginFingerprint> results = new ConcurrentDictionary<string, PluginFingerprint>();

        // Plugins we have already warned about having no file on disk, so the warning is one line per plugin
        // per session rather than one per hashing pass.
        private static readonly HashSet<string> warnedDynamic = new HashSet<string>();

        private static Task pass;
        private static readonly object passLock = new object();

        /// <summary>
        /// Starts a background hashing pass over every loaded plugin. Idempotent: a call made while a pass is
        /// still running is a no-op, so the repeat SetModsActive on a listen host does not start a second one.
        /// </summary>
        internal static void BeginPass(Dictionary<string, BaseUnityPlugin> plugins) {
            if (plugins == null || plugins.Count == 0) { return; }

            lock (passLock) {
                if (pass != null && !pass.IsCompleted) { return; }

                List<KeyValuePair<string, BaseUnityPlugin>> work = plugins.ToList();
                // Bounded rather than unbounded: SHA256 over page-cached files parallelises linearly, but a
                // pack carrying several 100MB asset-bundle DLLs off a cold spinning disk is IO bound, and
                // fanning that out over every core just thrashes the head.
                int workers = Math.Max(1, Math.Min(4, Environment.ProcessorCount));

                pass = Task.Run(() => {
                    Stopwatch sw = Stopwatch.StartNew();
                    try {
                        Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = workers }, plugin => {
                            results[plugin.Key] = Fingerprint(plugin.Key, plugin.Value);
                        });
                        Logger.LogInfo($"Hashed {work.Count} plugin file(s) in {sw.ElapsedMilliseconds}ms.");
                    } catch (Exception e) {
                        Logger.LogWarning($"Plugin hashing pass failed: {e.Message}");
                    }
                });
            }
        }

        private static PluginFingerprint Fingerprint(string guid, BaseUnityPlugin plugin) {
            string location = ResolveLocation(plugin);
            if (string.IsNullOrEmpty(location)) {
                WarnDynamicOnce(guid);
                return new PluginFingerprint { Status = StatusDynamic };
            }
            string hash = HashFile(location, out string status);
            return new PluginFingerprint { Hash = hash, Status = status };
        }

        private static void WarnDynamicOnce(string guid) {
            lock (warnedDynamic) {
                if (!warnedDynamic.Add(guid)) { return; }
            }
            Logger.LogWarning($"Plugin {guid} has no file on disk (it was loaded from memory), so it cannot be file-verified. A server that verifies this mod will reject this client.");
        }

        /// <summary>
        /// Blocks until the current pass finishes. Returns false on timeout; the plugins that did not finish
        /// are recorded as <see cref="StatusTimedOut"/> rather than left absent, so a slow disk degrades to
        /// "unverifiable" instead of looking like a client that reported nothing.
        /// </summary>
        internal static bool WaitForPass(int timeoutMs) {
            Task current;
            lock (passLock) { current = pass; }
            if (current == null) { return true; }
            if (current.Wait(timeoutMs)) { return true; }

            Logger.LogWarning($"Plugin hashing did not finish within {timeoutMs}ms; the remaining plugins are reported as unverifiable.");
            return false;
        }

        /// <summary>
        /// Stamps Hash/HashStatus onto every entry of a mod dictionary from the completed pass. Entries the
        /// pass has not produced a result for are marked as timed out.
        /// </summary>
        internal static void ApplyTo(Dictionary<string, DataObjects.Mod> mods) {
            if (mods == null) { return; }
            foreach (KeyValuePair<string, DataObjects.Mod> entry in mods) {
                Apply(entry.Key, entry.Value);
            }
        }

        /// <summary>Stamps a single entry. Safe to call for a GUID the pass never saw.</summary>
        internal static void Apply(string guid, DataObjects.Mod mod) {
            if (mod == null) { return; }
            if (results.TryGetValue(guid, out PluginFingerprint fingerprint)) {
                mod.Hash = fingerprint.Hash;
                mod.HashStatus = fingerprint.Status;
                return;
            }
            mod.Hash = null;
            mod.HashStatus = StatusTimedOut;
        }

        /// <summary>The fingerprint computed for a plugin GUID, or null when the pass never produced one.</summary>
        internal static PluginFingerprint Get(string guid) {
            return results.TryGetValue(guid, out PluginFingerprint fingerprint) ? fingerprint : null;
        }

        /// <summary>
        /// Resolves the on-disk DLL a plugin was loaded from. Returns null when there is no such file, which is
        /// the case for an assembly loaded from a byte[] (BepInEx ScriptEngine, in-game plugin loaders). The
        /// caller reports that as <see cref="StatusDynamic"/> and never as a pass.
        /// </summary>
        internal static string ResolveLocation(BaseUnityPlugin plugin) {
            try {
                string location = plugin?.Info?.Location;
                if (!string.IsNullOrEmpty(location)) { return location; }
                // Same reach-the-assembly shape as modules/compat/ExtraSlots/ExtraSlotsAPI.cs uses. Assembly
                // .Location is likewise empty for a byte[] load, so both being empty is the real signal.
                return plugin?.GetType()?.Assembly?.Location;
            } catch (Exception e) {
                Logger.LogDebug($"Could not resolve a file location for a plugin: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Hashes one file. Returns null and sets <paramref name="status"/> on failure.
        /// </summary>
        internal static string HashFile(string path, out string status) {
            status = null;
            try {
                FileInfo info = new FileInfo(path);
                if (!info.Exists) {
                    status = StatusMissing;
                    return null;
                }

                if (fileCache.TryGetValue(path, out CacheEntry cached)
                    && cached.Length == info.Length
                    && cached.MTimeUtc == info.LastWriteTimeUtc) {
                    return cached.Hash;
                }

                string hash;
                // FileShare.ReadWrite is required, not defensive. BepInEx loads plugins with Assembly.LoadFile,
                // which holds an open handle on every one of them for the life of the process, so an exclusive
                // open throws for every single plugin and the whole feature silently reports "unreadable".
                // Delete is included so a mod manager swapping files underneath us cannot block the read either.
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536))
                using (SHA256 sha = SHA256.Create()) { // per file: SHA256 instances are not thread safe
                    hash = ToHex(sha.ComputeHash(fs));
                }

                fileCache[path] = new CacheEntry { Length = info.Length, MTimeUtc = info.LastWriteTimeUtc, Hash = hash };
                return hash;
            } catch (Exception e) {
                Logger.LogWarning($"Could not hash plugin file {path}: {e.Message}");
                status = StatusUnreadable;
                return null;
            }
        }

        /// <summary>Lowercase hex, no separators.</summary>
        internal static string ToHex(byte[] bytes) {
            if (bytes == null) { return null; }
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) { sb.Append(b.ToString("x2")); }
            return sb.ToString();
        }
    }
}
