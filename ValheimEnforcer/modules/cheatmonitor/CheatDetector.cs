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

        private static readonly string[] CheatEngineProcessNames = {
            "cheatengine-x86_64", "cheatengine-i386",
            "cheatengine-x86_64-sse4-avx2", "cheatengine"
        };

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

        internal static bool CheatEngineProcessRunning() {
            Process[] procs = null;
            try {
                procs = Process.GetProcesses();
                foreach (var p in procs) {
                    string pn = p.ProcessName ?? "";
                    if (CheatEngineProcessNames.Any(n => pn.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)) {
                        return true;
                    }
                }
            } catch (Exception e) {
                Logger.LogDebug($"CheatDetector.CheatEngineProcessRunning failed: {e.Message}");
            } finally {
                // Process objects hold OS handles; dispose them so the periodic scan does not leak.
                if (procs != null) {
                    foreach (var p in procs) {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
            return false;
        }

        //internal static bool SuspiciousNativeModuleLoaded(CheatSummaryReport cheatSummary) {
        //    try {
        //        foreach (ProcessModule m in Process.GetCurrentProcess().Modules) {
        //            string n = (m.ModuleName ?? "").ToLowerInvariant();
        //            if (n.StartsWith("speedhack-") || n.StartsWith("dbk32") || n.StartsWith("dbk64") || n.Contains("vehdebug")) {
        //                return true;
        //            }
        //        }
        //    } catch (Exception e) {
        //        Logger.LogDebug($"CheatDetector.SuspiciousNativeModuleLoaded failed: {e.Message}");
        //    }
        //    return false;
        //}

        //internal static bool CheatEngineWindowPresent(out string detail) {
        //    detail = null;
        //    if (Application.platform != RuntimePlatform.WindowsPlayer && Application.platform != RuntimePlatform.WindowsEditor) {
        //        return false;
        //    }
        //    string found = null;
        //    try {
        //        NativeWin32.EnumWindows((hWnd, _) => {
        //            var cls = new StringBuilder(256);
        //            NativeWin32.GetClassName(hWnd, cls, cls.Capacity);
        //            var txt = new StringBuilder(256);
        //            NativeWin32.GetWindowTextW(hWnd, txt, txt.Capacity);
        //            string c = cls.ToString();
        //            string t = txt.ToString();
        //            if (c.StartsWith("TfrmMain") || c.StartsWith("TfrmMemView") ||
        //                t.IndexOf("Cheat Engine", StringComparison.OrdinalIgnoreCase) >= 0) {
        //                found = $"window:class={c}|title={t}";
        //                return false;
        //            }
        //            return true;
        //        }, IntPtr.Zero);
        //    } catch (Exception e) {
        //        Logger.LogDebug($"CheatDetector.CheatEngineWindowPresent failed: {e.Message}");
        //        return false;
        //    }
        //    detail = found;
        //    return found != null;
        //}

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

                // Cheat Engine process check (throttled to the scan interval, handles disposed).
                if (ValConfig.DetectCheatEngine.Value && CharacterManager.PlayerCharacter != null) {
                    if (CheatEngineProcessRunning()) {
                        ReportCheatScanSummary(new CheatSummaryReport {
                            PlayerName = CharacterManager.PlayerCharacter.Name,
                            PlatformID = CharacterManager.PlayerCharacter.HostID,
                            CheatEngineStatus = new CheatEngineDetector { CheatEngineProcessDetected = true }
                        });
                    }
                }
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
