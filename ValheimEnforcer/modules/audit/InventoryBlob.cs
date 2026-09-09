using System;
using System.Collections.Generic;
using UnityEngine;
using Logger = ValheimEnforcer.Logger;

namespace ValheimEnforcer.modules.audit {

    /// <summary>
    /// Reads the item list out of a container's ZDO without touching the scene.
    ///
    /// A container stores its contents as base64 of an Inventory.Save package in the "items" ZDO string
    /// (Container.Save). The obvious way to read that back is Inventory.Load - but Load resolves every entry
    /// through ObjectDB.GetItemPrefab and Instantiates then Destroys a GameObject per item, which is far too
    /// expensive to do inside the ZDO stream and pointless when all that is wanted is names and counts. This
    /// walks the same wire format directly, mirroring Inventory.Load's version handling (100-109) field for
    /// field, and allocates nothing but the result.
    ///
    /// Every failure returns null rather than a partial answer. A container this cannot parse is skipped:
    /// half-decoding a blob would produce phantom "took everything" events, which is a far worse outcome for
    /// a moderation tool than a gap.
    /// </summary>
    internal static class InventoryBlob {

        /// <summary>
        /// Ceiling on how many entries a blob may claim. Larger than any real container (a chest is 4x6, and
        /// the biggest modded ones are far below this), so it only ever trips on a corrupt or hostile length
        /// prefix - which would otherwise have us loop allocating strings until the server died.
        /// </summary>
        private const int MaxItems = 4096;

        // Valheim's Version.Item values, named here rather than read from the game on purpose: the point of the
        // ceiling is that a format this was never written against is refused, and reading the game's live value
        // would hand a new layout to old parsing code instead.
        private const int ItemVersionAbandonedDN = 107;    // Version.Item.AbandonedDN - adds m_cheated
        private const int ItemVersionSmaller = 108;        // Version.Item.Smaller - the packed layout starts here
        private const int ItemVersionChunksNCheats = 109;  // Version.Item.ChunksNCheats - m_cheated again
        private const int ItemVersionMax = ItemVersionChunksNCheats;

        /// <summary>One stack line as recorded, aggregated by prefab and quality.</summary>
        internal sealed class Slot {
            internal string Prefab;
            internal int Quality;
            internal int Qty;
            /// <summary>Crafter of the first entry seen for this key; enough to say who made the stack.</summary>
            internal long CrafterId;
            internal string CrafterName;

            internal string Key { get { return Prefab + "|" + Quality; } }
        }

        /// <summary>
        /// A fingerprint of a container's raw item blob, for answering "did this change" without keeping the
        /// blob.
        ///
        /// The audit sees a container's ZDO every time anything about it is replicated, and for a cart or a
        /// ship being moved that is twenty times a second with the contents untouched - so the cheap
        /// "unchanged" answer has to stay cheap, which rules out parsing every time. Holding the previous
        /// base64 to compare against was the obvious alternative and cost a multi-kilobyte string per tracked
        /// container; this is eight bytes and reads the same characters the comparison would have.
        ///
        /// FNV-1a. Not a checksum against tampering - nothing here is a security decision - just a spread wide
        /// enough that two different blobs colliding is not a thing that happens. A collision would cost one
        /// unrecorded container change, and the next change to that container is measured against the state
        /// this one established, so it cannot compound.
        /// </summary>
        internal static long HashOf(string raw) {
            if (string.IsNullOrEmpty(raw)) { return 0L; }
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < raw.Length; i++) {
                hash ^= raw[i];
                hash *= 1099511628211UL;
            }
            return unchecked((long)hash);
        }

        /// <summary>
        /// The contents of a container, keyed by prefab and quality with stacks summed.
        ///
        /// Aggregated rather than returned per entry on purpose: an inventory holds several stacks of the
        /// same thing and the game splits and merges them constantly, so a per-entry diff would report
        /// tidying a chest as a flurry of takes and stores. Summing by prefab and quality makes a rearrange
        /// produce no difference at all, which is exactly the behaviour a moderator needs.
        /// </summary>
        internal static Dictionary<string, Slot> Parse(string base64) {
            if (string.IsNullOrEmpty(base64)) { return new Dictionary<string, Slot>(StringComparer.Ordinal); }

            try {
                ZPackage pkg = new ZPackage(base64);
                int version = pkg.ReadInt();

                // Versions below 100 are not a format this has ever seen; above ItemVersionMax is a game newer
                // than the one this was written against. Guessing at either is how a parser starts inventing
                // events.
                if (version < 100 || version > ItemVersionMax) {
                    Logger.LogDebug($"Audit skipped a container blob with unknown inventory version {version}.");
                    return null;
                }

                // Two layouts, and even the length prefix differs between them: flat is int-counted, packed is
                // ushort-counted.
                int count = version >= ItemVersionSmaller ? pkg.ReadUShort() : pkg.ReadInt();
                if (count < 0 || count > MaxItems) {
                    Logger.LogDebug($"Audit skipped a container blob claiming {count} items.");
                    return null;
                }

                Dictionary<string, Slot> slots = new Dictionary<string, Slot>(StringComparer.Ordinal);
                for (int i = 0; i < count; i++) {
                    Slot entry = version >= ItemVersionSmaller ? ReadPacked(pkg, version) : ReadFlat(pkg, version);
                    if (entry == null) { return null; }

                    // Vanilla writes "" - or, packed, no prefab at all - for an item whose prefab was lost, and
                    // skips it on load; so do we.
                    if (string.IsNullOrEmpty(entry.Prefab)) { continue; }

                    Slot slot;
                    string key = entry.Key;
                    if (!slots.TryGetValue(key, out slot)) {
                        slot = new Slot {
                            Prefab = entry.Prefab, Quality = entry.Quality,
                            CrafterId = entry.CrafterId, CrafterName = entry.CrafterName
                        };
                        slots[key] = slot;
                    }
                    slot.Qty += entry.Qty;
                }
                return slots;
            } catch (Exception e) {
                // Includes running off the end of a truncated package. Nothing here may throw into the ZDO
                // stream, and an unreadable container is a gap in the record, not a reason to stop.
                Logger.LogDebug($"Audit could not parse a container blob: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// One entry in the layout Valheim wrote up to and including <see cref="ItemVersionAbandonedDN"/>: a
        /// flat run of fields led by the prefab name. Mirrors Inventory.LoadOld. Null means the entry was not
        /// plausible, which fails the whole blob.
        /// </summary>
        private static Slot ReadFlat(ZPackage pkg, int version) {
            string name = pkg.ReadString();
            int stack = pkg.ReadInt();
            pkg.ReadSingle();      // m_durability
            pkg.ReadVector2i();    // m_gridPos - a move is not a take, so this is read past deliberately
            pkg.ReadBool();        // m_equipped

            int quality = 1;
            if (version >= 101) { quality = pkg.ReadInt(); }
            if (version >= 102) { pkg.ReadInt(); }   // m_variant

            long crafterId = 0L;
            string crafterName = "";
            if (version >= 103) {
                crafterId = pkg.ReadLong();
                crafterName = pkg.ReadString();
            }

            if (version >= 104) {
                int customCount = pkg.ReadInt();
                if (customCount < 0 || customCount > MaxItems) { return null; }
                for (int c = 0; c < customCount; c++) {
                    pkg.ReadString();
                    pkg.ReadString();
                }
            }

            if (version >= 105) { pkg.ReadInt(); }    // m_worldLevel
            if (version >= 106) { pkg.ReadBool(); }   // m_pickedUp
            // Vanilla reads m_cheated for AbandonedDN and again from ChunksNCheats on. Only the first is
            // reachable here - everything from Smaller up is written in the packed layout instead.
            if (version == ItemVersionAbandonedDN) { pkg.ReadBool(); }

            return new Slot { Prefab = name, Quality = quality, Qty = stack, CrafterId = crafterId, CrafterName = crafterName };
        }

        /// <summary>
        /// One entry in the layout from <see cref="ItemVersionSmaller"/> on, mirroring ItemDrop.ItemData.Load:
        /// the small fields are single bytes, every field still at its default is elided behind a bit in a flag
        /// byte, and the prefab arrives as a stable hash rather than as a name.
        /// </summary>
        private static Slot ReadPacked(ZPackage pkg, int version) {
            pkg.ReadInt();      // m_durability, in hundredths
            pkg.ReadByte();     // m_gridPos.x - a move is not a take, so this is read past deliberately
            pkg.ReadByte();     // m_gridPos.y
            pkg.ReadByte();     // m_worldLevel

            byte flags = pkg.ReadByte();
            int quality = (flags & 4) == 0 ? 1 : pkg.ReadUShort();
            int stack = (flags & 8) == 0 ? 1 : pkg.ReadUShort();
            if ((flags & 16) != 0) { pkg.ReadInt(); }   // m_variant

            long crafterId = 0L;
            string crafterName = "";
            if ((flags & 32) != 0) {
                crafterId = pkg.ReadLong();
                crafterName = pkg.ReadString();
            }

            // No prefab bit means nothing was recorded to resolve. Vanilla discards such an entry on load, so it
            // is not part of the contents and must not show up in a diff.
            int prefabHash = (flags & 64) != 0 ? pkg.ReadInt() : 0;

            if ((flags & 128) != 0) {
                int customCount = pkg.ReadNumItems();
                if (customCount < 0 || customCount > MaxItems) { return null; }
                for (int c = 0; c < customCount; c++) {
                    pkg.ReadString();
                    pkg.ReadString();
                }
            }

            if (version >= ItemVersionChunksNCheats) { pkg.ReadByte(); }   // m_cheated, as its own flag byte

            return new Slot {
                Prefab = prefabHash == 0 ? null : NameOf(prefabHash),
                Quality = quality, Qty = stack, CrafterId = crafterId, CrafterName = crafterName
            };
        }

        /// <summary>
        /// The prefab name behind a stable hash, for the packed layout that no longer records names.
        ///
        /// Both lookups are plain dictionary reads - unlike ObjectDB.GetItemPrefab(string) followed by
        /// Instantiate, which is the cost this whole file exists to avoid - so this stays affordable inside the
        /// ZDO stream. ObjectDB is asked first because these are items; ZNetScene covers a container holding
        /// something ObjectDB does not list.
        ///
        /// A hash that resolves nowhere still has to produce a stable key, or one unknown item would read as a
        /// take on one scan and a store on the next, so it falls back to the number it was given.
        /// </summary>
        private static string NameOf(int prefabHash) {
            GameObject prefab = null;
            if (ObjectDB.instance != null) { ObjectDB.instance.TryGetItemPrefab(prefabHash, out prefab); }
            if (prefab == null && ZNetScene.instance != null) { prefab = ZNetScene.instance.GetPrefab(prefabHash); }
            return prefab != null ? prefab.name : "#" + prefabHash;
        }

        /// <summary>
        /// What changed between two parses of the same container: positive quantities were added to the
        /// container, negative ones were taken out of it.
        ///
        /// Keys present on one side only are handled by walking both, so an item type appearing for the first
        /// time and one disappearing entirely are both reported.
        /// </summary>
        internal static List<Change> Diff(Dictionary<string, Slot> before, Dictionary<string, Slot> after) {
            List<Change> changes = new List<Change>();
            if (before == null || after == null) { return changes; }

            foreach (KeyValuePair<string, Slot> entry in after) {
                int was = before.TryGetValue(entry.Key, out Slot old) ? old.Qty : 0;
                int delta = entry.Value.Qty - was;
                if (delta != 0) { changes.Add(new Change { Slot = entry.Value, Delta = delta }); }
            }
            foreach (KeyValuePair<string, Slot> entry in before) {
                if (after.ContainsKey(entry.Key)) { continue; } // already accounted for above
                changes.Add(new Change { Slot = entry.Value, Delta = -entry.Value.Qty });
            }
            return changes;
        }

        internal sealed class Change {
            internal Slot Slot;
            /// <summary>Positive when the container gained, negative when it lost.</summary>
            internal int Delta;
        }
    }
}
