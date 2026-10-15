using BepInEx;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.mods {

    /// <summary>
    /// Server side: fills in the Thunderstore package of Mods.yaml entries that have none, so they can be offered
    /// to players (OfferModDownloads) and resolved (ResolveThunderstoreHashes) without an admin writing each one.
    ///
    /// Two sources, in order:
    /// 1. The mod's own folder. r2modman and Gale install a package to BepInEx/plugins/Owner-Name/ with its
    ///    manifest.json beside it, which names the package and the version installed. No network.
    /// 2. A search on thunderstore.io by the mod's name. A hit is only trusted after downloading that exact
    ///    version and finding the plugin in it - and, for a mod the server runs itself, finding the very file the
    ///    server has, by hash. Anything less would be guessing at which package a name means.
    ///
    /// What it fills in is marked with packageSource, and only those entries are ever touched again: a
    /// thunderstorePackage an admin wrote is theirs. Marked entries are re-derived when the mod is updated, so a
    /// filled-in version never goes stale.
    /// </summary>
    internal static class ThunderstorePackageFinder {

        internal const string SourceManifest = "Manifest";
        internal const string SourceLookup = "Thunderstore";

        private const string ListingUrl = "https://thunderstore.io/api/cyberstorm/listing/valheim/";
        private const long MaxListingBytes = 4L * 1024 * 1024;
        private const int MaxSearchTerms = 3;
        private const int MaxCandidatesPerMod = 3;

        /// <summary>One mod to look up, captured on the main thread so the worker touches nothing live.</summary>
        internal sealed class Candidate {
            internal string PluginId;
            internal string Name;
            internal string Version;
            internal string DllPath;
            /// <summary>Lowercase SHA256 of the DLL this server loaded, when it loads the mod.</summary>
            internal string LocalHash;
            /// <summary>From a manifest.json that names the package but not its owner.</summary>
            internal string ManifestName;
            internal string ManifestVersion;
            /// <summary>Owner and name of a package found before, tried first when the mod has been updated.</summary>
            internal string KnownOwner;
            internal string KnownName;
            /// <summary>The package this entry already has from an earlier fill, which a failed lookup takes away.</summary>
            internal string CurrentSpec;
        }

        /// <summary>A lookup's answer. A null Spec means "not on Thunderstore at this version".</summary>
        private sealed class Found {
            internal string PluginId;
            internal string Spec;
            internal string LocalHash;
            internal string Version;
        }

        private static readonly ConcurrentQueue<Found> found = new ConcurrentQueue<Found>();
        // "pluginId|version" this session has already searched for and not found. Not persisted, so a restart (or
        // an update of the mod) is a fresh try, and a mod that is not on Thunderstore costs one search per start.
        private static readonly HashSet<string> notFound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static volatile bool running;
        private static volatile bool runQueued;
        private static bool reportPending;
        private static DateTime modsFileStamp;

        internal static bool Enabled { get { return ValConfig.AutoFillThunderstorePackages != null && ValConfig.AutoFillThunderstorePackages.Value; } }

        internal static void RequestRun(string reason) {
            if (!Enabled) { return; }
            Logger.LogDebug($"Thunderstore package fill requested ({reason}).");
            runQueued = true;
        }

        internal static void Teardown() {
            runQueued = false;
            reportPending = false;
            while (found.TryDequeue(out Found _)) { }
            lock (notFound) { notFound.Clear(); }
        }

        /// <summary>Main thread, every frame from the resolver's host: applies finished lookups, starts queued runs.</summary>
        internal static void Tick() {
            DrainFound();
            if (!runQueued || running || !Enabled) { return; }
            if (ModManager.ModSettings == null) { return; }
            runQueued = false;
            Run();
        }

        private static IEnumerable<KeyValuePair<string, DataObjects.Mod>> Entries(DataObjects.Mods settings) {
            foreach (Dictionary<string, DataObjects.Mod> list in new[] { settings.RequiredMods, settings.OptionalMods, settings.AdminOnlyMods }) {
                if (list == null) { continue; }
                foreach (KeyValuePair<string, DataObjects.Mod> entry in list) {
                    if (entry.Value != null) { yield return entry; }
                }
            }
        }

        /// <summary>
        /// Whether this entry is ours to fill: it has no package, or it has one we filled in before. A package with
        /// no packageSource beside it was written by an admin and is never touched.
        /// </summary>
        private static bool Ours(DataObjects.Mod mod) {
            if (string.IsNullOrWhiteSpace(mod.ThunderstorePackage)) { return true; }
            return string.Equals(mod.PackageSource, SourceManifest, StringComparison.OrdinalIgnoreCase)
                || string.Equals(mod.PackageSource, SourceLookup, StringComparison.OrdinalIgnoreCase);
        }

        private static void Run() {
            DataObjects.Mods settings = ModManager.ModSettings;
            bool changed = false;
            List<Candidate> lookups = new List<Candidate>();

            foreach (KeyValuePair<string, DataObjects.Mod> entry in Entries(settings)) {
                DataObjects.Mod mod = entry.Value;
                // No version: any version is allowed, so there is no particular package to name.
                if (!Ours(mod) || !mod.IsVersioned()) { continue; }
                Candidate candidate = Describe(entry.Key, mod);
                if (!string.IsNullOrEmpty(mod.PackageSource)) { candidate.CurrentSpec = mod.ThunderstorePackage; }

                string fromManifest = ManifestSpec(candidate.DllPath, Paths.PluginPath, out candidate.ManifestName, out candidate.ManifestVersion);
                if (fromManifest != null) {
                    changed |= Apply(mod, entry.Key, fromManifest, SourceManifest);
                    continue;
                }

                string version = ThunderstoreVersion(candidate.ManifestVersion ?? candidate.Version);
                // Something we looked up before, still at the version it was found for, needs nothing.
                if (string.Equals(mod.PackageSource, SourceLookup, StringComparison.OrdinalIgnoreCase)
                    && ThunderstoreResolver.TryParseSpec(mod.ThunderstorePackage, out string owner, out string name, out string pinned)) {
                    if (pinned == version) { continue; }
                    candidate.KnownOwner = owner;
                    candidate.KnownName = name;
                }
                lock (notFound) {
                    if (notFound.Contains(entry.Key + "|" + version)) { continue; }
                }
                lookups.Add(candidate);
            }

            if (changed) { Persist("filled from installed package manifests"); }

            if (lookups.Count == 0) {
                Report(settings);
                return;
            }

            try { modsFileStamp = File.GetLastWriteTimeUtc(ValConfig.ModsConfigFilePath); } catch (Exception) { modsFileStamp = DateTime.MinValue; }
            long cap = (long)ValConfig.ThunderstoreMaxArchiveMB.Value * 1024 * 1024;
            running = true;
            reportPending = true;
            Logger.LogInfo($"Looking up {lookups.Count} mod(s) on Thunderstore to fill in their package: {string.Join(", ", lookups.Select(c => c.PluginId).ToArray())}.");
            Task.Run(() => {
                try {
                    foreach (Candidate candidate in lookups) {
                        string spec;
                        bool conclusive;
                        try {
                            spec = Lookup(candidate, cap, CancellationToken.None, out conclusive);
                        } catch (Exception e) {
                            // No network, most likely. One mod's failure is not a reason to skip the rest.
                            Logger.LogInfo($"Could not look up {candidate.PluginId} on Thunderstore: {e.Message}");
                            continue;
                        }
                        if (spec != null) {
                            found.Enqueue(new Found { PluginId = candidate.PluginId, Spec = spec });
                            continue;
                        }
                        lock (notFound) { notFound.Add(candidate.PluginId + "|" + ThunderstoreVersion(candidate.ManifestVersion ?? candidate.Version)); }
                        // Only a definite "not there" takes away a package found before. Thunderstore being
                        // unreachable says nothing about the mod, and the package stays until it can be asked.
                        if (conclusive && candidate.CurrentSpec != null) {
                            found.Enqueue(new Found { PluginId = candidate.PluginId, Spec = null, LocalHash = candidate.LocalHash, Version = candidate.Version });
                        }
                    }
                } catch (Exception e) {
                    Logger.LogWarning($"Thunderstore package lookup failed: {e.Message}");
                } finally {
                    running = false;
                }
            });
        }

        private static Candidate Describe(string pluginId, DataObjects.Mod mod) {
            Candidate candidate = new Candidate { PluginId = pluginId, Name = mod.Name ?? pluginId, Version = mod.Version };
            if (ModManager.ActiveMods != null && ModManager.ActiveMods.TryGetValue(pluginId, out BepInEx.BaseUnityPlugin plugin) && plugin != null) {
                candidate.DllPath = plugin.Info?.Location;
                candidate.Name = plugin.Info?.Metadata?.Name ?? candidate.Name;
                candidate.Version = plugin.Info?.Metadata?.Version?.ToString() ?? candidate.Version;
                candidate.LocalHash = PluginHasher.Get(pluginId)?.Hash;
            }
            return candidate;
        }

        private static bool Apply(DataObjects.Mod mod, string pluginId, string spec, string source) {
            if (string.Equals(mod.ThunderstorePackage, spec, StringComparison.OrdinalIgnoreCase)
                && string.Equals(mod.PackageSource, source, StringComparison.OrdinalIgnoreCase)) {
                return false;
            }
            Logger.LogInfo($"Filled in the Thunderstore package of {pluginId}: {spec} (from {(source == SourceManifest ? "its manifest.json" : "a Thunderstore lookup")}).");
            mod.ThunderstorePackage = spec;
            mod.PackageSource = source;
            return true;
        }

        private static void DrainFound() {
            if (found.IsEmpty) {
                // Not while another run is queued: its results would make this report out of date at once.
                if (reportPending && !running && !runQueued && ModManager.ModSettings != null) {
                    reportPending = false;
                    Report(ModManager.ModSettings);
                }
                return;
            }

            // Only an edit by somebody else counts. The hash resolver saving the file meanwhile does not, and
            // treating it as one threw these lookups away and ran them all again.
            if (ModManager.ModsFileEditedSince(modsFileStamp)) {
                // The admin edited the file while the lookups ran. Their edit wins; look again against it.
                while (found.TryDequeue(out Found _)) { }
                Logger.LogInfo("Mods.yaml changed while Thunderstore packages were being looked up; not overwriting it. Looking again against the new file.");
                runQueued = true;
                return;
            }

            DataObjects.Mods settings = ModManager.ModSettings;
            bool changed = false;
            bool recordLocal = ValConfig.RecordHashesForLoadedMods == null || ValConfig.RecordHashesForLoadedMods.Value;
            while (found.TryDequeue(out Found result)) {
                if (settings == null) { continue; }
                foreach (KeyValuePair<string, DataObjects.Mod> entry in Entries(settings)) {
                    if (entry.Key != result.PluginId || !Ours(entry.Value)) { continue; }
                    if (result.Spec != null) {
                        changed |= Apply(entry.Value, entry.Key, result.Spec, SourceLookup);
                        continue;
                    }
                    string dropped = entry.Value.ThunderstorePackage;
                    if (DropPackage(entry.Value, recordLocal ? result.LocalHash : null, result.Version)) {
                        changed = true;
                        Logger.LogInfo($"Dropped the Thunderstore package of {entry.Key} ({dropped}): version {result.Version} is not on Thunderstore, or is not the build this server runs. "
                            + (string.IsNullOrEmpty(result.LocalHash) ? "It has no recorded file hash until it is found again." : "Clients are checked against this server's own file again."));
                    }
                }
            }
            if (changed) { Persist("filled from Thunderstore lookups"); }
        }

        private static void Persist(string why) {
            ModManager.PersistModSettings();
            try { modsFileStamp = File.GetLastWriteTimeUtc(ValConfig.ModsConfigFilePath); } catch (Exception) { /* keep the old stamp */ }
            // A newly named package is something the hash resolver may now have work for.
            ThunderstoreResolver.RequestPass(why);
        }

        /// <summary>
        /// With OfferModDownloads on, says which listed mods still cannot be offered to a player and why - the
        /// question an admin otherwise only gets answered by a player who sees no Fix mods button.
        /// </summary>
        private static void Report(DataObjects.Mods settings) {
            if (ValConfig.OfferModDownloads == null || !ValConfig.OfferModDownloads.Value) { return; }
            List<string> missingPackage = new List<string>();
            List<string> missingHash = new List<string>();
            List<string> anyVersion = new List<string>();
            foreach (KeyValuePair<string, DataObjects.Mod> entry in Entries(settings)) {
                if (!entry.Value.IsVersioned()) { anyVersion.Add(entry.Key); }
                else if (!modsync.ModSyncPlan.TryPinnedSpec(entry.Value, out string _)) { missingPackage.Add(entry.Key); }
                else if (!entry.Value.HasRecordedHash()) { missingHash.Add(entry.Key); }
            }
            if (anyVersion.Count > 0) {
                Logger.LogInfo($"OfferModDownloads: these mods are listed without a version, so any version is allowed and they are neither file-checked nor offered to players: {string.Join(", ", anyVersion.ToArray())}.");
            }
            if (missingPackage.Count > 0) {
                Logger.LogInfo($"OfferModDownloads: these mods cannot be offered to players because no Thunderstore package is known for them: {string.Join(", ", missingPackage.ToArray())}. Give them a 'thunderstorePackage' in Mods.yaml.");
            }
            if (missingHash.Count > 0) {
                Logger.LogInfo($"OfferModDownloads: these mods cannot be offered to players because the server has no file hash for them: {string.Join(", ", missingHash.ToArray())}. Turn on ResolveThunderstoreHashes, or pin a hash by hand.");
            }
        }

        // ---- The parts below take everything as arguments, so they can be checked without the game. ----

        /// <summary>
        /// Takes away a filled-in package that can no longer be found for the version this server has.
        ///
        /// Leaving it would be worse than having none. Once ResolveThunderstoreHashes has recorded a package's
        /// hashes they outrank the server's own file (only Local hashes are refreshed at startup), so a mod the
        /// server has since updated to a build that is not on Thunderstore would go on demanding the old one of
        /// every client. A mod the server runs goes back to its own file's hash; one it does not run is left
        /// without a hash until a package is found again. Hashes an admin pinned (Manual) are not touched.
        /// </summary>
        internal static bool DropPackage(DataObjects.Mod mod, string localHash, string version) {
            if (mod == null || string.IsNullOrEmpty(mod.PackageSource) || string.IsNullOrEmpty(mod.ThunderstorePackage)) { return false; }
            mod.ThunderstorePackage = null;
            mod.PackageSource = null;
            if (string.Equals(mod.HashSource, HashPolicy.SourceThunderstore, StringComparison.OrdinalIgnoreCase)) {
                if (!string.IsNullOrEmpty(localHash)) {
                    mod.AcceptedHashes = new List<string> { localHash };
                    mod.HashSource = HashPolicy.SourceLocal;
                    mod.HashedFrom = "local:" + version;
                } else {
                    mod.AcceptedHashes = null;
                    mod.HashSource = null;
                    mod.HashedFrom = null;
                }
            }
            return true;
        }

        /// <summary>
        /// "Owner-Name-Version" from the manifest.json of the package folder a plugin DLL lives in, or null.
        ///
        /// The folder is the first one under BepInEx/plugins on the DLL's path. The manifest names the package and
        /// version but not its owner; that comes from the folder name, which r2modman and Gale write as
        /// "Owner-Name". When the folder is named some other way the manifest's name and version are still handed
        /// back, so a lookup can search by the package name rather than the plugin's.
        /// </summary>
        internal static string ManifestSpec(string dllPath, string pluginsRoot, out string manifestName, out string manifestVersion) {
            manifestName = null;
            manifestVersion = null;
            if (string.IsNullOrEmpty(dllPath) || !modsync.ModSyncPlan.IsUnder(dllPath, pluginsRoot)) { return null; }
            try {
                string root = Path.GetFullPath(pluginsRoot).TrimEnd('\\', '/');
                string relative = Path.GetFullPath(dllPath).Substring(root.Length + 1);
                string[] parts = relative.Split('\\', '/');
                if (parts.Length < 2) { return null; } // loose in plugins/, no package folder
                string folder = parts[0];
                string manifest = Path.Combine(Path.Combine(root, folder), "manifest.json");
                if (!File.Exists(manifest)) { return null; }

                JObject json = JObject.Parse(File.ReadAllText(manifest));
                string name = (string)json["name"];
                string version = (string)json["version_number"];
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(version)) { return null; }
                manifestName = name;
                manifestVersion = version;

                string suffix = "-" + name;
                if (!folder.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { return null; }
                string spec = folder.Substring(0, folder.Length - suffix.Length) + "-" + name + "-" + version;
                return ThunderstoreResolver.TryParseSpec(spec, out _, out _, out string parsedVersion) && parsedVersion != null ? spec : null;
            } catch (Exception e) {
                Logger.LogDebug($"Could not read the package manifest beside {dllPath}: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// A plugin version as Thunderstore writes one, Major.Minor.Patch, or null. BepInEx versions are
        /// System.Version, so "1.2" and "1.2.3.0" are both normal; a fourth part other than 0 has no Thunderstore
        /// equivalent.
        /// </summary>
        internal static string ThunderstoreVersion(string version) {
            if (string.IsNullOrEmpty(version)) { return null; }
            try {
                System.Version v = new System.Version(version);
                if (v.Revision > 0) { return null; }
                return string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}", v.Major, v.Minor, Math.Max(v.Build, 0));
            } catch (Exception) {
                return null;
            }
        }

        /// <summary>Letters and digits only, lowercased: "Valheim Armory", "valheim_armory" and "ValheimArmory" agree.</summary>
        internal static string Normalize(string text) {
            if (string.IsNullOrEmpty(text)) { return ""; }
            StringBuilder s = new StringBuilder(text.Length);
            foreach (char c in text) {
                if (char.IsLetterOrDigit(c)) { s.Append(char.ToLowerInvariant(c)); }
            }
            return s.ToString();
        }

        /// <summary>Whether every request a lookup made got an answer, so that "not found" can be believed.</summary>
        private sealed class LookupState {
            internal bool Unanswered;
        }

        /// <summary>
        /// Finds the package for one mod on thunderstore.io and proves it, or returns null. Worker thread.
        ///
        /// <paramref name="conclusive"/> says whether a null can be trusted: true when Thunderstore answered every
        /// search and download (a version that does not exist is an answer), false when any of them failed to get
        /// one - a timeout, a server error, no network.
        /// </summary>
        internal static string Lookup(Candidate candidate, long capBytes, CancellationToken token, out bool conclusive) {
            LookupState state = new LookupState();
            string spec = Lookup(candidate, capBytes, token, state);
            conclusive = spec == null && !state.Unanswered;
            return spec;
        }

        private static string Lookup(Candidate candidate, long capBytes, CancellationToken token, LookupState state) {
            string version = ThunderstoreVersion(candidate.ManifestVersion ?? candidate.Version);
            if (version == null) {
                Logger.LogInfo($"No Thunderstore package looked up for {candidate.PluginId}: its version '{candidate.Version}' is not one Thunderstore can have.");
                return null;
            }

            List<KeyValuePair<string, string>> tried = new List<KeyValuePair<string, string>>();
            foreach (KeyValuePair<string, string> package in Candidates(candidate, token, state)) {
                if (tried.Count >= MaxCandidatesPerMod) { break; }
                if (tried.Any(t => string.Equals(t.Key, package.Key, StringComparison.OrdinalIgnoreCase) && string.Equals(t.Value, package.Value, StringComparison.OrdinalIgnoreCase))) { continue; }
                tried.Add(package);

                string spec = $"{package.Key}-{package.Value}-{version}";
                if (!ThunderstoreResolver.TryParseSpec(spec, out string owner, out string name, out _)) { continue; }
                using (MemoryStream archive = ThunderstoreHttp.DownloadCapped(ThunderstoreHttp.DownloadUrlFor(owner, name, version), capBytes, token, out string why)) {
                    if (archive == null) {
                        // A 404 is Thunderstore saying the version does not exist; anything else is no answer.
                        if (why == null || !why.StartsWith("HTTP 404", StringComparison.Ordinal)) { state.Unanswered = true; }
                        Logger.LogDebug($"{spec} for {candidate.PluginId}: {why}");
                        continue;
                    }
                    if (Proves(archive, candidate, out string reason)) {
                        return spec;
                    }
                    Logger.LogInfo($"{spec} is not the package of {candidate.PluginId}: {reason}.");
                }
            }

            Logger.LogInfo(tried.Count == 0
                ? $"No Thunderstore package found for {candidate.PluginId} by its name."
                : $"No Thunderstore package found for {candidate.PluginId} at version {version} (tried {string.Join(", ", tried.Select(t => t.Key + "-" + t.Value).ToArray())}).");
            return null;
        }

        /// <summary>
        /// Packages that might be this mod: a package found before, then search results whose name matches one of
        /// the mod's names, a package whose owner matches the plugin id's author first.
        /// </summary>
        private static IEnumerable<KeyValuePair<string, string>> Candidates(Candidate candidate, CancellationToken token, LookupState state) {
            if (!string.IsNullOrEmpty(candidate.KnownOwner) && !string.IsNullOrEmpty(candidate.KnownName)) {
                yield return new KeyValuePair<string, string>(candidate.KnownOwner, candidate.KnownName);
            }

            List<string> names = new List<string>();
            foreach (string name in new[] {
                candidate.ManifestName,
                candidate.Name,
                candidate.PluginId.IndexOf('.') >= 0 ? candidate.PluginId.Substring(candidate.PluginId.LastIndexOf('.') + 1) : candidate.PluginId,
                string.IsNullOrEmpty(candidate.DllPath) ? null : Path.GetFileNameWithoutExtension(candidate.DllPath),
            }) {
                if (Normalize(name).Length >= 3 && !names.Any(n => Normalize(n) == Normalize(name))) { names.Add(name); }
            }
            HashSet<string> wanted = new HashSet<string>(names.Select(Normalize));
            string author = candidate.PluginId.IndexOf('.') >= 0 ? Normalize(candidate.PluginId.Substring(0, candidate.PluginId.IndexOf('.'))) : "";

            foreach (string term in names.Take(MaxSearchTerms)) {
                List<KeyValuePair<string, string>> results = Search(term, token, state);
                foreach (KeyValuePair<string, string> hit in results
                    .Where(r => wanted.Contains(Normalize(r.Value)))
                    .OrderBy(r => author.Length > 0 && Normalize(r.Key) == author ? 0 : 1)) {
                    yield return hit;
                }
            }
        }

        /// <summary>(owner, name) pairs from one listing search, or none.</summary>
        private static List<KeyValuePair<string, string>> Search(string term, CancellationToken token, LookupState state) {
            List<KeyValuePair<string, string>> results = new List<KeyValuePair<string, string>>();
            string url = ListingUrl + "?q=" + Uri.EscapeDataString(term) + "&ordering=most-downloaded&page=1&nsfw=true&deprecated=true";
            using (MemoryStream body = ThunderstoreHttp.DownloadCapped(url, MaxListingBytes, token, out string why)) {
                if (body == null) {
                    state.Unanswered = true;
                    Logger.LogDebug($"Thunderstore search for '{term}' failed: {why}");
                    return results;
                }
                try {
                    JObject json = JObject.Parse(Encoding.UTF8.GetString(body.ToArray()));
                    if (!(json["results"] is JArray hits)) { return results; }
                    foreach (JToken hit in hits) {
                        string owner = (string)hit["namespace"];
                        string name = (string)hit["name"];
                        if (!string.IsNullOrEmpty(owner) && !string.IsNullOrEmpty(name)) { results.Add(new KeyValuePair<string, string>(owner, name)); }
                    }
                } catch (Exception e) {
                    state.Unanswered = true;
                    Logger.LogDebug($"Thunderstore search for '{term}' returned something unreadable: {e.Message}");
                }
            }
            return results;
        }

        /// <summary>
        /// The proof: some DLL in the archive declares the plugin, and when the server runs the mod, that DLL is
        /// byte for byte the one it runs. Same archive limits as the hash resolver.
        /// </summary>
        internal static bool Proves(MemoryStream archive, Candidate candidate, out string reason) {
            reason = "it does not contain the plugin";
            long total = 0;
            using (ZipArchive zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true)) {
                if (zip.Entries.Count > ThunderstoreResolver.MaxZipEntries) {
                    reason = "the archive has too many files";
                    return false;
                }
                foreach (ZipArchiveEntry entry in zip.Entries) {
                    if (!entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) { continue; }
                    using (MemoryStream dll = new MemoryStream()) {
                        using (Stream source = entry.Open()) {
                            byte[] chunk = new byte[64 * 1024];
                            int read;
                            while ((read = source.Read(chunk, 0, chunk.Length)) > 0) {
                                total += read;
                                if (total > ThunderstoreResolver.MaxTotalUncompressedBytes || dll.Length + read > ThunderstoreResolver.MaxEntryBytes) {
                                    reason = "the archive is larger than the safety limit once unpacked";
                                    return false;
                                }
                                dll.Write(chunk, 0, read);
                            }
                        }
                        List<string> guids = ThunderstoreResolver.ReadPluginGuids(dll);
                        if (guids == null || !guids.Contains(candidate.PluginId)) { continue; }
                        if (string.IsNullOrEmpty(candidate.LocalHash)) { return true; }

                        dll.Position = 0;
                        string hash;
                        using (SHA256 sha = SHA256.Create()) { hash = PluginHasher.ToHex(sha.ComputeHash(dll)); }
                        if (string.Equals(hash, candidate.LocalHash, StringComparison.OrdinalIgnoreCase)) { return true; }
                        reason = "it contains the plugin, but not the build this server runs";
                    }
                }
            }
            return false;
        }
    }
}
