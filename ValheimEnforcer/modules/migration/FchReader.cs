using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;

namespace ValheimEnforcer.modules.migration {

    /// <summary>One inventory entry as it appears on disk, before any prefab resolution.</summary>
    internal sealed class FchItem {
        /// <summary>
        /// Set only by the layouts that record a name. From Version.Item.Smaller on the file carries
        /// <see cref="PrefabHash"/> instead and this is null; exactly one of the two is ever populated.
        /// </summary>
        internal string PrefabName;
        /// <summary>
        /// The prefab's stable hash, for the layouts that no longer write its name. Resolving it needs
        /// ObjectDB, which is the caller's job - see the class remarks on why this file stays Unity-free.
        /// </summary>
        internal int PrefabHash;
        internal int Stack;
        internal float Durability;
        internal Vector2i GridPos;
        internal bool Equipped;
        internal int Quality;
        internal int Variant;
        internal long CrafterID;
        internal string CrafterName = "";
        internal Dictionary<string, string> CustomData;
        internal int WorldLevel;
    }

    /// <summary>The parts of a player profile the enforcer's character store actually models.</summary>
    internal sealed class FchProfile {
        internal string PlayerName;
        internal long PlayerID;
        internal List<FchItem> Items = new List<FchItem>();
        internal Dictionary<Skills.SkillType, float> SkillLevels = new Dictionary<Skills.SkillType, float>();
        internal Dictionary<string, string> CustomData = new Dictionary<string, string>();
    }

    /// <summary>
    /// Forward-only reader for a vanilla <c>.fch</c> player profile. ServerCharacters stores its server-side
    /// characters as untouched vanilla profiles (it calls stock <c>PlayerProfile.SavePlayerToDisk</c>), so this
    /// reads its files as well as Valheim's own.
    ///
    /// Nothing here needs Unity. The Unity dependency people expect is in vanilla's <c>Inventory.Load</c>, which
    /// calls <c>ObjectDB.instance.GetItemPrefab</c> + <c>Object.Instantiate</c> to re-materialize each item -
    /// but only AFTER every field of that item has already been consumed, so the stream position never depends
    /// on prefab resolution. Skipping that step leaves plain managed code that runs headless, and it also reads
    /// strictly more than the game does: vanilla silently drops an item whose prefab no longer resolves.
    ///
    /// From <c>Version.Item.Smaller</c> the file stopped naming prefabs and records a stable hash instead, so
    /// reading "more than the game does" no longer extends to those items - the name simply is not in the file.
    /// The hash is handed to the caller on <see cref="FchItem.PrefabHash"/> rather than resolved here, which is
    /// what keeps this reader Unity-free.
    ///
    /// Two deliberate departures from what vanilla does with the same bytes, both because this is a migration
    /// and a wrong answer is worse than no answer:
    ///  - The SHA512 trailer is verified. <c>PlayerProfile.LoadPlayerDataFromDisk</c> reads it and throws it away.
    ///  - A truncated file is an error. <c>LoadPlayerFromDisk</c> catches the mid-stream EndOfStreamException and
    ///    still returns true, yielding a half-populated profile that looks like a success.
    /// </summary>
    internal static class FchReader {

        // Layouts this reader has been written against, from Valheim's Version.cs. Anything NEWER is refused
        // rather than guessed at: a added field shifts every subsequent read, and importing a desynced parse
        // would write nonsense into a player's character save.
        private const int ProfileVersionMin = 27;  // Version.IsPlayerVersionCompatible lower bound
        private const int ProfileVersionMax = 46;     // Version.c_PlayerVersion (Player.DeepNorth)
        private const int PlayerDataVersionMax = 33;  // Version.c_PlayerDataVersion (PlayerData.ChunkedNorth)
        private const int ItemDataVersionMax = 109;   // Version.c_ItemDataVersion (Item.ChunksNCheats)
        private const int SkillsVersionMax = 2;

        // Individual layout milestones the readers below branch on, by their Version.cs names. Valheim shipped
        // several of these as a one-off version that a later one re-adopted, so they are compared for equality
        // as well as for "at least", exactly as the game's own loaders do.
        private const int ProfileVersionAbandonedDN = 44;    // Version.Player.AbandonedDN
        private const int ProfileVersionDeepNorth = 46;      // Version.Player.DeepNorth
        private const int PlayerDataVersionAbandonedDN = 31; // Version.PlayerData.AbandonedDN
        private const int PlayerDataVersionChunkedNorth = 33;// Version.PlayerData.ChunkedNorth
        private const int ItemVersionAbandonedDN = 107;      // Version.Item.AbandonedDN - adds m_cheated
        private const int ItemVersionSmaller = 108;          // Version.Item.Smaller - the packed layout starts here
        private const int ItemVersionChunksNCheats = 109;    // Version.Item.ChunksNCheats - m_cheated again

        private const int MaxPlausibleHashLength = 1024;

        /// <summary>
        /// Reads a profile off disk. Returns false with a human-readable <paramref name="error"/> for anything
        /// unreadable, unsupported or corrupt - the caller reports it and moves on to the next file.
        /// </summary>
        internal static bool TryRead(string path, out FchProfile profile, out string error) {
            profile = null;
            error = null;
            try {
                if (!TryReadEnvelope(path, out byte[] payload, out error)) { return false; }
                profile = ReadProfile(new ZPackage(payload));
                return true;
            } catch (EndOfStreamException) {
                error = "file ends mid-record (truncated or not a player profile)";
                return false;
            } catch (InvalidDataException e) {
                error = e.Message;
                return false;
            } catch (Exception e) {
                error = $"{e.GetType().Name}: {e.Message}";
                return false;
            }
        }

        // The file wraps the profile package in a plain BinaryWriter frame - written outside any ZPackage by
        // PlayerProfile.SavePlayerToDisk - so it is read with a plain BinaryReader:
        //   int32 dataLength | data | int32 hashLength | SHA512(data)
        private static bool TryReadEnvelope(string path, out byte[] payload, out string error) {
            payload = null;
            error = null;

            using (FileStream stream = File.OpenRead(path))
            using (BinaryReader reader = new BinaryReader(stream)) {
                long fileLength = stream.Length;
                if (fileLength < 8) { error = "file is too small to be a player profile"; return false; }

                int dataLength = reader.ReadInt32();
                if (dataLength <= 0 || dataLength > fileLength) {
                    error = $"declared payload length {dataLength} is not plausible for a {fileLength} byte file";
                    return false;
                }
                byte[] data = reader.ReadBytes(dataLength);
                if (data.Length != dataLength) { error = "file is truncated inside the profile payload"; return false; }

                int hashLength = reader.ReadInt32();
                if (hashLength <= 0 || hashLength > MaxPlausibleHashLength) {
                    error = $"declared checksum length {hashLength} is not plausible";
                    return false;
                }
                byte[] storedHash = reader.ReadBytes(hashLength);
                if (storedHash.Length != hashLength) { error = "file is truncated inside the checksum"; return false; }

                // Matches ZPackage.GenerateHash(), which is SHA512 over the whole payload array.
                using (SHA512 sha = SHA512.Create()) {
                    if (!sha.ComputeHash(data).SequenceEqual(storedHash)) {
                        error = "checksum does not match the payload; the file is corrupt";
                        return false;
                    }
                }

                payload = data;
                return true;
            }
        }

        // Mirrors PlayerProfile.LoadPlayerFromDisk. Everything before m_playerName is skipped, but it has to be
        // skipped in exactly the right shape or the name lands on the wrong bytes.
        private static FchProfile ReadProfile(ZPackage pkg) {
            int version = pkg.ReadInt();
            if (version < ProfileVersionMin) {
                throw new InvalidDataException($"player profile version {version} predates the oldest version Valheim itself loads ({ProfileVersionMin})");
            }
            if (version > ProfileVersionMax) {
                throw new InvalidDataException($"player profile version {version} is newer than this build understands ({ProfileVersionMax}); the mod needs rebuilding against the current game");
            }

            // DeepNorth moved every stat map up here and made the whole block per-profile: an outer loop over
            // profiles, each holding its stat floats followed by nine string->float maps, one of which
            // (enemy stats) is itself a list of maps. Before that the block was a flat run of floats and the
            // maps lived after the start seed, which is where the tail below still reads them from.
            if (version >= ProfileVersionDeepNorth || version == ProfileVersionAbandonedDN) {
                int statCount = pkg.ReadInt();
                int profileCount = pkg.ReadInt();
                for (int i = 0; i < profileCount; i++) {
                    for (int s = 0; s < statCount; s++) { pkg.ReadSingle(); }
                    SkipStringFloatMap(pkg);                  // known worlds
                    SkipStringFloatMap(pkg);                  // known world keys
                    SkipStringFloatMap(pkg);                  // known commands
                    int enemyGroups = pkg.ReadInt();
                    for (int g = 0; g < enemyGroups; g++) { SkipStringFloatMap(pkg); }
                    SkipStringFloatMap(pkg);                  // item pickup stats
                    SkipStringFloatMap(pkg);                  // item craft stats
                    SkipStringFloatMap(pkg);                  // pickable stats
                    SkipStringFloatMap(pkg);                  // food eaten stats
                    SkipStringFloatMap(pkg);                  // pieces placed stats
                }
            } else if (version >= 38) {
                int statCount = pkg.ReadInt();
                for (int i = 0; i < statCount; i++) { pkg.ReadSingle(); }
            } else if (version >= 28) {
                for (int i = 0; i < 4; i++) { pkg.ReadInt(); } // kills, deaths, crafts, builds
            }

            if (version >= 40) { pkg.ReadBool(); } // m_firstSpawn

            int worldCount = pkg.ReadInt();
            for (int i = 0; i < worldCount; i++) {
                pkg.ReadLong();                                     // world uid
                pkg.ReadBool(); pkg.ReadVector3();                  // custom spawn point
                pkg.ReadBool(); pkg.ReadVector3();                  // logout point
                if (version >= 30) { pkg.ReadBool(); pkg.ReadVector3(); } // death point
                pkg.ReadVector3();                                  // home point
                if (version >= 29 && pkg.ReadBool()) { pkg.ReadByteArray(); } // map data
            }

            FchProfile profile = new FchProfile {
                PlayerName = pkg.ReadString(),
                PlayerID = pkg.ReadLong()
            };
            pkg.ReadString(); // m_startSeed

            if (version >= 38) {
                pkg.ReadBool();  // m_usedCheats
                pkg.ReadLong();  // date created
                // These maps moved into the per-profile stats block above as of DeepNorth, so they are only
                // still here on the versions that predate it.
                if (version < ProfileVersionDeepNorth && version != ProfileVersionAbandonedDN) {
                    SkipStringFloatMap(pkg); // known worlds
                    SkipStringFloatMap(pkg); // known world keys
                    SkipStringFloatMap(pkg); // known commands
                    if (version >= 42) {
                        SkipStringFloatMap(pkg); // enemy stats
                        SkipStringFloatMap(pkg); // item pickup stats
                        SkipStringFloatMap(pkg); // item craft stats
                    }
                }
            }

            if (!pkg.ReadBool()) {
                throw new InvalidDataException("profile contains no character data");
            }
            ReadPlayerData(new ZPackage(pkg.ReadByteArray()), profile);
            return profile;
        }

        // Mirrors Player.Load. Only the inventory, skills and custom data are kept; the rest is skipped in
        // place because the blocks are not individually addressable - they have to be walked in order.
        private static void ReadPlayerData(ZPackage pkg, FchProfile profile) {
            int version = pkg.ReadInt();
            if (version > PlayerDataVersionMax) {
                throw new InvalidDataException($"character data version {version} is newer than this build understands ({PlayerDataVersionMax}); the mod needs rebuilding against the current game");
            }

            if (version >= 7) { pkg.ReadSingle(); }  // max health
            pkg.ReadSingle();                        // health
            if (version >= 10) { pkg.ReadSingle(); } // max stamina
            if (version >= 8 && version < 28) { pkg.ReadBool(); }   // legacy first spawn
            if (version >= 20) { pkg.ReadSingle(); } // time since death
            if (version >= 23) { pkg.ReadString(); } // guardian power
            if (version >= 24) { pkg.ReadSingle(); } // guardian power cooldown
            if (version == 2) { pkg.ReadZDOID(); }

            ReadInventory(pkg, profile);

            SkipStringList(pkg);                     // known recipes
            if (version < 15) {
                SkipStringList(pkg);                 // legacy known stations
            } else {
                int stations = pkg.ReadInt();        // known stations: name -> level
                for (int i = 0; i < stations; i++) { pkg.ReadString(); pkg.ReadInt(); }
            }
            SkipStringList(pkg);                     // known materials
            if (version < 19 || version >= 21) { SkipStringList(pkg); } // shown tutorials
            if (version >= 6) { SkipStringList(pkg); }                  // uniques
            if (version >= 9) { SkipStringList(pkg); }                  // trophies
            // ChunkedNorth switched known biomes from the Heightmap.Biome enum to biome names, so the element
            // width changes with it - four bytes per entry before, a length-prefixed string after.
            if (version >= PlayerDataVersionChunkedNorth || version == PlayerDataVersionAbandonedDN) {
                SkipStringList(pkg);
            } else if (version >= 18) {
                int biomes = pkg.ReadInt();
                for (int i = 0; i < biomes; i++) { pkg.ReadInt(); }
            }
            if (version >= 22) {
                int texts = pkg.ReadInt();
                for (int i = 0; i < texts; i++) { pkg.ReadString(); pkg.ReadString(); }
            }
            if (version >= 4) { pkg.ReadString(); pkg.ReadString(); }       // beard, hair
            if (version >= 5) { pkg.ReadVector3(); pkg.ReadVector3(); }     // skin colour, hair colour
            if (version >= 11) { pkg.ReadInt(); }                           // model index
            if (version >= 12) { SkipFoods(pkg, version); }

            if (version >= 17) { ReadSkills(pkg, profile); }

            if (version >= 26) {
                int entries = pkg.ReadInt();
                for (int i = 0; i < entries; i++) {
                    string key = pkg.ReadString();
                    profile.CustomData[key] = pkg.ReadString();
                }
                // stamina / max eitr / eitr follow, then the build UI blob on ChunkedNorth and AbandonedDN.
                // None of it is modelled by the character store, and nothing is read after this, so the block
                // is left unwalked rather than skipped field by field.
            }
        }

        // Mirrors Inventory.Load, which since Version.Item.Smaller picks between two entry layouts and two
        // widths of length prefix.
        private static void ReadInventory(ZPackage pkg, FchProfile profile) {
            int version = pkg.ReadInt();
            if (version > ItemDataVersionMax) {
                throw new InvalidDataException($"inventory version {version} is newer than this build understands ({ItemDataVersionMax}); the mod needs rebuilding against the current game");
            }
            bool packed = version >= ItemVersionSmaller;
            int count = packed ? pkg.ReadUShort() : pkg.ReadInt();

            for (int i = 0; i < count; i++) {
                FchItem item = packed ? ReadPackedItem(pkg, version) : ReadFlatItem(pkg, version);

                // No prefab means the item had no drop prefab when it was saved; vanilla discards these on
                // load too.
                if (!string.IsNullOrEmpty(item.PrefabName) || item.PrefabHash != 0) { profile.Items.Add(item); }
            }
        }

        // Version.Item 100-107, mirroring Inventory.LoadOld: a flat run of fields led by the prefab name.
        private static FchItem ReadFlatItem(ZPackage pkg, int version) {
            FchItem item = new FchItem {
                PrefabName = pkg.ReadString(),
                Stack = pkg.ReadInt(),
                Durability = pkg.ReadSingle(),
                GridPos = pkg.ReadVector2i(),
                Equipped = pkg.ReadBool()
            };
            item.Quality = version >= 101 ? pkg.ReadInt() : 1;
            item.Variant = version >= 102 ? pkg.ReadInt() : 0;
            if (version >= 103) {
                item.CrafterID = pkg.ReadLong();
                item.CrafterName = pkg.ReadString();
            }
            if (version >= 104) {
                int customEntries = pkg.ReadInt();
                for (int c = 0; c < customEntries; c++) {
                    string key = pkg.ReadString();
                    string value = pkg.ReadString();
                    if (item.CustomData == null) { item.CustomData = new Dictionary<string, string>(); }
                    item.CustomData[key] = value;
                }
            }
            item.WorldLevel = version >= 105 ? pkg.ReadInt() : 0;
            if (version >= 106) { pkg.ReadBool(); }                       // picked up
            // Vanilla reads m_cheated for AbandonedDN and again from ChunksNCheats on; only the first of those
            // is reachable here, since everything from Smaller up is written packed instead.
            if (version == ItemVersionAbandonedDN) { pkg.ReadBool(); }    // cheated

            return item;
        }

        // Version.Item 108+, mirroring ItemDrop.ItemData.Load: the small fields are single bytes, every field
        // still at its default is elided behind a bit in a flag byte, and the prefab is a stable hash. Durability
        // is a hundredths-of-a-point int rather than a float, and quality and stack are ushorts defaulting to 1.
        private static FchItem ReadPackedItem(ZPackage pkg, int version) {
            FchItem item = new FchItem { Durability = pkg.ReadInt() * 0.01f };
            // Read into locals first: these are three consecutive bytes and the order they come off the stream
            // in is the whole contract, so it should not rest on C#'s argument evaluation order.
            int gridX = pkg.ReadByte();
            int gridY = pkg.ReadByte();
            item.GridPos = new Vector2i(gridX, gridY);
            item.WorldLevel = pkg.ReadByte();

            byte flags = pkg.ReadByte();
            item.Equipped = (flags & 2) != 0;
            item.Quality = (flags & 4) == 0 ? 1 : pkg.ReadUShort();
            item.Stack = (flags & 8) == 0 ? 1 : pkg.ReadUShort();
            item.Variant = (flags & 16) != 0 ? pkg.ReadInt() : 0;
            if ((flags & 32) != 0) {
                item.CrafterID = pkg.ReadLong();
                item.CrafterName = pkg.ReadString();
            }
            item.PrefabHash = (flags & 64) != 0 ? pkg.ReadInt() : 0;

            if ((flags & 128) != 0) {
                int customEntries = pkg.ReadNumItems();
                for (int c = 0; c < customEntries; c++) {
                    string key = pkg.ReadString();
                    string value = pkg.ReadString();
                    if (item.CustomData == null) { item.CustomData = new Dictionary<string, string>(); }
                    item.CustomData[key] = value;
                }
            }

            if (version >= ItemVersionChunksNCheats) { pkg.ReadByte(); }   // cheated, as its own flag byte

            return item;
        }

        // Mirrors Skills.Load. Skill types are deliberately NOT filtered through Skills.IsSkillValid: modded
        // skills use hashed type values the enum does not name, and the enforcer's own save path keeps them
        // (a live save records whatever GetSkillList returns), so dropping them here would make an imported
        // character look like it had lost skills.
        private static void ReadSkills(ZPackage pkg, FchProfile profile) {
            int version = pkg.ReadInt();
            if (version > SkillsVersionMax) {
                throw new InvalidDataException($"skills version {version} is newer than this build understands ({SkillsVersionMax}); the mod needs rebuilding against the current game");
            }
            int count = pkg.ReadInt();
            for (int i = 0; i < count; i++) {
                Skills.SkillType type = (Skills.SkillType)pkg.ReadInt();
                float level = pkg.ReadSingle();
                if (version >= 2) { pkg.ReadSingle(); } // accumulator, not modelled
                profile.SkillLevels[type] = level;
            }
        }

        private static void SkipFoods(ZPackage pkg, int version) {
            int foods = pkg.ReadInt();
            for (int i = 0; i < foods; i++) {
                if (version >= 14) {
                    pkg.ReadString();
                    if (version >= 25) {
                        pkg.ReadSingle();
                    } else {
                        pkg.ReadSingle();
                        if (version >= 16) { pkg.ReadSingle(); }
                    }
                } else {
                    pkg.ReadString();
                    for (int f = 0; f < 6; f++) { pkg.ReadSingle(); }
                    if (version >= 13) { pkg.ReadSingle(); }
                }
            }
        }

        private static void SkipStringList(ZPackage pkg) {
            int count = pkg.ReadInt();
            for (int i = 0; i < count; i++) { pkg.ReadString(); }
        }

        private static void SkipStringFloatMap(ZPackage pkg) {
            int count = pkg.ReadInt();
            for (int i = 0; i < count; i++) { pkg.ReadString(); pkg.ReadSingle(); }
        }
    }
}
