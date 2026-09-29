using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.cheatmonitor {

    /// <summary>
    /// Decides whether a managed assembly loaded into the game is accounted for - that is, whether an installed
    /// mod, a patcher, BepInEx or the game itself explains its presence - and describes it when it is not.
    ///
    /// Accounted means one of:
    ///   - it is dynamic (Harmony and MonoMod generate these constantly);
    ///   - it was loaded from a file under the plugins, patchers or BepInEx core folders, the game's Managed
    ///     folder, or the game's Mono runtime;
    ///   - it was loaded from memory under the name of a file in the Managed folder, which is how BepInEx
    ///     hands on game assemblies a patcher has rewritten;
    ///   - it was loaded from memory and an accounted plugin carries it as an embedded resource, which is how
    ///     mods bundle their dependencies;
    ///   - it is one of the helper assemblies HarmonyX and MonoMod build at runtime.
    ///
    /// The last three go by name, so they only ever account for the one loaded assembly with that identity.
    ///
    /// Everything else is reported for the server to judge. Nothing about the machine goes with it: the file
    /// name leaves without its folder.
    /// </summary>
    internal static class CodeProvenance {

        internal const int MaxNamespaces = 8;
        internal const int MaxFieldLength = 128;

        // Assemblies HarmonyX and MonoMod build at runtime and load from memory rather than emitting as
        // dynamic ones.
        private static readonly HashSet<string> RuntimeHelperNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "MonoMod.Utils.Cil.ILGeneratorProxy",
        };
        private static readonly string[] RuntimeHelperPrefixes = { "DMDASM.", "HarmonySharedState", "MonoMod.Utils.GetManagedSizeHelper" };

        /// <summary>Where accounted-for assemblies live, gathered once on the main thread.</summary>
        internal sealed class Roots {
            internal string[] Dirs = new string[0];
            internal HashSet<string> ManagedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Simple names of assemblies an accounted plugin carries in its resources.
            internal HashSet<string> EmbeddedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal int EmbeddedScanAssemblyCount = -1;
        }

        private static Roots roots;

        /// <summary>Main thread only.</summary>
        internal static Roots Current() {
            if (roots != null) { return roots; }
            Roots r = new Roots();
            List<string> dirs = new List<string>();
            AddDir(dirs, () => BepInEx.Paths.PluginPath);
            AddDir(dirs, () => BepInEx.Paths.PatcherPluginPath);
            AddDir(dirs, () => BepInEx.Paths.BepInExAssemblyDirectory);
            AddDir(dirs, () => BepInEx.Paths.ManagedPath);
            AddDir(dirs, () => Path.GetDirectoryName(typeof(object).Assembly.Location));
            AddDir(dirs, () => Path.Combine(BepInEx.Paths.GameRootPath, "MonoBleedingEdge"));
            r.Dirs = dirs.ToArray();
            try {
                string managed = BepInEx.Paths.ManagedPath;
                if (!string.IsNullOrEmpty(managed) && Directory.Exists(managed)) {
                    foreach (string file in Directory.GetFiles(managed, "*.dll")) {
                        r.ManagedNames.Add(Path.GetFileNameWithoutExtension(file));
                    }
                }
            } catch (Exception e) {
                Logger.LogDebug($"CodeProvenance could not list the Managed folder: {e.Message}");
            }
            roots = r;
            return r;
        }

        private static void AddDir(List<string> dirs, Func<string> get) {
            try {
                string dir = get();
                if (string.IsNullOrEmpty(dir)) { return; }
                string full = Path.GetFullPath(dir);
                if (!full.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)) {
                    full += Path.DirectorySeparatorChar;
                }
                dirs.Add(full);
            } catch (Exception) {
                // A root that cannot be resolved accounts for nothing; everything it would have covered is
                // then reported, which is the direction to fail in.
            }
        }

        /// <summary>
        /// The pure half of the decision, over plain values, so it can be tested without a game. An empty
        /// <paramref name="location"/> means the assembly was loaded from memory.
        /// </summary>
        internal static bool IsAccounted(string location, string simpleName, bool isDynamic, string[] dirs,
                                         ICollection<string> managedNames, ICollection<string> embeddedNames) {
            if (isDynamic) { return true; }
            if (!string.IsNullOrEmpty(location)) {
                string full;
                try { full = Path.GetFullPath(location); } catch (Exception) { return false; }
                if (dirs != null) {
                    foreach (string dir in dirs) {
                        if (!string.IsNullOrEmpty(dir) && full.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) { return true; }
                    }
                }
                return false;
            }
            if (string.IsNullOrEmpty(simpleName)) { return false; }
            if (managedNames != null && managedNames.Contains(simpleName)) { return true; }
            if (EmbeddedCovers(embeddedNames, simpleName)) { return true; }
            if (RuntimeHelperNames.Contains(simpleName)) { return true; }
            foreach (string prefix in RuntimeHelperPrefixes) {
                if (simpleName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }

        /// <summary>Whether a loaded assembly is accounted for. Main thread only.</summary>
        internal static bool IsAccounted(Assembly asm) {
            if (asm == null) { return true; }
            if (asm.IsDynamic) { return true; }
            Roots r = Current();
            string location;
            string name;
            try {
                location = asm.Location;
                name = asm.GetName().Name;
            } catch (Exception) {
                return true; // nothing to describe it by; not worth a report
            }
            if (!string.IsNullOrEmpty(location)) {
                return IsAccounted(location, name, false, r.Dirs, r.ManagedNames, r.EmbeddedNames);
            }

            // Loaded from memory. The rules below go by name, so they only speak for the one assembly carrying
            // that identity: a second copy of an identity that is already loaded is not explained by it.
            if (SharesIdentity(asm)) { return false; }
            if (IsAccounted(location, name, false, r.Dirs, r.ManagedNames, r.EmbeddedNames)) { return true; }

            // Loaded from memory under a name nothing explains yet. Plugins that bundle their dependencies carry
            // them as resources; learn those names now, once per change in what is loaded, and ask again.
            RefreshEmbeddedNames(r);
            return IsAccounted(location, name, false, r.Dirs, r.ManagedNames, r.EmbeddedNames);
        }

        /// <summary>Whether another loaded assembly carries exactly the same full name.</summary>
        private static bool SharesIdentity(Assembly asm) {
            try {
                string fullName = asm.FullName;
                foreach (Assembly other in AppDomain.CurrentDomain.GetAssemblies()) {
                    if (other == null || ReferenceEquals(other, asm) || other.IsDynamic) { continue; }
                    if (string.Equals(other.FullName, fullName, StringComparison.Ordinal)) { return true; }
                }
            } catch (Exception) {
                // Nothing to compare against; fall through to the name rules.
            }
            return false;
        }

        private static void RefreshEmbeddedNames(Roots r) {
            Assembly[] loaded;
            try { loaded = AppDomain.CurrentDomain.GetAssemblies(); } catch (Exception) { return; }
            if (loaded.Length == r.EmbeddedScanAssemblyCount) { return; }
            r.EmbeddedScanAssemblyCount = loaded.Length;
            foreach (Assembly candidate in loaded) {
                if (candidate == null || candidate.IsDynamic) { continue; }
                string location;
                try { location = candidate.Location; } catch (Exception) { continue; }
                if (string.IsNullOrEmpty(location)) { continue; }
                if (!IsAccounted(location, null, false, r.Dirs, null, null)) { continue; }
                string[] resources;
                try { resources = candidate.GetManifestResourceNames(); } catch (Exception) { continue; }
                foreach (string resource in resources) {
                    string embedded = EmbeddedAssemblyName(resource);
                    if (embedded != null) { r.EmbeddedNames.Add(embedded); }
                }
            }
        }

        /// <summary>
        /// The simple assembly name a resource carries, or null when it does not look like an assembly:
        /// "costura.yamldotnet.dll.compressed" and "MyMod.Libs.YamlDotNet.dll" both give "YamlDotNet"
        /// (compared case-insensitively). A dotted prefix is dropped one segment at a time, so a dotted
        /// assembly name ("Newtonsoft.Json") is covered too.
        /// </summary>
        internal static string EmbeddedAssemblyName(string resource) {
            if (string.IsNullOrEmpty(resource)) { return null; }
            string name = resource;
            if (name.EndsWith(".compressed", StringComparison.OrdinalIgnoreCase)) { name = name.Substring(0, name.Length - ".compressed".Length); }
            if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) { return null; }
            name = name.Substring(0, name.Length - 4);
            if (name.StartsWith("costura.", StringComparison.OrdinalIgnoreCase)) { name = name.Substring("costura.".Length); }
            return name.Length == 0 ? null : name;
        }

        /// <summary>
        /// True when an embedded-resource name set accounts for <paramref name="simpleName"/>, allowing for
        /// the resource carrying a folder-like dotted prefix in front of the name.
        /// </summary>
        internal static bool EmbeddedCovers(ICollection<string> embeddedNames, string simpleName) {
            if (embeddedNames == null || string.IsNullOrEmpty(simpleName)) { return false; }
            if (embeddedNames.Contains(simpleName)) { return true; }
            foreach (string name in embeddedNames) {
                if (name.Length > simpleName.Length && name.EndsWith("." + simpleName, StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }

        /// <summary>
        /// Describes an unaccounted assembly for the server, and hands back its types that draw with OnGUI.
        /// Main thread only.
        /// </summary>
        internal static DataObjects.UnaccountedAssembly Describe(Assembly asm, out List<Type> guiTypes) {
            guiTypes = new List<Type>();
            DataObjects.UnaccountedAssembly entry = new DataObjects.UnaccountedAssembly();
            try {
                entry.Name = Cap(asm.GetName().Name);
                entry.Mvid = asm.ManifestModule.ModuleVersionId.ToString("D");
                string location = asm.Location;
                entry.FromBytes = string.IsNullOrEmpty(location);
                entry.FileName = entry.FromBytes ? null : Cap(Path.GetFileName(location));
            } catch (Exception e) {
                Logger.LogDebug($"CodeProvenance could not describe an assembly: {e.Message}");
            }

            Type[] types;
            try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = ex.Types; } catch (Exception) { types = new Type[0]; }

            HashSet<string> namespaces = new HashSet<string>(StringComparer.Ordinal);
            foreach (Type t in types) {
                if (t == null) { continue; }
                try {
                    string ns = t.Namespace;
                    if (!string.IsNullOrEmpty(ns) && namespaces.Count < MaxNamespaces) { namespaces.Add(Cap(ns)); }
                    if (typeof(MonoBehaviour).IsAssignableFrom(t)
                        && t.GetMethod("OnGUI", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) != null) {
                        guiTypes.Add(t);
                    }
                } catch (Exception) {
                    // A type that cannot be inspected is skipped rather than failing the whole description.
                }
            }
            entry.Namespaces = new List<string>(namespaces);
            return entry;
        }

        /// <summary>Whether any of these behaviour types has a live instance. Main thread only.</summary>
        internal static bool AnyLive(List<Type> guiTypes) {
            if (guiTypes == null) { return false; }
            foreach (Type t in guiTypes) {
                try {
                    // FindObjectsOfTypeAll rather than FindObjectsOfType: the latter skips objects hidden from the
                    // scene, which is how a persistent host object is usually created.
                    UnityEngine.Object[] found = Resources.FindObjectsOfTypeAll(t);
                    if (found != null && found.Length > 0) { return true; }
                } catch (Exception) {
                    // Keep looking at the rest.
                }
            }
            return false;
        }

        internal static string Cap(string s) {
            if (string.IsNullOrEmpty(s) || s.Length <= MaxFieldLength) { return s; }
            return s.Substring(0, MaxFieldLength);
        }
    }
}
