using System;
using System.Collections.Generic;
using System.Diagnostics;
using ValheimEnforcer.modules.audit;
using ValheimEnforcer.modules.character;
using ValheimEnforcer.modules.network;
using ValheimEnforcer.modules.worldintegrity;

namespace ValheimEnforcer.common {

    /// <summary>
    /// The one place that knows what this mod is holding in memory, and how much the process is using
    /// overall. Feeds enforcer-memory and the periodic log line (MemoryReportIntervalMinutes).
    ///
    /// Everything here is a count or a size read straight off the structure that owns it; nothing is
    /// walked, sorted or copied, so it is safe to run on a full server. Each figure is produced on its own
    /// so one that cannot be read - a vanilla field renamed by an update, an API missing on this platform -
    /// is reported as unavailable rather than blanking the rest.
    ///
    /// The vanilla figures are READ ONLY. They are there so an operator can tell the parts of the server's
    /// memory this mod does not touch - and cannot: the per-peer object tables and the dead-object list
    /// belong to the game, grow with uptime, and are the business of a performance mod.
    /// </summary>
    internal static class MemoryReport {

        internal static List<string> Lines() {
            List<string> lines = new List<string>();
            Add(lines, "Process", ProcessLine);
            Add(lines, "Managed heap", ManagedLine);
            Add(lines, "Character store", CharacterStoreLine);
            Add(lines, "Per-player tables", TablesLine);
            Add(lines, "Audit", AuditLine);
            Add(lines, "Save archives", ArchiveLine);
            Add(lines, "Connections", ConnectionsLine);
            Add(lines, "World objects", WorldLine);
            Add(lines, "Per-peer object tables", PeerTablesLine);
            return lines;
        }

        internal static void LogSummary() {
            foreach (string line in Lines()) { Logger.LogInfo($"[memory] {line}"); }
        }

        private static void Add(List<string> lines, string label, Func<string> produce) {
            string value;
            try {
                value = produce();
            } catch (Exception e) {
                value = $"unavailable ({e.GetType().Name}: {e.Message})";
            }
            if (value != null) { lines.Add($"{label}: {value}"); }
        }

        // Null when the feature is off, so the report does not carry a row about something nobody enabled.
        // Counts are since this process started; the size is the last archive written, not the total on disk,
        // because walking the archive folder is a directory scan and nothing else in this report does one.
        private static string ArchiveLine() {
            if (ValConfig.EnableSaveArchives == null || !ValConfig.EnableSaveArchives.Value) { return null; }
            modules.archive.SaveArchiver.Stats stats = modules.archive.SaveArchiver.Snapshot();
            string last = stats.LastUtc == DateTime.MinValue
                ? "none yet this session"
                : $"last {modules.archive.SaveArchiver.Describe(stats.LastBytes)} at {stats.LastUtc:HH:mm:ss}Z";
            return $"{stats.Written} written, {stats.Failed} failed, {stats.Pruned} pruned, {stats.QueueDepth} queued; {last}";
        }

        private static string ProcessLine() {
            using (Process process = Process.GetCurrentProcess()) {
                return $"working set {Mb(process.WorkingSet64)}, peak {Mb(process.PeakWorkingSet64)}";
            }
        }

        private static string ManagedLine() {
            string line = $"{Mb(GC.GetTotalMemory(false))} in use, {GC.CollectionCount(0)} collection(s)";
            long heap = 0;
            long used = 0;
            try {
                heap = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong();
                used = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            } catch (Exception) {
                // Not every player build exposes these; the GC figures stand on their own.
            }
            if (heap > 0) { line += $"; mono heap {Mb(heap)} reserved, {Mb(used)} used"; }
            return line;
        }

        private static string CharacterStoreLine() {
            CharacterStore.Stats s = CharacterStore.Snapshot();
            string eviction = s.IdleMinutes > 0
                ? $"idle limit {s.IdleMinutes} min, last sweep dropped {s.LastSweepEvicted} ({s.TotalEvicted} this session)"
                : "idle eviction off";
            return $"{s.Cached} character(s) held ({s.Parsed} parsed, {s.Placeholders} on disk only, {s.Dirty} awaiting write), " +
                   $"{s.QueueDepth} update(s) queued; {eviction}";
        }

        private static string TablesLine() {
            FirstSaveEnforcement.Counts(out int armedNew, out int armedReturning);
            return $"drift resync cooldowns {ValConfig.DriftResyncTrackedCount}, guard report cooldowns {RpcGuardPolicy.TrackedCount}, " +
                   $"structure notify cooldowns {StructureValidator.TrackedCount}, item origin cooldowns {ItemOriginValidator.TrackedCount}, " +
                   $"trust records {PeerTrust.TrackedCount}, first-save armed {armedNew} new / {armedReturning} returning, " +
                   $"known player ids {KnownPlayerIds.Count()}";
        }

        private static string AuditLine() {
            return $"{ContainerAudit.SnapshotCount} container snapshot(s), {AuditLog.BufferedCount} event(s) buffered, {DamageAudit.WindowCount} damage window(s)";
        }

        private static string ConnectionsLine() {
            if (ZNet.instance == null) { return "no ZNet"; }
            return $"{ZNet.instance.GetPeers().Count} peer(s), {ZNet.instance.GetNrOfPlayers()} player(s)";
        }

        private static string WorldLine() {
            if (ZDOMan.instance == null) { return "no ZDOMan"; }
            string line = $"{ZDOMan.instance.m_objectsByID.Count} live, {ZDOMan.instance.m_deadZDOs.Count} remembered as destroyed";
            if (ZoneSystem.instance != null) { line += $", {ZoneSystem.instance.m_generatedZones.Count} zone(s) generated"; }
            return line;
        }

        // The game keeps, per connection, a table of every object it has ever sent that peer, and only ever
        // trims it when an object is destroyed or changes zone. It is the vanilla structure that grows
        // fastest with player count and uptime, so it is worth naming alongside the mod's own figures.
        private static string PeerTablesLine() {
            if (ZDOMan.instance == null) { return "no ZDOMan"; }
            long total = 0;
            int max = 0;
            int peers = 0;
            string maxName = null;
            foreach (var peer in ZDOMan.instance.m_peers) {
                if (peer == null || peer.m_zdos == null) { continue; }
                peers++;
                int count = peer.m_zdos.Count;
                total += count;
                if (count > max) {
                    max = count;
                    maxName = peer.m_peer?.m_playerName;
                }
            }
            if (peers == 0) { return "no peers"; }
            string largest = string.IsNullOrEmpty(maxName) ? $"largest {max}" : $"largest {max} ({maxName})";
            return $"{total} object(s) tracked across {peers} peer(s), {largest}";
        }

        private static string Mb(long bytes) {
            return $"{bytes / (1024d * 1024d):F1} MB";
        }
    }
}
