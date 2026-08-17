using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;

namespace ValheimEnforcer.modules.migration {

    /// <summary>One inventory entry as it appears on disk, before any prefab resolution.</summary>
    internal sealed class FchItem {
        internal string PrefabName;
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
        private const int ProfileVersionMax = 43;
        private const int PlayerDataVersionMax = 29;  // Version.m_playerDataVersion
        private const int ItemDataVersionMax = 106;   // Version.m_itemDataVersion
        private const int SkillsVersionMax = 2;

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

            if (version >= 38) {
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
                SkipStringFloatMap(pkg); // known worlds
                SkipStringFloatMap(pkg); // known world keys
                SkipStringFloatMap(pkg); // known commands
                if (version >= 42) {
                    SkipStringFloatMap(pkg); // enemy stats
                    SkipStringFloatMap(pkg); // item pickup stats
                    SkipStringFloatMap(pkg); // item craft stats
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
            if (version >= 18) {
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
                // stamina / max eitr / eitr follow, and are not modelled by the character store.
            }
        }

        // Mirrors Inventory.Load.
        private static void ReadInventory(ZPackage pkg, FchProfile profile) {
            int version = pkg.ReadInt();
            if (version > ItemDataVersionMax) {
                throw new InvalidDataException($"inventory version {version} is newer than this build understands ({ItemDataVersionMax}); the mod needs rebuilding against the current game");
            }
            int count = pkg.ReadInt();

            for (int i = 0; i < count; i++) {
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
                if (version >= 106) { pkg.ReadBool(); } // picked up

                // An empty prefab name means the item had no drop prefab when it was saved; vanilla discards
                // these on load too.
                if (!string.IsNullOrEmpty(item.PrefabName)) { profile.Items.Add(item); }
            }
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
