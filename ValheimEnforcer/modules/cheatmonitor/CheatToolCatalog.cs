using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.cheatmonitor {

    internal enum MatchMode { Exact, Prefix, Contains }

    internal enum WindowMatch { None, Weak, Strong }

    /// <summary>What a proxy-family module turned out to be. Same three tiers as a window match.</summary>
    internal enum ProxyVerdict { None, Weak, Strong }

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
        /// <summary>
        /// Harmony owner ids the tool registers its patches under, checked in-process with
        /// Harmony.HasAnyPatches. Only consulted by the injected-assembly vector.
        /// </summary>
        public string[] HarmonyIds = Empty;
        /// <summary>
        /// Prefixes of item custom-data keys the tool writes. Read on the server from the items a client
        /// reports, so it does not depend on the client's own scan.
        /// </summary>
        public string[] DataKeyPrefixes = Empty;
        /// <summary>
        /// ZDO long keys the tool writes onto objects it creates. Read on the server from the objects a
        /// client's packets create.
        /// </summary>
        public string[] ZdoLongKeys = Empty;
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
            // Valheaven. Neither a BepInEx plugin nor an external memory editor: it ships as a version.dll
            // proxy dropped beside valheim.exe, which Windows loads ahead of the real one, and which then
            // loads a managed menu into the game - using BepInEx's Harmony when BepInEx is there.
            //
            // It is not in BepInEx/plugins, not in the declared mod list, has no process of its own and draws
            // inside the game, so it is seen through what it leaves in the process and in the world:
            //   - the proxy DLL, by where it was loaded from and by its hashes (JudgeProxyModule);
            //   - the menu assembly, ValheavenModMenu, whose types sit in the ValheimAdminMenu and Valheaven
            //     namespaces;
            //   - the Harmony id its patches are registered under;
            //   - the markers it writes onto items and onto the objects it places, which the server reads
            //     from data it receives anyway (CheatArtifacts).
            new CheatToolSignature {
                Tool = "Valheaven",
                AssemblyNamespaces = new[] { "Valheaven", "ValheimAdminMenu" },
                AssemblyNames = new[] { "ValheavenModMenu", "Valheaven", "ValheimAdminMenu" },
                HarmonyIds = new[] { "local.valheim.adminmenu", "valheaven.bootstrap" },
                DataKeyPrefixes = new[] { "valheaven." },
                ZdoLongKeys = new[] { "valheaven.planted.owner" },
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
            // VerifyDoorstopTarget and DetectUnlistedGameFolderDlls are here for the same reason: each changes
            // which already-examined modules deserve a second look.
            string key = $"{ValConfig.DetectCheatTools.Value}|{ValConfig.DetectCheatEngine.Value}|{ValConfig.DetectProxyLoaders.Value}|{ValConfig.VerifyDoorstopTarget.Value}|{ValConfig.DetectUnlistedGameFolderDlls.Value}|{ValConfig.AdditionalCheatProcesses.Value}";
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
                if (sig.AssemblyNamespaces.Length == 0 && sig.AssemblyNames.Length == 0 && sig.HarmonyIds.Length == 0) { continue; }
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
        //
        // A loader nobody has identified stays here even though legitimate ones install exactly this way -
        // x360ce as xinput1_3, a mod manager's doorstop as version.dll. An unidentified injector is not
        // owed the benefit of the doubt, and an admin who knows what theirs is puts its hash in
        // AllowedProxyLoaderHashes, which is a statement about one file rather than about a name.
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
        // nothing; what separates them is which assembly it started (VerifyDoorstop).
        private const string DoorstopName = "winhttp.dll";
        private static readonly string[] DoorstopMarkers = {
            "doorstop_config.ini", ".doorstop_version", "winhttp.dll.config"
        };
        private const string DoorstopIniName = "doorstop_config.ini";
        private const string PreloaderFileName = "BepInEx.Preloader.dll";
        internal const string PreloaderAssemblyName = "BepInEx.Preloader";

        // Known SHA256s of cheat loader builds, of the whole file. A proxy DLL says a loader is installed; a
        // hash match says which one, which is the difference between ActionOnDetection and an auto-ban.
        private static readonly Dictionary<string, string> ProxyLoaderHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { "b381ba752c760398866dc8933816f07b9b0c64651972d413a716ad1d255c7985", "Valheaven" },
            { "229aa381485ea15099775581673cd6388ce7dbeadb6a1bde8858df4021e418e3", "Valheaven" },
            { "726066d7780ae8bd9f9e0e4e3604584a47df4bf85dedfc61b2495716bff42d85", "Valheaven" },
            { "80d486a3aca93cde32f746cf29b75675149236bbb3e4e6a7c1e92eb0cec59b7a", "Valheaven" },
            { "c1f721d92b2325aef0d36f5bc5164f4cce0b5c55d5fb91d06bbff74b120d12de", "Valheaven" }
        };

        // Known SHA256s of a cheat loader's code alone - the executable sections, see PeCodeHash. A loader
        // that carries its payload inside itself changes its file hash with every payload, while its code
        // stays the same.
        private static readonly Dictionary<string, string> ProxyLoaderCodeHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { "a338aed3d6a91ed3bda300ec1b9b16ff2696aa78542fccabc4c4c023cb5732a5", "Valheaven" }
        };

        // There is deliberately no shipped table of builds to treat as good. Trust here is the admin's to
        // grant, one file at a time, through AllowedProxyLoaderHashes.

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
        /// Whether a module that <see cref="IsProxyLoaderName"/> accepted is worth reading off disk, now
        /// that its path is known. False for the genuine system DLL, and for a module whose path could not
        /// be resolved - a failed lookup is not evidence of anything.
        ///
        /// Split out from the judgement below so that the several hundred genuine system DLLs the game
        /// loads under these names are dismissed on their path, without anything being hashed.
        /// </summary>
        internal static bool ProxyNeedsInspection(string modulePath) {
            return !string.IsNullOrEmpty(modulePath) && !IsUnderWindows(modulePath);
        }

        /// <summary>
        /// The verdict on a proxy-family module whose file has been hashed. <paramref name="tool"/> comes
        /// back non-null when the hash named a specific build, so the label carries the tool's own name and
        /// the server resolves what to do about it from its own catalog, as it does for every other
        /// detection.
        ///
        /// The order matters, and the known-cheat table is first on purpose. An allowlisted hash, an
        /// ignored name and a doorstop marker beside the file are all statements about something nobody has
        /// identified; none of them gets to speak for a build this mod has.
        ///
        /// A null hash - a file locked or deleted between enumeration and reading - falls through to the
        /// name and path tests, which is what this did before it hashed anything. Fail closed on detection,
        /// fail open on exemption.
        /// </summary>
        internal static ProxyVerdict JudgeProxyModule(string moduleName, string modulePath, string sha256, string codeHash, bool nameIgnored,
                                                      DoorstopContext doorstop, out string tool, out string note) {
            tool = null;
            note = null;
            if (string.IsNullOrEmpty(moduleName) || string.IsNullOrEmpty(modulePath)) { return ProxyVerdict.None; }

            // 1. A build known to be a cheat, by either hash. Nothing below can reach past this.
            tool = ProxyToolForHash(sha256, codeHash);
            if (tool != null) { return ProxyVerdict.Strong; }

            // 2. Explicit admin intent about this exact file. The only thing that grants trust here.
            if (IsAllowedProxyHash(sha256)) { return ProxyVerdict.None; }

            // 3. The name, or the directory, is allowlisted.
            if (nameIgnored || IsIgnoredPath(modulePath)) { return ProxyVerdict.None; }

            // 4. BepInEx's own doorstop. Only ever winhttp.dll: every client on this server has BepInEx in the
            //    game root, so its presence says nothing about a file under any other name.
            if (string.Equals(moduleName, DoorstopName, StringComparison.OrdinalIgnoreCase)) {
                if (doorstop == null || !doorstop.Verify) {
                    // Weak rather than strong when the markers are absent: a broken or hand-assembled BepInEx
                    // install reaches that state honestly.
                    return HasDoorstopMarker(modulePath) ? ProxyVerdict.None : ProxyVerdict.Weak;
                }
                DoorstopCheck check = VerifyDoorstop(modulePath, doorstop, out string targetName);
                if (check == DoorstopCheck.Verified) { return ProxyVerdict.None; }
                note = $"target={(string.IsNullOrEmpty(targetName) ? "?" : targetName)} ({(check == DoorstopCheck.Mismatch ? "mismatch" : "undetermined")})";
                return ProxyVerdict.Weak;
            }

            // A name in neither family reaches here only through the game-folder listing
            // (DetectUnlistedGameFolderDlls), and falls to Weak with the weak family.
            return Matches(moduleName, StrongProxyNames, MatchMode.Exact) ? ProxyVerdict.Strong : ProxyVerdict.Weak;
        }

        /// <summary>The tool a proxy DLL's file SHA256 identifies, or null when the build is not one we know.</summary>
        internal static string ProxyToolForHash(string sha256) {
            if (string.IsNullOrEmpty(sha256)) { return null; }
            return ProxyLoaderHashes.TryGetValue(sha256, out string tool) ? tool : null;
        }

        /// <summary>The tool either hash identifies: the whole file first, then its code.</summary>
        internal static string ProxyToolForHash(string sha256, string codeHash) {
            string tool = ProxyToolForHash(sha256);
            if (tool != null || string.IsNullOrEmpty(codeHash)) { return tool; }
            return ProxyLoaderCodeHashes.TryGetValue(codeHash, out tool) ? tool : null;
        }

        // ---- Doorstop -------------------------------------------------------------------------------------

        internal enum DoorstopCheck { Verified, Mismatch, Undetermined }

        /// <summary>
        /// What the doorstop check needs to know about this process. Everything here is read on the main
        /// thread and handed to the scan worker as a finished object; the worker never touches the AppDomain
        /// or the environment itself.
        /// </summary>
        internal sealed class DoorstopContext {
            internal bool Verify;
            /// <summary>DOORSTOP_INVOKE_DLL_PATH, which Doorstop sets to the assembly it started.</summary>
            internal string EnvTarget;
            /// <summary>--doorstop-target-assembly (Doorstop 4) or --doorstop-target (Doorstop 3).</summary>
            internal string CommandLineTarget;
            /// <summary>The folder of the BepInEx this game is running.</summary>
            internal string RunningCoreDir;
            internal int PreloaderCount;
            internal string GameRoot;

            private static DoorstopContext captured;

            /// <summary>
            /// Main thread only. None of this moves within a process, so it is gathered once; only the setting
            /// is read each time.
            /// </summary>
            internal static DoorstopContext Current(bool verify) {
                DoorstopContext cached = captured;
                if (cached == null) {
                    cached = Capture();
                    captured = cached;
                }
                if (cached.Verify == verify) { return cached; }
                return new DoorstopContext {
                    Verify = verify,
                    EnvTarget = cached.EnvTarget,
                    CommandLineTarget = cached.CommandLineTarget,
                    RunningCoreDir = cached.RunningCoreDir,
                    PreloaderCount = cached.PreloaderCount,
                    GameRoot = cached.GameRoot,
                };
            }

            private static DoorstopContext Capture() {
                DoorstopContext ctx = new DoorstopContext { Verify = true };
                try { ctx.EnvTarget = Environment.GetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH"); } catch (Exception) { }
                try { ctx.CommandLineTarget = CommandLineDoorstopTarget(Environment.GetCommandLineArgs()); } catch (Exception) { }
                try { ctx.RunningCoreDir = Path.GetDirectoryName(typeof(BepInEx.BaseUnityPlugin).Assembly.Location); } catch (Exception) { }
                try { ctx.GameRoot = BepInEx.Paths.GameRootPath; } catch (Exception) { }
                try {
                    foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies()) {
                        if (asm.IsDynamic) { continue; }
                        if (string.Equals(asm.GetName().Name, PreloaderAssemblyName, StringComparison.OrdinalIgnoreCase)) {
                            ctx.PreloaderCount++;
                        }
                    }
                } catch (Exception) { }
                return ctx;
            }
        }

        /// <summary>The doorstop target named on the command line, or null.</summary>
        internal static string CommandLineDoorstopTarget(string[] args) {
            if (args == null) { return null; }
            string[] keys = { "--doorstop-target-assembly", "--doorstop-target" };
            for (int i = 0; i < args.Length; i++) {
                string arg = args[i];
                if (string.IsNullOrEmpty(arg)) { continue; }
                foreach (string key in keys) {
                    if (string.Equals(arg, key, StringComparison.OrdinalIgnoreCase)) {
                        return i + 1 < args.Length ? args[i + 1] : null;
                    }
                    if (arg.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) {
                        return arg.Substring(key.Length + 1);
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Whether the winhttp.dll at <paramref name="modulePath"/> started the BepInEx this game is running.
        ///
        /// The target is what Doorstop itself says it started, then the command line, then its ini. A relative
        /// target is resolved against the game folder, which is where Doorstop resolves it too. Verified means
        /// that target is the preloader in the running BepInEx's own folder, and that exactly one preloader
        /// is loaded.
        /// </summary>
        internal static DoorstopCheck VerifyDoorstop(string modulePath, DoorstopContext ctx, out string targetName) {
            targetName = null;
            if (ctx == null) { return DoorstopCheck.Undetermined; }
            try {
                string moduleDir = Path.GetDirectoryName(modulePath);
                string target = FirstNonEmpty(ctx.EnvTarget, ctx.CommandLineTarget, ReadIniTarget(moduleDir));
                if (target == null) { return DoorstopCheck.Undetermined; }

                string baseDir = string.IsNullOrEmpty(ctx.GameRoot) ? moduleDir : ctx.GameRoot;
                string full = ResolvePath(target, baseDir);
                if (full == null) { return DoorstopCheck.Undetermined; }
                targetName = Path.GetFileName(full);

                if (ctx.PreloaderCount == 0 || string.IsNullOrEmpty(ctx.RunningCoreDir)) { return DoorstopCheck.Undetermined; }
                if (ctx.PreloaderCount > 1) { return DoorstopCheck.Mismatch; }

                string expected = ResolvePath(Path.Combine(ctx.RunningCoreDir, PreloaderFileName), ctx.RunningCoreDir);
                return string.Equals(full, expected, StringComparison.OrdinalIgnoreCase) ? DoorstopCheck.Verified : DoorstopCheck.Mismatch;
            } catch (Exception) {
                return DoorstopCheck.Undetermined;
            }
        }

        private static string FirstNonEmpty(params string[] values) {
            foreach (string value in values) {
                if (!string.IsNullOrEmpty(value) && value.Trim().Length > 0) { return value; }
            }
            return null;
        }

        /// <summary>A path with quotes stripped and separators normalised, made absolute against a base.</summary>
        internal static string ResolvePath(string path, string baseDir) {
            if (string.IsNullOrEmpty(path)) { return null; }
            string trimmed = path.Trim().Trim('"').Trim();
            if (trimmed.Length == 0) { return null; }
            trimmed = trimmed.Replace('/', Path.DirectorySeparatorChar);
            if (!Path.IsPathRooted(trimmed)) {
                if (string.IsNullOrEmpty(baseDir)) { return null; }
                trimmed = Path.Combine(baseDir, trimmed);
            }
            return Path.GetFullPath(trimmed);
        }

        /// <summary>
        /// The target assembly named in the doorstop_config.ini beside the dll: target_assembly (Doorstop 4)
        /// or targetAssembly (Doorstop 3). Null when there is no ini or no such key.
        /// </summary>
        internal static string ReadIniTarget(string dir) {
            if (string.IsNullOrEmpty(dir)) { return null; }
            try {
                string ini = Path.Combine(dir, DoorstopIniName);
                if (!File.Exists(ini)) { return null; }
                return ParseIniTarget(File.ReadAllLines(ini));
            } catch (Exception) {
                return null;
            }
        }

        internal static string ParseIniTarget(string[] lines) {
            if (lines == null) { return null; }
            foreach (string raw in lines) {
                if (raw == null) { continue; }
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line[0] == '[') { continue; }
                int eq = line.IndexOf('=');
                if (eq <= 0) { continue; }
                string key = line.Substring(0, eq).Trim();
                if (!string.Equals(key, "target_assembly", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(key, "targetAssembly", StringComparison.OrdinalIgnoreCase)) { continue; }
                string value = line.Substring(eq + 1).Trim();
                return value.Length == 0 ? null : value;
            }
            return null;
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

        /// <summary>
        /// True if the admin has allowlisted a directory the module sits in, letting a proxy DLL be
        /// exempted by where it lives rather than by blinding its name everywhere.
        ///
        /// Only entries that look like a path count. IsIgnored is a substring match, and the entries
        /// admins already have in this setting are program names: matching "steam" against a full path
        /// would exempt everything under the Steam folder, which is to say the game folder, which is to
        /// say the entire vector. So an entry earns a path comparison by containing a separator.
        /// </summary>
        internal static bool IsIgnoredPath(string modulePath) {
            if (string.IsNullOrEmpty(modulePath)) { return false; }
            foreach (string entry in IgnoreList()) {
                if (entry.IndexOf('\\') < 0 && entry.IndexOf('/') < 0) { continue; }
                if (modulePath.IndexOf(entry, StringComparison.OrdinalIgnoreCase) >= 0) { return true; }
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

        // The proxy hash allowlist, in the same volatile-snapshot shape as the ignore list above and for
        // the same reason: JudgeProxyModule runs on the scan worker, and a set rebuilt underneath it would
        // be a data race against the main thread doing the rebuilding. Replaced wholesale, never mutated.
        private static string allowedProxyHashesRaw;
        private static volatile HashSet<string> allowedProxyHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Main thread only. Re-parses the proxy hash allowlist if the admin has edited it.</summary>
        internal static void RefreshAllowedProxyHashes() {
            string raw = ValConfig.AllowedProxyLoaderHashes.Value ?? "";
            if (raw == allowedProxyHashesRaw) { return; }
            HashSet<string> parsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in SplitList(raw)) {
                string normalized = NormalizeHash(entry);
                if (normalized == null) {
                    // Said out loud rather than dropped. A hash pasted with a stray character does nothing
                    // at all, and an admin watching the same player keep getting kicked has no other way
                    // to find out why.
                    Logger.LogWarning($"AllowedProxyLoaderHashes: '{entry}' is not a SHA256 (64 hex characters) and is ignored.");
                    continue;
                }
                parsed.Add(normalized);
            }
            allowedProxyHashes = parsed;
            allowedProxyHashesRaw = raw;
        }

        /// <summary>True if the admin has vouched for this exact file. Worker-thread safe.</summary>
        internal static bool IsAllowedProxyHash(string sha256) {
            return !string.IsNullOrEmpty(sha256) && allowedProxyHashes.Contains(sha256);
        }

        /// <summary>
        /// A config entry reduced to 64 bare hex characters, or null if it is not a SHA256. Admins paste
        /// these out of log lines, file manager dialogs and VirusTotal, so the punctuation those add -
        /// a "sha256:" prefix, spaces, colons, hyphens - is stripped rather than rejected. Case is left
        /// alone; the set that holds the result is case-insensitive.
        /// </summary>
        internal static string NormalizeHash(string entry) {
            if (string.IsNullOrEmpty(entry)) { return null; }
            string value = entry.Trim();
            // A "sha256:" / "sha-256:" prefix, not a separator inside the digits. The first colon is the
            // one that ends the prefix; a value that is colon-separated all the way through does not start
            // with "sha" and keeps every colon, which the loop below drops anyway.
            if (value.StartsWith("sha", StringComparison.OrdinalIgnoreCase)) {
                int colon = value.IndexOf(':');
                if (colon >= 0 && colon < value.Length - 1) { value = value.Substring(colon + 1); }
            }
            StringBuilder digits = new StringBuilder(64);
            foreach (char c in value) {
                if (c == ' ' || c == '\t' || c == '-' || c == ':') { continue; }
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) { return null; }
                if (digits.Length == 64) { return null; }
                digits.Append(c);
            }
            return digits.Length == 64 ? digits.ToString() : null;
        }

        // ---- Server-side judgement ------------------------------------------------------------------------
        //
        // The client reports what it saw; these decide, from the server's own tables, what that means. A
        // client that lies can only lie about itself, because the ban always lands on the socket it reported
        // from.

        internal const string AdminListedLoaderLabel = "Admin-listed loader build";
        internal const string AdminListedAssemblyLabel = "Admin-listed assembly";

        internal enum ServerProxyOutcome { Unknown, Banned, Allowed }

        private static readonly Regex FileHashPattern = new Regex(@"sha256=([0-9a-fA-F]{64})", RegexOptions.Compiled);
        private static readonly Regex CodeHashPattern = new Regex(@"code=([0-9a-fA-F]{64})", RegexOptions.Compiled);

        // Server only, main thread only; same parse-on-change shape as the allowlist above.
        private static string bannedProxyHashesRaw;
        private static HashSet<string> bannedProxyHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static void RefreshBannedProxyHashes() {
            string raw = ValConfig.BannedProxyLoaderHashes?.Value ?? "";
            if (raw == bannedProxyHashesRaw) { return; }
            HashSet<string> parsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in SplitList(raw)) {
                string normalized = NormalizeHash(entry);
                if (normalized == null) {
                    Logger.LogWarning($"BannedProxyLoaderHashes: '{entry}' is not a SHA256 (64 hex characters) and is ignored.");
                    continue;
                }
                parsed.Add(normalized);
            }
            bannedProxyHashes = parsed;
            bannedProxyHashesRaw = raw;
        }

        /// <summary>
        /// The server's own verdict on one proxy detection, from the hashes the client put in its detail.
        ///
        /// Checked in this order: a build this mod knows, then one the admin has banned, then one the admin
        /// has allowed. <paramref name="hash"/> comes back as the hash that decided it, or the file hash when
        /// nothing did. Main thread only.
        /// </summary>
        internal static ServerProxyOutcome ServerProxyVerdict(string detail, out string tool, out string hash) {
            tool = null;
            hash = null;
            if (string.IsNullOrEmpty(detail)) { return ServerProxyOutcome.Unknown; }
            RefreshBannedProxyHashes();
            RefreshAllowedProxyHashes();
            return ServerProxyVerdict(detail, bannedProxyHashes, allowedProxyHashes, out tool, out hash);
        }

        /// <summary>The same verdict against explicit tables, with no config behind it.</summary>
        internal static ServerProxyOutcome ServerProxyVerdict(string detail, ICollection<string> banned, ICollection<string> allowed,
                                                              out string tool, out string hash) {
            tool = null;
            hash = null;
            if (string.IsNullOrEmpty(detail)) { return ServerProxyOutcome.Unknown; }

            Match fileMatch = FileHashPattern.Match(detail);
            Match codeMatch = CodeHashPattern.Match(detail);
            string file = fileMatch.Success ? fileMatch.Groups[1].Value.ToLowerInvariant() : null;
            string code = codeMatch.Success ? codeMatch.Groups[1].Value.ToLowerInvariant() : null;
            hash = file ?? code;
            if (hash == null) { return ServerProxyOutcome.Unknown; }

            string known = ProxyToolForHash(file);
            if (known != null) { tool = known; hash = file; return ServerProxyOutcome.Banned; }
            if (!string.IsNullOrEmpty(code) && ProxyLoaderCodeHashes.TryGetValue(code, out known)) {
                tool = known;
                hash = code;
                return ServerProxyOutcome.Banned;
            }

            if (banned != null) {
                if (file != null && banned.Contains(file)) { tool = AdminListedLoaderLabel; hash = file; return ServerProxyOutcome.Banned; }
                if (code != null && banned.Contains(code)) { tool = AdminListedLoaderLabel; hash = code; return ServerProxyOutcome.Banned; }
            }

            // Only the file hash can allow: an allowance is a statement about one exact file.
            if (allowed != null && file != null && allowed.Contains(file)) { hash = file; return ServerProxyOutcome.Allowed; }
            return ServerProxyOutcome.Unknown;
        }

        // The admin's namespace and MVID ban lists for unaccounted code. Server only, main thread only.
        private static string bannedCodeRaw;
        private static List<string> bannedNamespaces = new List<string>();
        private static HashSet<string> bannedMvids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static void RefreshBannedCode() {
            string raw = $"{ValConfig.BannedAssemblyNamespaces?.Value}|{ValConfig.BannedAssemblyMvids?.Value}";
            if (raw == bannedCodeRaw) { return; }
            bannedNamespaces = SplitList(ValConfig.BannedAssemblyNamespaces?.Value);
            HashSet<string> mvids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in SplitList(ValConfig.BannedAssemblyMvids?.Value)) {
                string normalized = NormalizeMvid(entry);
                if (normalized == null) {
                    Logger.LogWarning($"BannedAssemblyMvids: '{entry}' is not a GUID and is ignored.");
                    continue;
                }
                mvids.Add(normalized);
            }
            bannedMvids = mvids;
            bannedCodeRaw = raw;
        }

        /// <summary>A GUID in its canonical "D" form, or null when the entry is not one.</summary>
        internal static string NormalizeMvid(string entry) {
            if (string.IsNullOrEmpty(entry)) { return null; }
            return Guid.TryParse(entry.Trim(), out Guid guid) ? guid.ToString("D") : null;
        }

        /// <summary>
        /// The tool one reported unaccounted assembly belongs to, from the server's own catalog and ban lists,
        /// or null when nothing claims it. <paramref name="ban"/> says whether that verdict bans. Main thread only.
        /// </summary>
        internal static string ServerCodeVerdict(DataObjects.UnaccountedAssembly code, out bool ban) {
            ban = false;
            if (code == null) { return null; }
            RefreshBannedCode();

            foreach (CheatToolSignature[] table in new[] { AutoBanTools, GeneralTools }) {
                foreach (CheatToolSignature sig in table) {
                    bool hit = Matches(code.Name, sig.AssemblyNames, MatchMode.Exact);
                    if (!hit && code.Namespaces != null) {
                        foreach (string ns in code.Namespaces) {
                            if (MatchesNamespace(ns, sig.AssemblyNamespaces)) { hit = true; break; }
                        }
                    }
                    if (!hit && code.PatchOwners != null) {
                        foreach (string owner in code.PatchOwners) {
                            if (Matches(owner, sig.HarmonyIds, MatchMode.Exact)) { hit = true; break; }
                        }
                    }
                    if (hit) {
                        ban = sig.AutoBan;
                        return sig.Tool;
                    }
                }
            }

            if (code.Namespaces != null && bannedNamespaces.Count > 0) {
                foreach (string ns in code.Namespaces) {
                    if (MatchesNamespace(ns, bannedNamespaces.ToArray())) {
                        ban = true;
                        return $"{AdminListedAssemblyLabel} ({ns})";
                    }
                }
            }
            string mvid = NormalizeMvid(code.Mvid);
            if (mvid != null && bannedMvids.Contains(mvid)) {
                ban = true;
                return $"{AdminListedAssemblyLabel} ({mvid})";
            }
            return null;
        }

        // ---- Artifacts ------------------------------------------------------------------------------------
        //
        // Markers cheat tools write into data the server receives anyway. Built from the whole catalog, not
        // the client's scan toggles: nothing here runs on a client.

        private static volatile List<KeyValuePair<int, string>> zdoArtifactKeys;

        /// <summary>The tool whose marker an item custom-data key is, or null.</summary>
        internal static string ToolForItemDataKey(string key) {
            if (string.IsNullOrEmpty(key)) { return null; }
            foreach (CheatToolSignature[] table in new[] { AutoBanTools, GeneralTools }) {
                foreach (CheatToolSignature sig in table) {
                    if (Matches(key, sig.DataKeyPrefixes, MatchMode.Prefix)) { return sig.Tool; }
                }
            }
            return null;
        }

        /// <summary>Every catalogued ZDO marker, as the key hash ZDO values are stored under, with its tool.</summary>
        internal static List<KeyValuePair<int, string>> ZdoArtifactKeys() {
            List<KeyValuePair<int, string>> cached = zdoArtifactKeys;
            if (cached != null) { return cached; }
            List<KeyValuePair<int, string>> keys = new List<KeyValuePair<int, string>>();
            foreach (CheatToolSignature[] table in new[] { AutoBanTools, GeneralTools }) {
                foreach (CheatToolSignature sig in table) {
                    foreach (string key in sig.ZdoLongKeys) {
                        if (!string.IsNullOrEmpty(key)) { keys.Add(new KeyValuePair<int, string>(key.GetStableHashCode(), sig.Tool)); }
                    }
                }
            }
            zdoArtifactKeys = keys;
            return keys;
        }

        /// <summary>
        /// A value that changes whenever anything about what counts as a detection changes.
        ///
        /// The module scan remembers which modules it has already examined, and that memory is only valid for
        /// as long as the rules it examined them under are. Main thread only, alongside RefreshIgnoreList.
        /// </summary>
        internal static string PolicyKey() {
            return $"{enabledCacheKey}|{ignoreListRaw}|{allowedProxyHashesRaw}";
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
