using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.mods {

    /// <summary>
    /// Binds a client's mod declaration to a value the server picked, so the declaration cannot be a canned
    /// answer.
    ///
    /// Without this, everything a client reports about itself is the same every session: the same plugin list,
    /// the same file hashes, forever. A client patched to skip the work can hold one captured payload and
    /// replay it for the rest of time, which costs the attacker a single constant.
    ///
    /// The server therefore hands each connection a fresh random nonce before the client builds its report, and
    /// the client returns a digest over the nonce and the exact declaration it is sending. The server recomputes
    /// the digest from the declaration it received and its own nonce; they agree only if the client actually ran
    /// this code, this session, over this data.
    ///
    /// <b>What that does and does not prove.</b> It proves the report was <i>generated now, by code that ran</i>.
    /// It does not prove the report is <i>true</i> - an attacker who keeps the original DLLs on disk and hashes
    /// those still produces a valid digest, and nothing running inside a process the attacker owns can ever
    /// settle that. What it buys is the difference between "return a constant blob", which is a one-line patch,
    /// and "keep a correct hashing path alive per connection". Every description of this feature should claim
    /// that and nothing more.
    ///
    /// The ordering this depends on is a property of the vanilla handshake, not of anything added here. The
    /// client invokes ServerHandshake; the server's prefix sends its mod payload (carrying the nonce); vanilla
    /// then invokes ClientHandshake; the client's prefix sends its own payload. One ordered ZRpc, so the nonce
    /// is always in hand before the client's report is built - no extra round trip, and no new RPC.
    /// </summary>
    internal static class Attestation {

        internal const string Off = "Off";
        internal const string Report = "Report";
        internal const string Require = "Require";

        /// <summary>32 bytes. Long enough that precomputing against a guess is not a strategy.</summary>
        private const int NonceBytes = 32;

        // Server side: host id -> the nonce issued to that connection. Cleared on disconnect, exactly like
        // ModManager's RejectedHosts and ValidatedHosts, which the same ZNet.Disconnect patch already clears.
        private static readonly Dictionary<string, string> issued = new Dictionary<string, string>();

        // Client side: the nonce this client was handed by the server it is connecting to.
        private static string heldNonce;

        private static readonly RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();

        internal static string Policy() {
            return ValConfig.AttestationPolicy?.Value ?? Off;
        }

        internal static bool Enabled() {
            return Policy() != Off;
        }

        // ---- Server side ---------------------------------------------------------------------------------

        /// <summary>
        /// Mints and records a nonce for one connection. Returns null when the feature is off, which leaves the
        /// field out of the payload entirely and keeps the whole thing inert.
        ///
        /// Cryptographic RNG rather than System.Random on purpose: a predictable nonce is a nonce an attacker
        /// can precompute a table against, which is the exact thing being prevented.
        /// </summary>
        internal static string Issue(string hostId) {
            if (!Enabled() || string.IsNullOrEmpty(hostId)) { return null; }
            byte[] bytes = new byte[NonceBytes];
            lock (rng) { rng.GetBytes(bytes); }
            string nonce = Convert.ToBase64String(bytes);
            lock (issued) { issued[hostId] = nonce; }
            return nonce;
        }

        /// <summary>The nonce issued to this connection, or null if none was.</summary>
        internal static string IssuedTo(string hostId) {
            if (string.IsNullOrEmpty(hostId)) { return null; }
            lock (issued) {
                return issued.TryGetValue(hostId, out string nonce) ? nonce : null;
            }
        }

        /// <summary>Drops a connection's nonce. Called from the same disconnect hook that clears the mod gates.</summary>
        internal static void Clear(string hostId) {
            if (string.IsNullOrEmpty(hostId)) { return; }
            lock (issued) { issued.Remove(hostId); }
        }

        internal static void ClearAll() {
            lock (issued) { issued.Clear(); }
        }

        /// <summary>Outcome of checking one client's attestation.</summary>
        internal enum Verdict {
            /// <summary>Not in force, or the digest matched.</summary>
            Pass,
            /// <summary>The client returned no digest. An older client looks exactly like this.</summary>
            Missing,
            /// <summary>A digest was returned and it is not the one this declaration and nonce produce.</summary>
            Mismatch,
            /// <summary>We never issued a nonce for this connection, so there is nothing to check against.</summary>
            NotIssued
        }

        /// <summary>
        /// Checks a client's declaration against the nonce this server gave it.
        ///
        /// Note what is being compared: the digest is recomputed from the declaration that <i>arrived</i>. So a
        /// pass says the client hashed exactly what it sent, with our nonce - it says nothing about whether
        /// what it sent is the truth. See the class comment.
        /// </summary>
        internal static Verdict Check(string hostId, DataObjects.Mods declaration) {
            if (!Enabled()) { return Verdict.Pass; }

            string nonce = IssuedTo(hostId);
            if (string.IsNullOrEmpty(nonce)) { return Verdict.NotIssued; }

            string reported = declaration?.Attestation;
            if (string.IsNullOrEmpty(reported)) { return Verdict.Missing; }

            string expected = Compute(nonce, declaration);
            return string.Equals(expected, reported, StringComparison.OrdinalIgnoreCase)
                ? Verdict.Pass
                : Verdict.Mismatch;
        }

        // ---- Client side ---------------------------------------------------------------------------------

        /// <summary>Remembers the nonce out of the server's mod payload. Null clears it.</summary>
        internal static void HoldNonce(string nonce) {
            heldNonce = nonce;
        }

        /// <summary>
        /// The digest for the declaration this client is about to send, or null when the server issued no
        /// nonce - in which case the field is simply absent, which is what an older server expects anyway.
        /// </summary>
        internal static string Respond(DataObjects.Mods declaration) {
            if (string.IsNullOrEmpty(heldNonce)) { return null; }
            return Compute(heldNonce, declaration);
        }

        // ---- Shared --------------------------------------------------------------------------------------

        /// <summary>
        /// SHA256 over the nonce and a canonical rendering of the declaration.
        ///
        /// Both sides must produce byte-identical input, so the canonical form is fully ordered and carries no
        /// dictionary iteration order, no formatting choices and no culture-sensitive text.
        /// </summary>
        internal static string Compute(string nonce, DataObjects.Mods declaration) {
            string canonical = Canonicalize(declaration);
            using (SHA256 sha = SHA256.Create()) {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(nonce + "\n" + canonical));
                return PluginHasher.ToHex(digest);
            }
        }

        /// <summary>
        /// One line per declared plugin and patcher, ordinal-sorted within each group.
        ///
        /// A missing hash is rendered as its status token rather than as an empty string, so "I could not hash
        /// this" and "I am not telling you" are different inputs and produce different digests. The "M:" and
        /// "P:" prefixes keep a plugin GUID from ever colliding with a patcher path.
        /// </summary>
        internal static string Canonicalize(DataObjects.Mods declaration) {
            List<string> lines = new List<string>();

            if (declaration?.ActiveMods != null) {
                foreach (KeyValuePair<string, DataObjects.Mod> mod in declaration.ActiveMods) {
                    lines.Add("M:" + mod.Key + "=" + Fingerprint(mod.Value?.Hash, mod.Value?.HashStatus));
                }
            }
            if (declaration?.ActivePatchers != null) {
                foreach (KeyValuePair<string, DataObjects.PatcherEntry> patcher in declaration.ActivePatchers) {
                    lines.Add("P:" + patcher.Key + "=" + Fingerprint(patcher.Value?.Hash, patcher.Value?.HashStatus));
                }
            }

            lines.Sort(StringComparer.Ordinal);
            return string.Join("\n", lines.ToArray());
        }

        private static string Fingerprint(string hash, string status) {
            if (!string.IsNullOrEmpty(hash)) { return hash; }
            return "!" + (string.IsNullOrEmpty(status) ? "none" : status);
        }
    }
}
