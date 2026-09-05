using System;
using ValheimEnforcer.common;
using Logger = ValheimEnforcer.Logger;

namespace ValheimEnforcer.modules.audit {

    /// <summary>
    /// The gate every part of the audit consults, and the one definition of who a recorded peer is.
    ///
    /// Modelled on <see cref="modules.network.RpcGuardPolicy"/>, but with one deliberate difference: this
    /// feature never refuses, drops, kicks or bans anything. It only records. That is why
    /// <see cref="Recorded"/> defaults to including admins where the guards default to exempting them - an
    /// exemption from a punishment is a kindness, an exemption from a recording is a blind spot in exactly
    /// the account most worth being able to account for.
    /// </summary>
    internal static class AuditPolicy {

        /// <summary>
        /// True when the audit is switched on and we are the server. Every entry point opens with this, and
        /// it is what makes the feature genuinely inert when off rather than merely quiet.
        ///
        /// The item, container and damage halves used to have a switch each. They no longer do: an audit is
        /// only worth having if it accounts for the whole of what a player did, and three of the four ways to
        /// configure it produced a record with a hole in it that nothing in the output announced. So there is
        /// one switch, and this is it.
        /// </summary>
        internal static bool Active() {
            if (ValConfig.EnableAuditLog == null || !ValConfig.EnableAuditLog.Value) { return false; }
            return ZNet.instance != null && ZNet.instance.IsServer();
        }

        /// <summary>
        /// Whether this peer's activity is recorded. Only ever false when an admin has deliberately turned
        /// AuditExemptAdmins on, which is off by default - see the class comment.
        /// </summary>
        internal static bool Recorded(ZNetPeer peer) {
            if (peer == null) { return false; }
            if (ValConfig.AuditExemptAdmins == null || !ValConfig.AuditExemptAdmins.Value) { return true; }
            string hostId = AccountOf(peer);
            return string.IsNullOrEmpty(hostId) || !ZNet.instance.IsAdmin(hostId);
        }

        /// <summary>
        /// The account an event is filed under: the socket's host name, never anything out of a payload.
        ///
        /// This is the same value the admin list, the ban list and <see cref="ValConfig"/>'s admin gate key
        /// on, so a recorded account always means the same thing as an enforced one. It is not always the
        /// same spelling the character store filed a save under (see <see cref="PlatformIds"/>), which is why
        /// every query compares with PlatformIds.Matches rather than string equality.
        ///
        /// Goes through PeerIdentity rather than reading the socket directly, so the answer is resolved once
        /// per connection instead of once per packet - GetHostName builds a new string every call, and this
        /// sits on the ZDO stream and the damage relay.
        /// </summary>
        internal static string AccountOf(ZNetPeer peer) {
            return modules.character.PeerIdentity.AccountFor(peer);
        }

        internal static string CharacterOf(ZNetPeer peer) {
            return peer != null ? peer.m_playerName : null;
        }

        /// <summary>
        /// The peer holding a ZDO, or null. Used to attribute a container change to whoever owned the chest
        /// when it changed, which is the opener - Container.RPC_RequestOpen transfers ownership to them.
        /// </summary>
        internal static ZNetPeer PeerForOwner(long ownerUid) {
            if (ownerUid == 0L || ZNet.instance == null) { return null; }
            return ZNet.instance.GetPeer(ownerUid);
        }

        /// <summary>
        /// A world position an admin can fly to. Rounded to whole metres on purpose: this ends up in every
        /// container line, and six decimal places of float noise would triple the size of a day file to say
        /// nothing a moderator can use.
        /// </summary>
        internal static string Position(UnityEngine.Vector3 pos) {
            return $"{Math.Round(pos.x)}/{Math.Round(pos.y)}/{Math.Round(pos.z)}";
        }

        /// <summary>
        /// Records an event, swallowing anything that goes wrong.
        ///
        /// Every caller sits on a path that must not throw - the ZDO stream, the routed RPC relay, the delta
        /// handler. An audit that stops recording is a lost report; an audit that throws is a desynced
        /// server, so the failure direction is not a close call.
        /// </summary>
        internal static void Record(AuditEvent entry) {
            try {
                if (entry == null || string.IsNullOrEmpty(entry.Acct)) { return; }
                AuditLog.Enqueue(entry);
            } catch (Exception e) {
                Logger.LogDebug($"Audit could not record a {entry?.Kind} event: {e.Message}");
            }
        }

        /// <summary>Builds an event with the fields every kind shares already filled in.</summary>
        internal static AuditEvent Event(string account, string character, string kind) {
            AuditEvent entry = new AuditEvent {
                Acct = account,
                Character = character,
                Kind = kind
            };
            // Sets the stamp AND remembers the DateTime behind it, so nothing on the write path ever parses
            // the string back - the flush groups a batch into day files by exactly this value.
            entry.SetTime(DateTime.UtcNow);
            return entry;
        }

        internal static AuditEvent Event(ZNetPeer peer, string kind) {
            return Event(AccountOf(peer), CharacterOf(peer), kind);
        }
    }
}
