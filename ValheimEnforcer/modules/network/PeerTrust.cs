using System;
using System.Collections.Generic;
using ValheimEnforcer.common;
using ValheimEnforcer.modules.notifications;

namespace ValheimEnforcer.modules.network {

    /// <summary>
    /// Ties what a client <i>said</i> at join to what it has since been caught <i>doing</i>.
    ///
    /// The two halves of this mod have always run past each other. Mod validation asks the client to describe
    /// itself and has to take the answer on trust; the server-authoritative guards watch the wire and cannot be
    /// lied to. Neither knows about the other, so a player who declares a clean mod list and then sends traffic
    /// no clean client produces shows up as two unrelated log lines nobody connects.
    ///
    /// This connects them. Every peer the join gate lets through is, by construction, running only mods the
    /// server approved - that is what the gate is for. So a guard trip from that peer means an approved mod set
    /// produced traffic the server does not sanction, and the guard is the half of that sentence that cannot be
    /// forged.
    ///
    /// <b>What it deliberately does not do is decide which half lied.</b> A guard trip cannot tell "the client
    /// lied about its mods" apart from "a mod the admin really did approve legitimately sends this", and
    /// nothing can compute that in general. Pretending otherwise produces confident, wrong bans. What this
    /// delivers instead is the line a moderator can act on: who, which guard, and what they claimed to be
    /// running when they joined.
    ///
    /// Server side only, and cleared per connection.
    /// </summary>
    internal static class PeerTrust {

        /// <summary>What one connected peer declared, and what it has tripped since.</summary>
        internal sealed class Record {
            internal string HostId;
            internal string PlayerName;
            internal int DeclaredMods;
            internal int DeclaredPatchers;
            /// <summary>
            /// True when the client declared nothing the server does not itself require, and the server offers
            /// no optional mods. In that case there is no client-side mod in the picture that the server does
            /// not also run, which is the narrow case where the inference is actually tight.
            /// </summary>
            internal bool BaselineOnly;
            internal readonly HashSet<string> Guards = new HashSet<string>(StringComparer.Ordinal);
            internal string FirstDetail;
            internal DateTime FirstTrip;
            internal DateTime LastTrip;
            /// <summary>The action has already been applied for this connection; it is not applied again.</summary>
            internal bool Acted;
        }

        // Keyed by socket host id. Main thread only: every writer is inside an RPC handler.
        private static readonly Dictionary<string, Record> records = new Dictionary<string, Record>();

        private static bool Enabled() {
            return ValConfig.ReportClientContradictions != null && ValConfig.ReportClientContradictions.Value;
        }

        /// <summary>
        /// Records what a client declared, at the moment its mod list passed validation.
        ///
        /// Captured even when the feature is off, because it is free and because turning the setting on mid-session
        /// should not leave already-connected players with no declaration on file.
        /// </summary>
        internal static void Declare(string hostId, DataObjects.Mods declared, DataObjects.Mods authoritative) {
            if (string.IsNullOrEmpty(hostId)) { return; }

            Record record = new Record {
                HostId = hostId,
                DeclaredMods = declared?.ActiveMods?.Count ?? 0,
                DeclaredPatchers = declared?.ActivePatchers?.Count ?? 0,
                BaselineOnly = IsBaselineOnly(declared, authoritative),
            };
            records[hostId] = record;
        }

        /// <summary>
        /// True when nothing the client declared is outside the server's own required set, and the server
        /// publishes no optional mods for anything else to hide in.
        ///
        /// AdminOnly is not consulted: admins are exempt from the guards, so a peer that trips one is a
        /// non-admin, and a non-admin carrying an admin-only mod never got past validation in the first place.
        /// </summary>
        private static bool IsBaselineOnly(DataObjects.Mods declared, DataObjects.Mods authoritative) {
            if (declared?.ActiveMods == null || authoritative?.RequiredMods == null) { return false; }
            if (authoritative.OptionalMods != null && authoritative.OptionalMods.Count > 0) { return false; }

            foreach (string guid in declared.ActiveMods.Keys) {
                if (!authoritative.RequiredMods.ContainsKey(guid)) { return false; }
            }
            return true;
        }

        /// <summary>
        /// Notes that a server-authoritative guard refused something this peer sent.
        ///
        /// Called from the two places that produce such a refusal: <see cref="RpcGuardPolicy"/> and the
        /// structure validator. Only enforced refusals reach here - a correction the guard made silently (a
        /// rebound chat name) is not evidence of anything, because an ordinary chat mod produces it too.
        /// </summary>
        internal static void NoteGuardTrip(ZNetPeer peer, string guard, string detail) {
            if (!Enabled() || peer == null) { return; }
            string hostId = RpcGuardPolicy.HostIdOf(peer);
            if (string.IsNullOrEmpty(hostId)) { return; }

            if (!records.TryGetValue(hostId, out Record record)) {
                // No declaration on file: this peer connected before the feature was on, or through a path that
                // never validated. Still worth tracking - the guard trips are the unforgeable half - so start a
                // record and let the report say the declaration is unknown.
                record = new Record { HostId = hostId, DeclaredMods = -1 };
                records[hostId] = record;
            }

            record.PlayerName = string.IsNullOrEmpty(peer.m_playerName) ? record.PlayerName : peer.m_playerName;
            record.LastTrip = DateTime.UtcNow;
            if (!record.Guards.Add(guard)) { return; } // already seen this guard from this peer; nothing new to say

            if (record.Guards.Count == 1) {
                record.FirstTrip = record.LastTrip;
                record.FirstDetail = detail;
            }

            Report(record, guard, detail);
            Enforce(record);
        }

        private static void Report(Record record, string guard, string detail) {
            string who = string.IsNullOrEmpty(record.PlayerName) ? record.HostId : $"{record.PlayerName} ({record.HostId})";
            string declared = record.DeclaredMods < 0
                ? "no mod declaration is on file for this connection"
                : record.BaselineOnly
                    ? $"declared {record.DeclaredMods} mod(s) at join, all of them mods this server requires, and this server offers no optional mods - so nothing they declared can account for it"
                    : $"declared {record.DeclaredMods} mod(s) and {record.DeclaredPatchers} patcher(s) at join";

            Logger.LogWarning(
                $"Client contradiction: {who} tripped the [{guard}] guard, and {declared}. " +
                $"{record.Guards.Count} distinct guard(s) tripped this session. Guard detail: {detail}");

            Notify(record, guard, detail);
        }

        private static void Notify(Record record, string guard, string detail) {
            if (ValConfig.DiscordNotifyClientContradiction == null || !ValConfig.DiscordNotifyClientContradiction.Value) { return; }

            DiscordNotifier.Notify(NotificationEvent.ClientContradiction, new Dictionary<string, string> {
                { "player", record.PlayerName ?? "unknown" },
                { "playerId", record.HostId ?? "unknown" },
                { "guard", guard },
                { "guards", string.Join(", ", ToArray(record.Guards)) },
                { "guardCount", record.Guards.Count.ToString() },
                { "declaredMods", record.DeclaredMods < 0 ? "unknown" : record.DeclaredMods.ToString() },
                { "declaredPatchers", record.DeclaredPatchers < 0 ? "unknown" : record.DeclaredPatchers.ToString() },
                { "baselineOnly", record.BaselineOnly ? "yes" : "no" },
                { "reason", detail ?? "" },
            });
        }

        /// <summary>
        /// Applies ContradictionAction once this peer has tripped enough distinct guards.
        ///
        /// Distinct guards rather than total trips, deliberately. One player hitting zdo-destroy four hundred
        /// times is one behaviour and the guard already stopped it; tripping teleport-player, global-key and
        /// zdo-destroy once each is a toolkit, and that is the pattern worth acting on.
        /// </summary>
        private static void Enforce(Record record) {
            if (record.Acted) { return; }
            int threshold = Math.Max(1, ValConfig.ContradictionThreshold.Value);
            if (record.Guards.Count < threshold) { return; }

            string action = ValConfig.ContradictionAction?.Value ?? "Log";
            if (action == "Log") { return; }

            record.Acted = true;
            string who = string.IsNullOrEmpty(record.PlayerName) ? record.HostId : record.PlayerName;
            string reason = $"Tripped {record.Guards.Count} server-side guards ({string.Join(", ", ToArray(record.Guards))}) " +
                            $"while declaring {(record.DeclaredMods < 0 ? "an unknown mod list" : record.DeclaredMods + " mod(s)")}";

            switch (action) {
                case "Kick":
                    Logger.LogWarning($"Kicking {who}: {reason}.");
                    ZNet.instance.Kick(record.HostId);
                    break;
                case "Ban":
                    Logger.LogWarning($"Banning {who}: {reason}.");
                    ValConfig.BanHost(record.HostId, reason);
                    break;
            }
        }

        private static string[] ToArray(HashSet<string> set) {
            string[] copy = new string[set.Count];
            set.CopyTo(copy);
            Array.Sort(copy, StringComparer.Ordinal);
            return copy;
        }

        /// <summary>Every connection with at least one trip this session, for the terminal command.</summary>
        internal static List<Record> Flagged() {
            List<Record> flagged = new List<Record>();
            foreach (Record record in records.Values) {
                if (record.Guards.Count > 0) { flagged.Add(record); }
            }
            return flagged;
        }

        internal static void Clear(string hostId) {
            if (string.IsNullOrEmpty(hostId)) { return; }
            records.Remove(hostId);
        }

        internal static void Reset() {
            records.Clear();
        }
    }
}
