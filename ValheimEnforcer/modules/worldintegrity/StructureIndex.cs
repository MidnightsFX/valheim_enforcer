using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimEnforcer.common;
using ValheimEnforcer.modules.cheatmonitor;

namespace ValheimEnforcer.modules.worldintegrity {

    /// <summary>
    /// Classifies every prefab this world knows about, answering the three questions the structure validator
    /// asks of an incoming ZDO: can a player build this, is it a structure at all, and what is the most health
    /// it was ever designed to have.
    ///
    /// "Can a player build this" is answered by PieceTable membership rather than by anything on the piece
    /// itself, because a PieceTable entry is precisely what makes a prefab placeable. The hammer, the hoe, the
    /// cultivator and every bulk-building or blueprint mod all place out of the same tables, and Jotunn
    /// registers modded pieces into them, so a server's custom content is covered without anyone listing it.
    /// A prefab that carries a Piece component but sits in no table is one no legitimate build path can
    /// produce - which is exactly the village, dungeon and ruin geometry that shows a nameplate with no
    /// crafter on it.
    ///
    /// Built lazily, once per world. It needs ObjectDB and ZNetScene up AND every mod to have finished
    /// registering its content, and there is no single event that means both.
    /// </summary>
    internal static class StructureIndex {

        /// <summary>
        /// Relative slack on the health ceiling. Health arrives as a float that has been through repeated
        /// subtract-and-resend cycles, so an exact greater-than against a recomputed maximum would eventually
        /// trip on rounding alone.
        /// </summary>
        private const float HealthEpsilon = 0.01f;

        /// <summary>
        /// Prefabs a client legitimately creates that are in no piece table. Hardcoded rather than
        /// configurable, on the same reasoning as CheatToolCatalog: getting this list wrong is a correctness
        /// bug in the detector, not a matter of server policy.
        ///
        /// The terrain names are the set Valheim itself enumerates in ZDOMan.ConvertCreationTime. Most are hoe
        /// or cultivator table entries and would pass on that alone; listing them keeps the detector correct
        /// even if a mod rebuilds those tables.
        /// </summary>
        private static readonly HashSet<string> AlwaysAllowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "_TerrainCompiler",
            "Player_tombstone",
            "cultivate", "raise", "path", "paved_road", "mud_road", "digg", "digg_v2",
            "replant", "LevelTerrain", "ship_construction",
            "HeathRockPillar", "HeathRockPillar_frac",
        };

        private static bool built;
        private static readonly HashSet<int> nonBuildableStructures = new HashSet<int>();
        private static readonly Dictionary<int, float> designedHealth = new Dictionary<int, float>();
        private static readonly Dictionary<int, string> prefabNames = new Dictionary<int, string>();

        /// <summary>
        /// Builds the tables on first use. Returns false when the world is not far enough along to classify
        /// anything, in which case every caller treats the ZDO as fine - a detector that cannot tell what a
        /// prefab is must not guess.
        /// </summary>
        internal static bool EnsureBuilt() {
            if (built) { return true; }
            if (ZNetScene.instance == null || ZNetScene.instance.m_prefabs == null) { return false; }

            try {
                HashSet<int> buildable = CollectBuildablePieces();
                // No piece tables at all means ObjectDB is not ready yet, or a mod has emptied them. Either
                // way every piece would look non-buildable, so refuse to build rather than flag the world.
                if (buildable.Count == 0) {
                    Logger.LogDebug("Structure index deferred: no piece tables are populated yet.");
                    return false;
                }

                nonBuildableStructures.Clear();
                designedHealth.Clear();
                prefabNames.Clear();

                foreach (GameObject prefab in ZNetScene.instance.m_prefabs) {
                    if (prefab == null) { continue; }
                    int hash = prefab.name.GetStableHashCode();
                    prefabNames[hash] = prefab.name;

                    WearNTear wear = prefab.GetComponent<WearNTear>();
                    if (wear != null) { designedHealth[hash] = wear.m_health; }

                    if (prefab.GetComponent<Piece>() == null) { continue; }
                    if (buildable.Contains(hash)) { continue; }
                    if (AlwaysAllowed.Contains(prefab.name)) { continue; }
                    nonBuildableStructures.Add(hash);
                }

                built = true;
                Logger.LogInfo($"Structure index built: {buildable.Count} buildable pieces, {nonBuildableStructures.Count} non-buildable structures, {designedHealth.Count} prefabs with a health ceiling.");
                return true;
            } catch (Exception e) {
                // Nothing here may take the ZDO stream down. A failed build leaves built=false so the next
                // packet retries; if it keeps failing the detector stays quiet, which is the safe direction
                // for a check that can delete player content.
                Logger.LogWarning($"Could not build the structure index, structure validation is inactive: {e.Message}");
                return false;
            }
        }

        /// <summary>Drops the tables so the next world rebuilds them. Called from the ZNet.Shutdown teardown.</summary>
        internal static void Invalidate() {
            built = false;
            nonBuildableStructures.Clear();
            designedHealth.Clear();
            prefabNames.Clear();
            ignoreListRaw = null;
            ignoreListParsed = new List<string>();
        }

        internal static bool IsBuilt() { return built; }

        /// <summary>
        /// Every prefab reachable from a PieceTable. Read from ObjectDB's items, where the hammer, hoe and
        /// cultivator hang their tables, and unioned with a scan of loaded PieceTable assets, which catches a
        /// table a mod registered without attaching it to an ObjectDB item.
        /// </summary>
        private static HashSet<int> CollectBuildablePieces() {
            HashSet<int> buildable = new HashSet<int>();

            if (ObjectDB.instance != null && ObjectDB.instance.m_items != null) {
                foreach (GameObject item in ObjectDB.instance.m_items) {
                    if (item == null) { continue; }
                    ItemDrop drop = item.GetComponent<ItemDrop>();
                    if (drop == null || drop.m_itemData == null || drop.m_itemData.m_shared == null) { continue; }
                    AddTable(buildable, drop.m_itemData.m_shared.m_buildPieces);
                }
            }

            foreach (PieceTable table in Resources.FindObjectsOfTypeAll<PieceTable>()) {
                AddTable(buildable, table);
            }

            return buildable;
        }

        private static void AddTable(HashSet<int> buildable, PieceTable table) {
            if (table == null || table.m_pieces == null) { return; }
            foreach (GameObject piece in table.m_pieces) {
                if (piece == null) { continue; }
                buildable.Add(piece.name.GetStableHashCode());
            }
        }

        /// <summary>True for a prefab that carries a Piece component and is in no piece table.</summary>
        internal static bool IsNonBuildableStructure(int prefabHash) {
            return built && nonBuildableStructures.Contains(prefabHash);
        }

        /// <summary>The prefab's authored WearNTear health, before any world-level boost.</summary>
        internal static bool TryGetDesignedHealth(int prefabHash, out float health) {
            health = 0f;
            return built && designedHealth.TryGetValue(prefabHash, out health);
        }

        internal static string NameOf(int prefabHash) {
            string name;
            if (prefabNames.TryGetValue(prefabHash, out name)) { return name; }
            return $"unknown prefab ({prefabHash})";
        }

        /// <summary>
        /// The highest health this prefab can legitimately hold. WearNTear.Awake adds the world-level boost on
        /// top of the authored value and RPC_Repair writes exactly that total, so it is the real ceiling; the
        /// configured multiplier is headroom for mods that raise piece health at runtime rather than on the
        /// prefab.
        /// </summary>
        internal static float HealthLimitFor(float authoredHealth) {
            float worldLevelBoost = 1f;
            if (Game.m_worldLevel > 0 && Game.instance != null) {
                worldLevelBoost += Game.m_worldLevel * Game.instance.m_worldLevelPieceHPMultiplier;
            }
            float allowance = Mathf.Max(1f, ValConfig.StructureHealthAllowedMultiplier.Value);
            return authoredHealth * worldLevelBoost * allowance * (1f + HealthEpsilon);
        }

        // ---- Admin allowlist ------------------------------------------------------------------------------

        // Only consulted once a prefab has already been flagged, which is rare, so the parsed list is cached
        // to avoid re-splitting the setting rather than for throughput.
        private static string ignoreListRaw;
        private static List<string> ignoreListParsed = new List<string>();

        /// <summary>
        /// True if an admin has allowlisted this prefab. Matched as a case-insensitive substring so one entry
        /// covers a family of prefabs a mod adds, and applied last so it overrides everything above.
        /// </summary>
        internal static bool IsIgnored(int prefabHash) {
            string raw = ValConfig.IgnoredStructurePrefabs.Value ?? "";
            if (raw != ignoreListRaw) {
                ignoreListParsed = CheatToolCatalog.SplitList(raw);
                ignoreListRaw = raw;
            }
            if (ignoreListParsed.Count == 0) { return false; }

            string name = NameOf(prefabHash);
            foreach (string entry in ignoreListParsed) {
                if (name.IndexOf(entry, StringComparison.OrdinalIgnoreCase) >= 0) { return true; }
            }
            return false;
        }
    }
}
