using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ValheimEnforcer.modules.mods;

namespace ValheimEnforcer.modules.cheatmonitor {

    /// <summary>
    /// Checks that the cheat detector is still the code that was shipped: that nothing has patched its own
    /// methods, and that it is still attached to the game's assembly loading.
    ///
    /// This mod never patches any of the methods listed here, so any patch on one of them is a finding,
    /// whoever registered it. Findings are medium confidence - logged by the server and posted to the
    /// moderation channel, never acted on by themselves.
    /// </summary>
    internal static class CheatSelfCheck {

        internal const string TamperLabel = "Anti-cheat tampering";
        internal const int MaxFindings = 8;
        private const int MaxFindingLength = 128;

        private static List<MethodBase> targets;

        /// <summary>The methods whose integrity the detector depends on. Resolved once.</summary>
        private static List<MethodBase> Targets() {
            if (targets != null) { return targets; }
            List<MethodBase> found = new List<MethodBase>();
            AddDeclared(found, typeof(CheatDetector),
                "ReportCheatScanSummary", "MatchAssembly", "ScanProcesses", "ScanLoadedModules", "ScanWindows", "HeartbeatSnapshot");
            AddDeclared(found, typeof(CheatDetector.CheatDetectorBehaviour),
                "Update", "RunPeriodicScan", "CollectFinishedScan", "DrainPending", "InspectAssembly", "ReportNewDetections", "OnAssemblyLoaded");
            AddDeclared(found, typeof(CheatToolCatalog),
                "JudgeProxyModule", "ProxyToolForHash", "IsIgnored", "Matches", "MatchesNamespace", "AssemblySignatures", "Enabled");
            AddDeclared(found, typeof(CheatHeartbeat), "BuildAnswer", "OnClientReceive");
            AddDeclared(found, typeof(CodeProvenance), "IsAccounted");
            AddDeclared(found, typeof(PluginHasher), "HashFile");
            AddDeclared(found, typeof(PeCodeHash), "HashFile", "HashBytes");
            AddDeclared(found, typeof(CheatSelfCheck), "Run");
            try {
                MethodInfo getAssemblies = typeof(AppDomain).GetMethod("GetAssemblies", Type.EmptyTypes);
                if (getAssemblies != null) { found.Add(getAssemblies); }
            } catch (Exception) { }
            targets = found;
            return found;
        }

        private static void AddDeclared(List<MethodBase> into, Type type, params string[] names) {
            HashSet<string> wanted = new HashSet<string>(names, StringComparer.Ordinal);
            try {
                foreach (MethodInfo m in AccessTools.GetDeclaredMethods(type)) {
                    if (wanted.Contains(m.Name)) { into.Add(m); }
                }
            } catch (Exception e) {
                Logger.LogDebug($"CheatSelfCheck could not resolve methods on {type?.Name}: {e.Message}");
            }
        }

        /// <summary>
        /// Runs the check and returns what it found, at most <see cref="MaxFindings"/> entries. Main thread
        /// only. <paramref name="loadHandler"/> is the detector's own AssemblyLoad subscription, or null to skip
        /// that half.
        /// </summary>
        internal static List<string> Run(AssemblyLoadEventHandler loadHandler) {
            List<string> findings = new List<string>();
            foreach (MethodBase target in Targets()) {
                if (findings.Count >= MaxFindings) { break; }
                Patches info;
                try { info = Harmony.GetPatchInfo(target); } catch (Exception) { continue; }
                if (info == null) { continue; }
                Describe(findings, target, info.Prefixes, "prefix");
                Describe(findings, target, info.Postfixes, "postfix");
                Describe(findings, target, info.Transpilers, "transpiler");
                Describe(findings, target, info.Finalizers, "finalizer");
                Describe(findings, target, info.ILManipulators, "il");
            }

            if (loadHandler != null && findings.Count < MaxFindings && !StillSubscribed(loadHandler)) {
                findings.Add("the assembly load subscription is gone");
            }
            return findings;
        }

        private static void Describe(List<string> findings, MethodBase target, IEnumerable<Patch> patches, string kind) {
            if (patches == null) { return; }
            foreach (Patch patch in patches) {
                if (patch == null || findings.Count >= MaxFindings) { continue; }
                string source;
                try {
                    Type declaring = patch.PatchMethod?.DeclaringType;
                    source = declaring == null ? "dynamic" : declaring.Assembly.GetName().Name;
                } catch (Exception) {
                    source = "unknown";
                }
                string text = $"{kind} on {target.DeclaringType?.Name}.{target.Name} by {patch.owner} ({source})";
                findings.Add(text.Length > MaxFindingLength ? text.Substring(0, MaxFindingLength) : text);
            }
        }

        /// <summary>
        /// Whether the handler is still on the AppDomain's AssemblyLoad event. Read through the event's backing
        /// field; a runtime that stores it differently answers true, because a check that cannot look must not
        /// accuse.
        /// </summary>
        private static bool StillSubscribed(AssemblyLoadEventHandler handler) {
            try {
                FieldInfo field = typeof(AppDomain).GetField("AssemblyLoad", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (field == null) { return true; }
                Delegate current = field.GetValue(AppDomain.CurrentDomain) as Delegate;
                if (current == null) { return false; }
                foreach (Delegate d in current.GetInvocationList()) {
                    if (d.Method == handler.Method && ReferenceEquals(d.Target, handler.Target)) { return true; }
                }
                return false;
            } catch (Exception) {
                return true;
            }
        }
    }
}
