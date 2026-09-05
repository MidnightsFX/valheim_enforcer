using System;
using UnityEngine;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.network {

    /// <summary>What the filter decided about one routed packet.</summary>
    internal enum RpcVerdict {
        /// <summary>Nothing to do. The packet has not been touched and must be forwarded byte for byte.</summary>
        Pass,
        /// <summary>Refuse it. The server neither handles nor relays it, so no client ever sees it.</summary>
        Drop,
        /// <summary>The payload was corrected in place; the caller must re-serialize before continuing.</summary>
        Rewritten
    }

    /// <summary>
    /// Inspects the routed RPCs that pass through the server on their way to another client.
    ///
    /// Three of the vanilla routed messages are dangerous for the same reason: the server relays them without
    /// looking at them, and the receiving client acts on them without being able to tell where they really
    /// came from. Sitting in the relay is the only place any of it can be checked, because for two of the
    /// three the server is not the destination and never runs the handler at all.
    ///
    ///   ChatMessage        - carries the sender's display NAME inside the payload. Rewritten, not dropped.
    ///   RPC_TeleportPlayer - teleports whoever receives it. Dropped for non-admins.
    ///   RPC_Damage         - carries a HitData the attacker's client wrote. Dropped when the numbers are
    ///                        impossible.
    ///
    /// Called from <see cref="RoutedRpcGuard"/>, which already owns the prefix on ZRoutedRpc.RPC_RoutedRPC and
    /// has resolved the real peer behind the connection. Keeping one prefix on that method matters: two
    /// patches both parsing and re-serializing the same package would double the cost on the hottest path in
    /// the game and make the order they run in significant.
    /// </summary>
    internal static class RoutedRpcFilter {

        // Resolved once. GetStableHashCode is deterministic, but it is a per-character loop over the name and
        // this is consulted for every routed packet the server handles.
        private static readonly int ChatMessageHash = "ChatMessage".GetStableHashCode();
        private static readonly int TeleportPlayerHash = "RPC_TeleportPlayer".GetStableHashCode();
        private static readonly int DamageHash = "RPC_Damage".GetStableHashCode();

        /// <summary>
        /// True when at least one of the relay filters is live. Checked before the header of a packet is even
        /// read, so a server with all of them off pays nothing for this file existing.
        ///
        /// Note that a default server is not one of those: the audit is on by default and reads the damage
        /// payload, so the damage branch below is live out of the box even with every RPC guard off. That is
        /// the intended trade - it is what makes enforcer-audit-damage have anything to report - but it is
        /// worth knowing that turning EnableAuditLog off is what actually silences this file.
        /// </summary>
        internal static bool AnyEnabled() {
            // Checked BEFORE the guard master switch, not after. The audit reads the damage payload without
            // being an RPC guard and must not depend on EnableRpcGuards being on - putting this below the
            // early return would make switching the audit on do nothing at all on most servers.
            if (modules.audit.AuditPolicy.Active()) { return true; }
            if (!RpcGuardPolicy.Active()) { return false; }
            return ValConfig.GuardChatSenderName.Value
                || ValConfig.GuardPlayerTeleportRpc.Value
                || ValConfig.GuardDamageRpc.Value;
        }

        /// <summary>
        /// Whether this method is one we inspect. An int comparison against three constants, run on every
        /// routed packet; everything expensive happens only after this says yes.
        ///
        /// Each guard branch tests RpcGuardPolicy.Active() itself rather than leaning on AnyEnabled() having
        /// established it. That used to be safe because AnyEnabled() could only be true when the guards were
        /// on; now that the audit can make it true on its own, an inherited assumption here would quietly
        /// switch the chat-name and teleport guards back on for a server that has EnableRpcGuards off.
        /// </summary>
        internal static bool IsWatched(int methodHash) {
            bool guards = RpcGuardPolicy.Active();
            if (methodHash == ChatMessageHash) { return guards && ValConfig.GuardChatSenderName.Value; }
            if (methodHash == TeleportPlayerHash) { return guards && ValConfig.GuardPlayerTeleportRpc.Value; }
            if (methodHash == DamageHash) {
                return (guards && ValConfig.GuardDamageRpc.Value) || modules.audit.AuditPolicy.Active();
            }
            return false;
        }

        /// <summary>
        /// Whether judging this method can end in <see cref="RpcVerdict.Rewritten"/>, and therefore needs the
        /// packet materialised as a RoutedRPCData that can be re-serialized.
        ///
        /// Only the chat rebind edits anything. The other two read and then either pass or drop, and for those
        /// <see cref="InspectInPlace"/> answers off the caller's own read cursor - which matters because
        /// vanilla's ZRoutedRpc.RPC_RoutedRPC parses this package into a RoutedRPCData of its own straight
        /// afterwards, so a parse here to reach a payload we never change is the same work done twice and
        /// thrown away. On a busy server RPC_Damage is the highest-rate routed message there is.
        /// </summary>
        internal static bool MayRewrite(int methodHash) {
            return methodHash == ChatMessageHash;
        }

        // ---- Deferred observation -------------------------------------------------------------------------

        /// <summary>
        /// Whether the only thing left to do with this packet is write down that it happened.
        ///
        /// True for a hit on a server that is auditing but would never refuse this peer: the audit switched on
        /// with the RPC guards off, which is the shape a server turning the audit on actually runs, or the
        /// guards on with this peer exempt. When it is true the payload does not have to be read before the
        /// packet is forwarded, because there is no verdict for the relay to wait on.
        ///
        /// It has to be false whenever the guard could refuse the hit. ZRoutedRpc.RouteRPC hands the bytes to
        /// the socket inside the original method - ZSteamSocket.Send pushes them to Steam then and there
        /// rather than queueing them for the next frame - so a packet that has been relayed cannot be
        /// unrelayed, and a decision to drop has to be made in front of it.
        /// </summary>
        internal static bool RecordsOnly(ZNetPeer peer, int methodHash) {
            if (methodHash != DamageHash) { return false; }
            if (!modules.audit.AuditPolicy.Active()) { return false; }
            if (!RpcGuardPolicy.Active() || !ValConfig.GuardDamageRpc.Value) { return true; }
            return RpcGuardPolicy.IsExempt(peer);
        }

        // What the guard's prefix armed for its finalizer to read once the packet has gone. One slot is
        // enough: RPC_RoutedRPC is driven from ZRpc's socket receive loop and nothing it calls re-enters it,
        // so a second packet cannot start while one is armed. The finalizer clears it unconditionally,
        // including when vanilla threw - an armed slot surviving into the next packet would attribute a hit
        // to whoever sent the one after it.
        private static ZNetPeer deferredPeer;
        private static ZDOID deferredVictim;
        private static ZPackage deferredSource;
        private static int deferredOffset;

        /// <summary>
        /// Remembers where to find a hit that is being relayed now and read afterwards.
        /// <paramref name="offset"/> is the position of the parameters package, which is where the caller's
        /// cursor already sits once it has read the header.
        /// </summary>
        internal static void ArmDeferredRead(ZNetPeer peer, ZDOID victim, ZPackage pkg, int offset) {
            deferredPeer = peer;
            deferredVictim = victim;
            deferredSource = pkg;
            deferredOffset = offset;
        }

        /// <summary>
        /// Reads and records the armed hit, now that the packet has been forwarded. Costs nothing at all when
        /// nothing is armed, which is every routed packet that is not a hit on an auditing server.
        ///
        /// Reads the package the prefix was handed rather than whatever the original method finished with: the
        /// question an audit answers is what this client sent, and the cursor is put back afterwards so this
        /// stays invisible to anything else looking at the same packet.
        /// </summary>
        internal static void RunDeferredRead() {
            ZNetPeer peer = deferredPeer;
            ZPackage pkg = deferredSource;
            ZDOID victim = deferredVictim;
            deferredPeer = null;
            deferredSource = null;
            if (peer == null || pkg == null) { return; }

            int restore = pkg.GetPos();
            try {
                pkg.SetPos(deferredOffset);
                pkg.ReadInt(); // the parameters package's length prefix; the hit is the bytes after it
                HitData hit = ScratchHit;
                hit.Deserialize(ref pkg);
                modules.audit.DamageAudit.Observe(peer, hit, victim);
            } catch (Exception e) {
                // Past the relay, so there is nothing left to protect but the rest of the packet loop.
                Logger.LogDebug($"RoutedRpcFilter could not record a relayed hit: {e.Message}");
            }
            try { pkg.SetPos(restore); } catch (Exception) { /* the package is finished with either way */ }
        }

        /// <summary>
        /// Judges a packet the caller has read the header of, without materialising a RoutedRPCData.
        ///
        /// <paramref name="pkg"/>'s cursor must sit immediately after the method hash, which is where the
        /// parameters package begins (RoutedRPCData.Serialize writes it last, length-prefixed). Only ever
        /// called for methods <see cref="MayRewrite"/> says nothing rewrites, so the cursor is left wherever
        /// this stopped and the caller resets it.
        ///
        /// Never throws, for the same reason <see cref="Inspect"/> does not: a filter that cannot make sense
        /// of a payload passes it, and a truncated package here is one vanilla's own parse will reject.
        /// </summary>
        internal static RpcVerdict InspectInPlace(ZNetPeer peer, int methodHash, ZDOID targetZdo, ZPackage pkg) {
            try {
                if (methodHash == TeleportPlayerHash) { return JudgeTeleport(peer); }
                if (methodHash == DamageHash) {
                    bool auditing = modules.audit.AuditPolicy.Active();
                    // Nobody wants this payload: the audit is off and the guard would exempt this peer anyway.
                    // Checked before the read so an exempt admin in a long fight costs exactly what they did
                    // before the audit existed - which is nothing.
                    if (!auditing && RpcGuardPolicy.IsExempt(peer)) { return RpcVerdict.Pass; }
                    pkg.ReadInt(); // the parameters package's length prefix; the hit is the bytes after it
                    HitData hit = ScratchHit;
                    hit.Deserialize(ref pkg);
                    return JudgeParsedDamage(peer, hit, targetZdo, auditing);
                }
            } catch (Exception e) {
                Logger.LogDebug($"RoutedRpcFilter could not inspect method {methodHash}: {e.Message}");
            }
            return RpcVerdict.Pass;
        }

        /// <summary>
        /// Judges one fully-parsed packet. Never throws: a filter that cannot make sense of a payload passes
        /// it, because breaking an unrecognised message is worse than missing one.
        /// </summary>
        internal static RpcVerdict Inspect(ZNetPeer peer, ZRoutedRpc.RoutedRPCData data) {
            try {
                if (data.m_methodHash == ChatMessageHash) { return BindChatName(peer, data); }
                if (data.m_methodHash == TeleportPlayerHash) { return JudgeTeleport(peer); }
                if (data.m_methodHash == DamageHash) { return JudgeDamage(peer, data); }
            } catch (Exception e) {
                Logger.LogDebug($"RoutedRpcFilter could not inspect method {data.m_methodHash}: {e.Message}");
            }
            return RpcVerdict.Pass;
        }

        // ---- ChatMessage ---------------------------------------------------------------------------------

        /// <summary>
        /// Replaces the name in a chat payload with the one the server holds for this connection.
        ///
        /// Chat.RPC_ChatMessage takes a UserInfo the sending client filled in, and every receiver renders the
        /// name out of it. RoutedRpcGuard has already made the sender ID honest, but the name lives in the
        /// payload rather than the header, so it is untouched by that - which is exactly how the cheat tools
        /// in circulation talk as somebody else.
        ///
        /// This is the one filter that ignores the admin exemption. An admin has no legitimate reason to speak
        /// under another player's name, and an admin account is the most valuable one to impersonate.
        ///
        /// Only the name is rebound. The platform ID travelling beside it is left alone: it feeds Valheim's
        /// UGC name filtering rather than anything a player reads, and deriving a well-formed one from the
        /// socket host id would be guesswork for no gain.
        ///
        /// Parameter layout, from ZRpc.Serialize over Chat's Register&lt;Vector3, int, UserInfo, string&gt;:
        /// three floats, an int, then the UserInfo's two strings, then the message text.
        /// </summary>
        private static RpcVerdict BindChatName(ZNetPeer peer, ZRoutedRpc.RoutedRPCData data) {
            string authoritative = peer?.m_playerName;
            // Nothing to bind to. A peer with no name recorded has not finished its handshake, and inventing
            // one here would be worse than leaving the payload alone.
            if (string.IsNullOrEmpty(authoritative)) { return RpcVerdict.Pass; }

            ZPackage pkg = data.m_parameters;
            pkg.SetPos(0);

            float x = pkg.ReadSingle();
            float y = pkg.ReadSingle();
            float z = pkg.ReadSingle();
            int type = pkg.ReadInt();
            string claimedName = pkg.ReadString();
            string userId = pkg.ReadString();
            string text = pkg.ReadString();

            // Anything left over means this is not the shape we know - another mod has re-registered
            // ChatMessage with extra parameters. Rebuilding would truncate them, so leave it alone.
            if (pkg.GetPos() != pkg.Size()) {
                pkg.SetPos(0);
                return RpcVerdict.Pass;
            }

            if (string.Equals(claimedName, authoritative, StringComparison.Ordinal)) {
                pkg.SetPos(0);
                return RpcVerdict.Pass; // honest, and the overwhelmingly common case
            }

            ZPackage rebuilt = new ZPackage();
            rebuilt.Write(x);
            rebuilt.Write(y);
            rebuilt.Write(z);
            rebuilt.Write(type);
            rebuilt.Write(authoritative);
            rebuilt.Write(userId);
            rebuilt.Write(text);
            rebuilt.SetPos(0);
            data.m_parameters = rebuilt;

            // Logged, because a moderator wants to know somebody tried - but never enforced: the rebind is
            // the whole remedy, and a chat mod that decorates names looks identical to this on the wire. The
            // message text is deliberately left out; the server log is not a chat transcript.
            RpcGuardPolicy.Report(peer, "chat-name",
                $"chat payload claimed the name '{RpcGuardPolicy.Trim(claimedName)}'; rebound to '{authoritative}'");
            return RpcVerdict.Rewritten;
        }

        // ---- RPC_TeleportPlayer --------------------------------------------------------------------------

        /// <summary>
        /// Refuses RPC_TeleportPlayer from anyone who is not an admin.
        ///
        /// The receiving client teleports itself wherever the payload says, and a sender may address the
        /// message to everybody at once. Vanilla sends it from exactly one place - the admin-only 'recall'
        /// console command (Terminal.cs) - so the admin exemption is not a concession here, it is the whole
        /// legitimate population of this RPC.
        /// </summary>
        private static RpcVerdict JudgeTeleport(ZNetPeer peer) {
            if (RpcGuardPolicy.IsExempt(peer)) { return RpcVerdict.Pass; }

            RpcGuardPolicy.Refuse(peer, "teleport-player",
                "sent RPC_TeleportPlayer, which only the admin-only 'recall' command legitimately sends");
            return RpcVerdict.Drop;
        }

        // ---- RPC_Damage ----------------------------------------------------------------------------------

        /// <summary>
        /// Drops a hit carrying values the game cannot produce.
        ///
        /// Damage is resolved on the client that owns the victim, from a HitData the ATTACKER's client wrote,
        /// and Character.RPC_Damage validates none of it. The server is only the relay, so this is the single
        /// point where the numbers can be looked at at all.
        ///
        /// This is a sanity bound and nothing more. Working out whether a plausible hit was earned would mean
        /// modelling every weapon, skill, buff and world modifier on the server, and getting that wrong means
        /// deleting legitimate combat. What it does catch is the whole class that matters: the non-finite
        /// values that corrupt a health bar permanently, negative components that heal the attacker's target
        /// into an unkillable state, and the absurd totals used to one-shot players and bosses.
        /// </summary>
        /// <summary>
        /// The one HitData every relayed hit is read into.
        ///
        /// Reused rather than allocated per packet. This is only safe because of what a hit is used for here:
        /// it is read on the server's main thread, inside one prefix, and nothing keeps a reference past the
        /// return - DamageAudit copies out the numbers it wants and the guard turns it into a string. Nothing
        /// on that path dispatches another routed RPC, so no second read can begin while one is live.
        ///
        /// HitData.Deserialize assigns every field this file and DamageAudit read, taking the wire value or an
        /// explicit default for each, so no value ever carries over from the previous hit.
        /// </summary>
        private static readonly HitData ScratchHit = new HitData();

        private static RpcVerdict JudgeDamage(ZNetPeer peer, ZRoutedRpc.RoutedRPCData data) {
            bool auditing = modules.audit.AuditPolicy.Active();
            // Nobody wants this payload: the audit is off and the guard would exempt this peer anyway. Kept
            // as an early return so an exempt admin in a long fight costs exactly what it did before the
            // audit existed - which is nothing.
            if (!auditing && RpcGuardPolicy.IsExempt(peer)) { return RpcVerdict.Pass; }

            ZPackage pkg = data.m_parameters;
            pkg.SetPos(0);

            HitData hit = ScratchHit;
            hit.Deserialize(ref pkg);
            pkg.SetPos(0); // the caller forwards this package untouched when we pass

            return JudgeParsedDamage(peer, hit, data.m_targetZDO, auditing);
        }

        /// <summary>
        /// What to do about a hit that has already been read, whichever way it was read. The exemption checks
        /// that bracket it are the point of the split: reading the payload is what the audit needed, refusing
        /// is what the guard exemption is about, and the two now have independent switches.
        /// </summary>
        private static RpcVerdict JudgeParsedDamage(ZNetPeer peer, HitData hit, ZDOID victim, bool auditing) {
            // Observation first, and deliberately ahead of the exemption below. The guard exemption exists so
            // an admin is not refused; it is not a reason to stop recording them, and an audit that skipped
            // admins would have its blind spot in the accounts most worth accounting for.
            if (auditing) {
                modules.audit.DamageAudit.Observe(peer, hit, victim);
            }

            if (!RpcGuardPolicy.Active() || !ValConfig.GuardDamageRpc.Value) { return RpcVerdict.Pass; }
            if (RpcGuardPolicy.IsExempt(peer)) { return RpcVerdict.Pass; }

            string fault = FaultIn(hit) ?? PvpFaultIn(hit, victim);
            if (fault == null) { return RpcVerdict.Pass; }

            RpcGuardPolicy.Refuse(peer, "damage", $"sent a hit that {fault}");
            return RpcVerdict.Drop;
        }

        /// <summary>
        /// Why this hit is forbidden PvP, or null when it is not.
        ///
        /// Vanilla does make this decision - Character.RPC_Damage refuses a player-on-player hit when the
        /// victim has PvP off - but it lets the hit through whenever hit.m_ignorePVP is set, and that flag
        /// travels in the payload the ATTACKER wrote. Setting it is all it takes to kill players who never
        /// opted in. Re-running the decision here is the only place the flag can be disregarded, because the
        /// server is not the destination and never runs vanilla's own check.
        ///
        /// Two exemptions, both matching what vanilla sets the flag for legitimately:
        ///   - self-damage, which is Aoe.cs:480's `m_owner == character` case - a player standing in their own
        ///     fire is attacker and victim at once, and must keep hurting;
        ///   - anything where either end is not a player, which is every ordinary fight in the game.
        ///
        /// The residual cost is stated in the setting's own description: an area-effect prefab that sets the
        /// flag deliberately loses its PvP pass-through. That is why this one is off by default.
        /// </summary>
        private static string PvpFaultIn(HitData hit, ZDOID victimId) {
            if (!ValConfig.GuardPvpDamage.Value) { return null; }
            if (ZDOMan.instance == null || victimId == ZDOID.None) { return null; }

            // Attacker and victim being the same character is self-damage, never PvP.
            if (hit.m_attacker == victimId) { return null; }
            // No attacker at all is drowning, falling, fire - nothing to judge.
            if (hit.m_attacker == ZDOID.None) { return null; }

            ZDO victim = ZDOMan.instance.GetZDO(victimId);
            if (victim == null || !IsPlayerZdo(victim)) { return null; }
            // The victim opted in. Their own client would have accepted this anyway.
            if (victim.GetBool(ZDOVars.s_pvp, false)) { return null; }

            ZDO attacker = ZDOMan.instance.GetZDO(hit.m_attacker);
            if (attacker == null || !IsPlayerZdo(attacker)) { return null; } // a creature hit them

            return "targets a player who has PvP switched off" +
                   (hit.m_ignorePVP ? ", using the attacker-supplied ignore-PvP flag" : "");
        }

        private static bool IsPlayerZdo(ZDO zdo) {
            return zdo.GetPrefab() == PlayerPrefabHash();
        }

        // Resolved from the prefab Game actually spawns players from, so a total-conversion that renames it is
        // still handled; the literal is the fallback for the window before Game.instance exists.
        private static int playerPrefabHash;
        private static int PlayerPrefabHash() {
            if (playerPrefabHash != 0) { return playerPrefabHash; }
            GameObject prefab = Game.instance != null ? Game.instance.m_playerPrefab : null;
            playerPrefabHash = prefab != null ? prefab.name.GetStableHashCode() : "Player".GetStableHashCode();
            return playerPrefabHash;
        }

        /// <summary>What is wrong with this hit, or null when nothing is.</summary>
        private static string FaultIn(HitData hit) {
            HitData.DamageTypes dmg = hit.m_damage;

            if (BadComponent(dmg.m_damage) || BadComponent(dmg.m_blunt) || BadComponent(dmg.m_slash)
                || BadComponent(dmg.m_pierce) || BadComponent(dmg.m_chop) || BadComponent(dmg.m_pickaxe)
                || BadComponent(dmg.m_fire) || BadComponent(dmg.m_frost) || BadComponent(dmg.m_lightning)
                || BadComponent(dmg.m_poison) || BadComponent(dmg.m_spirit)) {
                return "carries a damage component that is negative, NaN or infinite";
            }

            // The struct's own sum, not HitData.GetTotalDamage() - that one resolves the attacker through the
            // scene to add the world-level bonus, which the server cannot do for a remote attacker.
            float total = dmg.GetTotalDamage();
            float limit = Mathf.Max(1f, ValConfig.MaxAllowedHitDamage.Value);
            if (total > limit) {
                return $"totals {total:G6} damage, above the {limit:G6} allowed";
            }

            // Not damage, but written by the same client and just as unbounded. A non-finite push force
            // launches the victim out of the world; a non-finite multiplier poisons the stagger and backstab
            // maths on the receiving end.
            if (!Finite(hit.m_pushForce) || !Finite(hit.m_staggerMultiplier)
                || !Finite(hit.m_backstabBonus) || !Finite(hit.m_skillRaiseAmount)) {
                return "carries a non-finite push force or damage multiplier";
            }

            return null;
        }

        private static bool BadComponent(float value) {
            return !Finite(value) || value < 0f;
        }

        private static bool Finite(float value) {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
