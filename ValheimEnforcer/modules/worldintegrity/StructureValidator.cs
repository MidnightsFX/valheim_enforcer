using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using ValheimEnforcer.common;
using ValheimEnforcer.modules.notifications;

namespace ValheimEnforcer.modules.worldintegrity {

    /// <summary>One thing a client did that a legitimate client cannot do.</summary>
    internal sealed class StructureOffence {
        internal ZDOID Id;
        internal int PrefabHash;
        internal string PrefabName;
        internal Vector3 Position;
        internal string Reason;
        internal float Health = float.NaN;
        internal long Creator;

        internal string Where() {
            return $"{Position.x:F0}, {Position.y:F0}, {Position.z:F0}";
        }

        public override string ToString() {
            return $"{PrefabName} at ({Where()}) - {Reason}";
        }
    }

    /// <summary>
    /// Server-side validation of the structures clients create.
    ///
    /// Valheim has no placement RPC. Player.PlacePiece is a local Object.Instantiate and the ZDO reaches the
    /// server through the generic ZDOData stream like any other, where ZDOMan validates nothing about it -
    /// not the prefab, not the position, not a single value. So the only place to catch a spawned dungeon
    /// wall is the moment its ZDO arrives, attributed to the peer whose packet carried it.
    ///
    /// Two things are checked, both chosen because a legitimate client cannot produce them:
    ///
    ///   1. A newly created ZDO for a prefab that has a Piece component and is in no piece table. Nothing a
    ///      player holds can place one, blueprint mods included, and clients never create world-generated
    ///      content themselves - ZoneSystem only spawns location and vegetation ZDOs in SpawnMode.Full, which
    ///      is server-only (ZoneSystem.cs:541).
    ///   2. Health above what the prefab was designed to hold. WearNTear has no indestructible sentinel; an
    ///      unbreakable piece is just an absurd float under the "health" key, and the genuine ceiling is what
    ///      RPC_Repair writes.
    ///
    /// Attribution is always the peer's socket host id, never a character name and never the ZDO's "creator"
    /// field - both are client-supplied, and the creator on a cheated piece is zero anyway.
    /// </summary>
    internal static class StructureValidator {

        /// <summary>Individual offences written to the log per packet before the rest are summarised.</summary>
        private const int LogDetailCap = 10;

        /// <summary>
        /// Minimum gap between Discord posts about one player. A cheat tool dropping a village places hundreds
        /// of pieces in a second; without this the webhook gets a post each and Discord rate limits the server
        /// out of its real notifications.
        /// </summary>
        private static readonly TimeSpan NotifyCooldown = TimeSpan.FromSeconds(60);
        private static readonly Dictionary<string, DateTime> lastNotified = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        // ---- Per-packet state -----------------------------------------------------------------------------

        // Set only while inside ZDOMan.RPC_ZDOData for a peer this detector is watching. Everything below
        // keys off it being non-null, which is what keeps world load, server-local writes and an exempt
        // admin's packets out of the inspection path entirely.
        private static ZNetPeer inboundPeer;
        private static ZDOID lastCreated = ZDOID.None;
        private static bool createdThisPacket;
        private static readonly List<StructureOffence> pending = new List<StructureOffence>();

        /// <summary>Opens the bracket around one client's ZDOData packet.</summary>
        internal static void BeginPacket(ZRpc rpc) {
            inboundPeer = null;
            createdThisPacket = false;
            lastCreated = ZDOID.None;
            if (pending.Count > 0) { pending.Clear(); }

            // Everything from here down runs inside the ZDO stream, which is the one thing on a server that
            // must not be allowed to throw - a broken packet loop desyncs or disconnects everybody. The
            // detector going quiet is always the better failure.
            try {
                if (!ValConfig.EnableStructureValidation.Value) { return; }
                if (ZNet.instance == null || !ZNet.instance.IsServer()) { return; }
                if (!ValConfig.DetectNonBuildableStructures.Value && !ValConfig.DetectExcessiveStructureHealth.Value) { return; }

                ZNetPeer peer = PeerFor(rpc);
                if (peer == null) { return; }
                if (IsExempt(peer)) { return; }
                inboundPeer = peer;
            } catch (Exception e) {
                inboundPeer = null;
                Logger.LogDebug($"Structure validation could not open a packet: {e.Message}");
            }
        }

        /// <summary>
        /// Closes the bracket and acts on anything found. Runs as a Harmony finalizer rather than a postfix so
        /// an exception out of vanilla cannot leave the watch flag set, which would then have us inspecting
        /// ZDOs the server wrote itself.
        /// </summary>
        internal static void EndPacket() {
            ZNetPeer peer = inboundPeer;
            inboundPeer = null;
            createdThisPacket = false;
            lastCreated = ZDOID.None;
            if (pending.Count == 0) { return; }

            List<StructureOffence> offences = new List<StructureOffence>(pending);
            pending.Clear();
            if (peer == null) { return; }

            try {
                Act(peer, offences, true);
            } catch (Exception e) {
                Logger.LogWarning($"Structure validation failed while acting on {offences.Count} detection(s): {e}");
            }
        }

        /// <summary>Records the id of a ZDO the peer's packet just created, for the Deserialize that follows it.</summary>
        internal static void NoteCreated(ZDOID uid) {
            if (inboundPeer == null) { return; }
            lastCreated = uid;
            createdThisPacket = true;
        }

        /// <summary>
        /// Reads the health a ZDO held before the client's write landed on it. Kept so an over-limit value
        /// that was already there is not blamed on whoever happens to own the ZDO now - ownership migrates to
        /// the nearest player every couple of seconds, so without this an innocent passer-by who hits a
        /// cheated structure once would be the one reported.
        /// </summary>
        internal static float CaptureHealth(ZDO zdo) {
            if (inboundPeer == null || zdo == null) { return float.NaN; }
            try {
                if (!ValConfig.DetectExcessiveStructureHealth.Value) { return float.NaN; }
                return zdo.GetFloat(ZDOVars.s_health, float.NaN);
            } catch (Exception e) {
                Logger.LogDebug($"Structure validation could not read a health value: {e.Message}");
                return float.NaN;
            }
        }

        /// <summary>Evaluates one fully-populated ZDO that arrived in the current packet.</summary>
        internal static void Inspect(ZDO zdo, float previousHealth) {
            if (inboundPeer == null || zdo == null) { return; }
            try {
                Evaluate(zdo, previousHealth);
            } catch (Exception e) {
                Logger.LogDebug($"Structure validation could not inspect an object: {e.Message}");
            }
        }

        private static void Evaluate(ZDO zdo, float previousHealth) {
            bool isNew = createdThisPacket && zdo.m_uid == lastCreated;
            createdThisPacket = false;

            int prefabHash = zdo.GetPrefab();
            if (prefabHash == 0) { return; }
            if (!StructureIndex.EnsureBuilt()) { return; }

            if (isNew && ValConfig.DetectNonBuildableStructures.Value && StructureIndex.IsNonBuildableStructure(prefabHash)) {
                Queue(zdo, prefabHash, "placed a structure no build tool can place", float.NaN);
                return;
            }

            if (!ValConfig.DetectExcessiveStructureHealth.Value) { return; }
            float authored;
            if (!StructureIndex.TryGetDesignedHealth(prefabHash, out authored) || authored <= 0f) { return; }

            float current = zdo.GetFloat(ZDOVars.s_health, float.NaN);
            if (float.IsNaN(current)) { return; }

            float limit = StructureIndex.HealthLimitFor(authored);
            if (!IsOverLimit(current, limit)) { return; }
            // Already over before this write, so the damage predates the packet. The sweep command is what
            // finds those; blaming the current owner would be wrong.
            if (IsOverLimit(previousHealth, limit)) { return; }

            Queue(zdo, prefabHash, $"set structure health to {Describe(current)}, above the {Describe(limit)} this prefab can hold", current);
        }

        /// <summary>
        /// Gate on ZNetScene.RPC_SpawnObject, the second way to get a structure into the world.
        ///
        /// ZNetScene.SpawnObject has no callers anywhere in the game assembly - it is a routed RPC that makes
        /// every receiver, the server included, Instantiate an arbitrary prefab by hash. Because the resulting
        /// ZDO is created by the server rather than sent by the client, the ZDOData path above never sees it,
        /// so it needs its own check. Returns false to skip the vanilla handler.
        /// </summary>
        internal static bool AllowSpawnObject(long spawner, Vector3 pos, int prefabHash) {
            try {
                if (!ValConfig.EnableStructureValidation.Value) { return true; }
                if (!ValConfig.DetectNonBuildableStructures.Value) { return true; }
                if (ZNet.instance == null || !ZNet.instance.IsServer()) { return true; }
                if (!StructureIndex.EnsureBuilt()) { return true; }
                if (!StructureIndex.IsNonBuildableStructure(prefabHash)) { return true; }
                if (StructureIndex.IsIgnored(prefabHash)) { return true; }

                ZNetPeer peer = ZNet.instance.GetPeer(spawner);
                if (peer != null && IsExempt(peer)) { return true; }

                StructureOffence offence = new StructureOffence {
                    Id = ZDOID.None,
                    PrefabHash = prefabHash,
                    PrefabName = StructureIndex.NameOf(prefabHash),
                    Position = pos,
                    Reason = "asked the server to spawn a structure no build tool can place (SpawnObject RPC)",
                };

                // No removal pass: nothing is instantiated, because this returns false.
                Act(peer, new List<StructureOffence> { offence }, false);
                return false;
            } catch (Exception e) {
                // Let it through rather than blocking on a bug of ours.
                Logger.LogWarning($"Structure validation failed while checking a SpawnObject request: {e}");
                return true;
            }
        }

        // ---- Rules ----------------------------------------------------------------------------------------

        private static bool IsOverLimit(float value, float limit) {
            if (float.IsNaN(value)) { return false; }
            if (float.IsInfinity(value)) { return true; }
            return value > limit;
        }

        private static void Queue(ZDO zdo, int prefabHash, string reason, float health) {
            pending.Add(new StructureOffence {
                Id = zdo.m_uid,
                PrefabHash = prefabHash,
                PrefabName = StructureIndex.NameOf(prefabHash),
                Position = zdo.GetPosition(),
                Reason = reason,
                Health = health,
                Creator = zdo.GetLong(ZDOVars.s_creator, 0L),
            });
        }

        /// <summary>
        /// The peer a connection belongs to. Vanilla's own ZNet.GetPeer(ZRpc) does exactly this but is private,
        /// and this runs on every ZDOData packet - not somewhere to lean on skipped access checks. GetPeers()
        /// hands back the live list rather than a copy, so the loop is the same work vanilla would have done.
        /// </summary>
        private static ZNetPeer PeerFor(ZRpc rpc) {
            if (rpc == null) { return null; }
            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            for (int i = 0; i < peers.Count; i++) {
                if (peers[i] != null && peers[i].m_rpc == rpc) { return peers[i]; }
            }
            return null;
        }

        /// <summary>
        /// Whether a peer is outside this check. Admins hold devcommands, and spawning a non-buildable prefab
        /// is a normal thing to do with it, so they are exempt by default - see StructureValidationExemptAdmins.
        /// </summary>
        private static bool IsExempt(ZNetPeer peer) {
            if (!ValConfig.StructureValidationExemptAdmins.Value) { return false; }
            string hostId = peer.m_socket != null ? peer.m_socket.GetHostName() : null;
            return !string.IsNullOrEmpty(hostId) && ZNet.instance.IsAdmin(hostId);
        }

        // ---- Reporting and enforcement --------------------------------------------------------------------

        /// <summary>
        /// Logs, optionally removes, notifies and enforces - once for the whole batch. A cheat tool dropping a
        /// village produces hundreds of offences in one packet, and kicking somebody once per piece or posting
        /// a webhook message per piece is not useful to anybody.
        /// </summary>
        private static void Act(ZNetPeer peer, List<StructureOffence> offences, bool canRemove) {
            List<StructureOffence> actionable = new List<StructureOffence>();
            foreach (StructureOffence offence in offences) {
                if (StructureIndex.IsIgnored(offence.PrefabHash)) { continue; }
                actionable.Add(offence);
            }
            if (actionable.Count == 0) { return; }

            string playerName = peer != null ? peer.m_playerName : "unknown";
            string hostId = peer != null && peer.m_socket != null ? peer.m_socket.GetHostName() : null;
            string who = string.IsNullOrEmpty(hostId) ? playerName : $"{playerName} ({hostId})";

            int shown = Math.Min(actionable.Count, LogDetailCap);
            for (int i = 0; i < shown; i++) {
                StructureOffence offence = actionable[i];
                string creator = offence.Creator == 0L ? "no creator" : $"creator {offence.Creator}";
                Logger.LogWarning($"Structure validation: {who} {offence.Reason} - {offence.PrefabName} at ({offence.Where()}), {creator}.");
            }
            if (actionable.Count > shown) {
                Logger.LogWarning($"Structure validation: and {actionable.Count - shown} more from {who} in the same batch.");
            }

            int removed = 0;
            if (canRemove && ValConfig.RemoveDetectedStructures.Value) {
                foreach (StructureOffence offence in actionable) {
                    if (Remove(offence.Id)) { removed++; }
                }
                Logger.LogWarning($"Structure validation: removed {removed} of {actionable.Count} flagged object(s) placed by {who}.");
            }

            Notify(playerName, hostId, actionable, removed, canRemove);
            Enforce(hostId, playerName, actionable);
        }

        /// <summary>
        /// Takes ownership and destroys the ZDO. ZDOMan.DestroyZDO refuses a ZDO this session does not own, and
        /// the destroy it broadcasts also lands the id in m_deadZDOs, which is what stops the client simply
        /// re-sending the structure on the next packet (ZDOMan.RPC_ZDOData rejects a dead id).
        /// </summary>
        internal static bool Remove(ZDOID id) {
            if (ZDOMan.instance == null || id == ZDOID.None) { return false; }
            ZDO zdo = ZDOMan.instance.GetZDO(id);
            if (zdo == null) { return false; }
            zdo.SetOwner(ZDOMan.GetSessionID());
            ZDOMan.instance.DestroyZDO(zdo);
            return true;
        }

        private static void Notify(string playerName, string hostId, List<StructureOffence> offences, int removed, bool canRemove) {
            if (!ValConfig.DiscordNotifyStructureFlagged.Value) { return; }

            string key = string.IsNullOrEmpty(hostId) ? playerName ?? "" : hostId;
            DateTime last;
            if (lastNotified.TryGetValue(key, out last) && DateTime.UtcNow - last < NotifyCooldown) { return; }
            lastNotified[key] = DateTime.UtcNow;

            StructureOffence first = offences[0];
            string action = ValConfig.StructureValidationAction.Value ?? "Log";
            if (canRemove && ValConfig.RemoveDetectedStructures.Value) {
                action = removed > 0 ? $"{action}, removed {removed}" : $"{action}, removal failed";
            }

            DiscordNotifier.Notify(NotificationEvent.StructureFlagged, new Dictionary<string, string> {
                { "player", playerName ?? "unknown" },
                { "playerId", hostId ?? "unknown" },
                { "prefab", first.PrefabName },
                { "position", first.Where() },
                { "reason", first.Reason },
                { "creator", first.Creator == 0L ? "none" : first.Creator.ToString() },
                { "health", Describe(first.Health) },
                { "count", offences.Count.ToString() },
                { "action", action },
            });
        }

        private static void Enforce(string hostId, string playerName, List<StructureOffence> offences) {
            if (string.IsNullOrEmpty(hostId)) { return; }
            string action = ValConfig.StructureValidationAction.Value ?? "Log";
            string reason = $"Structure validation: {offences[0].PrefabName} - {offences[0].Reason}" +
                            (offences.Count > 1 ? $" (and {offences.Count - 1} more)" : "");

            switch (action) {
                case "Kick":
                    Logger.LogWarning($"Kicking {playerName} for placing invalid structures.");
                    ZNet.instance.Kick(hostId);
                    break;
                case "Ban":
                    Logger.LogWarning($"Banning {playerName} for placing invalid structures.");
                    ValConfig.BanHost(hostId, reason);
                    break;
                case "Log":
                default:
                    break;
            }
        }

        /// <summary>Renders a health value for a human. G6 keeps 1E+30 readable instead of thirty digits.</summary>
        internal static string Describe(float value) {
            if (float.IsNaN(value)) { return "unset"; }
            if (float.IsInfinity(value)) { return "infinite"; }
            return value.ToString("G6", CultureInfo.InvariantCulture);
        }

        /// <summary>Drops per-world state. Called from the ZNet.Shutdown teardown.</summary>
        internal static void Reset() {
            inboundPeer = null;
            createdThisPacket = false;
            lastCreated = ZDOID.None;
            pending.Clear();
            lastNotified.Clear();
        }
    }
}
