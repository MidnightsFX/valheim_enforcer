using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using ValheimEnforcer.common;
using ValheimEnforcer.modules.character;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.modules.cheatmonitor {
    internal static class CheatDetector {

        // ValheimTooler is detected by the namespace of the types it loads rather than the
        // assembly name, so renaming the injected assembly does not evade detection.
        private const string ToolerNamespace = "ValheimTooler";
        private const string ToolerNamespacePrefix = "ValheimTooler.";

        // Upper bound on window matches collected in a single EnumWindows pass, so a pathological
        // desktop cannot turn one scan into an unbounded report.
        private const int MaxWindowMatches = 8;

        internal static void Initialize() {
            if (ZNet.instance != null && ZNet.instance.IsDedicated()) {
                return;
            }
            if (ValConfig.EnableCheatDetection.Value == false) {
                return;
            }

            GameObject host = new GameObject("VE_CheatDetector");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<CheatDetectorBehaviour>();
            Logger.LogDebug("CheatDetector initialized.");
        }

        /// <summary>
        /// True if the assembly hosts any type in the ValheimTooler namespace. Skips dynamic
        /// assemblies (Harmony/DMD) which never host the cheat and throw on GetTypes(), and
        /// tolerates partially-loadable assemblies via ReflectionTypeLoadException.
        /// </summary>
        internal static bool AssemblyHostsTooler(Assembly asm, out string detail) {
            detail = null;
            if (asm == null || asm.IsDynamic) { return false; }
            try {
                Type[] types;
                try {
                    types = asm.GetTypes();
                } catch (ReflectionTypeLoadException ex) {
                    types = ex.Types;
                }

                foreach (Type t in types) {
                    if (t == null) { continue; }
                    string ns = t.Namespace;
                    if (ns == null) { continue; }
                    if (ns == ToolerNamespace || ns.StartsWith(ToolerNamespacePrefix, StringComparison.Ordinal)) {
                        detail = $"type:{t.FullName} asm:{asm.GetName().Name}";
                        return true;
                    }
                }
            } catch (Exception e) {
                Logger.LogDebug($"CheatDetector.AssemblyHostsTooler failed for {asm.FullName}: {e.Message}");
            }
            return false;
        }

        /// <summary>
        /// Walks the running processes once and tests every signature against each name. A single
        /// enumeration is used regardless of catalog size; the cost here is the syscall, not the
        /// string matching.
        /// </summary>
        internal static List<CheatToolDetection> ScanProcesses(List<CheatToolSignature> signatures) {
            List<CheatToolDetection> found = new List<CheatToolDetection>();
            Process[] procs = null;
            try {
                procs = Process.GetProcesses();
                bool genericTrainers = ValConfig.DetectGenericTrainers.Value;
                foreach (Process p in procs) {
                    string name;
                    // ProcessName throws for processes that exit between enumeration and access.
                    try { name = p.ProcessName ?? ""; } catch { continue; }
                    if (name.Length == 0 || CheatToolCatalog.IsIgnored(name)) { continue; }

                    foreach (CheatToolSignature sig in signatures) {
                        if (CheatToolCatalog.Matches(name, sig.ProcessNames, sig.ProcessMatch)) {
                            Add(found, sig.Tool, "process", name);
                        }
                    }

                    if (genericTrainers && CheatToolCatalog.IsGenericTrainerName(name)) {
                        Add(found, CheatToolCatalog.GenericTrainerLabel, "process", name);
                    }
                }
            } catch (Exception e) {
                Logger.LogDebug($"CheatDetector.ScanProcesses failed: {e.Message}");
            } finally {
                // Process objects hold OS handles; dispose them so the periodic scan does not leak.
                if (procs != null) {
                    foreach (Process p in procs) {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
            return found;
        }

        /// <summary>
        /// Inspects the native modules loaded into our own process. This is the only vector that sees
        /// a cheat which has already injected and then closed its launcher, and it is unaffected by
        /// renaming the tool's executable.
        /// </summary>
        internal static List<CheatToolDetection> ScanLoadedModules(List<CheatToolSignature> signatures) {
            List<CheatToolDetection> found = new List<CheatToolDetection>();
            try {
                using (Process self = Process.GetCurrentProcess()) {
                    foreach (ProcessModule m in self.Modules) {
                        string name;
                        try { name = m.ModuleName ?? ""; } catch { continue; }
                        if (name.Length == 0 || CheatToolCatalog.IsIgnored(name)) { continue; }

                        foreach (CheatToolSignature sig in signatures) {
                            if (CheatToolCatalog.Matches(name, sig.ModuleNames, MatchMode.Prefix)) {
                                Add(found, sig.Tool, "module", name);
                            }
                        }
                    }
                }
            } catch (Exception e) {
                Logger.LogDebug($"CheatDetector.ScanLoadedModules failed: {e.Message}");
            }
            return found;
        }

        /// <summary>
        /// Enumerates top-level windows and matches their class and title. Catches tools renamed to
        /// evade the process check. Generic framework classes (Cheat Engine's TfrmMain, shared by
        /// every Delphi app with a form named frmMain) only produce weak detections, which the
        /// server logs but never enforces. MainWindowTitle is deliberately not used: it is slow and
        /// comes back empty for windowless and elevated processes.
        /// </summary>
        internal static List<CheatToolDetection> ScanWindows(List<CheatToolSignature> signatures) {
            List<CheatToolDetection> found = new List<CheatToolDetection>();
            if (Application.platform != RuntimePlatform.WindowsPlayer && Application.platform != RuntimePlatform.WindowsEditor) {
                return found;
            }
            List<CheatToolSignature> windowed = signatures
                .Where(s => s.WindowClasses.Length > 0 || s.WeakWindowClasses.Length > 0 || s.WindowTitles.Length > 0).ToList();
            if (windowed.Count == 0) { return found; }

            try {
                // Held in a local so the delegate cannot be collected while EnumWindows is running.
                NativeWin32.EnumWindowsProc callback = (hWnd, _) => {
                    StringBuilder cls = new StringBuilder(256);
                    NativeWin32.GetClassName(hWnd, cls, cls.Capacity);
                    StringBuilder txt = new StringBuilder(256);
                    NativeWin32.GetWindowTextW(hWnd, txt, txt.Capacity);
                    string c = cls.ToString();
                    string t = txt.ToString();

                    if (CheatToolCatalog.IsIgnored(c) || CheatToolCatalog.IsIgnored(t)) { return true; }

                    foreach (CheatToolSignature sig in windowed) {
                        WindowMatch match = CheatToolCatalog.MatchWindow(c, t, sig);
                        if (match != WindowMatch.None) {
                            Add(found, sig.Tool, "window", $"class={c}|title={t}", match == WindowMatch.Weak);
                        }
                    }
                    // Keep enumerating so every distinct tool on screen is reported, not just the first.
                    return found.Count < MaxWindowMatches;
                };
                NativeWin32.EnumWindows(callback, IntPtr.Zero);
            } catch (Exception e) {
                Logger.LogDebug($"CheatDetector.ScanWindows failed: {e.Message}");
            }
            return found;
        }

        // One entry per tool per scan; the first sighting carries the detail, except that a strong
        // sighting replaces a weak one so enumeration order cannot hide enforceable evidence.
        private static void Add(List<CheatToolDetection> found, string tool, string vector, string detail, bool weak = false) {
            foreach (CheatToolDetection existing in found) {
                if (existing.Tool == tool) {
                    if (existing.Weak && !weak) {
                        existing.Weak = false;
                        existing.Vector = vector;
                        existing.Detail = detail;
                    }
                    return;
                }
            }
            found.Add(new CheatToolDetection { Tool = tool, Vector = vector, Detail = detail, Weak = weak });
        }

        //internal static bool DebuggerAttached(out string detail) {
        //    detail = null;
        //    if (System.Diagnostics.Debugger.IsAttached) {
        //        detail = "managed-debugger";
        //        return true;
        //    }
        //    if (Application.platform != RuntimePlatform.WindowsPlayer && Application.platform != RuntimePlatform.WindowsEditor) {
        //        return false;
        //    }
        //    try {
        //        if (NativeWin32.IsDebuggerPresent()) {
        //            detail = "IsDebuggerPresent";
        //            return true;
        //        }
        //        bool remote = false;
        //        NativeWin32.CheckRemoteDebuggerPresent(Process.GetCurrentProcess().Handle, ref remote);
        //        if (remote) {
        //            detail = "CheckRemoteDebuggerPresent";
        //            return true;
        //        }
        //    } catch (Exception e) {
        //        Logger.LogDebug($"CheatDetector.DebuggerAttached failed: {e.Message}");
        //    }
        //    return false;
        //}

        internal static void ReportCheatScanSummary(CheatSummaryReport report) {
            try {
                if (ZNet.instance != null && ZNet.instance.GetServerPeer() != null && ValConfig.CheatDetectionRPC != null) {
                    string yaml = DataObjects.yamlserializer.Serialize(report);
                    ZPackage package = new ZPackage();
                    package.Write(yaml);
                    ValConfig.CheatDetectionRPC.SendPackage(ZNet.instance.GetServerPeer().m_uid, package);
                }
            } catch (Exception e) {
                Logger.LogDebug($"CheatDetector.ReportCheatScanSummary failed: {e.Message}");
            }
        }

        private static class NativeWin32 {
            public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

            [DllImport("user32.dll")]
            public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

            [DllImport("kernel32.dll")]
            public static extern bool IsDebuggerPresent();

            [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
            public static extern bool CheckRemoteDebuggerPresent(IntPtr hProcess, ref bool isDebuggerPresent);
        }

        internal class CheatDetectorBehaviour : MonoBehaviour {
            private float nextScan;

            // Assemblies (by full name) already type-inspected. Ensures GetTypes() runs at most
            // once per assembly for the lifetime of the behaviour. Main-thread only.
            private readonly HashSet<string> inspected = new HashSet<string>();

            // Newly-loaded assemblies queued by the AssemblyLoad event. The event can fire on a
            // non-Unity thread while the assembly is still loading, so we only enqueue here and
            // inspect on the main thread in Update() where types are fully available.
            private readonly ConcurrentQueue<Assembly> pending = new ConcurrentQueue<Assembly>();

            private bool toolerDetected;
            private string toolerDetail;
            private bool reported;

            // Tools already reported this session. Without this latch a tool left running would be
            // re-reported every scan interval, flooding the server log under the Log action and
            // re-triggering the kick under Kick.
            private readonly HashSet<string> reportedTools = new HashSet<string>();

            // Rotates the three scan vectors across successive ticks so their cost never lands on
            // the same frame. Process enumeration in particular is a blocking syscall.
            private int scanPhase;

            private void OnEnable() {
                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoaded;
            }

            private void OnDisable() {
                AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoaded;
            }

            private void Start() {
                // Inspect what is already loaded once, spread over frames to avoid a hitch. The
                // AssemblyLoad subscription (OnEnable, runs before Start) already covers anything
                // that loads during the sweep; the inspected-set dedupes the overlap.
                StartCoroutine(InitialAssemblySweep());
            }

            private void OnAssemblyLoaded(object sender, AssemblyLoadEventArgs args) {
                if (args?.LoadedAssembly != null) {
                    pending.Enqueue(args.LoadedAssembly);
                }
            }

            private void Update() {
                // Always drain the queue so it cannot grow unbounded, even while disabled.
                bool enabled = ValConfig.EnableCheatDetection.Value;
                DrainPending(enabled && ValConfig.DetectValheimTooler.Value);

                if (!enabled) { return; }

                // Retry reporting until the local character identity is available.
                if (toolerDetected && !reported) { TryReportTooler(); }

                if (Time.unscaledTime < nextScan) { return; }
                nextScan = Time.unscaledTime + Mathf.Max(5, ValConfig.CheatScanIntervalSeconds.Value);
                RunPeriodicScan();
            }

            private void DrainPending(bool inspect) {
                while (pending.TryDequeue(out Assembly asm)) {
                    if (inspect) { InspectAssembly(asm); }
                }
            }

            private IEnumerator InitialAssemblySweep() {
                const int batchSize = 15;
                int processed = 0;
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies()) {
                    if (ValConfig.EnableCheatDetection.Value && ValConfig.DetectValheimTooler.Value) {
                        InspectAssembly(asm);
                    }
                    if (++processed % batchSize == 0) {
                        yield return null;
                    }
                }
            }

            // Inspects an assembly exactly once (deduped by full name). Latches detection; the
            // actual report is sent from TryReportTooler once the player identity is known.
            private void InspectAssembly(Assembly asm) {
                if (asm == null) { return; }
                string id = asm.FullName;
                if (id != null && !inspected.Add(id)) { return; }

                if (!toolerDetected && AssemblyHostsTooler(asm, out string detail)) {
                    toolerDetected = true;
                    toolerDetail = detail;
                    Logger.LogWarning($"ValheimTooler detected ({detail}).");
                }
            }

            private void TryReportTooler() {
                if (CharacterManager.PlayerCharacter == null) { return; }
                reported = true;
                Logger.LogWarning($"Reporting ValheimTooler detection to server for ban ({toolerDetail}).");
                ReportCheatScanSummary(new CheatSummaryReport {
                    PlayerName = CharacterManager.PlayerCharacter.Name,
                    PlatformID = CharacterManager.PlayerCharacter.HostID,
                    ValheimToolerStatus = true
                });
            }

            private void RunPeriodicScan() {
                // Fallback assembly sweep: covers the rare case a native injector loads an
                // assembly without raising the managed AssemblyLoad event. Cached assemblies are
                // skipped, so this is near-free in steady state.
                if (ValConfig.DetectValheimTooler.Value && !toolerDetected) {
                    foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies()) {
                        InspectAssembly(asm);
                        if (toolerDetected) { break; }
                    }
                }

                // Identity comes from the character save, and the report is useless without it.
                if (CharacterManager.PlayerCharacter == null) { return; }

                List<CheatToolSignature> signatures = CheatToolCatalog.Enabled();
                if (signatures.Count == 0 && !ValConfig.DetectGenericTrainers.Value) { return; }

                List<CheatToolDetection> detections;
                switch (scanPhase++ % 3) {
                    case 0:
                        detections = ScanProcesses(signatures);
                        break;
                    case 1:
                        detections = ValConfig.ScanLoadedModules.Value
                            ? ScanLoadedModules(signatures)
                            : new List<CheatToolDetection>();
                        break;
                    default:
                        detections = ValConfig.ScanWindowTitles.Value
                            ? ScanWindows(signatures)
                            : new List<CheatToolDetection>();
                        break;
                }

                ReportNewDetections(detections);
            }

            // Sends only tools not already reported this session, in a single report. Weak and
            // strong sightings latch under separate keys so an early weak sighting (a generic
            // window class) cannot suppress a later enforceable detection of the same tool.
            private void ReportNewDetections(List<CheatToolDetection> detections) {
                List<CheatToolDetection> fresh = null;
                foreach (CheatToolDetection d in detections) {
                    if (d.Weak && reportedTools.Contains(d.Tool)) { continue; }
                    if (!reportedTools.Add(d.Weak ? d.Tool + "|weak" : d.Tool)) { continue; }
                    if (fresh == null) { fresh = new List<CheatToolDetection>(); }
                    fresh.Add(d);
                    if (d.Weak) {
                        Logger.LogWarning($"Possible cheat tool, low confidence (server will log only): {d.Tool} ({d.Vector}: {d.Detail}).");
                    } else {
                        Logger.LogWarning($"Cheat tool detected: {d.Tool} ({d.Vector}: {d.Detail}).");
                    }
                }
                if (fresh == null) { return; }

                ReportCheatScanSummary(new CheatSummaryReport {
                    PlayerName = CharacterManager.PlayerCharacter.Name,
                    PlatformID = CharacterManager.PlayerCharacter.HostID,
                    DetectedTools = fresh
                });
            }

            //private IEnumerator SpeedhackDriftLoop() {
            //    while (true) {
            //        yield return new WaitForSecondsRealtime(10f);
            //        if (!ValConfig.EnableCheatDetection.Value || !ValConfig.DetectSpeedhack.Value) continue;

            //        Stopwatch sw = Stopwatch.StartNew();
            //        float u0 = Time.unscaledTime;
            //        yield return new WaitForSecondsRealtime(2f);
            //        float uDelta = Time.unscaledTime - u0;
            //        double wallDelta = sw.Elapsed.TotalSeconds;
            //        if (Math.Abs(uDelta - wallDelta) > 0.4) {
            //            ReportDetection("Speedhack", $"uDelta={uDelta:F3} wall={wallDelta:F3}");
            //        }
            //    }
            //}
        }
    }
}
