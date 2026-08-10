using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.cheatmonitor {

    internal static class KnownCheaterTracker {

        private const string EmbeddedResourceName = "ValheimEnforcer.assets.KnownCheaters.yaml";

        // Authoritative in-memory map: host/platform id -> reason.
        private static readonly Dictionary<string, string> Cheaters = new Dictionary<string, string>();

        /// <summary>
        /// Loads the embedded seed and the config-folder file, merges them, populates the
        /// in-memory map, and writes the merged union back to disk so the internal seed is
        /// always reflected on disk.
        /// </summary>
        internal static void Initialize() {
            Cheaters.Clear();

            // Seed entries are always included.
            foreach (var entry in ReadEmbeddedSeed()) {
                Upsert(entry);
            }

            // Layer admin/auto-added entries from disk on top.
            string path = ValConfig.KnownCheatersFilePath;
            if (File.Exists(path)) {
                try {
                    foreach (var entry in Parse(File.ReadAllText(path))) {
                        Upsert(entry);
                    }
                } catch (Exception e) {
                    Logger.LogWarning($"Failed to read KnownCheaters file at {path}: {e.Message}");
                }
            }

            SaveToDisk();
            Logger.LogDebug($"KnownCheaterTracker initialized with {Cheaters.Count} entr(ies).");
        }

        /// <summary>
        /// Replaces the in-memory map from yaml text (file-watcher reload), then re-applies the
        /// embedded seed so internal entries can never be dropped by a manual edit.
        /// </summary>
        internal static void LoadFromText(string yaml) {
            Cheaters.Clear();
            foreach (var entry in ReadEmbeddedSeed()) {
                Upsert(entry);
            }
            try {
                foreach (var entry in Parse(yaml)) {
                    Upsert(entry);
                }
            } catch (Exception e) {
                Logger.LogWarning($"Failed to parse KnownCheaters update: {e.Message}");
            }
            Logger.LogInfo($"KnownCheaters list reloaded ({Cheaters.Count} entries).");
        }

        /// <summary>
        /// Adds a cheater to the list and persists it. No-op if the id is already present.
        /// </summary>
        internal static void AddCheater(string id, string reason) {
            if (string.IsNullOrEmpty(id)) { return; }
            if (Cheaters.ContainsKey(id)) { return; }

            Cheaters[id] = reason ?? "";
            Logger.LogInfo($"Added {id} to the known cheaters list ({reason}).");
            SaveToDisk();
        }

        /// <summary>
        /// True if the supplied host/platform id matches a listed cheater. Tolerates a platform
        /// prefix difference (e.g. "Steam_76561..." vs raw "76561...").
        /// </summary>
        internal static bool IsListed(string hostId) {
            return FindMatchKey(hostId) != null;
        }

        internal static string GetReason(string hostId) {
            string key = FindMatchKey(hostId);
            return key != null ? Cheaters[key] : null;
        }

        private static string FindMatchKey(string hostId) {
            if (string.IsNullOrEmpty(hostId)) { return null; }
            if (Cheaters.ContainsKey(hostId)) { return hostId; }

            foreach (var key in Cheaters.Keys) {
                if (PlatformIds.Matches(key, hostId)) { return key; }
            }
            return null;
        }

        private static void Upsert(DataObjects.KnownCheaterEntry entry) {
            if (entry == null || string.IsNullOrEmpty(entry.Id)) { return; }
            Cheaters[entry.Id] = entry.Reason ?? "";
        }

        private static List<DataObjects.KnownCheaterEntry> Parse(string yaml) {
            if (string.IsNullOrWhiteSpace(yaml)) { return new List<DataObjects.KnownCheaterEntry>(); }
            var parsed = DataObjects.yamldeserializer.Deserialize<List<DataObjects.KnownCheaterEntry>>(yaml);
            return parsed ?? new List<DataObjects.KnownCheaterEntry>();
        }

        private static List<DataObjects.KnownCheaterEntry> ReadEmbeddedSeed() {
            try {
                using (Stream stream = typeof(ValheimEnforcer).Assembly.GetManifestResourceStream(EmbeddedResourceName)) {
                    if (stream == null) {
                        Logger.LogWarning($"Embedded KnownCheaters seed resource '{EmbeddedResourceName}' was not found.");
                        return new List<DataObjects.KnownCheaterEntry>();
                    }
                    using (StreamReader reader = new StreamReader(stream)) {
                        return Parse(reader.ReadToEnd());
                    }
                }
            } catch (Exception e) {
                Logger.LogWarning($"Failed to read embedded KnownCheaters seed: {e.Message}");
                return new List<DataObjects.KnownCheaterEntry>();
            }
        }

        private static void SaveToDisk() {
            var entries = Cheaters.Select(kvp => new DataObjects.KnownCheaterEntry { Id = kvp.Key, Reason = kvp.Value }).ToList();
            try {
                ValConfig.GetSecondaryConfigDirectoryPath();
                File.WriteAllText(ValConfig.KnownCheatersFilePath, DataObjects.yamlserializer.Serialize(entries));
            } catch (Exception e) {
                Logger.LogWarning($"Failed to write KnownCheaters file at {ValConfig.KnownCheatersFilePath}: {e.Message}");
            }
        }
    }
}
