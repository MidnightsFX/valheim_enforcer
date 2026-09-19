using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using ValheimEnforcer.common;
using ValheimEnforcer.modules.character;

namespace ValheimEnforcer.modules.bannetwork {

    /// <summary>
    /// Remembers subject hashes in both directions.
    ///
    /// <para>Forward (id to subject) is a memo over <see cref="BanSubject.Hash"/>. It is not there for speed
    /// - a digest is microseconds and the join gate simply computes one - but so that the reverse map is
    /// filled as a side effect of every lookup the server does anyway.</para>
    ///
    /// <para>Reverse (subject to id) is the one that earns its keep. A pulled network entry is an opaque
    /// digest, and an owner deciding whether to override it needs to know who it refers to. Every id this
    /// server already knows - its saves, its bans, its admins, whoever is connected - is hashed once at
    /// startup, so any network entry concerning someone who has ever played here resolves to a readable id.
    /// Anyone else stays opaque, which is the intended behaviour rather than a gap: the network deliberately
    /// cannot tell this server who a stranger is.</para>
    /// </summary>
    internal static class BanSubjectCache {

        /// <summary>
        /// Far more than any real server's player base. The cap exists so a stream of connection attempts
        /// from spoofed ids cannot grow this without bound.
        /// </summary>
        private const int MaxEntries = 32768;

        /// <summary>
        /// The startup seed reads every character save directory, so it is bounded and runs off the main
        /// thread. Anyone left out still resolves the moment they connect.
        /// </summary>
        private const int MaxSeededSaves = 5000;

        private static readonly ConcurrentDictionary<string, string> Forward = new ConcurrentDictionary<string, string>();
        private static readonly ConcurrentDictionary<string, string> Reverse = new ConcurrentDictionary<string, string>();

        private static bool capacityWarned;
        private static volatile bool active;

        internal static int Count { get { return Forward.Count; } }

        // ---- Lifecycle ------------------------------------------------------------------------------------

        /// <summary>
        /// Called on the main thread from the ZNet.Start patch, which is also the only safe place to read
        /// ZNet for the seed - the background seed that follows must never touch it.
        /// </summary>
        internal static void Initialize() {
            if (active) { return; }
            if (!BanSubject.SelfTest()) { return; }
            active = true;

            List<string> fromZNet = GatherFromZNet();
            // One shot, not a standing thread: hashing is cheap, and the only slow part is enumerating the
            // save directories. Nothing waits on the result - an id that has not been seeded yet is simply
            // not resolvable for display until it is, which costs nobody a ban.
            Task.Run(() => Seed(fromZNet));
        }

        internal static void Teardown() {
            active = false;
            Forward.Clear();
            Reverse.Clear();
            capacityWarned = false;
        }

        // ---- Queries --------------------------------------------------------------------------------------

        /// <summary>
        /// The subject for an account, computing and memoising it if needed.
        ///
        /// Safe from any thread and cheap enough for the connect handshake, which is where it is called from.
        /// </summary>
        internal static string Hash(string hostId) {
            if (string.IsNullOrEmpty(hostId) || !BanSubject.Usable) { return null; }
            string normalized = PlatformIds.Normalize(hostId);
            if (string.IsNullOrEmpty(normalized)) { return null; }
            if (Forward.TryGetValue(normalized, out string cached)) { return cached; }

            string hash = BanSubject.Hash(normalized);
            Remember(normalized, hash);
            return hash;
        }

        /// <summary>
        /// The account id behind a subject, if this server has ever seen it. Null means only "not known
        /// here" - the network cannot tell anyone who a subject is, by design.
        /// </summary>
        internal static string Resolve(string subject) {
            if (string.IsNullOrEmpty(subject)) { return null; }
            return Reverse.TryGetValue(subject.ToLowerInvariant(), out string id) ? id : null;
        }

        private static void Remember(string normalizedId, string hash) {
            if (string.IsNullOrEmpty(normalizedId) || string.IsNullOrEmpty(hash)) { return; }
            if (Forward.Count >= MaxEntries) {
                if (!capacityWarned) {
                    capacityWarned = true;
                    Logger.LogWarning($"Ban network: the subject lookup table has reached {MaxEntries} entries and has stopped growing. "
                                    + "Bans are unaffected; some network entries will show as a hash rather than an id.");
                }
                return;
            }
            Forward[normalizedId] = hash;
            Reverse[hash] = normalizedId;
        }

        // ---- Seeding --------------------------------------------------------------------------------------

        /// <summary>
        /// Hashes everyone this server already knows. Runs off the main thread and touches only files and
        /// this module's own stores - never ZNet, whose share of the seed was captured in <see cref="Initialize"/>.
        ///
        /// Ordered by how likely the answer is to be wanted: whoever is connected, an admin, or already
        /// banned comes first, then the back catalogue of saves.
        /// </summary>
        private static void Seed(List<string> fromZNet) {
            try {
                int seeded = Add(fromZNet, int.MaxValue);

                List<string> banned = new List<string>();
                foreach (BanRecord record in BanStore.All()) {
                    if (!string.IsNullOrEmpty(record.Id)) { banned.Add(record.Id); }
                }
                seeded += Add(banned, int.MaxValue);

                List<string> saves = new List<string>();
                try {
                    saves.AddRange(CharacterSaves.Accounts());
                } catch (Exception e) {
                    Logger.LogDebug($"Ban network: could not read the character saves while seeding: {e.Message}");
                }
                int skipped = Math.Max(0, saves.Count - MaxSeededSaves);
                seeded += Add(saves, MaxSeededSaves);

                if (!active) { return; }
                string tail = skipped > 0
                    ? $" {skipped} older save(s) were left out; they resolve when that player next connects."
                    : "";
                Logger.LogInfo($"Ban network: indexed {seeded} known account id(s), so network entries concerning them show an id rather than a hash.{tail}");
            } catch (Exception e) {
                Logger.LogWarning($"Ban network: seeding the subject lookup table failed: {e.Message}");
            }
        }

        private static int Add(List<string> ids, int limit) {
            int added = 0;
            foreach (string id in ids) {
                if (!active || added >= limit) { break; }
                string normalized = PlatformIds.Normalize(id);
                if (string.IsNullOrEmpty(normalized) || Forward.ContainsKey(normalized)) { continue; }
                Remember(normalized, BanSubject.Hash(normalized));
                added++;
            }
            return added;
        }

        /// <summary>Main thread only: everything ZNet knows about that is worth resolving later.</summary>
        private static List<string> GatherFromZNet() {
            List<string> ids = new List<string>();
            try {
                if (ZNet.instance == null) { return ids; }
                foreach (ZNetPeer peer in ZNet.instance.GetPeers()) {
                    if (peer == null) { continue; }
                    string account = PeerIdentity.AccountFor(peer);
                    if (!string.IsNullOrEmpty(account)) { ids.Add(account); }
                }
                List<string> admins = ZNet.instance.GetAdminList();
                if (admins != null) {
                    foreach (string admin in admins) {
                        if (!string.IsNullOrEmpty(admin)) { ids.Add(AdminIds.Canonical(admin)); }
                    }
                }
            } catch (Exception e) {
                Logger.LogDebug($"Ban network: could not read ZNet while seeding: {e.Message}");
            }
            return ids;
        }
    }
}
