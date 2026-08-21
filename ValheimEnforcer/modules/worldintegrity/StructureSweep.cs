using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.worldintegrity {

    /// <summary>
    /// Walks every ZDO the world holds and reports structures that look placed rather than generated.
    ///
    /// This exists because the live detector only sees structures as they arrive. A server that has already
    /// been hit needs a way to find what is out there, and once an admin has read the report, a way to remove
    /// it. ZDOMan keeps every persistent ZDO for the world in memory, so this really is world-wide - which on
    /// an established server is hundreds of thousands of objects, hence the chunking across frames.
    ///
    /// The hard part is that the live detector's strongest signal does not survive being applied to the whole
    /// world. "A prefab with a Piece component that no build tool can place" is exactly what a dvergr town, a
    /// crypt and a stone ruin are made of - the difference is that a client created the cheated one, and
    /// nothing durable records that. So the sweep adds a test the live path does not need: whether the object
    /// sits in a zone the world generated a location into. Dungeon interiors are bounded to their entrance's
    /// zone (DungeonGenerator.m_zoneCenter is the zone position, m_zoneSize is 64x64x64), so a zone lookup
    /// covers surface and interior alike.
    ///
    /// That test is deliberately generous, and the cost of it is stated in the report: a structure spawned
    /// next to real ruins is excluded along with the ruins. Missing one is recoverable; deleting a player's
    /// dungeon is not, and the live detector catches that case anyway, wherever it happens.
    /// </summary>
    internal static class StructureSweep {

        /// <summary>ZDOs examined per frame. Small enough not to show as a hitch, large enough to finish.</summary>
        private const int ChunkSize = 4000;

        /// <summary>
        /// Objects deleted per frame. Far smaller than the scan chunk: ZDOMan batches destroys into one
        /// routed RPC per Update, so removing thousands without yielding would put the whole batch in a
        /// single packet.
        /// </summary>
        private const int RemoveChunkSize = 200;

        /// <summary>Individual objects listed in the report before it switches to counts only.</summary>
        private const int DetailCap = 100;

        /// <summary>
        /// Most objects an unfiltered 'remove' will delete. Past this the likeliest explanation is that this
        /// server's content classifies differently from vanilla's, not that somebody placed that many
        /// structures by hand - so it stops and asks for a prefab filter instead of emptying the world.
        /// </summary>
        private const int UnfilteredRemoveCap = 500;

        private static GameObject host;

        internal static bool Running { get { return host != null; } }

        /// <summary>
        /// Kicks off a scan. <paramref name="replyTo"/> is the peer to send the report back to, or 0 when the
        /// command was run on the server itself. Returns false with a reason when it cannot start.
        /// </summary>
        internal static bool Start(long replyTo, bool remove, string filter, out string problem) {
            problem = null;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) {
                problem = "A structure scan only runs on the server.";
                return false;
            }
            if (ZDOMan.instance == null) {
                problem = "The world is not loaded yet.";
                return false;
            }
            if (Running) {
                problem = "A structure scan is already running.";
                return false;
            }
            if (!StructureIndex.EnsureBuilt()) {
                problem = "The prefab index is not available yet - try again once the world has finished loading.";
                return false;
            }

            host = new GameObject("VE_StructureSweep");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            SweepBehaviour behaviour = host.AddComponent<SweepBehaviour>();
            behaviour.ReplyTo = replyTo;
            behaviour.Remove = remove;
            behaviour.Filter = string.IsNullOrEmpty(filter) ? null : filter;
            return true;
        }

        /// <summary>Stops a scan in progress, on world shutdown.</summary>
        internal static void Abort() {
            if (host == null) { return; }
            UnityEngine.Object.Destroy(host);
            host = null;
        }

        /// <summary>
        /// True when this position belongs to a location the world generated - a surface ruin, a village, or
        /// the interior of the dungeon underneath one. Its own zone counts outright, because that is where a
        /// dungeon's rooms live; a neighbouring zone's location counts only when its radius actually reaches.
        ///
        /// Returns true when there is no location data at all, so an unknown world is treated as entirely
        /// generated and nothing gets deleted on a guess.
        /// </summary>
        internal static bool InGeneratedLocation(Vector3 pos) {
            ZoneSystem zones = ZoneSystem.instance;
            if (zones == null || zones.m_locationInstances == null || zones.m_locationInstances.Count == 0) { return true; }

            Vector2i zone = ZoneSystem.GetZone(pos);
            for (int dx = -1; dx <= 1; dx++) {
                for (int dy = -1; dy <= 1; dy++) {
                    ZoneSystem.LocationInstance instance;
                    if (!zones.m_locationInstances.TryGetValue(new Vector2i(zone.x + dx, zone.y + dy), out instance)) { continue; }
                    if (dx == 0 && dy == 0) { return true; }
                    if (instance.m_location == null) { continue; }
                    float radius = Mathf.Max(instance.m_location.m_exteriorRadius, instance.m_location.m_interiorRadius);
                    float dxp = pos.x - instance.m_position.x;
                    float dzp = pos.z - instance.m_position.z;
                    if (dxp * dxp + dzp * dzp <= (radius + 8f) * (radius + 8f)) { return true; }
                }
            }
            return false;
        }

        private static void Finished() {
            if (host == null) { return; }
            UnityEngine.Object.Destroy(host);
            host = null;
        }

        private sealed class Hit {
            internal ZDOID Id;
            internal string PrefabName;
            internal Vector3 Position;
            internal string Reason;
            internal float Health = float.NaN;
            internal long Creator;
        }

        private class SweepBehaviour : MonoBehaviour {
            internal long ReplyTo;
            internal bool Remove;
            internal string Filter;

            // A field rather than a ref local: this is counted from inside an iterator, and a plain field
            // keeps the classifier's signature to one argument.
            private int excludedAsGenerated;

            private void Start() {
                StartCoroutine(Run());
            }

            private IEnumerator Run() {
                // Read out of the try rather than yielding from inside its catch, which C# does not allow in
                // an iterator.
                List<ZDO> snapshot = null;
                string readError = null;
                try {
                    snapshot = new List<ZDO>(ZDOMan.instance.m_objectsByID.Values);
                } catch (Exception e) {
                    readError = e.Message;
                }
                if (snapshot == null) {
                    Report($"Structure scan could not read the world's objects: {readError}");
                    Finished();
                    yield break;
                }

                Logger.LogInfo($"Structure scan started over {snapshot.Count} object(s)" +
                               $"{(Remove ? ", removal requested" : "")}" +
                               $"{(Filter != null ? $", filtered to '{Filter}'" : "")}.");

                List<Hit> hits = new List<Hit>();
                int checkedCount = 0;

                // Pass one classifies and changes nothing, so the safety checks below get to see the whole
                // picture before a single object is deleted.
                for (int i = 0; i < snapshot.Count; i++) {
                    checkedCount++;
                    try {
                        Hit hit = Classify(snapshot[i]);
                        if (hit != null) { hits.Add(hit); }
                    } catch (Exception e) {
                        Logger.LogDebug($"Structure scan skipped an object: {e.Message}");
                    }
                    if (i % ChunkSize == ChunkSize - 1) { yield return null; }
                }

                int removed = -1;
                string refusal = null;
                if (Remove) {
                    if (Filter == null && hits.Count > UnfilteredRemoveCap) {
                        refusal = $"Refusing to remove {hits.Count} objects at once without a prefab filter - more than {UnfilteredRemoveCap} " +
                                  "usually means this server's content classifies differently from vanilla's rather than that somebody placed them all. " +
                                  "Re-run with a prefab name from the list above.";
                    } else {
                        removed = 0;
                        for (int i = 0; i < hits.Count; i++) {
                            if (StructureValidator.Remove(hits[i].Id)) {
                                removed++;
                                Logger.LogWarning($"Structure scan removed {hits[i].PrefabName} at ({Where(hits[i].Position)}).");
                            }
                            if (i % RemoveChunkSize == RemoveChunkSize - 1) { yield return null; }
                        }
                    }
                }

                Report(Compose(checkedCount, hits, excludedAsGenerated, removed, refusal));
                Finished();
            }

            /// <summary>
            /// The same two rules the live detector uses, minus the "was this new" and "did this write push it
            /// over" parts, which only mean anything against an arriving packet - plus the generated-location
            /// test, which the live path does not need.
            /// </summary>
            private Hit Classify(ZDO zdo) {
                if (zdo == null) { return null; }
                int prefabHash = zdo.GetPrefab();
                if (prefabHash == 0) { return null; }
                if (StructureIndex.IsIgnored(prefabHash)) { return null; }

                string name = StructureIndex.NameOf(prefabHash);
                if (Filter != null && name.IndexOf(Filter, StringComparison.OrdinalIgnoreCase) < 0) { return null; }

                string reason = null;
                float health = float.NaN;

                if (StructureIndex.IsNonBuildableStructure(prefabHash)) {
                    if (InGeneratedLocation(zdo.GetPosition())) {
                        excludedAsGenerated++;
                        return null;
                    }
                    reason = "not placeable by any build tool, and not part of a generated location";
                } else {
                    float authored;
                    if (!StructureIndex.TryGetDesignedHealth(prefabHash, out authored) || authored <= 0f) { return null; }
                    float current = zdo.GetFloat(ZDOVars.s_health, float.NaN);
                    if (float.IsNaN(current)) { return null; }
                    float limit = StructureIndex.HealthLimitFor(authored);
                    if (!float.IsInfinity(current) && current <= limit) { return null; }
                    health = current;
                    reason = $"health above the {StructureValidator.Describe(limit)} this prefab can hold";
                }

                return new Hit {
                    Id = zdo.m_uid,
                    PrefabName = name,
                    Position = zdo.GetPosition(),
                    Reason = reason,
                    Health = health,
                    Creator = zdo.GetLong(ZDOVars.s_creator, 0L),
                };
            }

            private static string Where(Vector3 pos) {
                return $"{pos.x:F0}, {pos.y:F0}, {pos.z:F0}";
            }

            private string Compose(int checkedCount, List<Hit> hits, int excludedAsGenerated, int removed, string refusal) {
                StringBuilder sb = new StringBuilder();
                sb.Append($"Structure scan complete: {checkedCount} object(s) checked, {hits.Count} flagged");
                if (Filter != null) { sb.Append($" (filtered to '{Filter}')"); }
                sb.Append($". {excludedAsGenerated} non-buildable object(s) skipped as generated world content.");

                if (hits.Count == 0) {
                    sb.Append("\nNothing in this world looks placed by a cheat.");
                    return sb.ToString();
                }

                Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (Hit hit in hits) {
                    int seen;
                    counts.TryGetValue(hit.PrefabName, out seen);
                    counts[hit.PrefabName] = seen + 1;
                }

                sb.Append("\nBy prefab:");
                foreach (KeyValuePair<string, int> entry in counts) {
                    sb.Append($"\n  {entry.Key} x{entry.Value}");
                }

                int shown = Math.Min(hits.Count, DetailCap);
                sb.Append($"\nObjects ({shown} of {hits.Count} listed):");
                for (int i = 0; i < shown; i++) {
                    Hit hit = hits[i];
                    sb.Append($"\n  {hit.PrefabName} at ({Where(hit.Position)}) - {hit.Reason}");
                    if (!float.IsNaN(hit.Health)) { sb.Append($", health {StructureValidator.Describe(hit.Health)}"); }
                    sb.Append(hit.Creator == 0L ? ", no creator" : $", creator {hit.Creator}");
                }
                if (hits.Count > shown) {
                    sb.Append($"\n  ... {hits.Count - shown} more not listed. Narrow the scan with a prefab filter to see them.");
                }

                if (refusal != null) {
                    sb.Append("\n").Append(refusal);
                } else if (removed >= 0) {
                    sb.Append($"\nRemoved {removed} of {hits.Count} flagged object(s).");
                } else {
                    sb.Append("\nNothing was changed. Re-run with 'remove confirm' to delete these.");
                }
                return sb.ToString();
            }

            private void Report(string text) {
                foreach (string line in text.Split('\n')) { Logger.LogInfo(line); }
                if (ReplyTo != 0L) { ValConfig.SendStructureScanReport(ReplyTo, text); }
            }
        }
    }
}
