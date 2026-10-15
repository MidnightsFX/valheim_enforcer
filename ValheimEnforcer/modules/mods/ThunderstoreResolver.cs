using HarmonyLib;
using Mono.Cecil;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.mods {

    /// <summary>
    /// Server side: resolves file hashes for mods the server does not load itself, by downloading the pinned
    /// Thunderstore package and hashing the DLLs inside it.
    ///
    /// This exists because a client-only mod (a UI or QoL plugin the server never runs) has no local file for
    /// <see cref="PluginHasher"/> to fingerprint, so without it the only way to pin one is for an admin to
    /// paste a hash by hand.
    ///
    /// Only thunderstore.io and its CDN are ever contacted. Arbitrary download URLs are deliberately not
    /// supported: a package coordinate that has to survive <see cref="TryParseSpec"/> cannot express a
    /// different host, which keeps an admin-authored config field from becoming a request-forgery primitive.
    /// </summary>
    internal static class ThunderstoreResolver {

        private static volatile bool firstPassDone = false;

        /// <summary>
        /// False only while a server's first resolve pass is still outstanding. While false, Strict enforcement
        /// is deferred for mods with an unresolved thunderstorePackage - see <see cref="HashPolicy.Evaluate"/> -
        /// because without that a Strict server rejects every client for the seconds its downloads take after a
        /// restart.
        ///
        /// Always true off the server. Only the server resolves, so a client evaluating the server's list for
        /// feedback must not defer: if it did, a player whose connection is about to be refused for an unpinned
        /// mod would be told everything was fine.
        /// </summary>
        internal static bool ResolutionSettled {
            get { return firstPassDone || ZNet.instance == null || !ZNet.instance.IsServer(); }
        }

        // Thunderstore owner and package names cannot contain '-', so splitting on it is unambiguous. These
        // two patterns are also the injection guard: with no '/', '.', '%' or '..' able to survive them,
        // nothing an admin types can move the request off the path shape below.
        private static readonly Regex NamePart = new Regex(@"^[A-Za-z0-9_]+$", RegexOptions.Compiled);
        internal static readonly Regex VersionPart = new Regex(@"^\d{1,6}\.\d{1,6}\.\d{1,6}$", RegexOptions.Compiled);

        // Shared with the client mod sync, which reads the same archives with the same limits.
        internal const int MaxZipEntries = 2000;
        internal const long MaxEntryBytes = 64L * 1024 * 1024;
        internal const long MaxTotalUncompressedBytes = 400L * 1024 * 1024;

        private sealed class Attempt {
            public int Failures;
            public DateTime NextAttemptUtc;
        }

        /// <summary>Hashes resolved for one plugin GUID, handed back to the main thread.</summary>
        private sealed class ResolvedEntry {
            public string PluginID;
            public List<string> Hashes;
            public string HashedFrom;
        }

        // spec -> backoff state. Not persisted: a restart is a legitimate "try again now".
        private static readonly Dictionary<string, Attempt> attempts = new Dictionary<string, Attempt>();
        private static readonly ConcurrentQueue<ResolvedEntry> resolved = new ConcurrentQueue<ResolvedEntry>();

        private static ResolverBehaviour host;
        private static CancellationTokenSource cancellation;
        private static volatile bool running;
        private static volatile bool passQueued;

        internal static void Initialize() {
            if (host != null) { return; }
            cancellation = new CancellationTokenSource();
            GameObject go = new GameObject("VE_ThunderstoreResolver");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            host = go.AddComponent<ResolverBehaviour>();
            Logger.LogDebug("Thunderstore hash resolver initialized.");
        }

        internal static void Teardown() {
            try { cancellation?.Cancel(); } catch (Exception) { /* already disposed */ }
            cancellation?.Dispose();
            cancellation = null;
            if (host != null) {
                UnityEngine.Object.Destroy(host.gameObject);
                host = null;
            }
            while (resolved.TryDequeue(out ResolvedEntry _)) { }
            ThunderstorePackageFinder.Teardown();
            running = false;
            passQueued = false;
            firstPassDone = false;
        }

        /// <summary>Queues a resolve pass. Idempotent while one is already running.</summary>
        internal static void RequestPass(string reason) {
            if (!ValConfig.ResolveThunderstoreHashes.Value) {
                // Nothing will ever resolve, so Strict must not defer on our account.
                firstPassDone = true;
                return;
            }
            if (host == null) { return; }
            Logger.LogDebug($"Thunderstore hash resolve requested ({reason}).");
            passQueued = true;
        }

        /// <summary>
        /// Collects the packages that still need resolving. Main thread: reads the live ModSettings.
        /// </summary>
        private static List<KeyValuePair<string, string>> CollectWork() {
            List<KeyValuePair<string, string>> work = new List<KeyValuePair<string, string>>();
            DataObjects.Mods settings = ModManager.ModSettings;
            if (settings == null) { return work; }

            foreach (Dictionary<string, DataObjects.Mod> list in new[] { settings.RequiredMods, settings.OptionalMods, settings.AdminOnlyMods }) {
                if (list == null) { continue; }
                foreach (KeyValuePair<string, DataObjects.Mod> entry in list) {
                    DataObjects.Mod mod = entry.Value;
                    if (mod == null || string.IsNullOrWhiteSpace(mod.ThunderstorePackage)) { continue; }
                    // Listed with no version: any version is allowed, so there is no build to hash.
                    if (!mod.IsVersioned()) { continue; }

                    if (!TryResolveSpec(entry.Key, mod, out string spec)) { continue; }

                    // Already resolved from exactly this coordinate - a restart re-downloads nothing.
                    if (mod.HasRecordedHash() && string.Equals(mod.HashedFrom, spec, StringComparison.OrdinalIgnoreCase)) { continue; }

                    if (!ShouldAttempt(spec)) { continue; }
                    if (work.Any(w => w.Value == spec)) { continue; } // one fetch per package
                    work.Add(new KeyValuePair<string, string>(entry.Key, spec));
                }
            }
            return work;
        }

        /// <summary>
        /// Builds the full "Owner-Name-Version" coordinate for an entry, falling back to the mod's own version
        /// when the spec omits one.
        /// </summary>
        private static bool TryResolveSpec(string key, DataObjects.Mod mod, out string spec) {
            spec = null;
            if (!TryParseSpec(mod.ThunderstorePackage, out string owner, out string name, out string version)) {
                Logger.LogWarning($"Mods.yaml entry '{key}' has an unusable thunderstorePackage '{mod.ThunderstorePackage}'. Expected Owner-ModName or Owner-ModName-Version, with no punctuation beyond the separating dashes.");
                return false;
            }

            if (version == null) {
                // A plugin's BepInEx version is frequently not its Thunderstore version_number (four-part
                // assembly versions are common), and there is no JSON parser in this project to ask the API
                // with. Refusing with an actionable message beats guessing wrong and pinning the wrong file.
                if (mod.Version != null && VersionPart.IsMatch(mod.Version)) {
                    version = mod.Version;
                } else {
                    Logger.LogWarning($"Mods.yaml entry '{key}' has thunderstorePackage '{mod.ThunderstorePackage}' with no version, and its version field '{mod.Version}' is not a Thunderstore version. Pin it explicitly, e.g. '{owner}-{name}-1.0.0'.");
                    return false;
                }
            }

            spec = $"{owner}-{name}-{version}";
            return true;
        }

        /// <summary>
        /// Parses "Owner-ModName" or "Owner-ModName-Version", the dependency string format a Thunderstore
        /// manifest uses.
        /// </summary>
        internal static bool TryParseSpec(string spec, out string owner, out string name, out string version) {
            owner = name = version = null;
            if (string.IsNullOrWhiteSpace(spec)) { return false; }

            string[] parts = spec.Trim().Split('-');
            if (parts.Length != 2 && parts.Length != 3) { return false; }
            if (!NamePart.IsMatch(parts[0]) || !NamePart.IsMatch(parts[1])) { return false; }
            if (parts.Length == 3 && !VersionPart.IsMatch(parts[2])) { return false; }

            owner = parts[0];
            name = parts[1];
            version = parts.Length == 3 ? parts[2] : null;
            return true;
        }

        private static bool ShouldAttempt(string spec) {
            lock (attempts) {
                if (!attempts.TryGetValue(spec, out Attempt attempt)) { return true; }
                if (attempt.NextAttemptUtc == DateTime.MaxValue) { return false; } // given up until config changes
                return DateTime.UtcNow >= attempt.NextAttemptUtc;
            }
        }

        private static void NoteFailure(string spec, string reason) {
            int failures;
            DateTime next;
            lock (attempts) {
                if (!attempts.TryGetValue(spec, out Attempt attempt)) {
                    attempt = new Attempt();
                    attempts[spec] = attempt;
                }
                attempt.Failures++;
                failures = attempt.Failures;
                // 1 min, 5 min, 30 min, then stop until the file changes or the server restarts. A dead pin
                // should not turn into a retry loop against Thunderstore for the life of the process.
                switch (failures) {
                    case 1: next = DateTime.UtcNow.AddMinutes(1); break;
                    case 2: next = DateTime.UtcNow.AddMinutes(5); break;
                    case 3: next = DateTime.UtcNow.AddMinutes(30); break;
                    default: next = DateTime.MaxValue; break;
                }
                attempt.NextAttemptUtc = next;
            }

            string when = next == DateTime.MaxValue
                ? "no further attempts until Mods.yaml changes or the server restarts"
                : $"retrying after {next:HH:mm:ss}Z";
            Logger.LogWarning($"Could not resolve Thunderstore package '{spec}': {reason} ({when}).");
        }

        private static void NoteSuccess(string spec) {
            lock (attempts) { attempts.Remove(spec); }
        }

        /// <summary>
        /// Runs one resolve pass on a worker thread. Touches nothing Unity or ZNet owns: results go onto
        /// <see cref="resolved"/> and are applied by <see cref="ResolverBehaviour.Update"/> on the main thread.
        /// </summary>
        private static void RunPass(List<KeyValuePair<string, string>> work, CancellationToken token) {
            long cap = (long)ValConfig.ThunderstoreMaxArchiveMB.Value * 1024 * 1024;

            // Sequential on purpose: each archive is held in memory while its DLLs are hashed, so resolving in
            // parallel would multiply the peak allocation by the number of packages.
            foreach (KeyValuePair<string, string> item in work) {
                if (token.IsCancellationRequested) { return; }
                string declaringGuid = item.Key;
                string spec = item.Value;

                try {
                    TryParseSpec(spec, out string owner, out string name, out string version);
                    string url = ThunderstoreHttp.DownloadUrlFor(owner, name, version);

                    using (MemoryStream archive = ThunderstoreHttp.DownloadCapped(url, cap, token, out string failure)) {
                        // Recorded against the spec, which is what ShouldAttempt looks up. This used to be keyed
                        // by the URL, so a package that failed to download was never backed off.
                        if (archive == null) {
                            NoteFailure(spec, failure);
                            continue;
                        }
                        Dictionary<string, List<string>> byGuid = HashArchiveDlls(archive, spec, declaringGuid, out int dllCount);
                        if (dllCount == 0) {
                            NoteFailure(spec, "the archive contains no DLLs");
                            continue;
                        }
                        foreach (KeyValuePair<string, List<string>> entry in byGuid) {
                            resolved.Enqueue(new ResolvedEntry { PluginID = entry.Key, Hashes = entry.Value, HashedFrom = spec });
                        }
                        NoteSuccess(spec);
                        Logger.LogInfo($"Resolved {spec}: {dllCount} DLL(s), hashes recorded for {byGuid.Count} plugin id(s).");
                    }
                } catch (OperationCanceledException) {
                    return;
                } catch (Exception e) {
                    NoteFailure(spec, e.Message);
                }
            }
        }

        /// <summary>
        /// Hashes every DLL in the archive, attributing each to the plugin GUID declared inside it where that
        /// can be read.
        ///
        /// Nothing is ever written to disk, which is why there is no zip-slip handling here: entry names never
        /// become paths, so path traversal is not expressible. It also means no temp files to clean up after a
        /// crash and no %TEMP% permissions to get wrong on a locked down host.
        /// </summary>
        private static Dictionary<string, List<string>> HashArchiveDlls(MemoryStream archiveBytes, string spec, string declaringGuid, out int dllCount) {
            Dictionary<string, List<string>> byGuid = new Dictionary<string, List<string>>();
            List<string> unattributed = new List<string>();
            dllCount = 0;
            long totalUncompressed = 0;

            using (ZipArchive zip = new ZipArchive(archiveBytes, ZipArchiveMode.Read, leaveOpen: true)) {
                if (zip.Entries.Count > MaxZipEntries) {
                    Logger.LogWarning($"{spec}: archive has {zip.Entries.Count} entries, over the {MaxZipEntries} limit. Skipping.");
                    return byGuid;
                }

                foreach (ZipArchiveEntry entry in zip.Entries) {
                    if (entry.FullName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) {
                        Logger.LogWarning($"{spec}: nested archive '{entry.FullName}' ignored; nested archives are not opened.");
                        continue;
                    }
                    if (!entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) { continue; }
                    if (entry.Length > MaxEntryBytes) {
                        Logger.LogWarning($"{spec}: '{entry.FullName}' is larger than the {MaxEntryBytes / (1024 * 1024)}MB per-file limit. Skipping it.");
                        continue;
                    }

                    using (MemoryStream dll = new MemoryStream()) {
                        using (Stream source = entry.Open()) {
                            byte[] chunk = new byte[64 * 1024];
                            int read;
                            while ((read = source.Read(chunk, 0, chunk.Length)) > 0) {
                                // Counted from bytes actually read, not from entry.Length: that comes out of
                                // the central directory and a crafted archive is free to lie about it. This is
                                // the zip bomb guard.
                                totalUncompressed += read;
                                if (totalUncompressed > MaxTotalUncompressedBytes || dll.Length + read > MaxEntryBytes) {
                                    Logger.LogWarning($"{spec}: uncompressed contents exceeded the safety limit while reading '{entry.FullName}'. Skipping the rest of the archive.");
                                    dllCount = 0;
                                    return byGuid;
                                }
                                dll.Write(chunk, 0, read);
                            }
                        }

                        dllCount++;
                        string hash;
                        dll.Position = 0;
                        using (SHA256 sha = SHA256.Create()) { hash = PluginHasher.ToHex(sha.ComputeHash(dll)); }

                        List<string> guids = ReadPluginGuids(dll);
                        if (guids == null) {
                            unattributed.Add(hash);
                            continue;
                        }
                        foreach (string guid in guids) {
                            if (string.IsNullOrEmpty(guid)) { continue; }
                            if (!byGuid.TryGetValue(guid, out List<string> list)) {
                                list = new List<string>();
                                byGuid[guid] = list;
                            }
                            if (!list.Contains(hash)) { list.Add(hash); }
                        }
                    }
                }
            }

            // Anything whose GUID could not be read (native DLL, obfuscated assembly, a shared library) is
            // offered to the entry that declared the package, so a pin still works when Cecil cannot help.
            // Accepting several hashes for one id is safe: the client reports one hash per id, and producing a
            // file matching any of them is still a preimage problem.
            if (unattributed.Count > 0 && !byGuid.ContainsKey(declaringGuid)) {
                Logger.LogInfo($"{spec}: could not read plugin metadata from {unattributed.Count} DLL(s); accepting all of them for {declaringGuid}.");
                byGuid[declaringGuid] = unattributed;
            }

            return byGuid;
        }

        /// <summary>
        /// Reads the GUIDs from a DLL's [BepInPlugin] attributes so each hash can be attributed to the exact
        /// plugin id it belongs to. Returns null for a native DLL, an obfuscated assembly, or anything Cecil
        /// cannot read; the caller then falls back to the whole-archive accepted set.
        /// </summary>
        internal static List<string> ReadPluginGuids(MemoryStream dll) {
            try {
                dll.Position = 0;
                using (AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(dll)) {
                    List<string> guids = new List<string>();
                    foreach (TypeDefinition type in assembly.MainModule.Types) {
                        foreach (CustomAttribute attribute in type.CustomAttributes) {
                            if (attribute.AttributeType.FullName != "BepInEx.BepInPlugin") { continue; }
                            if (attribute.ConstructorArguments.Count > 0) {
                                guids.Add(attribute.ConstructorArguments[0].Value as string);
                            }
                        }
                    }
                    return guids.Count > 0 ? guids : null;
                }
            } catch (Exception e) {
                Logger.LogDebug($"Could not read plugin metadata from an archive DLL: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Drives the resolver from the main thread: starts a pass when one is queued and applies finished
        /// results into the live mod settings.
        /// </summary>
        internal class ResolverBehaviour : MonoBehaviour {

            private DateTime modsFileStamp;

            public void Update() {
                // Packages first: one it fills in is work for the pass below.
                ThunderstorePackageFinder.Tick();
                DrainResults();

                if (!passQueued || running) { return; }
                passQueued = false;

                if (!ValConfig.ResolveThunderstoreHashes.Value) {
                    firstPassDone = true;
                    return;
                }

                List<KeyValuePair<string, string>> work = CollectWork();
                if (work.Count == 0) {
                    firstPassDone = true;
                    return;
                }

                try { modsFileStamp = File.GetLastWriteTimeUtc(ValConfig.ModsConfigFilePath); } catch (Exception) { modsFileStamp = DateTime.MinValue; }

                running = true;
                CancellationToken token = cancellation?.Token ?? CancellationToken.None;
                Logger.LogInfo($"Resolving Thunderstore hashes for {work.Count} package(s).");
                Task.Run(() => {
                    try {
                        RunPass(work, token);
                    } catch (Exception e) {
                        Logger.LogWarning($"Thunderstore resolve pass failed: {e.Message}");
                    } finally {
                        running = false;
                        // Settled means "the first pass is over", not "everything succeeded" - that is exactly
                        // what bounds the Strict grace window.
                        firstPassDone = true;
                    }
                });
            }

            private void DrainResults() {
                if (resolved.IsEmpty) { return; }

                bool changed = false;
                while (resolved.TryDequeue(out ResolvedEntry entry)) {
                    // Applied into the LIVE settings, never into a snapshot the worker captured: a file watcher
                    // reload during the pass has already replaced ModSettings, and results have to merge onto
                    // the admin's newest edit rather than resurrect a stale copy.
                    if (ApplyResolved(entry)) { changed = true; }
                }
                if (!changed) { return; }

                // Only an edit by somebody else counts; the package fill saving the file meanwhile does not.
                if (ModManager.ModsFileEditedSince(modsFileStamp)) {
                    Logger.LogInfo("Mods.yaml changed while hashes were being resolved; not overwriting it. Re-resolving against the new file.");
                    RequestPass("mods file changed during resolve");
                    return;
                }

                ModManager.PersistModSettings();
                try { modsFileStamp = File.GetLastWriteTimeUtc(ValConfig.ModsConfigFilePath); } catch (Exception) { /* keep the old stamp */ }
            }

            private static bool ApplyResolved(ResolvedEntry entry) {
                DataObjects.Mods settings = ModManager.ModSettings;
                if (settings == null || entry?.Hashes == null || entry.Hashes.Count == 0) { return false; }

                bool changed = false;
                foreach (Dictionary<string, DataObjects.Mod> list in new[] { settings.RequiredMods, settings.OptionalMods, settings.AdminOnlyMods }) {
                    // The null check is on the value, not just on the lookup: an entry written with nothing under
                    // it is a present key with a null value, which TryGetValue reports as a hit.
                    if (list == null || !list.TryGetValue(entry.PluginID, out DataObjects.Mod mod) || mod == null) { continue; }
                    // The admin may have taken the version off while the download ran.
                    if (!mod.IsVersioned()) { continue; }

                    // Never overwrite a hash an admin pinned by hand.
                    if (string.Equals(mod.HashSource, HashPolicy.SourceManual, StringComparison.OrdinalIgnoreCase)) {
                        Logger.LogDebug($"Keeping the manually pinned hash for {entry.PluginID}; not applying the resolved one.");
                        continue;
                    }

                    mod.AcceptedHashes = new List<string>(entry.Hashes);
                    mod.HashSource = HashPolicy.SourceThunderstore;
                    mod.HashedFrom = entry.HashedFrom;
                    changed = true;
                    Logger.LogInfo($"Recorded {entry.Hashes.Count} hash(es) for {entry.PluginID} from {entry.HashedFrom}.");
                }
                return changed;
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Start))]
        public static class ZNet_Start_Patch {
            [HarmonyPostfix]
            private static void Postfix(ZNet __instance) {
                if (!__instance.IsServer()) { return; }
                Initialize();
                ThunderstorePackageFinder.RequestRun("server start");
                RequestPass("server start");
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
        public static class ZNet_Shutdown_Patch {
            [HarmonyPostfix]
            private static void Postfix() {
                Teardown();
            }
        }
    }
}
