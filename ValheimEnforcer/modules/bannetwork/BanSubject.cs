using System;
using System.Security.Cryptography;
using System.Text;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.bannetwork {

    /// <summary>
    /// Turns a platform account id into the "subject" the ban network stores and serves.
    ///
    /// <para><b>Who hashes what.</b> Reports leave this server carrying the <i>raw</i> platform id: HTTPS is
    /// the encryption and the API key is the authorisation, and a caller who can reach the endpoint has
    /// already been approved by a person. The network hashes on ingest and keeps only the digest, so the feed
    /// comes back as digests and this server hashes a connecting player's id locally to match. That is the
    /// only reason this type exists on the client side at all.</para>
    ///
    /// <para><b>What the hashing is worth - stated plainly, because it would be easy to imply more.</b> A
    /// single cheap hash of a SteamID is reversible in seconds by anyone who cares to; the salt below ships
    /// inside a publicly distributed DLL and the identifier space is small and enumerable. This is not
    /// protection. It stops the network's database being a directly greppable list of SteamIDs, and that is
    /// the whole of it. That is a fair trade for this particular data: any approved server can already
    /// download the entire list, which is no different from the server owners who swap ban lists in plain
    /// text today. <b>Do not describe this as anonymity</b>, in the docs or anywhere else.</para>
    ///
    /// <para>Deliberately cheap. An earlier design used a heavy key-derivation function here, which bought
    /// little against an enumerable input space and cost tens of milliseconds per account - enough that
    /// hashing had to move to its own thread and the join gate had to decide without it. A plain digest is
    /// microseconds, so the connect handshake gets a complete answer immediately and none of that machinery
    /// is needed.</para>
    /// </summary>
    internal static class BanSubject {

        /// <summary>
        /// Must match the ban network's SUBJECT_SALT. Not a secret - it ships in this assembly - but the two
        /// sides must agree or every digest silently fails to match and the network bans nobody.
        /// </summary>
        private const string Salt = "vebn-subject-salt-v1-REPLACE-BEFORE-RELEASE";

        /// <summary>128 bits, hex encoded: 32 characters. Wide enough that collisions are not a concern.</summary>
        internal const int OutputBytes = 16;

        internal const int HexLength = OutputBytes * 2;

        // The derivation, written down once because two implementations in two languages have to agree:
        //
        //   subject = lowercase_hex( SHA-256( utf8(Salt) || utf8(PlatformIds.Normalize(hostId)) ) )[:32]
        //
        // Normalising first means "Steam_7656..." and a bare "7656..." produce the same subject. The same
        // account reaches this mod under both spellings depending on which socket it arrived on, and two
        // subjects for one account would be a ban that applies on Steam but not on PlayFab.
        //
        // Changing any input changes every subject on the network, so this is a wire contract.

        private static readonly byte[] SaltBytes = Encoding.UTF8.GetBytes(Salt);

        private static bool selfTestRun;
        private static bool selfTestPassed;

        /// <summary>The subject for an account id, or null when there is nothing to hash.</summary>
        internal static string Hash(string hostId) {
            if (string.IsNullOrEmpty(hostId)) { return null; }
            string normalized = PlatformIds.Normalize(hostId);
            if (string.IsNullOrEmpty(normalized)) { return null; }

            byte[] idBytes = Encoding.UTF8.GetBytes(normalized);
            byte[] input = new byte[SaltBytes.Length + idBytes.Length];
            Buffer.BlockCopy(SaltBytes, 0, input, 0, SaltBytes.Length);
            Buffer.BlockCopy(idBytes, 0, input, SaltBytes.Length, idBytes.Length);

            using (SHA256 sha = SHA256.Create()) {
                byte[] digest = sha.ComputeHash(input);
                StringBuilder sb = new StringBuilder(HexLength);
                for (int i = 0; i < OutputBytes; i++) { sb.Append(digest[i].ToString("x2")); }
                return sb.ToString();
            }
        }

        /// <summary>True when a string is shaped like a subject, so a command can tell one from an id.</summary>
        internal static bool LooksLikeHash(string value) {
            if (string.IsNullOrEmpty(value) || value.Length != HexLength) { return false; }
            foreach (char c in value) {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) { return false; }
            }
            return true;
        }

        /// <summary>
        /// Known-answer check that this build derives the subject the network expects.
        ///
        /// The failure it guards against is not a crash but a silent mismatch: a digest that is stable and
        /// self-consistent, matches nothing any other server produces, and leaves the feature looking healthy
        /// while banning nobody. The same vectors are asserted on the Worker side, so the two cannot drift
        /// apart without one of them going red.
        /// </summary>
        internal static bool SelfTest() {
            if (selfTestRun) { return selfTestPassed; }
            selfTestRun = true;

            // Generated from the salt above, not transcribed. The second case is the prefix-stripping one:
            // a platform-prefixed id must land on the same subject as the bare form.
            string[][] vectors = {
                new[] { "76561198012345678", "22b28db21946dd6f2db385541e77a692" },
                new[] { "Steam_76561198012345678", "22b28db21946dd6f2db385541e77a692" },
                new[] { "76561199100910386", "e8eaa009991351c1bd7d26498b8cec93" },
            };

            foreach (string[] vector in vectors) {
                string actual;
                try {
                    actual = Hash(vector[0]);
                } catch (Exception e) {
                    Logger.LogError($"Ban network: subject hashing threw ({e.Message}). The ban network is disabled for this session.");
                    selfTestPassed = false;
                    return false;
                }
                if (!string.Equals(actual, vector[1], StringComparison.Ordinal)) {
                    Logger.LogError($"Ban network: subject hashing self test FAILED for '{vector[0]}' "
                                  + $"(expected {vector[1]}, got {actual ?? "null"}). Subjects would not match any other "
                                  + "server's, so the ban network is disabled for this session. Please report this.");
                    selfTestPassed = false;
                    return false;
                }
            }

            selfTestPassed = true;
            return true;
        }

        /// <summary>Whether hashing may be relied on. False once the self test has failed.</summary>
        internal static bool Usable {
            get { return !selfTestRun || selfTestPassed; }
        }
    }
}
