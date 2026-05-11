using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using ValheimEnforcer.common;
using ValheimEnforcer.modules.character;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.modules {
    internal static class CheatDetector {

        private static readonly HashSet<string> ReportedSignals = new HashSet<string>();

        private static readonly string[] ToolerAssemblyNames = {
            "ValheimTooler", "ValheimToolerMod", "RapidGUI"
        };

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

        internal static bool ValheimToolerLoaded() {
            try {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) {
                    string n = asm.GetName().Name ?? "";
                    if (ToolerAssemblyNames.Any(t => t.Equals(n, StringComparison.OrdinalIgnoreCase))) {
                        return true;
                    }
                }
            } catch (Exception e) {
                Logger.LogDebug($"CheatDetector.ValheimToolerLoaded failed: {e.Message}");
            }
            return false;
        }

        internal static bool CheatEngineProcessRunning() {
            try {
                foreach (var p in Process.GetProcesses()) {
                    string pn = p.ProcessName ?? "";
                    if (CheatEngineProcessNames.Any(n => pn.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)) {
                        return true;
                    }
                }
            } catch (Exception e) {
                Logger.LogDebug($"CheatDetector.CheatEngineProcessRunning failed: {e.Message}");
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

            private void Start() {
                // Not adding speedhack detection yet as its expensive
                //StartCoroutine(SpeedhackDriftLoop());
            }

            private void Update() {
                if (!ValConfig.EnableCheatDetection.Value) return;
                if (Time.unscaledTime < nextScan) return;
                nextScan = Time.unscaledTime + Mathf.Max(1, ValConfig.CheatScanIntervalSeconds.Value);
                RunScan();
            }

            private static void RunScan() {
                // Skip if the local character is not yet set
                if (CharacterManager.PlayerCharacter == null) {
                    return;
                }

                CheatSummaryReport report = new CheatSummaryReport {
                    PlayerName = CharacterManager.PlayerCharacter.Name,
                    PlatformID = CharacterManager.PlayerCharacter.HostID,
                    CheatEngineStatus = new CheatEngineDetector()
                };

                // check for ValheimTooler
                if (ValConfig.DetectValheimTooler.Value && ValheimToolerLoaded()) {
                    report.ValheimToolerStatus = true;
                }

                // Check for cheatEngine
                if (ValConfig.DetectCheatEngine.Value) {
                    if (CheatEngineProcessRunning()) {
                        report.CheatEngineStatus.CheatEngineProcessDetected = true;
                    }
                }

                // Check for suspicious native modules
                //SuspiciousNativeModuleLoaded(report);

                if (report.cheatsDetected()) {
                    ReportCheatScanSummary(report);
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
