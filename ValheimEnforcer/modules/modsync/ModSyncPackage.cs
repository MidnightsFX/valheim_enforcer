using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using ValheimEnforcer.modules.mods;

namespace ValheimEnforcer.modules.modsync {

    /// <summary>
    /// Turns one downloaded Thunderstore archive into files staged for install, after proving it is the build the
    /// server checks for.
    ///
    /// Two passes over the archive, which is already in memory. The first reads and hashes every DLL and decides
    /// where every entry would go; nothing is written unless every plugin the package was downloaded for hashes to
    /// a value the server recorded. Only then does the second pass write files, and only under the staging folder.
    /// The real plugins folder is not touched here at all - that happens after the game has exited.
    /// </summary>
    internal static class ModSyncPackage {

        internal enum Place { Skip, Plugins, Config, Unsupported }

        /// <summary>Where one archive entry goes, and its path relative to that place.</summary>
        internal struct Mapping {
            internal Place Place;
            internal string Relative;
        }

        /// <summary>One package, verified and written under the staging folder.</summary>
        internal sealed class Staged {
            internal ModSyncPlan.PackageInstall Package;
            /// <summary>Becomes BepInEx/plugins/Owner-Name.</summary>
            internal string PluginsDir;
            /// <summary>Copied into BepInEx/config, file by file, only where no file of that name exists yet.</summary>
            internal string ConfigDir;
        }

        private sealed class DllInfo {
            internal string Entry;
            internal string Hash;
            internal List<KeyValuePair<string, string>> Plugins;
        }

        /// <summary>
        /// Where an archive entry goes, following the layout r2modman and Gale use for a BepInEx package: a
        /// leading BepInEx/ folder is dropped, plugins/ and everything loose at the root go to the package's
        /// own plugins folder, config/ goes to BepInEx/config. Patchers, core and monomod are refused - they
        /// load before any plugin, and a server is not the place to decide a player gets one.
        ///
        /// Returns false for an entry name that could escape the folder it is written into.
        /// </summary>
        internal static bool TryMap(string entryName, out Mapping mapping) {
            mapping = new Mapping { Place = Place.Skip };
            if (string.IsNullOrEmpty(entryName)) { return true; }
            if (entryName.IndexOf(':') >= 0 || entryName.StartsWith("/") || entryName.StartsWith("\\")) { return false; }

            List<string> parts = entryName.Replace('\\', '/').Split('/').Where(p => p.Length > 0).ToList();
            if (parts.Count == 0) { return true; }
            foreach (string part in parts) {
                if (part == "." || part == ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { return false; }
            }
            // A directory entry ends in a separator and carries no file of its own.
            if (entryName.EndsWith("/") || entryName.EndsWith("\\")) { return true; }
            if (string.Equals(parts[0], "__MACOSX", StringComparison.OrdinalIgnoreCase)) { return true; }

            if (string.Equals(parts[0], "BepInEx", StringComparison.OrdinalIgnoreCase)) {
                parts.RemoveAt(0);
                if (parts.Count == 0) { return true; }
            }

            switch (parts[0].ToLowerInvariant()) {
                case "plugins":
                    if (parts.Count == 1) { return true; }
                    mapping = new Mapping { Place = Place.Plugins, Relative = Path.Combine(parts.Skip(1).ToArray()) };
                    return true;
                case "config":
                    if (parts.Count == 1) { return true; }
                    mapping = new Mapping { Place = Place.Config, Relative = Path.Combine(parts.Skip(1).ToArray()) };
                    return true;
                case "patchers":
                case "core":
                case "monomod":
                    mapping = new Mapping { Place = Place.Unsupported, Relative = string.Join("/", parts) };
                    return true;
                default:
                    mapping = new Mapping { Place = Place.Plugins, Relative = Path.Combine(parts.ToArray()) };
                    return true;
            }
        }

        /// <summary>
        /// Verifies one archive against the server's record and writes it under <paramref name="stagingRoot"/>.
        /// Returns false with <paramref name="failure"/> in plain words when anything does not check out, in which
        /// case nothing for this package is left in the staging folder.
        /// </summary>
        internal static bool Stage(MemoryStream archive, ModSyncPlan.PackageInstall package, string stagingRoot, out Staged staged, out string failure) {
            staged = null;
            failure = null;
            string pluginsDir = Path.Combine(stagingRoot, package.FolderName);
            string configDir = Path.Combine(stagingRoot, package.FolderName + ".config");
            // Cleared before anything else, so a refusal below never leaves an earlier attempt's files behind
            // looking like this one's.
            DeleteDirectory(pluginsDir);
            DeleteDirectory(configDir);

            try {
                using (ZipArchive zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true)) {
                    if (zip.Entries.Count > ThunderstoreResolver.MaxZipEntries) {
                        failure = $"{package.Spec} has {zip.Entries.Count} files, more than the {ThunderstoreResolver.MaxZipEntries} allowed";
                        return false;
                    }

                    // Pass one: map every entry and read every DLL. Nothing is written.
                    List<DllInfo> dlls = new List<DllInfo>();
                    long totalUncompressed = 0;
                    foreach (ZipArchiveEntry entry in zip.Entries) {
                        if (!TryMap(entry.FullName, out Mapping mapping)) {
                            failure = $"{package.Spec} contains a file named '{entry.FullName}', which is not a safe path";
                            return false;
                        }
                        if (mapping.Place == Place.Unsupported) {
                            failure = $"{package.Spec} contains '{mapping.Relative}'. Patchers and BepInEx core files are not installed by a server; install this package with your mod manager";
                            return false;
                        }
                        if (mapping.Place != Place.Plugins || !entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) { continue; }

                        byte[] bytes = ReadCapped(entry, ref totalUncompressed);
                        if (bytes == null) {
                            failure = $"{package.Spec} is larger than the safety limit once unpacked";
                            return false;
                        }
                        string hash;
                        using (SHA256 sha = SHA256.Create()) { hash = PluginHasher.ToHex(sha.ComputeHash(bytes)); }
                        dlls.Add(new DllInfo { Entry = entry.FullName, Hash = hash, Plugins = ReadPlugins(bytes) });
                    }

                    if (!Verify(package, dlls, out failure)) { return false; }

                    // Pass two: write, into the folders cleared above.
                    totalUncompressed = 0;
                    foreach (ZipArchiveEntry entry in zip.Entries) {
                        TryMap(entry.FullName, out Mapping mapping);
                        if (mapping.Place != Place.Plugins && mapping.Place != Place.Config) { continue; }

                        string root = mapping.Place == Place.Plugins ? pluginsDir : configDir;
                        string target = Path.GetFullPath(Path.Combine(root, mapping.Relative));
                        // TryMap already refused anything that could climb out; this is the check that does not
                        // depend on it being right.
                        if (!ModSyncPlan.IsUnder(target, root)) {
                            failure = $"{package.Spec} contains a file that would be written outside its own folder";
                            DeleteDirectory(pluginsDir);
                            DeleteDirectory(configDir);
                            return false;
                        }

                        byte[] bytes = ReadCapped(entry, ref totalUncompressed);
                        if (bytes == null) {
                            failure = $"{package.Spec} is larger than the safety limit once unpacked";
                            DeleteDirectory(pluginsDir);
                            DeleteDirectory(configDir);
                            return false;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        File.WriteAllBytes(target, bytes);
                    }
                }
            } catch (InvalidDataException e) {
                failure = $"{package.Spec} is not a readable zip archive ({e.Message})";
                DeleteDirectory(pluginsDir);
                DeleteDirectory(configDir);
                return false;
            }

            if (!Directory.Exists(pluginsDir)) {
                failure = $"{package.Spec} contains no plugin files";
                DeleteDirectory(configDir);
                return false;
            }
            staged = new Staged { Package = package, PluginsDir = pluginsDir, ConfigDir = Directory.Exists(configDir) ? configDir : null };
            return true;
        }

        /// <summary>
        /// The whole point of pinning: every plugin this package was downloaded for has to be in it, in a DLL whose
        /// hash the server recorded, at the version the server enforces. The hash ties the download to the exact
        /// file the server checks on the next connection, so what passes here is what passes there.
        /// </summary>
        private static bool Verify(ModSyncPlan.PackageInstall package, List<DllInfo> dlls, out string failure) {
            failure = null;
            foreach (KeyValuePair<string, List<string>> wanted in package.AcceptedHashes) {
                string id = wanted.Key;
                HashSet<string> accepted = new HashSet<string>(wanted.Value ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

                // The DLLs that declare this plugin. When none can be read (an obfuscated build, say) the server
                // recorded the package's unattributed DLLs for it instead, so any of those matching is the match.
                List<DllInfo> declaring = dlls.Where(d => d.Plugins.Any(p => p.Key == id)).ToList();
                List<DllInfo> candidates = declaring.Count > 0 ? declaring : dlls;
                DllInfo match = candidates.FirstOrDefault(d => accepted.Contains(d.Hash));
                if (match == null) {
                    failure = declaring.Count > 0
                        ? $"{package.Spec}: {id} in the download is not the file the server recorded (got {Short(declaring[0].Hash)}, the server accepts {string.Join(", ", accepted.Select(Short).ToArray())})"
                        : $"{package.Spec} does not contain {id}, or not the build the server recorded";
                    return false;
                }

                if (package.RequiredVersions.TryGetValue(id, out string required)) {
                    KeyValuePair<string, string> plugin = match.Plugins.FirstOrDefault(p => p.Key == id);
                    if (plugin.Key != null && NormalizeVersion(plugin.Value) != NormalizeVersion(required)) {
                        failure = $"{package.Spec} carries {id} {plugin.Value}, but the server requires {required}. The server's Mods.yaml pins a package that does not match its own version requirement";
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// The version string a client reports for a plugin is BepInEx's System.Version of the attribute text, so
        /// the comparison goes through the same conversion: "1.02.3" is reported as "1.2.3".
        /// </summary>
        private static string NormalizeVersion(string version) {
            try { return new System.Version(version).ToString(); } catch (Exception) { return version; }
        }

        private static string Short(string hash) {
            return string.IsNullOrEmpty(hash) || hash.Length <= 12 ? hash : hash.Substring(0, 12);
        }

        /// <summary>
        /// One entry's bytes, counted as they are read rather than trusted from the central directory - a crafted
        /// archive is free to lie about sizes. Null once the per-file or whole-archive limit is passed.
        /// </summary>
        private static byte[] ReadCapped(ZipArchiveEntry entry, ref long totalUncompressed) {
            using (Stream source = entry.Open())
            using (MemoryStream buffer = new MemoryStream()) {
                byte[] chunk = new byte[64 * 1024];
                int read;
                while ((read = source.Read(chunk, 0, chunk.Length)) > 0) {
                    totalUncompressed += read;
                    if (totalUncompressed > ThunderstoreResolver.MaxTotalUncompressedBytes || buffer.Length + read > ThunderstoreResolver.MaxEntryBytes) {
                        return null;
                    }
                    buffer.Write(chunk, 0, read);
                }
                return buffer.ToArray();
            }
        }

        /// <summary>
        /// (plugin id, version) for every [BepInPlugin] in a DLL, read with Cecil so nothing is loaded. Top-level
        /// types only, matching the resolver, so a plugin is attributed to the same DLL on both sides. Empty for
        /// anything Cecil cannot read.
        /// </summary>
        private static List<KeyValuePair<string, string>> ReadPlugins(byte[] dll) {
            List<KeyValuePair<string, string>> plugins = new List<KeyValuePair<string, string>>();
            try {
                using (MemoryStream stream = new MemoryStream(dll))
                using (AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(stream)) {
                    foreach (TypeDefinition type in assembly.MainModule.Types) {
                        foreach (CustomAttribute attribute in type.CustomAttributes) {
                            if (attribute.AttributeType.FullName != "BepInEx.BepInPlugin") { continue; }
                            if (attribute.ConstructorArguments.Count < 3) { continue; }
                            string id = attribute.ConstructorArguments[0].Value as string;
                            if (string.IsNullOrEmpty(id)) { continue; }
                            plugins.Add(new KeyValuePair<string, string>(id, attribute.ConstructorArguments[2].Value as string));
                        }
                    }
                }
            } catch (Exception e) {
                Logger.LogDebug($"Could not read plugin metadata from a downloaded DLL: {e.Message}");
            }
            return plugins;
        }

        internal static void DeleteDirectory(string path) {
            if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
        }
    }
}
