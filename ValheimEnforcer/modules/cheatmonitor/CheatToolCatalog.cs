using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ValheimEnforcer.modules.cheatmonitor {

    internal enum MatchMode { Exact, Prefix, Contains }

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
        /// <summary>Prefix-matched against top-level window class names.</summary>
        public string[] WindowClasses = Empty;
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
            // Complements the assembly-namespace scan in CheatDetector: catches the launcher before
            // it has injected, and the injected assembly if the managed AssemblyLoad event is missed.
            new CheatToolSignature {
                Tool = "ValheimTooler",
                ProcessNames = new[] { "valheimtoolerlauncher" }, ProcessMatch = MatchMode.Contains,
                ModuleNames = new[] { "valheimtooler" },
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
            // up helpers like WeMod.Updater.exe. The trainer itself injects into valheim.exe under a
            // per-build randomised DLL name, so the module fingerprints here are the only signal once
            // the launcher has been closed.
            new CheatToolSignature {
                Tool = "WeMod/Wand",
                ProcessNames = new[] { "wemod" }, ProcessMatch = MatchMode.Prefix,
                ModuleNames = new[] { "trainerlib_x64", "celib_x64" },
                WindowTitles = new[] { "WeMod" }, WindowTitleMatch = MatchMode.Exact
            },
            // Second entry for the same tool: "wand" and "infinity" are short, ordinary words that
            // legitimate software contains (Wandering Village, Wanderlust, Infinity Nikki...), so
            // they must match exactly. Detections collapse onto the shared label.
            new CheatToolSignature {
                Tool = "WeMod/Wand",
                ProcessNames = new[] { "wand", "infinity" }, ProcessMatch = MatchMode.Exact,
                WindowTitles = new[] { "Wand" }, WindowTitleMatch = MatchMode.Exact
            },
            // TfrmMain/TfrmMemView survive renaming the executable. The title check is a bare
            // "Cheat Engine" prefix because 7.6 dropped the version number from the caption.
            new CheatToolSignature {
                Tool = "CheatEngine",
                ProcessNames = new[] { "cheatengine", "cheat engine", "magic-engine" }, ProcessMatch = MatchMode.Prefix,
                ModuleNames = new[] { "speedhack-", "dbk32", "dbk64", "vehdebug" },
                WindowClasses = new[] { "TfrmMain", "TfrmMemView" },
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

            return signatures;
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
        private static string ignoreListRaw;
        private static List<string> ignoreListParsed = new List<string>();

        private static List<string> IgnoreList() {
            string raw = ValConfig.IgnoredCheatProcesses.Value ?? "";
            if (raw != ignoreListRaw) {
                ignoreListParsed = SplitList(raw);
                ignoreListRaw = raw;
            }
            return ignoreListParsed;
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
