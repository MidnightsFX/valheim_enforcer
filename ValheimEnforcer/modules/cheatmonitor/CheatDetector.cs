using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
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

        // The one live detector. Initialize is wired to MinimapManager.OnVanillaMapDataLoaded, which
        // fires on every world load, and the host is DontDestroyOnLoad - so without this guard every
        // join added another detector that nothing ever destroyed. Each one ran its own process and
        // module scan on its own drifting timer, which is what turned a periodic cost into a stream of
        // irregular stalls that got worse the longer the game stayed open. Mirrors
        // CharacterDeltaTracker.DeltaTracker.
        private static CheatDetectorBehaviour instance;

        internal static void Initialize() {
            if (ZNet.instance != null && ZNet.instance.IsDedicated() || instance != null) {
                return;
            }
            if (ValConfig.EnableCheatDetection.Value == false) {
                return;
            }

            GameObject host = new GameObject("VE_CheatDetector");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            instance = host.AddComponent<CheatDetectorBehaviour>();
            Logger.LogDebug("CheatDetector initialized.");
        }

        /// <summary>
        /// Destroys the detector at the end of a session so returning to the menu does not leave one
        /// behind for the next world load to duplicate. Destroying the host disables the behaviour,
        /// and OnDisable releases its AppDomain.AssemblyLoad subscription.
        /// </summary>
        internal static void Teardown() {
            if (instance == null) { return; }
            UnityEngine.Object.Destroy(instance.gameObject);
            instance = null;
            // Not a direct clear of the module cache: a scan may still be running on a worker thread and
            // holding that set. Bumping the generation makes the NEXT scan's captured policy key differ, so the
            // worker clears it itself - keeping every write to that set on the one thread that owns it.
            sessionGeneration++;
            Logger.LogDebug("CheatDetector torn down.");
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
        internal static List<CheatToolDetection> ScanProcesses(List<CheatToolSignature> signatures, bool genericTrainers, bool includeElevated) {
            List<CheatToolDetection> found = new List<CheatToolDetection>();
            StallWatch timer = StallWatch.StartBackground("Cheat scan: process enumeration");
            try {
                foreach (string name in RunningProcessNames(includeElevated)) {
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
                timer.Stop();
            }
            return found;
        }

        /// <summary>
        /// The names of the running processes, without the ".exe" suffix.
        ///
        /// Process.GetProcesses cannot be trusted for this under Unity's Mono. Mono builds each entry by opening the
        /// process with PROCESS_ALL_ACCESS and silently leaves out every one it cannot open - which, for a game running
        /// as an ordinary user, is every program run as administrator and every service: about a third of the
        /// processes on a typical desktop. Any catalog tool run elevated was invisible to this scan, whatever the
        /// catalog said.
        ///
        /// A Toolhelp snapshot lists every process by image name without opening any of them, so there is nothing to
        /// be refused. It is also one syscall where the Mono route was an OpenProcess and a read of the target's memory
        /// per process.
        /// </summary>
        private static IEnumerable<string> RunningProcessNames(bool includeElevated) {
            // Not a Unity API, so it is safe to read from the worker thread.
            bool windows = Environment.OSVersion.Platform == PlatformID.Win32NT;
            return windows && includeElevated ? NativeProcessNames() : ManagedProcessNames();
        }

        private static IEnumerable<string> NativeProcessNames() {
            IntPtr snapshot = NativeWin32.CreateToolhelp32Snapshot(NativeWin32.TH32CS_SNAPPROCESS, 0);
            if (snapshot == NativeWin32.InvalidHandleValue) {
                Logger.LogDebug("CreateToolhelp32Snapshot failed; falling back to the managed process list.");
                return ManagedProcessNames();
            }

            List<string> names = new List<string>();
            try {
                NativeWin32.PROCESSENTRY32W entry = new NativeWin32.PROCESSENTRY32W();
                entry.dwSize = (uint)Marshal.SizeOf(typeof(NativeWin32.PROCESSENTRY32W));
                // There is always at least System, so a first call that fails means the entry itself was refused (a
                // struct size the OS does not accept), not an empty machine. Fall back to the old list rather than
                // report nothing running at all.
                if (!NativeWin32.Process32FirstW(snapshot, ref entry)) {
                    Logger.LogDebug($"Process32First failed ({Marshal.GetLastWin32Error()}); falling back to the managed process list.");
                    return ManagedProcessNames();
                }
                do {
                    string exe = entry.szExeFile ?? "";
                    names.Add(exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe.Substring(0, exe.Length - 4) : exe);
                } while (NativeWin32.Process32NextW(snapshot, ref entry));
            } finally {
                NativeWin32.CloseHandle(snapshot);
            }
            return names;
        }

        /// <summary>
        /// The original implementation, kept for anything that is not Windows and for when ScanElevatedProcesses is off.
        /// On Windows under Mono it is missing every process the game cannot open; see RunningProcessNames.
        /// </summary>
        private static IEnumerable<string> ManagedProcessNames() {
            List<string> names = new List<string>();
            Process[] procs = null;
            try {
                procs = Process.GetProcesses();
                foreach (Process p in procs) {
                    // ProcessName throws for processes that exit between enumeration and access.
                    try { names.Add(p.ProcessName ?? ""); } catch { }
                }
            } finally {
                // Process objects hold OS handles; dispose them so the periodic scan does not leak.
                if (procs != null) {
                    foreach (Process p in procs) {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
            return names;
        }

        /// <summary>
        /// Inspects the native modules loaded into our own process. This is the only vector that sees
        /// a cheat which has already injected and then closed its launcher, and it is unaffected by
        /// renaming the tool's executable.
        /// </summary>
        internal static List<CheatToolDetection> ScanLoadedModules(List<CheatToolSignature> signatures, string policyKey) {
            List<CheatToolDetection> found = new List<CheatToolDetection>();
            StallWatch timer = StallWatch.StartBackground("Cheat scan: loaded module enumeration");
            try {
                // A change to the catalog or the allowlist means every module has to be reconsidered, because a
                // module skipped under the old policy might match under the new one. Anything else - the same
                // policy, the same modules - is a no-op after the first pass.
                if (moduleScanPolicy != policyKey) {
                    seenModules.Clear();
                    moduleScanPolicy = policyKey;
                }

                foreach (string name in NewlyLoadedModuleNames()) {
                    if (name.Length == 0 || CheatToolCatalog.IsIgnored(name)) { continue; }

                    foreach (CheatToolSignature sig in signatures) {
                        if (CheatToolCatalog.Matches(name, sig.ModuleNames, MatchMode.Prefix)) {
                            Add(found, sig.Tool, "module", name);
                        }
                    }
                }
            } catch (Exception e) {
                Logger.LogDebug($"CheatDetector.ScanLoadedModules failed: {e.Message}");
            } finally {
                timer.Stop();
            }
            return found;
        }

        // Module handles already examined, and the catalog+allowlist they were examined under. Worker-thread
        // only: the scan task is never allowed to overlap itself (RunPeriodicScan skips a tick while one is in
        // flight), so no lock is needed. Cleared on Teardown so a new session re-examines everything - a
        // detection latch is per-session, and carrying the seen-set across would let a module reported to one
        // server go unreported to the next.
        private static readonly HashSet<IntPtr> seenModules = new HashSet<IntPtr>();
        private static string moduleScanPolicy;

        /// <summary>
        /// Incremented on teardown, and folded into the policy key the main thread hands each scan. A session
        /// boundary has to invalidate the seen-set - the per-session detection latch resets there, so a module
        /// reported to one server would otherwise go unreported to the next - but the main thread must not
        /// touch the set to do it.
        /// </summary>
        private static int sessionGeneration;


        /// <summary>
        /// The base names of modules loaded into this process that have not been examined yet.
        ///
        /// This used to be <c>Process.Modules</c>, which was measured at ~116ms on a modded install and is the
        /// reason this method exists. That property does not just list modules: for every one of the several
        /// hundred a modded Valheim loads, .NET resolves the full file path and builds a ProcessModule holding
        /// base address, memory size and entry point - none of which is used here, since the only thing read is
        /// the name.
        ///
        /// Two changes. The enumeration asks the OS for module handles only, which is one call returning an
        /// array of pointers, and a name is resolved only for a handle not seen before. Modules are loaded at
        /// startup and essentially never after, so the steady state is a single syscall and a set comparison
        /// with no string work at all.
        ///
        /// Nothing about what gets detected changes: a cheat that injects mid-session appears as a new handle
        /// on the next tick, which is exactly the case this vector exists for.
        /// </summary>
        private static IEnumerable<string> NewlyLoadedModuleNames() {
            // psapi is Windows-only, and the managed path still works everywhere else. Not a Unity API, so it
            // is safe to read from the worker thread.
            bool windows = Environment.OSVersion.Platform == PlatformID.Win32NT;
            return windows ? NativeModuleNames() : ManagedModuleNames();
        }

        private static IEnumerable<string> NativeModuleNames() {
            List<string> names = new List<string>();
            IntPtr self = NativeWin32.GetCurrentProcess(); // pseudo-handle; nothing to close

            IntPtr[] handles = new IntPtr[512];
            uint needed;
            if (!NativeWin32.EnumProcessModules(self, handles, (uint)(handles.Length * IntPtr.Size), out needed)) {
                Logger.LogDebug("EnumProcessModules failed; falling back to the managed module list.");
                return ManagedModuleNames();
            }

            // The first call also reports how much space the full list wants. Grow once and re-ask rather than
            // looping: the count only moves when something loads mid-enumeration, and missing a straggler
            // costs nothing because the next tick picks it up as a new handle anyway.
            int count = (int)(needed / IntPtr.Size);
            if (count > handles.Length) {
                handles = new IntPtr[count];
                if (!NativeWin32.EnumProcessModules(self, handles, (uint)(handles.Length * IntPtr.Size), out needed)) {
                    return ManagedModuleNames();
                }
                count = Math.Min((int)(needed / IntPtr.Size), handles.Length);
            }

            StringBuilder buffer = new StringBuilder(260);
            for (int i = 0; i < count; i++) {
                IntPtr handle = handles[i];
                if (handle == IntPtr.Zero || !seenModules.Add(handle)) { continue; }

                buffer.Length = 0;
                uint written = NativeWin32.GetModuleBaseName(self, handle, buffer, (uint)buffer.Capacity);
                if (written == 0) { continue; } // unloaded between the two calls
                names.Add(buffer.ToString());
            }
            return names;
        }

        /// <summary>
        /// The original implementation, kept for anything that is not Windows. Deduped by name rather than by
        /// handle, since that is what this path exposes; the saving is only in the signature matching, but the
        /// enumeration cost is not worth optimising for a platform Valheim has no native client on.
        /// </summary>
        private static IEnumerable<string> ManagedModuleNames() {
            List<string> names = new List<string>();
            using (Process self = Process.GetCurrentProcess()) {
                foreach (ProcessModule m in self.Modules) {
                    string name;
                    try { name = m.ModuleName ?? ""; } catch { continue; }
                    if (name.Length == 0) { continue; }
                    // A synthetic handle per distinct name: this path has no real ones, and the seen-set is
                    // what makes a repeat scan cheap either way.
                    if (!seenModules.Add(new IntPtr(name.GetStableHashCode()))) { continue; }
                    names.Add(name);
                }
            }
            return names;
        }

        /// <summary>
        /// Enumerates top-level windows and matches their class and title. Catches tools renamed to
        /// evade the process check. Generic framework classes (Cheat Engine's TfrmMain, shared by
        /// every Delphi app with a form named frmMain) only produce weak detections, which the
        /// server logs but never enforces. MainWindowTitle is deliberately not used: it is slow and
        /// comes back empty for windowless and elevated processes.
        /// </summary>
        internal static List<CheatToolDetection> ScanWindows(List<CheatToolSignature> signatures) {
            // The Windows-platform check lives in the caller: Application.platform is a Unity API and
            // this now runs on a worker thread.
            List<CheatToolDetection> found = new List<CheatToolDetection>();
            List<CheatToolSignature> windowed = signatures
                .Where(s => s.WindowClasses.Length > 0 || s.WeakWindowClasses.Length > 0 || s.WindowTitles.Length > 0).ToList();
            if (windowed.Count == 0) { return found; }

            StallWatch timer = StallWatch.StartBackground("Cheat scan: window enumeration");
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
            } finally {
                timer.Stop();
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

            // Module enumeration, used instead of Process.Modules - see NewlyLoadedModuleNames for why.
            // psapi.dll's exports forward to kernel32 on every supported Windows, so this is the portable
            // spelling rather than an old one.
            [DllImport("kernel32.dll")]
            public static extern IntPtr GetCurrentProcess();

            [DllImport("psapi.dll", SetLastError = true)]
            public static extern bool EnumProcessModules(IntPtr hProcess, [Out] IntPtr[] lphModule, uint cb, out uint lpcbNeeded);

            [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern uint GetModuleBaseName(IntPtr hProcess, IntPtr hModule, StringBuilder lpBaseName, uint nSize);

            [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
            public static extern bool CheckRemoteDebuggerPresent(IntPtr hProcess, ref bool isDebuggerPresent);

            // Process enumeration, used instead of Process.GetProcesses - see RunningProcessNames for why.
            public const uint TH32CS_SNAPPROCESS = 0x00000002;
            public static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            public struct PROCESSENTRY32W {
                public uint dwSize;
                public uint cntUsage;
                public uint th32ProcessID;
                public IntPtr th32DefaultHeapID;
                public uint th32ModuleID;
                public uint cntThreads;
                public uint th32ParentProcessID;
                public int pcPriClassBase;
                public uint dwFlags;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
                public string szExeFile;
            }

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
            public static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
            public static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool CloseHandle(IntPtr hObject);
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

            // The scan in flight, or null. Process and module enumeration are blocking Windows
            // syscalls that take hundreds of milliseconds on a modded install, so they run off the
            // main thread; Update collects the result and does the reporting back on the main thread.
            private Task<List<CheatToolDetection>> scanTask;

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

                CollectFinishedScan();

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

            /// <summary>
            /// Hands a completed background scan to the reporting path, on the main thread. Reading
            /// Exception observes a faulted task, so a failed scan cannot surface later as an
            /// unobserved task exception.
            /// </summary>
            private void CollectFinishedScan() {
                if (scanTask == null || !scanTask.IsCompleted) { return; }
                Task<List<CheatToolDetection>> finished = scanTask;
                scanTask = null;

                if (finished.Status == TaskStatus.RanToCompletion) {
                    // The scan is dispatched with an identity and collected a few frames later, so the
                    // session can end in between - ReportNewDetections addresses the report with
                    // CharacterManager.PlayerCharacter, which Game.Logout has already nulled by then.
                    // Drop the result rather than report an unattributable detection; a tool that is
                    // still running is found again by the next scan.
                    if (finished.Result != null && CharacterManager.PlayerCharacter != null) {
                        ReportNewDetections(finished.Result);
                    }
                    return;
                }
                if (finished.Exception != null) {
                    Logger.LogDebug($"CheatDetector background scan failed: {finished.Exception.GetBaseException().Message}");
                }
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

                // A scan still running means the previous tick's syscall has not come back yet. Skip
                // this tick rather than queueing behind it, so a slow machine cannot build a backlog.
                if (scanTask != null) { return; }

                bool genericTrainers = ValConfig.DetectGenericTrainers.Value;
                List<CheatToolSignature> signatures = CheatToolCatalog.Enabled();
                if (signatures.Count == 0 && !genericTrainers) { return; }

                // Everything the scan needs from Unity or from config is read here, on the main
                // thread, and captured by value. The worker below touches nothing else: the signature
                // list is an immutable published snapshot, and RefreshIgnoreList republishes the
                // allowlist so IsIgnored only ever reads it.
                CheatToolCatalog.RefreshIgnoreList();
                // Captured here with the rest, so the worker never reads catalog state that the main thread
                // could be rewriting underneath it. The module scan uses it to decide whether the modules it
                // has already examined still need re-examining.
                string policyKey = $"{CheatToolCatalog.PolicyKey()}|{sessionGeneration}";
                int phase = scanPhase++ % 3;
                bool scanModules = ValConfig.ScanLoadedModules.Value;
                bool scanElevated = ValConfig.ScanElevatedProcesses.Value;
                bool scanWindows = ValConfig.ScanWindowTitles.Value
                    && (Application.platform == RuntimePlatform.WindowsPlayer
                        || Application.platform == RuntimePlatform.WindowsEditor);

                // None of the three vectors touch a Unity object, so they belong off the main thread.
                // Process and module enumeration are the expensive ones: blocking syscalls whose cost
                // grows with what is running and, on a modded install, with the hundreds of modules loaded.
                scanTask = Task.Run(() => {
                    switch (phase) {
                        case 0:
                            return ScanProcesses(signatures, genericTrainers, scanElevated);
                        case 1:
                            return scanModules ? ScanLoadedModules(signatures, policyKey) : new List<CheatToolDetection>();
                        default:
                            return scanWindows ? ScanWindows(signatures) : new List<CheatToolDetection>();
                    }
                });
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
