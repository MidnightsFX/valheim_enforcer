using System;
using System.Collections.Generic;
using Logger = ValheimEnforcer.Logger;

namespace ValheimEnforcer.modules.audit {

    /// <summary>
    /// Reads the item list out of a container's ZDO without touching the scene.
    ///
    /// A container stores its contents as base64 of an Inventory.Save package in the "items" ZDO string
    /// (Container.Save). The obvious way to read that back is Inventory.Load - but Load resolves every entry
    /// through ObjectDB.GetItemPrefab and Instantiates then Destroys a GameObject per item, which is far too
    /// expensive to do inside the ZDO stream and pointless when all that is wanted is names and counts. This
    /// walks the same wire format directly, mirroring Inventory.Load's version handling (100-106) field for
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
                int count = pkg.ReadInt();

                // Versions below 100 are not a format this has ever seen; above 106 is a game newer than the
                // one this was written against. Guessing at either is how a parser starts inventing events.
                if (version < 100 || version > 106) {
                    Logger.LogDebug($"Audit skipped a container blob with unknown inventory version {version}.");
                    return null;
                }
                if (count < 0 || count > MaxItems) {
                    Logger.LogDebug($"Audit skipped a container blob claiming {count} items.");
                    return null;
                }

                Dictionary<string, Slot> slots = new Dictionary<string, Slot>(StringComparer.Ordinal);
                for (int i = 0; i < count; i++) {
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

                    // Vanilla writes "" for an item whose prefab was lost and skips it on load; so do we.
                    if (string.IsNullOrEmpty(name)) { continue; }

                    Slot slot;
                    string key = name + "|" + quality;
                    if (!slots.TryGetValue(key, out slot)) {
                        slot = new Slot { Prefab = name, Quality = quality, CrafterId = crafterId, CrafterName = crafterName };
                        slots[key] = slot;
                    }
                    slot.Qty += stack;
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
