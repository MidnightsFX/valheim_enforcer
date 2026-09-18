using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace ValheimEnforcer.modules.cheatmonitor {

    internal enum MatchMode { Exact, Prefix, Contains }

    internal enum WindowMatch { None, Weak, Strong }

    /// <summary>
    /// One cheat tool and the fingerprints that identify it. A tool may be detectable through any
    /// combination of vectors; process names alone are trivially defeated by renaming the executable,
    /// so the module and window fingerprints exist to survive that.
    /// </summary>
    internal sealed class CheatToolSignature {
        /// <summary>Canonical label. This is what travels over the wire and lands in the ban reason.</summary>
        public string Tool;
        /// <summary>Matched against Process.ProcessName (no ".exe" suffix), case-insensitive.</summary>
        public string[] ProcessNames = Empty;
        public MatchMode ProcessMatch = MatchMode.Exact;
        /// <summary>Prefix-matched against the module names loaded into our own process.</summary>
        public string[] ModuleNames = Empty;
        /// <summary>
        /// Namespaces hosted by a managed assembly loaded into the game. Matched exactly or as a dotted
        /// prefix, so "Valheaven.UI" matches "Valheaven" and "ValheavenSomething" does not.
        ///
        /// This is the vector that sees an injected cheat: it has no file in BepInEx/plugins to hash, it may
        /// have no process and no window of its own, and the namespace of its own types is the one thing it
        /// cannot rename without rewriting itself.
        /// </summary>
        public string[] AssemblyNamespaces = Empty;
        /// <summary>Simple assembly names, matched exactly. Weaker than a namespace - the file is renameable.</summary>
        public string[] AssemblyNames = Empty;
        /// <summary>Prefix-matched against top-level window class names.</summary>
        public string[] WindowClasses = Empty;
        /// <summary>
        /// Exact-matched window class names that are too generic to convict on their own (framework
        /// defaults shared by legitimate software). A weak match is reported and logged server-side
        /// but never kicked or banned without a strong detection.
        /// </summary>
        public string[] WeakWindowClasses = Empty;
        /// <summary>Matched against top-level window titles.</summary>
        public string[] WindowTitles = Empty;
        /// <summary>
        /// Defaults to Prefix because most tools append a version to the caption. Override to Exact
        /// for short titles that a legitimate application could plausibly begin with.
        /// </summary>
        public MatchMode WindowTitleMatch = MatchMode.Prefix;
        /// <summary>True for tools with no legitimate use whatsoever; these ban regardless of ActionOnDetection.</summary>
        public bool AutoBan;

        private static readonly string[] Empty = new string[0];
    }

    /// <summary>
    /// The built-in cheat tool signature table plus the admin-supplied additions.
    ///
    /// The base table is hardcoded rather than config-driven so that a malformed or cleared config
    /// cannot silently disable detection. Admins extend it through ValConfig.AdditionalCheatProcesses
    /// and suppress false positives through ValConfig.IgnoredCheatProcesses.
    /// </summary>
    internal static class CheatToolCatalog {

        internal const string AdditionalToolLabel = "Admin-listed tool";
        internal const string GenericTrainerLabel = "Generic trainer";
        internal const string ProxyLoaderLabel = "Proxy loader";
        // The one tool with its own config toggle predating the assembly vector, so the label is needed
        // by name in AssemblySignatures. Everything else is gated by the vector, not per-tool.
        internal const string ToolerLabel = "ValheimTooler";

        // Catches "Valheim Trainer.exe", "Hitman 3 Trainer - FLiNG.exe" and the Cheat Happens
        // "<id>-<author>-<Game> Trainer.exe" naming, without enumerating every trainer author.
        // Word-bounded so that unrelated executables merely containing the letters do not match.
        private static readonly Regex GenericTrainerPattern =
            new Regex(@"\btrainer\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Tools that exist only to cheat in a game. These are banned on sight, mirroring the
        /// pre-existing ValheimTooler carve-out in OnServerReceiveCheatReport.
        /// </summary>
        private static readonly CheatToolSignature[] AutoBanTools = {
            // Three vectors on one tool: the launcher process before it has injected, the native module if
            // one is loaded, and - the rename-proof one - the namespace of the types the injected assembly
            // hosts. The namespace used to be a pair of constants in CheatDetector; it lives here now so
            // that adding the next injected menu is an entry in this table rather than a code change.
            new CheatToolSignature {
                Tool = ToolerLabel,
                ProcessNames = new[] { "valheimtoolerlauncher" }, ProcessMatch = MatchMode.Contains,
                ModuleNames = new[] { "valheimtooler" },
                AssemblyNamespaces = new[] { "ValheimTooler" },
                AutoBan = true
            },
            // Valheaven, internally "ValheimAdminMenu". Neither a BepInEx plugin nor an external memory
            // editor: it ships as a version.dll proxy dropped beside valheim.exe, which Windows loads ahead
            // of the real one, and which then boots a managed assembly into the game - taking BepInEx's
            // Harmony if BepInEx is there and loading its own if not.
            //
            // That shape is invisible to everything else this mod does. It is not in BepInEx/plugins so the
            // plugin hashes never see it, it is not chainloaded so it is not in the declared mod list, it
            // has no process of its own, and it draws inside the game so it has no window. The two vectors
            // that do see it are the assembly it loads, by namespace, and the proxy DLL itself - caught not
            // by its name, which is a real Windows DLL, but by where it is loaded from (ClassifyProxyModule).
            //
            // Both spellings are carried because the tool answers to two names and we are working from its
            // release and from a server log rather than from the binary. "Valheaven" is what it calls itself
            // on screen and in its log lines; "ValheimAdminMenu" is the project behind it, which showed up in
            // the ValheimAdminMenu.Locales.<lang>.json resources it ships - and a resource named that way is
            // named after the assembly's root namespace, which makes it the likelier of the two to be what
            // the types actually sit in.
            new CheatToolSignature {
                Tool = "Valheaven",
                AssemblyNamespaces = new[] { "Valheaven", "ValheimAdminMenu" },
                AssemblyNames = new[] { "Valheaven", "ValheimAdminMenu" },
                AutoBan = true
            },
            // Injected-only; it has no process of its own, so the module vector is the only way to see it.
            new CheatToolSignature {
                Tool = "ValHack",
                ModuleNames = new[] { "valhack" },
                AutoBan = true
            },
            new CheatToolSignature {
                Tool = "Valheim Mod Menu",
                ProcessNames = new[] { "valheimmodmenuloader" }, ProcessMatch = MatchMode.Contains,
                AutoBan = true
            },
            // The standard Unity/Mono injector, and the delivery mechanism for most Valheim cheats.
            new CheatToolSignature {
                Tool = "SharpMonoInjector",
                ProcessNames = new[] { "smi", "smi_gui" }, ProcessMatch = MatchMode.Exact,
                AutoBan = true
            },
            new CheatToolSignature {
                Tool = "Xenos Injector",
                ProcessNames = new[] { "xenos", "xenos64" }, ProcessMatch = MatchMode.Exact,
                AutoBan = true
            },
            // Ships as "Extreme Injector v3.7.3.exe" - the version lives in the filename, so prefix.
            new CheatToolSignature {
                Tool = "Extreme Injector",
                ProcessNames = new[] { "extreme injector" }, ProcessMatch = MatchMode.Prefix,
                AutoBan = true
            }
        };

        /// <summary>
        /// General-purpose cheat tools. These honour the configured ActionOnDetection because a few
        /// of them see occasional non-cheating use (Cheat Engine in particular).
        /// </summary>
        private static readonly CheatToolSignature[] GeneralTools = {
            // WeMod, Wand and Infinity are the same product across three rebrands; all three names
            // remain in circulation. "wemod" is distinctive enough to prefix-match, which also picks
            // up WeModAuxiliaryService.exe - and Wand 12.x still ships a WeMod.exe beside Wand.exe, in
            // the same %LocalAppData%\WeMod folder.
            //
            // Injection is TrainerHost (next entry) loading TrainerLib into the game, which pulls in CELib
            // to assemble the cheat scripts and then the trainer itself. The trainer DLL is named per
            // download (Trainer_<id>_<hash>.dll) but always under that prefix, and nothing else loaded
            // into Valheim carries it. These module fingerprints are the only signal once the app is closed.
            new CheatToolSignature {
                Tool = "WeMod/Wand",
                ProcessNames = new[] { "wemod" }, ProcessMatch = MatchMode.Prefix,
                ModuleNames = new[] { "trainerlib_x64", "celib_x64", "trainer_" },
                WindowTitles = new[] { "WeMod" }, WindowTitleMatch = MatchMode.Exact
            },
            // Second entry for the same tool: "wand" and "infinity" are short, ordinary words that
            // legitimate software contains (Wandering Village, Wanderlust, Infinity Nikki...), so
            // they must match exactly. Detections collapse onto the shared label.
            //
            // TrainerHost is the injector and stays running while a trainer is attached. It ships under
            // the same name in WeMod 11 and Wand 12, and the generic trainer check cannot see it:
            // "TrainerHost" has no word break after "Trainer". WandAuxiliaryService is the rebranded
            // WeModAuxiliaryService, which "wand" alone does not reach. When either runs elevated, only
            // ScanElevatedProcesses lets the process scan see it.
            new CheatToolSignature {
                Tool = "WeMod/Wand",
                ProcessNames = new[] { "wand", "wandauxiliaryservice", "trainerhost_x64", "trainerhost_x86", "infinity" }, ProcessMatch = MatchMode.Exact,
                WindowTitles = new[] { "Wand" }, WindowTitleMatch = MatchMode.Exact
            },
            // TfrmMain/TfrmMemView survive renaming the executable, but they are Delphi's default
            // class names for forms called frmMain/frmMemView - any Delphi/Lazarus app can carry
            // them (Wondershare Helper does), so they are weak: logged, never enforced. The title
            // check is a bare "Cheat Engine" prefix because 7.6 dropped the version from the caption.
            new CheatToolSignature {
                Tool = "CheatEngine",
                ProcessNames = new[] { "cheatengine", "cheat engine", "magic-engine" }, ProcessMatch = MatchMode.Prefix,
                ModuleNames = new[] { "speedhack-", "dbk32", "dbk64", "vehdebug" },
                WeakWindowClasses = new[] { "TfrmMain", "TfrmMemView" },
                WindowTitles = new[] { "Cheat Engine" }
            },
            // Contains-matching covers "ArtMoney SE", "ArtMoney Pro" and "ArtMoneyProPortable".
            new CheatToolSignature {
                Tool = "ArtMoney",
                ProcessNames = new[] { "artmoney" }, ProcessMatch = MatchMode.Contains,
                WindowTitles = new[] { "ArtMoney" }
            },
            new CheatToolSignature {
                Tool = "PLITCH",
                ProcessNames = new[] { "plitch" }, ProcessMatch = MatchMode.Prefix,
                WindowTitles = new[] { "PLITCH" }
            },
            new CheatToolSignature {
                Tool = "Speed Gear",
                ProcessNames = new[] { "speedgear", "speederxp" }, ProcessMatch = MatchMode.Prefix
            },
            new CheatToolSignature {
                Tool = "Squalr",
                ProcessNames = new[] { "squalr" }, ProcessMatch = MatchMode.Exact
            },
            new CheatToolSignature {
                Tool = "WPE Pro",
                ProcessNames = new[] { "wpe pro", "wpe" }, ProcessMatch = MatchMode.Prefix,
                WindowTitles = new[] { "WPE PRO" }
            }
        };

        /// <summary>
        /// The signatures to scan with, honouring the current config. Admin additions from
        /// AdditionalCheatProcesses are appended as exact-match process-only entries.
        /// </summary>
        internal static List<CheatToolSignature> Enabled() {
            // Rebuilt only when one of the settings feeding it changes. This is called once per scan
            // tick and the result is handed to a worker thread, so it must be a stable snapshot rather
            // than a list rebuilt (and reallocated) underneath the scan every time.
            // DetectProxyLoaders belongs in this key even though it adds no signature to the list: the key
            // feeds PolicyKey, and PolicyKey is what makes the module scan re-examine modules it has already
            // seen. Leave it out and turning the setting on mid-session would find nothing, because every
            // module worth looking at was examined and dismissed under the old policy.
            string key = $"{ValConfig.DetectCheatTools.Value}|{ValConfig.DetectCheatEngine.Value}|{ValConfig.DetectProxyLoaders.Value}|{ValConfig.AdditionalCheatProcesses.Value}";
            List<CheatToolSignature> cached = enabledCache;
            if (cached != null && key == enabledCacheKey) { return cached; }

            List<CheatToolSignature> signatures = new List<CheatToolSignature>();

            if (ValConfig.DetectCheatTools.Value) {
                signatures.AddRange(AutoBanTools);
                foreach (CheatToolSignature sig in GeneralTools) {
                    // DetectCheatEngine predates this catalog and remains the toggle for that one tool.
                    if (sig.Tool == "CheatEngine" && !ValConfig.DetectCheatEngine.Value) { continue; }
                    signatures.Add(sig);
                }
            }

            foreach (string name in SplitList(ValConfig.AdditionalCheatProcesses.Value)) {
                signatures.Add(new CheatToolSignature {
                    Tool = $"{AdditionalToolLabel} ({name})",
                    ProcessNames = new[] { name },
                    ProcessMatch = MatchMode.Exact
                });
            }

            enabledCacheKey = key;
            enabledCache = signatures;
            return signatures;
        }

        // Written on the main thread only; the list itself is never mutated after publication, so a
        // worker holding a reference to an older snapshot still sees a consistent catalog.
        private static string enabledCacheKey;
        private static volatile List<CheatToolSignature> enabledCache;

        /// <summary>
        /// The signatures carrying an assembly fingerprint, for the injected-assembly vector.
        ///
        /// Deliberately NOT filtered by DetectCheatTools. That setting governs scanning the machine - what
        /// else is running, what else is installed - and this vector never leaves our own process: it looks
        /// only at assemblies already loaded into the game. DetectValheimTooler keeps gating its own entry,
        /// the way DetectCheatEngine gates Cheat Engine inside the main catalog, so a server that had turned
        /// that one off gets the behaviour it asked for and nothing else changes underneath it.
        /// </summary>
        internal static List<CheatToolSignature> AssemblySignatures() {
            string key = $"{ValConfig.DetectInjectedCheatAssemblies.Value}|{ValConfig.DetectValheimTooler.Value}";
            List<CheatToolSignature> cached = assemblyCache;
            if (cached != null && key == assemblyCacheKey) { return cached; }

            List<CheatToolSignature> signatures = new List<CheatToolSignature>();
            if (ValConfig.DetectInjectedCheatAssemblies.Value) {
                CollectAssemblySignatures(AutoBanTools, signatures);
                CollectAssemblySignatures(GeneralTools, signatures);
            }

            assemblyCacheKey = key;
            assemblyCache = signatures;
            return signatures;
        }

        private static void CollectAssemblySignatures(CheatToolSignature[] source, List<CheatToolSignature> into) {
            foreach (CheatToolSignature sig in source) {
                if (sig.AssemblyNamespaces.Length == 0 && sig.AssemblyNames.Length == 0) { continue; }
                if (sig.Tool == ToolerLabel && !ValConfig.DetectValheimTooler.Value) { continue; }
                into.Add(sig);
            }
        }

        private static string assemblyCacheKey;
        private static volatile List<CheatToolSignature> assemblyCache;

        /// <summary>
        /// True if a type in <paramref name="ns"/> belongs to this signature. Matched exactly or as a dotted
        /// prefix: "Valheaven" covers "Valheaven.UI" and does not cover "ValheavenAdjacent".
        /// </summary>
        internal static bool MatchesNamespace(string ns, string[] namespaces) {
            if (string.IsNullOrEmpty(ns) || namespaces == null) { return false; }
            foreach (string needle in namespaces) {
                if (string.IsNullOrEmpty(needle)) { continue; }
                if (ns.Length == needle.Length) {
                    if (string.Equals(ns, needle, StringComparison.OrdinalIgnoreCase)) { return true; }
                    continue;
                }
                if (ns.Length > needle.Length && ns[needle.Length] == '.'
                    && ns.StartsWith(needle, StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Splits a comma-separated config value, trimming blanks.</summary>
        internal static List<string> SplitList(string value) {
            List<string> entries = new List<string>();
            if (string.IsNullOrEmpty(value)) { return entries; }
            foreach (string raw in value.Split(',')) {
                string entry = raw.Trim();
                if (entry.Length > 0) { entries.Add(entry); }
            }
            return entries;
        }

        /// <summary>
        /// Whether a detection of this tool bans regardless of ActionOnDetection.
        ///
        /// Resolved from the server's own catalog by label. The client reports what it saw, never
        /// what should be done about it, so a tampered client cannot request a ban for another player.
        /// </summary>
        internal static bool IsAutoBan(string toolLabel) {
            if (string.IsNullOrEmpty(toolLabel)) { return false; }
            foreach (CheatToolSignature sig in AutoBanTools) {
                if (string.Equals(sig.Tool, toolLabel, StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }

        internal static bool IsGenericTrainerName(string processName) {
            if (string.IsNullOrEmpty(processName)) { return false; }
            return GenericTrainerPattern.IsMatch(processName);
        }

        // ---- Proxy loaders -------------------------------------------------------------------------
        //
        // Windows resolves a DLL from the application directory before it looks in System32. Dropping a
        // copy of a system DLL beside valheim.exe therefore gets it loaded into the game first; it does
        // whatever it likes and forwards the genuine exports on, and the game never notices. That is the
        // whole of a "proxy loader" install, and it is how Valheaven ships.
        //
        // The name is not the signal - every name below is a real Windows DLL the game genuinely loads.
        // The PATH is: the real one always resolves out of the Windows directory, and a copy next to the
        // game does not.

        // Names with no reason to exist beside a game executable. A copy of one of these in the game
        // folder is a loader and nothing else, so a sighting is enforceable.
        private static readonly string[] StrongProxyNames = {
            "version.dll", "winmm.dll", "dinput8.dll", "xinput1_3.dll", "xinput1_4.dll",
            "xinput9_1_0.dll", "dsound.dll", "wininet.dll", "msacm32.dll"
        };

        // The same trick with the names the graphics injectors use. ReShade, Special K and ENB all install
        // exactly this way, all three are legitimate, and between shader presets and streaming setups they
        // are common enough that convicting on one would be a false positive machine. Reported and logged,
        // never enforced on its own - the same treatment Cheat Engine's generic Delphi window classes get.
        private static readonly string[] WeakProxyNames = {
            "dxgi.dll", "d3d9.dll", "d3d10.dll", "d3d11.dll", "d3d12.dll", "ddraw.dll", "opengl32.dll"
        };

        // BepInEx's own doorstop is a winhttp.dll proxy in the game root, which is to say the mod loader
        // this server requires is itself an instance of the thing being detected. The name alone says
        // nothing; what separates them is the files Doorstop ships beside it.
        private const string DoorstopName = "winhttp.dll";
        private static readonly string[] DoorstopMarkers = {
            "doorstop_config.ini", ".doorstop_version", "winhttp.dll.config"
        };

        // Published SHA256s of Valheaven's version.dll. A proxy DLL says a loader is installed; a hash match
        // says which one, which is the difference between ActionOnDetection and an auto-ban. The author
        // reposted three builds in three days, so this names what it can and the path check above is what
        // actually does the detecting.
        private static readonly Dictionary<string, string> ProxyLoaderHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { "b381ba752c760398866dc8933816f07b9b0c64651972d413a716ad1d255c7985", "Valheaven" },
            { "229aa381485ea15099775581673cd6388ce7dbeadb6a1bde8858df4021e418e3", "Valheaven" },
            { "726066d7780ae8bd9f9e0e4e3604584a47df4bf85dedfc61b2495716bff42d85", "Valheaven" }
        };

        /// <summary>
        /// Whether this module name is one worth resolving a path for. Called once per newly loaded module,
        /// so it is a name comparison and nothing else; the path lookup only happens for a name that hits.
        /// </summary>
        internal static bool IsProxyLoaderName(string moduleName) {
            return Matches(moduleName, StrongProxyNames, MatchMode.Exact)
                || Matches(moduleName, WeakProxyNames, MatchMode.Exact)
                || string.Equals(moduleName, DoorstopName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Classifies a module that <see cref="IsProxyLoaderName"/> accepted, now that its path is known.
        /// False means this is the genuine system DLL, or BepInEx, or that the path could not be read -
        /// none of which is evidence of anything.
        /// </summary>
        internal static bool ClassifyProxyModule(string moduleName, string modulePath, out bool weak) {
            weak = false;
            if (string.IsNullOrEmpty(moduleName) || string.IsNullOrEmpty(modulePath)) { return false; }

            // The genuine article. Everything the game legitimately loads under these names lives here.
            if (IsUnderWindows(modulePath)) { return false; }

            if (string.Equals(moduleName, DoorstopName, StringComparison.OrdinalIgnoreCase)) {
                // Expected on every modded install, so it only counts when the files Doorstop ships beside
                // it are absent - the case where something has taken the name. Weak even then: a broken or
                // hand-assembled BepInEx install reaches the same state honestly.
                if (HasDoorstopMarker(modulePath)) { return false; }
                weak = true;
                return true;
            }

            weak = !Matches(moduleName, StrongProxyNames, MatchMode.Exact);
            return true;
        }

        /// <summary>The tool a proxy DLL's SHA256 identifies, or null when the build is not one we know.</summary>
        internal static string ProxyToolForHash(string sha256) {
            if (string.IsNullOrEmpty(sha256)) { return null; }
            return ProxyLoaderHashes.TryGetValue(sha256, out string tool) ? tool : null;
        }

        private static bool IsUnderWindows(string path) {
            try {
                // SystemRoot first: it is what the loader itself resolves against, and it is set on every
                // Windows. SpecialFolder.Windows is the fallback for a stripped environment block.
                string windows = Environment.GetEnvironmentVariable("SystemRoot");
                if (string.IsNullOrEmpty(windows)) {
                    windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                }
                if (string.IsNullOrEmpty(windows)) { return false; }
                return path.StartsWith(windows, StringComparison.OrdinalIgnoreCase);
            } catch (Exception) {
                return false;
            }
        }

        private static bool HasDoorstopMarker(string modulePath) {
            try {
                string dir = Path.GetDirectoryName(modulePath);
                if (string.IsNullOrEmpty(dir)) { return false; }
                foreach (string marker in DoorstopMarkers) {
                    if (File.Exists(Path.Combine(dir, marker))) { return true; }
                }
                // BepInEx sitting beside it is the same evidence by another route, and covers the installs
                // that ship a doorstop without its ini.
                return Directory.Exists(Path.Combine(dir, "BepInEx"));
            } catch (Exception) {
                return false;
            }
        }

        /// <summary>
        /// True if the admin has allowlisted this process/module/window name. Applied last so it wins
        /// over the built-in catalog, letting a server keep playing with a tool that trips a signature.
        /// </summary>
        internal static bool IsIgnored(string name) {
            if (string.IsNullOrEmpty(name)) { return false; }
            foreach (string entry in IgnoreList()) {
                if (name.IndexOf(entry, StringComparison.OrdinalIgnoreCase) >= 0) { return true; }
            }
            return false;
        }

        // IsIgnored is called once per process, module and window, so the parsed allowlist is cached
        // and only rebuilt when the admin edits the setting.
        //
        // The rebuild is deliberately separated from the read: IsIgnored now runs on the scan worker
        // thread, and having it mutate these statics would be a data race against the main thread
        // doing the same. RefreshIgnoreList is called on the main thread before a scan is dispatched;
        // IgnoreList only ever reads the published snapshot. Same volatile-snapshot shape as
        // CompatCustomData.RefreshEnabled.
        private static string ignoreListRaw;
        private static volatile List<string> ignoreListParsed = new List<string>();

        /// <summary>Main thread only. Re-parses the allowlist if the admin has edited it.</summary>
        internal static void RefreshIgnoreList() {
            string raw = ValConfig.IgnoredCheatProcesses.Value ?? "";
            if (raw == ignoreListRaw) { return; }
            ignoreListParsed = SplitList(raw);
            ignoreListRaw = raw;
        }

        private static List<string> IgnoreList() {
            return ignoreListParsed;
        }

        /// <summary>
        /// A value that changes whenever anything about what counts as a detection changes.
        ///
        /// The module scan remembers which modules it has already examined, and that memory is only valid for
        /// as long as the rules it examined them under are. Main thread only, alongside RefreshIgnoreList.
        /// </summary>
        internal static string PolicyKey() {
            return $"{enabledCacheKey}|{ignoreListRaw}";
        }

        // Window classes whose captions show content being VIEWED rather than software being RUN:
        // browsers and Electron/CEF apps put page and video titles there, File Explorer shows folder
        // names, terminals show paths and running commands. A YouTube tab called "cheat engine
        // tutorial" is not Cheat Engine, so title matching is skipped for these windows - which also
        // keeps browsing activity out of the detection report entirely. Class matching still applies;
        // no cheat tool ships under a browser's window class. Known cost: Electron-based tools
        // (WeMod's desktop app) lose their title vector, but keep their process and module vectors.
        private static readonly string[] ContentHostWindowClasses = {
            "Chrome_WidgetWin_",            // Chrome, Edge, Brave, Opera, Electron (Discord, WeMod), CEF
            "Mozilla",                      // Firefox (MozillaWindowClass and dialog variants)
            "ApplicationFrameWindow",       // UWP host frames
            "IEFrame",                      // Internet Explorer / legacy Edge
            "CabinetWClass",                // File Explorer - a folder named after a tool is not the tool
            "ExploreWClass",                // File Explorer, legacy class
            "ConsoleWindowClass",           // conhost terminals
            "CASCADIA_HOSTING_WINDOW_CLASS" // Windows Terminal
        };

        internal static bool IsContentHostWindow(string windowClass) {
            return Matches(windowClass, ContentHostWindowClasses, MatchMode.Prefix);
        }

        /// <summary>
        /// Classifies one window against one signature. Strong matches are enforceable; weak ones
        /// (generic class names) only ever produce a server log line. Weak classes match exactly:
        /// they are framework defaults, and a prefix would only widen an already-weak signal.
        /// Titles are ignored on content-host windows (browsers, Explorer, terminals), whose
        /// captions describe what the user is looking at, not what they are running.
        /// </summary>
        internal static WindowMatch MatchWindow(string windowClass, string windowTitle, CheatToolSignature sig) {
            if (Matches(windowClass, sig.WindowClasses, MatchMode.Prefix)) {
                return WindowMatch.Strong;
            }
            if (!IsContentHostWindow(windowClass) &&
                Matches(windowTitle, sig.WindowTitles, sig.WindowTitleMatch)) {
                return WindowMatch.Strong;
            }
            if (Matches(windowClass, sig.WeakWindowClasses, MatchMode.Exact)) {
                return WindowMatch.Weak;
            }
            return WindowMatch.None;
        }

        internal static bool Matches(string candidate, string[] needles, MatchMode mode) {
            if (string.IsNullOrEmpty(candidate) || needles == null) { return false; }
            foreach (string needle in needles) {
                if (string.IsNullOrEmpty(needle)) { continue; }
                switch (mode) {
                    case MatchMode.Exact:
                        if (string.Equals(candidate, needle, StringComparison.OrdinalIgnoreCase)) { return true; }
                        break;
                    case MatchMode.Prefix:
                        if (candidate.StartsWith(needle, StringComparison.OrdinalIgnoreCase)) { return true; }
                        break;
                    case MatchMode.Contains:
                        if (candidate.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) { return true; }
                        break;
                }
            }
            return false;
        }
    }
}
