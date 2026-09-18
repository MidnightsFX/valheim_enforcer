using System;
using System.Collections.Generic;
using System.IO;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.common {

    /// <summary>
    /// The incremental character update as plain package fields, instead of as a YAML document.
    ///
    /// A delta is the most frequent thing the server receives from this mod - one per player per rate-limit
    /// window - and it is parsed on the main thread, because what it says has to be bound to the connection it
    /// arrived on before anything else happens. As YAML that parse cost 152 KB of garbage for a 2.3 KB payload
    /// (measured on a real character: twenty-six skills, three effects, three items), which at a hundred
    /// players is around a megabyte a second of main-thread allocation to read a few hundred numbers. YAML earns
    /// its keep in the files an admin opens; on a wire where both ends are this mod it is pure cost.
    ///
    /// Nothing about what a delta means changes. <see cref="Read"/> produces the same
    /// <see cref="DeltaSummaryUpdate"/> the YAML handler does and both hand it to the same code, null for null:
    /// a null <see cref="DeltaSummaryUpdate.Foods"/> still means "not reporting", not "ate nothing".
    ///
    /// Negotiated, not assumed. NetworkCompatibility here is strict on the minor version only, so a client and a
    /// server a patch apart can meet, and a server that predates this has no handler for it - a routed RPC
    /// nobody registered is dropped without a word, which would leave that server's copy of every character
    /// going stale between full saves. So the server says it understands this (one flag appended to the
    /// character payload every client is sent at connect) and a client sends it only once it has been told so.
    /// The YAML handler stays registered for everybody else.
    /// </summary>
    internal static class DeltaWire {

        /// <summary>
        /// The layout version, written first. Fields may only ever be APPENDED to a layout: a reader takes the
        /// fields it knows and ignores whatever follows, so a client one patch ahead is still understood.
        /// </summary>
        internal const byte LayoutVersion = 1;

        /// <summary>
        /// Ceiling on any one count in a payload. Far above anything real - a full inventory is a few dozen items
        /// and the largest custom data set seen is in the low hundreds of keys - so it only ever trips on a
        /// corrupt or hostile count, which would otherwise have the reader looping until the package ran dry.
        /// </summary>
        private const int MaxEntries = 8192;

        // ---- Negotiation ---------------------------------------------------------------------------------

        /// <summary>Bit in the capability flags a server appends to its character payloads.</summary>
        internal const int CapabilityBinaryDelta = 1;

        private static bool serverAcceptsBinary;

        /// <summary>Client side: whether the server this session is connected to has said it reads this form.</summary>
        internal static bool ServerAcceptsBinary { get { return serverAcceptsBinary; } }

        /// <summary>Client side: the capability flags off a character payload. Absent on an older server, in
        /// which case this is never called and the client keeps sending YAML.</summary>
        internal static void NoteServerCapabilities(int capabilities) {
            serverAcceptsBinary = (capabilities & CapabilityBinaryDelta) != 0;
        }

        /// <summary>The session ended or a new one is starting; what the last server said no longer applies.</summary>
        internal static void ForgetServer() {
            serverAcceptsBinary = false;
        }

        /// <summary>Server side: the flags to advertise. Zero when BinaryDeltaUpdates is off, which is all it
        /// takes to have every client go back to the YAML form.</summary>
        internal static int ServerCapabilities() {
            bool binary = ValConfig.BinaryDeltaUpdates != null && ValConfig.BinaryDeltaUpdates.Value;
            return binary ? CapabilityBinaryDelta : 0;
        }

        // ---- Writing -------------------------------------------------------------------------------------

        internal static ZPackage Write(DeltaSummaryUpdate delta) {
            ZPackage pkg = new ZPackage();
            pkg.Write(LayoutVersion);
            WriteString(pkg, delta.Name);
            WriteString(pkg, delta.HostID);
            pkg.Write(delta.PlayerID);
            pkg.Write((byte)delta.DisconnectionState);

            if (delta.ItemModifications == null) {
                pkg.Write(-1);
            } else {
                pkg.Write(delta.ItemModifications.Count);
                foreach (ItemDelta change in delta.ItemModifications) {
                    pkg.Write(change != null);
                    if (change == null) { continue; }
                    pkg.Write((byte)change.Op);
                    WriteItem(pkg, change.Item);
                }
            }

            WriteStringMap(pkg, delta.PlayerCustomDataModifications);

            if (delta.RemovedCustomDataKeys == null) {
                pkg.Write(-1);
            } else {
                pkg.Write(delta.RemovedCustomDataKeys.Count);
                foreach (string key in delta.RemovedCustomDataKeys) { WriteString(pkg, key); }
            }

            if (delta.SkillLevels == null) {
                pkg.Write(-1);
            } else {
                pkg.Write(delta.SkillLevels.Count);
                foreach (KeyValuePair<Skills.SkillType, float> skill in delta.SkillLevels) {
                    pkg.Write((int)skill.Key);
                    pkg.Write(skill.Value);
                }
            }

            if (delta.ActiveCharacterEffects == null) {
                pkg.Write(-1);
            } else {
                pkg.Write(delta.ActiveCharacterEffects.Count);
                foreach (KeyValuePair<string, PackedStatusEffect> effect in delta.ActiveCharacterEffects) {
                    WriteString(pkg, effect.Key);
                    PackedStatusEffect se = effect.Value;
                    pkg.Write(se != null);
                    if (se == null) { continue; }
                    pkg.Write(se.TimeRemaining);
                    pkg.Write(se.Time);
                    pkg.Write(se.NameHash);
                    pkg.Write(se.DamageLeft);
                    pkg.Write(se.DamagePerHit);
                    pkg.Write(se.FireDamageLeft);
                    pkg.Write(se.FireDamagePerHit);
                    pkg.Write(se.SpiritDamageLeft);
                    pkg.Write(se.SpiritDamagePerHit);
                }
            }

            WriteString(pkg, delta.GuardianPower);

            if (delta.Foods == null) {
                pkg.Write(-1);
            } else {
                pkg.Write(delta.Foods.Count);
                foreach (PackedFood food in delta.Foods) {
                    pkg.Write(food != null);
                    if (food == null) { continue; }
                    WriteString(pkg, food.Name);
                    pkg.Write(food.Time);
                }
            }
            return pkg;
        }

        private static void WriteItem(ZPackage pkg, PackedItem item) {
            pkg.Write(item != null);
            if (item == null) { return; }
            WriteString(pkg, item.prefabName);
            pkg.Write(item.m_stack);
            pkg.Write(item.m_durability);
            pkg.Write(item.m_quality);
            pkg.Write(item.m_variant);
            pkg.Write(item.m_worldlevel);
            pkg.Write(item.m_crafterID);
            WriteString(pkg, item.m_crafterName);
            WriteStringMap(pkg, item.m_customdata);
            pkg.Write(item.m_equipped);
            pkg.Write(item.m_gridpos.x);
            pkg.Write(item.m_gridpos.y);
            // Never set on an item in a delta today - they belong to confiscation records - but carried so the
            // two forms of a delta cannot come to disagree about an item if that ever changes.
            WriteString(pkg, item.confiscatedReason);
            pkg.Write(item.confiscatedTime.Ticks);
            pkg.Write((byte)item.confiscatedTime.Kind);
            WriteString(pkg, item.confiscationId);
        }

        /// <summary>A string that may be null. ZPackage.Write(string) throws on one, and null is a value here.</summary>
        private static void WriteString(ZPackage pkg, string value) {
            pkg.Write(value != null);
            if (value != null) { pkg.Write(value); }
        }

        private static void WriteStringMap(ZPackage pkg, Dictionary<string, string> map) {
            if (map == null) {
                pkg.Write(-1);
                return;
            }
            pkg.Write(map.Count);
            foreach (KeyValuePair<string, string> entry in map) {
                pkg.Write(entry.Key); // a dictionary key is never null
                WriteString(pkg, entry.Value);
            }
        }

        // ---- Reading -------------------------------------------------------------------------------------

        /// <summary>
        /// Reads a delta off a package. Throws on anything that is not one - a truncated package, a layout this
        /// build has never heard of, a count that cannot be real - and the caller refuses the update, exactly
        /// as it refuses YAML that will not parse.
        /// </summary>
        internal static DeltaSummaryUpdate Read(ZPackage pkg) {
            byte version = pkg.ReadByte();
            if (version < LayoutVersion) { throw new InvalidDataException($"unknown delta layout {version}"); }

            DeltaSummaryUpdate delta = new DeltaSummaryUpdate();
            delta.Name = ReadString(pkg);
            delta.HostID = ReadString(pkg);
            delta.PlayerID = pkg.ReadLong();
            delta.DisconnectionState = (DisconnectionState)pkg.ReadByte();

            int items = ReadCount(pkg, "item change");
            if (items < 0) {
                delta.ItemModifications = null;
            } else {
                delta.ItemModifications = new List<ItemDelta>(items);
                for (int i = 0; i < items; i++) {
                    if (!pkg.ReadBool()) { delta.ItemModifications.Add(null); continue; }
                    ItemDelta change = new ItemDelta();
                    change.Op = (ItemDeltaChangeType)pkg.ReadByte();
                    change.Item = ReadItem(pkg);
                    delta.ItemModifications.Add(change);
                }
            }

            delta.PlayerCustomDataModifications = ReadStringMap(pkg, "custom data change");

            int removed = ReadCount(pkg, "removed custom data key");
            if (removed < 0) {
                delta.RemovedCustomDataKeys = null;
            } else {
                delta.RemovedCustomDataKeys = new List<string>(removed);
                for (int i = 0; i < removed; i++) { delta.RemovedCustomDataKeys.Add(ReadString(pkg)); }
            }

            int skills = ReadCount(pkg, "skill");
            if (skills < 0) {
                delta.SkillLevels = null;
            } else {
                delta.SkillLevels = new Dictionary<Skills.SkillType, float>(skills);
                for (int i = 0; i < skills; i++) {
                    Skills.SkillType skill = (Skills.SkillType)pkg.ReadInt();
                    delta.SkillLevels[skill] = pkg.ReadSingle();
                }
            }

            int effects = ReadCount(pkg, "status effect");
            if (effects < 0) {
                delta.ActiveCharacterEffects = null;
            } else {
                delta.ActiveCharacterEffects = new Dictionary<string, PackedStatusEffect>(effects);
                for (int i = 0; i < effects; i++) {
                    string name = ReadString(pkg);
                    PackedStatusEffect se = null;
                    if (pkg.ReadBool()) {
                        se = new PackedStatusEffect();
                        se.TimeRemaining = pkg.ReadSingle();
                        se.Time = pkg.ReadSingle();
                        se.NameHash = pkg.ReadInt();
                        se.DamageLeft = pkg.ReadSingle();
                        se.DamagePerHit = pkg.ReadSingle();
                        se.FireDamageLeft = pkg.ReadSingle();
                        se.FireDamagePerHit = pkg.ReadSingle();
                        se.SpiritDamageLeft = pkg.ReadSingle();
                        se.SpiritDamagePerHit = pkg.ReadSingle();
                    }
                    if (name == null) { throw new InvalidDataException("a status effect with no name"); }
                    delta.ActiveCharacterEffects[name] = se;
                }
            }

            delta.GuardianPower = ReadString(pkg);

            int foods = ReadCount(pkg, "food");
            if (foods < 0) {
                delta.Foods = null;
            } else {
                delta.Foods = new List<PackedFood>(foods);
                for (int i = 0; i < foods; i++) {
                    if (!pkg.ReadBool()) { delta.Foods.Add(null); continue; }
                    PackedFood food = new PackedFood();
                    food.Name = ReadString(pkg);
                    food.Time = pkg.ReadSingle();
                    delta.Foods.Add(food);
                }
            }
            return delta;
        }

        private static PackedItem ReadItem(ZPackage pkg) {
            if (!pkg.ReadBool()) { return null; }
            PackedItem item = new PackedItem();
            item.prefabName = ReadString(pkg);
            item.m_stack = pkg.ReadInt();
            item.m_durability = pkg.ReadSingle();
            item.m_quality = pkg.ReadInt();
            item.m_variant = pkg.ReadInt();
            item.m_worldlevel = pkg.ReadInt();
            item.m_crafterID = pkg.ReadLong();
            item.m_crafterName = ReadString(pkg);
            item.m_customdata = ReadStringMap(pkg, "item custom data entry");
            item.m_equipped = pkg.ReadBool();
            int x = pkg.ReadInt();
            int y = pkg.ReadInt();
            item.m_gridpos = new Vector2i(x, y);
            item.confiscatedReason = ReadString(pkg);
            long ticks = pkg.ReadLong();
            DateTimeKind kind = (DateTimeKind)pkg.ReadByte();
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks || !Enum.IsDefined(typeof(DateTimeKind), kind)) {
                throw new InvalidDataException("an item carrying a time that is not a time");
            }
            item.confiscatedTime = new DateTime(ticks, kind);
            item.confiscationId = ReadString(pkg);
            return item;
        }

        private static string ReadString(ZPackage pkg) {
            return pkg.ReadBool() ? pkg.ReadString() : null;
        }

        private static Dictionary<string, string> ReadStringMap(ZPackage pkg, string what) {
            int count = ReadCount(pkg, what);
            if (count < 0) { return null; }
            Dictionary<string, string> map = new Dictionary<string, string>(count);
            for (int i = 0; i < count; i++) {
                string key = pkg.ReadString();
                map[key] = ReadString(pkg);
            }
            return map;
        }

        /// <summary>A count, or -1 for "the list itself was null". Anything else out of range is not a delta.</summary>
        private static int ReadCount(ZPackage pkg, string what) {
            int count = pkg.ReadInt();
            if (count < -1 || count > MaxEntries) { throw new InvalidDataException($"an implausible {what} count ({count})"); }
            return count;
        }
    }
}
