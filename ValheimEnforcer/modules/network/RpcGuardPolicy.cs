using System;
using System.Collections.Generic;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.network {

    /// <summary>
    /// The shared decisions every RPC guard makes: is this feature live, is this peer exempt, how do we say
    /// what happened, and what happens to the player afterwards.
    ///
    /// The guards themselves are deliberately small and independent - each one knows about exactly one vanilla
    /// RPC - so everything they have in common lives here rather than being copied four times.
    ///
    /// All of this is server-side only. A client running the mod evaluates none of it: these guards exist
    /// precisely because the server must not take the client's word for anything, so a guard that ran on the
    /// client would be answering the wrong question.
    /// </summary>
    internal static class RpcGuardPolicy {

        /// <summary>
        /// True when the guards are switched on and we are the server. Every guard opens with this.
        /// </summary>
        internal static bool Active() {
            if (ValConfig.EnableRpcGuards == null || !ValConfig.EnableRpcGuards.Value) { return false; }
            return ZNet.instance != null && ZNet.instance.IsServer();
        }

        /// <summary>
        /// Whether this peer is held to the rules. Admins are exempt by default, matching
        /// StructureValidationExemptAdmins and for the same reason: vanilla's own admin-only commands send
        /// some of the very RPCs these guards refuse ('recall' sends RPC_TeleportPlayer to every peer), and an
        /// admin should not have to know this feature exists.
        ///
        /// The chat name binding deliberately does not consult this - see RoutedRpcFilter.
        /// </summary>
        internal static bool IsExempt(ZNetPeer peer) {
            if (peer == null) { return false; }
            if (ValConfig.RpcGuardExemptAdmins == null || !ValConfig.RpcGuardExemptAdmins.Value) { return false; }
            string hostId = HostIdOf(peer);
            return !string.IsNullOrEmpty(hostId) && ZNet.instance.IsAdmin(hostId);
        }

        /// <summary>
        /// The account id behind a connection. Resolved through PeerIdentity, which keeps it for the life of
        /// the connection: ISocket.GetHostName builds a fresh string on every call, and IsExempt asks for this
        /// on the relay path, once or twice per packet the filters look at.
        /// </summary>
        internal static string HostIdOf(ZNetPeer peer) {
            return modules.character.PeerIdentity.AccountFor(peer);
        }

        /// <summary>Name and host id together, for a log line a moderator can act on.</summary>
        internal static string Describe(ZNetPeer peer) {
            if (peer == null) { return "an unresolved peer"; }
            string hostId = HostIdOf(peer);
            string name = string.IsNullOrEmpty(peer.m_playerName) ? peer.m_uid.ToString() : peer.m_playerName;
            return string.IsNullOrEmpty(hostId) ? name : $"{name} ({hostId})";
        }

        // ---- Reporting -----------------------------------------------------------------------------------

        /// <summary>
        /// How long one peer's repeat of the same offence stays quiet in the log.
        ///
        /// These guards sit on the packet path, and the traffic they refuse arrives at whatever rate the
        /// sender feels like: a script pointing DestroyZDO at a base produces a refusal every frame. Without
        /// this the log - and the disk behind it - becomes the denial of service the guard was there to stop.
        /// </summary>
        private static readonly TimeSpan ReportCooldown = TimeSpan.FromSeconds(30);

        private sealed class Suppressed {
            internal DateTime LastLogged;
            internal int Since;
        }

        // Keyed by "hostId|guard". Server-side, and every guard runs on the main thread inside the RPC
        // handler, so a plain dictionary is enough.
        private static readonly Dictionary<string, Suppressed> reported = new Dictionary<string, Suppressed>();

        /// <summary>
        /// Reports one refusal and applies the configured action.
        ///
        /// Repeats of the same guard from the same peer inside the cooldown are counted rather than logged,
        /// and the count is folded into the next line that does come out. The action is applied on the lines
        /// that are logged, not on every suppressed repeat, so a burst produces one kick rather than a
        /// thousand.
        /// </summary>
        /// <param name="peer">Who sent it. Resolved from the socket, never from the packet.</param>
        /// <param name="guard">Short guard name, used both as the log prefix and the suppression key.</param>
        /// <param name="detail">What was wrong with this particular packet.</param>
        internal static void Refuse(ZNetPeer peer, string guard, string detail) {
            Record(peer, guard, detail, enforce: true);
        }

        /// <summary>
        /// Logs a correction without applying RpcGuardAction.
        ///
        /// For the guards that fix a packet rather than drop it, where the fix is the whole remedy and there
        /// is nothing left to punish. Kicking on top of it would only add a way to be wrong: a chat mod that
        /// decorates names is indistinguishable on the wire from an impersonation attempt, and one of those
        /// deserves a kick while the other deserves a rewrite and a log line.
        /// </summary>
        internal static void Report(ZNetPeer peer, string guard, string detail) {
            Record(peer, guard, detail, enforce: false);
        }

        private static void Record(ZNetPeer peer, string guard, string detail, bool enforce) {
            string hostId = HostIdOf(peer);
            string key = $"{(string.IsNullOrEmpty(hostId) ? peer?.m_uid.ToString() ?? "?" : hostId)}|{guard}";

            Suppressed state;
            if (!reported.TryGetValue(key, out state)) {
                state = new Suppressed { LastLogged = DateTime.MinValue, Since = 0 };
                reported[key] = state;
            }

            DateTime now = DateTime.UtcNow;
            if (now - state.LastLogged < ReportCooldown) {
                state.Since++;
                return;
            }

            string repeats = state.Since > 0 ? $" ({state.Since} further occurrence(s) suppressed)" : "";
            state.LastLogged = now;
            state.Since = 0;

            string what = enforce ? "refused" : "corrected";
            Logger.LogWarning($"RPC guard [{guard}]: {what} a message from {Describe(peer)} - {detail}.{repeats}");
            if (enforce) {
                // Only enforced refusals are evidence. A correction the guard made quietly - a rebound chat
                // name - is not, because an ordinary chat mod that decorates names produces exactly the same
                // thing on the wire.
                PeerTrust.NoteGuardTrip(peer, guard, detail);
                Enforce(peer, guard, detail);
            }
        }

        /// <summary>Bounds a client-supplied string before it reaches the log.</summary>
        internal static string Trim(string value) {
            if (string.IsNullOrEmpty(value)) { return "<empty>"; }
            return value.Length <= 64 ? value : value.Substring(0, 64) + "...";
        }

        /// <summary>
        /// Applies RpcGuardAction. The packet has already been dropped or corrected by the time this runs;
        /// this only decides what happens to the player, and defaults to nothing.
        /// </summary>
        private static void Enforce(ZNetPeer peer, string guard, string detail) {
            string hostId = HostIdOf(peer);
            if (string.IsNullOrEmpty(hostId)) { return; }

            string action = ValConfig.RpcGuardAction?.Value ?? "Log";
            switch (action) {
                case "Kick":
                    Logger.LogWarning($"Kicking {Describe(peer)} for a refused {guard} message.");
                    ZNet.instance.Kick(hostId);
                    break;
                case "Ban":
                    Logger.LogWarning($"Banning {Describe(peer)} for a refused {guard} message.");
                    ValConfig.BanHost(hostId, $"RPC guard [{guard}]: {detail}");
                    break;
                case "Log":
                default:
                    break;
            }
        }

        /// <summary>Drops the suppression table. Called from the ZNet.Shutdown teardown.</summary>
        internal static void Reset() {
            reported.Clear();
        }

        /// <summary>
        /// Drops one departing player's suppression entries. Called from the ZNet.Disconnect hook, so the
        /// table tracks connected players rather than everyone who has tripped a guard since startup. A
        /// player who reconnects starts with a clean cooldown, which only means their next refusal is logged
        /// rather than counted - the right side to err on.
        /// </summary>
        internal static void Forget(string hostId) {
            if (string.IsNullOrEmpty(hostId)) { return; }
            string prefix = hostId + "|";
            List<string> gone = null;
            foreach (string key in reported.Keys) {
                if (key.StartsWith(prefix, StringComparison.Ordinal)) { (gone ??= new List<string>()).Add(key); }
            }
            if (gone == null) { return; }
            foreach (string key in gone) { reported.Remove(key); }
        }

        internal static int TrackedCount => reported.Count;
    }
}
