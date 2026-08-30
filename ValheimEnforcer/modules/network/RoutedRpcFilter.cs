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
        /// read, so an untouched server pays nothing for this file existing.
        /// </summary>
        internal static bool AnyEnabled() {
            if (!RpcGuardPolicy.Active()) { return false; }
            return ValConfig.GuardChatSenderName.Value
                || ValConfig.GuardPlayerTeleportRpc.Value
                || ValConfig.GuardDamageRpc.Value;
        }

        /// <summary>
        /// Whether this method is one we inspect. An int comparison against three constants, run on every
        /// routed packet; everything expensive happens only after this says yes.
        /// </summary>
        internal static bool IsWatched(int methodHash) {
            if (methodHash == ChatMessageHash) { return ValConfig.GuardChatSenderName.Value; }
            if (methodHash == TeleportPlayerHash) { return ValConfig.GuardPlayerTeleportRpc.Value; }
            if (methodHash == DamageHash) { return ValConfig.GuardDamageRpc.Value; }
            return false;
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
        private static RpcVerdict JudgeDamage(ZNetPeer peer, ZRoutedRpc.RoutedRPCData data) {
            if (RpcGuardPolicy.IsExempt(peer)) { return RpcVerdict.Pass; }

            ZPackage pkg = data.m_parameters;
            pkg.SetPos(0);

            HitData hit = new HitData();
            hit.Deserialize(ref pkg);
            pkg.SetPos(0); // the caller forwards this package untouched when we pass

            string fault = FaultIn(hit) ?? PvpFaultIn(hit, data.m_targetZDO);
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
