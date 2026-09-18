using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.recovery {

    /// <summary>
    /// The sealed snapshot a client holds on the server's behalf, and the key that seals it.
    ///
    /// The shape of the problem: when a server crashes and comes back from an older save, everything since
    /// that save is gone and the server has no copy of it - but every connected client does, because the
    /// server sent each of them their own character moments before. Handing that back is only safe if the
    /// server can prove the thing it is being handed is its own and is newer than what it holds. So the server
    /// seals a snapshot, the client stores an opaque copy it can neither read nor alter, and the server
    /// unseals it on the way back in.
    ///
    /// Encrypt-then-MAC, which is the only ordering that is safe: the tag covers the header AND the
    /// ciphertext, and it is checked before a single byte is decrypted. A client that flips a bit anywhere is
    /// refused at the tag rather than handed to the AES implementation.
    ///
    /// The header is deliberately in the clear. The client files the blob by world and character, and the
    /// server checks who a blob claims to belong to before spending anything on decrypting it - both need to
    /// read those fields, and neither is secret. What is secret is the character itself, which is the
    /// server's authoritative record of somebody's inventory and progression and is none of the holder's
    /// business.
    ///
    /// Pure, and free of ZNet, Unity and ConfigEntry, so the CharacterStore worker can seal on its own thread
    /// and so the whole format can be exercised headlessly.
    /// </summary>
    internal static class RecoverySeal {

        /// <summary>Marks a payload as one of ours before anything else is read.</summary>
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("VERC");

        /// <summary>Bumped only for a change that an older build could not read. Nothing reads a blob it did
        /// not write - the key is per server - so there is no cross-version compatibility to keep, only a
        /// clear refusal.</summary>
        internal const int FormatVersion = 1;

        /// <summary>Everything a blob is allowed to expand into once unsealed. A character save is hundreds
        /// of kilobytes; this is far above any real one and far below what would hurt to allocate.</summary>
        internal const int MaxPlaintextBytes = 8 * 1024 * 1024;

        private const int IvBytes = 16;
        private const int TagBytes = 32;

        /// <summary>
        /// The key material, derived once and then immutable.
        ///
        /// One master secret, three labelled derivations. Using the same bytes for encryption and
        /// authentication is the classic way to weaken both, and the key id has to be publishable - it goes
        /// in the clear inside every blob so an admin can tell "this is from before the key was rotated"
        /// apart from "this has been tampered with" - so it gets a label of its own rather than being a hash
        /// of the secret.
        ///
        /// Immutable on purpose: a worker thread holding one keeps reading a consistent snapshot even while
        /// an admin rotates the key underneath it.
        /// </summary>
        internal sealed class Keys {
            internal readonly byte[] Enc;
            internal readonly byte[] Mac;
            internal readonly string Id;

            internal Keys(byte[] master) {
                if (master == null || master.Length < 32) {
                    throw new ArgumentException("A recovery key must be at least 32 bytes.", nameof(master));
                }
                Enc = Derive(master, "ve-recovery-enc");
                Mac = Derive(master, "ve-recovery-mac");
                byte[] id = Derive(master, "ve-recovery-id");
                Id = modules.mods.PluginHasher.ToHex(id).Substring(0, 16);
            }

            private static byte[] Derive(byte[] master, string label) {
                using (SHA256 sha = SHA256.Create()) {
                    byte[] tag = Encoding.UTF8.GetBytes(label);
                    byte[] input = new byte[master.Length + tag.Length];
                    Buffer.BlockCopy(master, 0, input, 0, master.Length);
                    Buffer.BlockCopy(tag, 0, input, master.Length, tag.Length);
                    return sha.ComputeHash(input);
                }
            }
        }

        /// <summary>What a blob says about itself, before anything is decrypted.</summary>
        internal sealed class Header {
            internal int Version;
            internal string KeyId;
            internal long WorldUid;
            internal string AccountId;
            internal string CharacterName;
            internal long Sequence;
            internal DateTime SealedUtc;

            internal string Describe() {
                return $"{CharacterName} ({AccountId}), sequence {Sequence}, sealed {SealedUtc:u}";
            }
        }

        /// <summary>Why a blob was refused. Each one is a different thing to tell an admin.</summary>
        internal enum Verdict {
            Ok,
            NotOurs,        // wrong magic, or a format this build does not read
            WrongKey,       // sealed under a key that is not ours - a rotation, or another server
            Tampered,       // the tag does not verify
            Unreadable,     // the tag verified and the contents still would not parse
        }

        // ---------------------------------------------------------------------------------------------
        // Sealing
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Seals a character snapshot. <paramref name="payload"/> is the character's YAML; the map is
        /// deliberately not part of this - see the note on <see cref="Open"/>.
        /// </summary>
        internal static byte[] Seal(Keys keys, Header header, string payload) {
            if (keys == null) { throw new ArgumentNullException(nameof(keys)); }
            if (header == null) { throw new ArgumentNullException(nameof(header)); }

            byte[] plaintext = Gzip.CompressText(payload ?? "");
            byte[] iv = new byte[IvBytes];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider()) {
                rng.GetBytes(iv);
            }

            byte[] ciphertext;
            using (Aes aes = Aes.Create()) {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = keys.Enc;
                aes.IV = iv;
                using (ICryptoTransform encryptor = aes.CreateEncryptor())
                using (MemoryStream output = new MemoryStream()) {
                    using (CryptoStream crypto = new CryptoStream(output, encryptor, CryptoStreamMode.Write)) {
                        crypto.Write(plaintext, 0, plaintext.Length);
                    }
                    ciphertext = output.ToArray();
                }
            }

            byte[] body = WriteBody(keys.Id, header, iv, ciphertext);
            byte[] tag;
            using (HMACSHA256 hmac = new HMACSHA256(keys.Mac)) {
                tag = hmac.ComputeHash(body);
            }

            byte[] sealed_ = new byte[body.Length + tag.Length];
            Buffer.BlockCopy(body, 0, sealed_, 0, body.Length);
            Buffer.BlockCopy(tag, 0, sealed_, body.Length, tag.Length);
            return sealed_;
        }

        // Everything the tag covers: magic, version, the whole header, the IV and the ciphertext. Written
        // through a BinaryWriter so the byte order is the same on every platform the game runs on, and so
        // every field is length-prefixed - a header field that could run into the next one is a header a
        // client could shift by choosing a clever character name.
        private static byte[] WriteBody(string keyId, Header header, byte[] iv, byte[] ciphertext) {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, new UTF8Encoding(false))) {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(keyId ?? "");
                writer.Write(header.WorldUid);
                writer.Write(header.AccountId ?? "");
                writer.Write(header.CharacterName ?? "");
                writer.Write(header.Sequence);
                writer.Write(header.SealedUtc.Ticks);
                writer.Write(iv.Length);
                writer.Write(iv);
                writer.Write(ciphertext.Length);
                writer.Write(ciphertext);
                writer.Flush();
                return stream.ToArray();
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Reading
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Reads the header WITHOUT verifying anything. Only ever for deciding where to file a blob or what
        /// to print about one - never for deciding to trust it. A caller acting on this is acting on what a
        /// client wrote.
        /// </summary>
        internal static bool TryPeek(byte[] blob, out Header header) {
            header = null;
            if (blob == null || blob.Length < Magic.Length + TagBytes) { return false; }
            try {
                using (MemoryStream stream = new MemoryStream(blob, false))
                using (BinaryReader reader = new BinaryReader(stream, new UTF8Encoding(false))) {
                    byte[] magic = reader.ReadBytes(Magic.Length);
                    for (int i = 0; i < Magic.Length; i++) {
                        if (magic[i] != Magic[i]) { return false; }
                    }
                    Header read = new Header { Version = reader.ReadInt32() };
                    if (read.Version != FormatVersion) { return false; }
                    read.KeyId = reader.ReadString();
                    read.WorldUid = reader.ReadInt64();
                    read.AccountId = reader.ReadString();
                    read.CharacterName = reader.ReadString();
                    read.Sequence = reader.ReadInt64();
                    read.SealedUtc = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
                    header = read;
                    return true;
                }
            } catch (Exception) {
                return false;
            }
        }

        /// <summary>
        /// Verifies a blob and returns what it holds.
        ///
        /// The payload is the character's YAML and nothing else. The map is not in here, and that is a
        /// decision rather than an omission: a server rollback does not touch the client's own map - the
        /// client has been holding it all along - so sealing a megabyte of it into every snapshot would be
        /// bandwidth spent to recover something that was never lost.
        ///
        /// <paramref name="payload"/> is only set when the verdict is Ok. Nothing is decrypted before the tag
        /// verifies.
        /// </summary>
        internal static Verdict Open(Keys keys, byte[] blob, out Header header, out string payload) {
            payload = null;
            header = null;
            if (keys == null || blob == null) { return Verdict.NotOurs; }
            if (!TryPeek(blob, out header)) { return Verdict.NotOurs; }

            if (blob.Length <= TagBytes) { return Verdict.NotOurs; }
            int bodyLength = blob.Length - TagBytes;

            // Checked before the tag so a rotated key is reported as a rotated key rather than as tampering,
            // which are very different things to put in front of an admin. It is not a security decision -
            // the tag below is - so an attacker writing our key id into their blob gains nothing.
            if (!string.Equals(header.KeyId, keys.Id, StringComparison.Ordinal)) { return Verdict.WrongKey; }

            byte[] expected;
            using (HMACSHA256 hmac = new HMACSHA256(keys.Mac)) {
                expected = hmac.ComputeHash(blob, 0, bodyLength);
            }
            if (!FixedTimeEquals(expected, blob, bodyLength)) { return Verdict.Tampered; }

            try {
                // Re-read from the verified bytes rather than trusting the peek above, so everything acted on
                // downstream has been under the tag.
                using (MemoryStream stream = new MemoryStream(blob, 0, bodyLength, false))
                using (BinaryReader reader = new BinaryReader(stream, new UTF8Encoding(false))) {
                    reader.ReadBytes(Magic.Length);
                    reader.ReadInt32();
                    reader.ReadString();
                    reader.ReadInt64();
                    reader.ReadString();
                    reader.ReadString();
                    reader.ReadInt64();
                    reader.ReadInt64();
                    int ivLength = reader.ReadInt32();
                    if (ivLength != IvBytes) { return Verdict.Unreadable; }
                    byte[] iv = reader.ReadBytes(ivLength);
                    int cipherLength = reader.ReadInt32();
                    if (cipherLength < 0 || cipherLength > bodyLength) { return Verdict.Unreadable; }
                    byte[] ciphertext = reader.ReadBytes(cipherLength);

                    byte[] plaintext;
                    using (Aes aes = Aes.Create()) {
                        aes.KeySize = 256;
                        aes.Mode = CipherMode.CBC;
                        aes.Padding = PaddingMode.PKCS7;
                        aes.Key = keys.Enc;
                        aes.IV = iv;
                        using (ICryptoTransform decryptor = aes.CreateDecryptor())
                        using (MemoryStream input = new MemoryStream(ciphertext, false))
                        using (CryptoStream crypto = new CryptoStream(input, decryptor, CryptoStreamMode.Read))
                        using (MemoryStream output = new MemoryStream()) {
                            byte[] buffer = new byte[8192];
                            int total = 0;
                            int read;
                            while ((read = crypto.Read(buffer, 0, buffer.Length)) > 0) {
                                total += read;
                                if (total > MaxPlaintextBytes) { return Verdict.Unreadable; }
                                output.Write(buffer, 0, read);
                            }
                            plaintext = output.ToArray();
                        }
                    }

                    payload = Gzip.DecompressText(plaintext, MaxPlaintextBytes);
                    return payload == null ? Verdict.Unreadable : Verdict.Ok;
                }
            } catch (Exception e) {
                // The tag already said these bytes are ours and unmodified, so anything failing here is a bug
                // or a corrupt file rather than an attack - but it still must not take the caller down.
                Logger.LogWarning($"A verified recovery snapshot could not be unsealed: {e.GetType().Name}: {e.Message}");
                return Verdict.Unreadable;
            }
        }

        // Compares in time independent of where the first difference is. A tag comparison that returns early
        // leaks, one byte at a time, how much of a guess was right.
        private static bool FixedTimeEquals(byte[] expected, byte[] blob, int offset) {
            if (expected.Length != TagBytes || blob.Length - offset != TagBytes) { return false; }
            int difference = 0;
            for (int i = 0; i < TagBytes; i++) {
                difference |= expected[i] ^ blob[offset + i];
            }
            return difference == 0;
        }
    }
}
