using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using ValheimEnforcer.common;
using ValheimEnforcer.modules.character;
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
    ///
    /// There are two inspection points, and which one a check uses is a cost decision:
    ///
    ///   - End of packet (InspectCreated), for anything that only concerns objects the packet CREATED: the
    ///     non-buildable check above, and the tombstone that death observation watches for. RPC_ZDOData
    ///     creates a ZDO only for an id the server has never seen, so this runs over a handful of objects
    ///     rather than every object replicated to the server.
    ///   - Inside ZDO.Deserialize (CaptureHealth / Inspect), for the health check alone, because it needs the
    ///     value the ZDO held BEFORE the client's write and that is gone by the time the packet ends. That
    ///     hook is only installed when the health check is actually configured on - see the patch class.
    ///
    /// The split is what lets a server run death observation, which is gated on ServerSideJoinEnforcement
    /// rather than on this feature, without instrumenting every deserialized ZDO.
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

        /// <summary>
        /// The ZDOs this packet created, inspected once the packet is done rather than as each one is
        /// deserialized. RPC_ZDOData creates a ZDO only for an id the server has never seen, so this is the
        /// small minority of what a packet carries - the rest are updates to objects that already exist.
        ///
        /// A set rather than the single "last created" slot this used to keep, because the inspection no
        /// longer happens in the Deserialize that immediately follows CreateNewZDO.
        /// </summary>
        private static readonly HashSet<ZDOID> createdZdos = new HashSet<ZDOID>();
        private static readonly List<StructureOffence> pending = new List<StructureOffence>();

        // Which of the jobs this packet is being watched for, decided once per packet instead of re-read per
        // ZDO. Structure detection respects the admin exemption and the master switch; death observation
        // applies to everyone whenever server-side join enforcement is on, because a death is a fact to
        // record, not a detection to act on.
        private static bool deathWatch;
        private static bool nonBuildableWatch;
        private static bool healthWatch;

        /// <summary>Whether created ids are worth collecting at all this packet - see <see cref="NoteCreated"/>.</summary>
        private static bool collectCreated;

        /// <summary>
        /// Whether the ZDO.Deserialize hook is present in this process, decided once at patch time by
        /// <see cref="WantsHealthHook"/>. Only the excessive-health check needs it, and it is the one check
        /// that cannot be answered after the packet: it compares the value against what the ZDO held BEFORE
        /// the client's write, which no longer exists once Deserialize has returned.
        /// </summary>
        internal static bool HealthHookInstalled { get; private set; }

        private static bool warnedHealthHookMissing;

        private static readonly int TombstoneHash = "Player_tombstone".GetStableHashCode();

        /// <summary>
        /// Whether to install the ZDO.Deserialize hook, called from that patch's Harmony Prepare. Reads config
        /// bound in the plugin's Awake, which runs before PatchAll.
        ///
        /// The point of asking at all: ZDO.Deserialize is the hottest method a server runs, called for every
        /// replicated object of every packet, and a Harmony wrapper on it is paid whether or not the body does
        /// anything. Death observation and the non-buildable check both work from the packet's created ZDOs
        /// and want nothing from this hook, so a server with structure validation off - the default - should
        /// not carry it. Changing either setting takes effect on the next restart; BeginPacket says so once if
        /// somebody turns the health check on in a running server.
        /// </summary>
        internal static bool WantsHealthHook() {
            try {
                HealthHookInstalled = ValConfig.EnableStructureValidation != null && ValConfig.EnableStructureValidation.Value
                    && ValConfig.DetectExcessiveStructureHealth != null && ValConfig.DetectExcessiveStructureHealth.Value;
            } catch (Exception e) {
                // Patch time. Losing the health check is survivable; failing to patch is not.
                HealthHookInstalled = false;
                Logger.LogWarning($"Could not read the structure validation settings while patching; the excessive-health check is off for this session: {e.Message}");
            }
            return HealthHookInstalled;
        }

        /// <summary>Opens the bracket around one client's ZDOData packet.</summary>
        internal static void BeginPacket(ZRpc rpc) {
            ClearPacketState();

            // Everything from here down runs inside the ZDO stream, which is the one thing on a server that
            // must not be allowed to throw - a broken packet loop desyncs or disconnects everybody. The
            // detector going quiet is always the better failure.
            try {
                bool nonBuildableOn = ValConfig.DetectNonBuildableStructures.Value;
                bool healthOn = ValConfig.DetectExcessiveStructureHealth.Value;
                bool structureOn = ValConfig.EnableStructureValidation.Value && (nonBuildableOn || healthOn);
                bool deathOn = ValConfig.ServerSideJoinEnforcement != null && ValConfig.ServerSideJoinEnforcement.Value;
                if (!structureOn && !deathOn) { return; }
                if (ZNet.instance == null || !ZNet.instance.IsServer()) { return; }

                ZNetPeer peer = PeerFor(rpc);
                if (peer == null) { return; }

                // Structure detection skips exempt admins; death observation does not - an admin dying still
                // creates a grave, and recording it is not a detection to be exempt from.
                bool structureWatch = structureOn && !IsExempt(peer);

                bool wantHealth = structureWatch && healthOn;
                if (wantHealth && !HealthHookInstalled) {
                    WarnHealthHookMissing();
                    wantHealth = false;
                }

                bool wantNonBuildable = structureWatch && nonBuildableOn;
                if (!deathOn && !wantNonBuildable && !wantHealth) { return; }

                inboundPeer = peer;
                deathWatch = deathOn;
                nonBuildableWatch = wantNonBuildable;
                healthWatch = wantHealth;
                collectCreated = deathOn || wantNonBuildable;
            } catch (Exception e) {
                ClearPacketState();
                Logger.LogDebug($"Structure validation could not open a packet: {e.Message}");
            }
        }

        /// <summary>
        /// Closes the bracket, inspects what the packet created and acts on anything found. Runs as a Harmony
        /// finalizer rather than a postfix so an exception out of vanilla cannot leave the watch flags set,
        /// which would then have us inspecting ZDOs the server wrote itself.
        /// </summary>
        internal static void EndPacket() {
            ZNetPeer peer = inboundPeer;
            bool death = deathWatch;
            bool nonBuildable = nonBuildableWatch;

            // Cleared before the scan below, not after. Nothing from here on is part of the client's packet,
            // so nothing it touches may be attributed to the peer - and with collectCreated already false, a
            // ZDO created as a side effect of acting on a detection cannot land in the set the scan is
            // enumerating.
            inboundPeer = null;
            deathWatch = false;
            nonBuildableWatch = false;
            healthWatch = false;
            collectCreated = false;

            try {
                if (peer != null && (death || nonBuildable) && createdZdos.Count > 0) {
                    InspectCreated(peer, death, nonBuildable);
                }
            } catch (Exception e) {
                Logger.LogDebug($"Structure validation could not inspect the objects a packet created: {e.Message}");
            }
            createdZdos.Clear();

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

        /// <summary>
        /// Inspects the ZDOs the packet created, once, after it has finished.
        ///
        /// This is where the two checks that only ever concerned NEW objects live - a death (the grave did not
        /// exist a moment ago) and a structure no build tool can place. Doing it here rather than inside
        /// ZDO.Deserialize is what lets a server run death observation without a hook on every deserialized
        /// object: RPC_ZDOData creates a ZDO only for an id the server has never seen, so this loop runs over
        /// a handful of objects where the old path ran over every object the packet carried.
        ///
        /// The prefab hash is read once per created ZDO and everything branches off it, so a packet that
        /// created nothing interesting costs one dictionary lookup and one comparison each.
        /// </summary>
        private static void InspectCreated(ZNetPeer peer, bool death, bool nonBuildable) {
            ZDOMan man = ZDOMan.instance;
            if (man == null) { return; }

            bool indexReady = false;
            bool indexChecked = false;
            bool deathRecorded = false;

            foreach (ZDOID id in createdZdos) {
                // Normally still here: ZDOMan.DestroyZDO only queues the id, and the removal itself happens
                // in a later frame, so even the ZDO vanilla kills on the spot for being a resurrected dead id
                // is still readable now. The check is for anything else that removed it inside the packet.
                ZDO zdo = man.GetZDO(id);
                if (zdo == null) { continue; }

                int prefabHash = zdo.GetPrefab();
                if (prefabHash == 0) { continue; }

                // Death observation: a Player_tombstone this peer's packet created means this peer just died.
                // Record it server-side so the grave cannot be duplicated by a client that skips its own death
                // handling (ClearTrackedItemsOnDeath). A raw hash compare, so no prefab index is needed and a
                // server running only this check never builds one.
                // Once per packet: a player dies once, and a packet carrying several graves - a client
                // re-sending a whole sector, say - must not repeat the store write behind this.
                if (death && !deathRecorded && prefabHash == TombstoneHash) {
                    deathRecorded = true;
                    ObserveDeath(peer);
                }

                if (!nonBuildable) { continue; }
                if (!indexChecked) {
                    indexReady = StructureIndex.EnsureBuilt();
                    indexChecked = true;
                }
                if (!indexReady) { continue; }
                if (!StructureIndex.IsNonBuildableStructure(prefabHash)) { continue; }

                // One report per piece: a non-buildable structure is the louder finding, so it supersedes any
                // excessive-health offence the deserialize path already queued for the same object.
                DropPending(id);
                Queue(zdo, prefabHash, "placed a structure no build tool can place", float.NaN);
            }
        }

        /// <summary>Removes an already-queued offence for one object, so it is not reported twice.</summary>
        private static void DropPending(ZDOID id) {
            for (int i = pending.Count - 1; i >= 0; i--) {
                if (pending[i].Id == id) { pending.RemoveAt(i); }
            }
        }

        /// <summary>
        /// Records the id of a ZDO the peer's packet just created, for <see cref="InspectCreated"/> to look at
        /// once the packet is done. The prefab is not readable yet - RPC_ZDOData creates the ZDO with a hash of
        /// zero and fills it in from the Deserialize that follows - so nothing can be decided here.
        /// </summary>
        internal static void NoteCreated(ZDOID uid) {
            if (!collectCreated) { return; }
            createdZdos.Add(uid);
        }

        /// <summary>
        /// Reads the health a ZDO held before the client's write landed on it. Kept so an over-limit value
        /// that was already there is not blamed on whoever happens to own the ZDO now - ownership migrates to
        /// the nearest player every couple of seconds, so without this an innocent passer-by who hits a
        /// cheated structure once would be the one reported.
        ///
        /// The reason the ZDO.Deserialize hook exists at all: this value is gone by the time the hook returns.
        /// </summary>
        internal static float CaptureHealth(ZDO zdo) {
            // healthWatch implies a watched peer and the health check being on, both settled in BeginPacket.
            if (!healthWatch || zdo == null) { return float.NaN; }
            try {
                return zdo.GetFloat(ZDOVars.s_health, float.NaN);
            } catch (Exception e) {
                Logger.LogDebug($"Structure validation could not read a health value: {e.Message}");
                return float.NaN;
            }
        }

        /// <summary>Evaluates the health one fully-populated ZDO arrived carrying.</summary>
        internal static void Inspect(ZDO zdo, float previousHealth) {
            if (!healthWatch || zdo == null) { return; }
            try {
                EvaluateHealth(zdo, previousHealth);
            } catch (Exception e) {
                Logger.LogDebug($"Structure validation could not inspect an object: {e.Message}");
            }
        }

        private static void EvaluateHealth(ZDO zdo, float previousHealth) {
            int prefabHash = zdo.GetPrefab();
            if (prefabHash == 0) { return; }
            if (!StructureIndex.EnsureBuilt()) { return; }

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
        /// Says once that the health check is configured on but its hook was not installed, because the
        /// setting was off when the process started. Once per session: this is inside the packet loop.
        /// </summary>
        private static void WarnHealthHookMissing() {
            if (warnedHealthHookMissing) { return; }
            warnedHealthHookMissing = true;
            Logger.LogWarning("DetectExcessiveStructureHealth is on but its hook was not installed, because EnableStructureValidation "
                              + "or DetectExcessiveStructureHealth was off when the server started. Restart the server to enable it. "
                              + "The non-buildable structure check and death observation are unaffected.");
        }

        /// <summary>Drops everything scoped to one packet.</summary>
        private static void ClearPacketState() {
            inboundPeer = null;
            deathWatch = false;
            nonBuildableWatch = false;
            healthWatch = false;
            collectCreated = false;
            if (createdZdos.Count > 0) { createdZdos.Clear(); }
            if (pending.Count > 0) { pending.Clear(); }
        }

        /// <summary>
        /// Records that the peer whose packet is being processed just died: clears the item list on their
        /// stored character and marks it a dirty disconnect, so the pre-death inventory can no longer be
        /// replayed onto the server as authoritative. This is the server-side twin of the client's
        /// CharacterManager.ClearTrackedItemsForDeath - a modified client that skips its copy can otherwise
        /// keep the pre-death items live on the server and dupe the grave on the next join.
        ///
        /// Attribution is always the packet's own peer, so a forged tombstone can only ever clear the SENDER's
        /// own character - never another player's. Runs on the main thread (at the end of the packet), so it
        /// does its own load/write rather than using the async store; deaths are rare, so the synchronous I/O
        /// is fine.
        /// </summary>
        private static void ObserveDeath(ZNetPeer peer) {
            if (peer == null || peer.m_socket == null) { return; }
            string endpoint = peer.m_socket.GetEndPointString();
            string name = peer.m_playerName;
            if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(name)) { return; }

            try {
                // Resolve the spelling the save is actually filed under (an account reaches us under more than
                // one id spelling; see PlatformIds), so the clear lands on the right file.
                if (!CharacterSaves.TryResolveSave(endpoint, name, out string saveId, out string saveName, out bool lookupFailed)) {
                    // No stored save (or the store could not be read) - nothing to clear, nothing to dupe.
                    if (lookupFailed) { Logger.LogDebug($"Death observed for {name} but the character store could not be read; leaving it alone."); }
                    return;
                }

                if (ValConfig.InternalStorageMode.Value) {
                    // Internal storage is main-thread only and has no async worker to race, so clear it inline.
                    DataObjects.Character stored = ValConfig.LoadCharacterFromSave(saveId, saveName);
                    if (stored == null) { return; }
                    bool alreadyCleared = (stored.PlayerItems == null || stored.PlayerItems.Count == 0)
                                          && stored.LastDisconnect == DataObjects.DisconnectionState.DirtyDisconnect;
                    if (alreadyCleared) { return; }
                    Logger.LogInfo($"Death observed for {saveName} ({saveId}); clearing the stored item list so the grave cannot be duplicated on rejoin.");
                    if (stored.PlayerItems == null) { stored.PlayerItems = new List<DataObjects.PackedItem>(); } else { stored.PlayerItems.Clear(); }
                    stored.ActiveCharacterEffects?.Clear();
                    stored.LastDisconnect = DataObjects.DisconnectionState.DirtyDisconnect;
                    ValConfig.WritePlayerCharacterToSave(saveId, stored);
                    return;
                }

                // Disk mode: route through the async store so the clear is ordered FIFO behind any full save
                // already queued for this character. Clearing on disk directly here would race a pending
                // pre-death full save in the worker queue, which would then be written after the clear and
                // restore the very inventory we are trying to drop.
                CharacterStore.SubmitDeath(saveId, saveName);
            } catch (Exception e) {
                Logger.LogDebug($"Death observation failed for {name}: {e.Message}");
            }
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
                if (ZNet.instance == null || !ZNet.instance.IsServer()) { return true; }

                // ZNetScene.SpawnObject has no caller anywhere in the game assembly - it is a routed RPC that
                // makes every receiver, the server included, Instantiate an arbitrary prefab by hash. So no
                // client-originated SpawnObject is legitimate, whatever the prefab: a structure, a creature, a
                // boss, an item. `spawner` is trustworthy here because RoutedRpcGuard corrects the routed sender
                // before this runs.
                ZNetPeer peer = ZNet.instance.GetPeer(spawner);
                if (peer != null && IsExempt(peer)) { return true; } // admins keep their devcommands-style freedom

                bool indexReady = StructureIndex.EnsureBuilt();
                if (indexReady && StructureIndex.IsIgnored(prefabHash)) { return true; } // allowlisted prefab

                bool isStructure = indexReady
                    && ValConfig.DetectNonBuildableStructures.Value
                    && StructureIndex.IsNonBuildableStructure(prefabHash);

                // A non-structure SpawnObject only blocks when BlockSpawnObjectRPC is on. A structure one is
                // covered by the structure check regardless, so it goes through even when the broad block is off.
                if (!isStructure && !ValConfig.BlockSpawnObjectRPC.Value) { return true; }

                string reason = isStructure
                    ? "asked the server to spawn a structure no build tool can place (SpawnObject RPC)"
                    : $"asked the server to spawn '{(indexReady ? StructureIndex.NameOf(prefabHash) : prefabHash.ToString())}' via the SpawnObject RPC, which nothing in the game legitimately sends";

                StructureOffence offence = new StructureOffence {
                    Id = ZDOID.None,
                    PrefabHash = prefabHash,
                    PrefabName = indexReady ? StructureIndex.NameOf(prefabHash) : prefabHash.ToString(),
                    Position = pos,
                    Reason = reason,
                };

                // No removal pass: nothing is instantiated, because this returns false. Act logs, posts the
                // structureFlagged notification to the moderation channel (Discord.NotifyStructureFlagged, on
                // by default) and applies StructureValidationAction - which defaults to Log, so the default
                // outcome is "block it and tell the mods" without a kick or ban.
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
        /// The peer a connection belongs to. This used to be a private copy of the peer-list walk; it now
        /// defers to PeerIdentity, which keeps a connection-to-peer map and answers in one hash lookup. This
        /// runs on every ZDOData packet, so the walk cost packets x connected players on a busy server - and
        /// having two definitions of "the peer behind this socket" was never worth the duplication either.
        /// </summary>
        private static ZNetPeer PeerFor(ZRpc rpc) {
            return modules.character.PeerIdentity.PeerFor(rpc);
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
            // Same signal as an RPC guard refusal: something this client did that its declared mod set does
            // not account for. PeerTrust correlates the two halves; see that class for what it does and does
            // not claim.
            network.PeerTrust.NoteGuardTrip(peer, "structure", actionable[0].Reason);
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
            ClearPacketState();
            lastNotified.Clear();
        }
    }
}
