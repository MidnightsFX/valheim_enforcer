using System;
using System.Collections.Generic;
using ValheimEnforcer.common;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.modules.character {

    /// <summary>
    /// Reads and writes the half of a character's progress that vanilla keeps in the player profile rather
    /// than in anything the server ever sees: what they know how to make, what they have killed, what they
    /// have picked up, and where they wake up.
    ///
    /// Client side by necessity. All of it hangs off a live <see cref="Player"/> and the local
    /// <see cref="PlayerProfile"/>, neither of which exists on a dedicated server - so unlike the item and
    /// skill rules there is no second copy running server-side to catch a modified client. What the server
    /// gets is the record, and the record is what it hands back; the enforcement this buys is that a value
    /// the server has never seen does not survive a join, not that the client was honest while it played.
    /// Said plainly in EnableProgressionSync's description too.
    ///
    /// Null is "not tracked" everywhere, and it is load-bearing. A save written while a setting was off has
    /// null for that section, and adopting the live value is the only safe reading: treating null as "knows
    /// nothing" would wipe every existing character once, the first time an admin enabled the setting.
    ///
    /// The map is not here - it is large enough to need its own channel and its own file. See
    /// <see cref="MapSync"/>.
    /// </summary>
    internal static class ProgressionSync {

        // Vanilla's PlayerStats holds one bucket per DifficultyRequirement, and one enemy-kill table per
        // KillModifiers. Both arrays are sized by the enum's terminator rather than by a literal, so a game
        // update that adds a difficulty does not silently drop the new bucket here.
        private static readonly int DifficultyBuckets = (int)DifficultyRequirement.Count;
        private static readonly int EnemyTables = (int)KillModifiers.CountNone;

        internal static bool Enabled {
            get { return ValConfig.EnableProgressionSync != null && ValConfig.EnableProgressionSync.Value; }
        }

        internal static bool KnownItemsEnabled {
            get { return Enabled && ValConfig.SyncKnownItems != null && ValConfig.SyncKnownItems.Value; }
        }

        internal static bool TrophiesEnabled {
            get { return Enabled && ValConfig.SyncTrophies != null && ValConfig.SyncTrophies.Value; }
        }

        internal static bool StatsEnabled {
            get { return Enabled && ValConfig.SyncPlayerStats != null && ValConfig.SyncPlayerStats.Value; }
        }

        internal static bool SpawnEnabled {
            get { return Enabled && ValConfig.SyncSpawnPoint != null && ValConfig.SyncSpawnPoint.Value; }
        }

        /// <summary>True when at least one section is being tracked, so callers can skip the whole pass.</summary>
        internal static bool AnyEnabled {
            get { return KnownItemsEnabled || TrophiesEnabled || StatsEnabled || SpawnEnabled || MapSync.Enabled; }
        }

        // ---------------------------------------------------------------------------------------------
        // Capture: live player -> record
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Builds the progression record from what this machine currently holds, merging into
        /// <paramref name="existing"/> rather than replacing it.
        ///
        /// Merging matters: each section is captured only while its own setting is on, and a section that is
        /// off must keep whatever the server already had rather than being nulled out of the next save. An
        /// admin turning SyncPlayerStats off for a week should not find every character's statistics gone.
        /// </summary>
        internal static Progression Capture(Player player, Progression existing) {
            if (!Enabled || player == null) { return existing; }

            PlayerProfile profile = Game.instance != null ? Game.instance.GetPlayerProfile() : null;
            Progression record = existing ?? new Progression();

            if (KnownItemsEnabled) {
                record.KnownRecipes = new List<string>(player.m_knownRecipes);
                record.KnownMaterials = new List<string>(player.m_knownMaterial);
                record.KnownStations = new Dictionary<string, int>(player.m_knownStations);
                record.KnownBiomes = new List<string>(player.m_knownBiome);
                // Through CompatKnownTexts rather than a plain copy: keys a mod owns never enter the
                // record, so nothing here can later hand a stale copy of them back. See that class.
                record.KnownTexts = compat.CompatKnownTexts.SnapshotForTracking(player.m_knownTexts);
                record.Uniques = new List<string>(player.m_uniques);
                record.ShownTutorials = new List<string>(player.m_shownTutorials);
            }

            if (TrophiesEnabled) {
                record.Trophies = new List<string>(player.m_trophies);
            }

            if (StatsEnabled && profile != null) {
                record.Stats = CaptureStats(profile);
            }

            if (SpawnEnabled && profile != null && ZNet.instance != null) {
                UnityEngine.Vector3 spawn = profile.GetCustomSpawnPoint();
                UnityEngine.Vector3 home = profile.GetHomePoint();
                record.Spawn = new SpawnPoints {
                    HaveCustomSpawn = profile.HaveCustomSpawnPoint(),
                    SpawnX = spawn.x, SpawnY = spawn.y, SpawnZ = spawn.z,
                    HomeX = home.x, HomeY = home.y, HomeZ = home.z,
                };
            }

            return record;
        }

        private static Dictionary<string, StatBucket> CaptureStats(PlayerProfile profile) {
            Dictionary<string, StatBucket> buckets = new Dictionary<string, StatBucket>(StringComparer.Ordinal);
            for (int i = 0; i < DifficultyBuckets && i < profile.m_playerStats.Length; i++) {
                PlayerProfile.PlayerStats stats = profile.m_playerStats[i];
                if (stats == null) { continue; }

                StatBucket bucket = new StatBucket {
                    // Keyed by name, never by index: vanilla writes these into the .fch positionally, so an
                    // index recorded here would land on a different counter the first time a member is
                    // inserted into PlayerStatType.
                    Stats = NonZero(stats.m_stats),
                    KnownWorlds = NonEmpty(stats.m_knownWorlds),
                    KnownWorldKeys = NonEmpty(stats.m_knownWorldKeys),
                    KnownCommands = NonEmpty(stats.m_knownCommands),
                    ItemPickup = NonEmpty(stats.m_itemPickupStats),
                    ItemCraft = NonEmpty(stats.m_itemCraftStats),
                    Pickable = NonEmpty(stats.m_pickableStats),
                    FoodEaten = NonEmpty(stats.m_foodEatenStats),
                    PiecesPlaced = NonEmpty(stats.m_piecesPlacedStats),
                };

                Dictionary<string, Dictionary<string, float>> enemies = null;
                for (int k = 0; k < EnemyTables && stats.m_enemyStats != null && k < stats.m_enemyStats.Length; k++) {
                    Dictionary<string, float> table = NonEmpty(stats.m_enemyStats[k]);
                    if (table == null) { continue; }
                    if (enemies == null) { enemies = new Dictionary<string, Dictionary<string, float>>(StringComparer.Ordinal); }
                    enemies[((KillModifiers)k).ToString()] = table;
                }
                bucket.EnemyStats = enemies;

                buckets[((DifficultyRequirement)i).ToString()] = bucket;
            }
            return buckets;
        }

        // Vanilla pre-fills all 205 counters with zero, so a dense copy would be mostly padding on a payload
        // that rides every full character save. Null rather than an empty dictionary so OmitDefaults drops it.
        private static Dictionary<string, float> NonZero(Dictionary<PlayerStatType, float> source) {
            if (source == null) { return null; }
            Dictionary<string, float> result = null;
            foreach (KeyValuePair<PlayerStatType, float> entry in source) {
                if (entry.Value == 0f) { continue; }
                if (entry.Key == PlayerStatType.Count || entry.Key == PlayerStatType.None) { continue; }
                if (result == null) { result = new Dictionary<string, float>(StringComparer.Ordinal); }
                result[entry.Key.ToString()] = entry.Value;
            }
            return result;
        }

        private static Dictionary<string, float> NonEmpty(Dictionary<string, float> source) {
            if (source == null || source.Count == 0) { return null; }
            return new Dictionary<string, float>(source, StringComparer.Ordinal);
        }

        // ---------------------------------------------------------------------------------------------
        // Apply: record -> live player
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Puts the server's copy back onto the live player. A null section is left alone - see the class
        /// note on why that is not the same as empty.
        /// </summary>
        internal static void Apply(Progression stored, Player player, string characterName) {
            if (!Enabled || stored == null || player == null) { return; }

            PlayerProfile profile = Game.instance != null ? Game.instance.GetPlayerProfile() : null;
            bool touchedKnownItems = false;

            if (KnownItemsEnabled) {
                touchedKnownItems |= Replace(player.m_knownMaterial, stored.KnownMaterials);
                touchedKnownItems |= Replace(player.m_knownBiome, stored.KnownBiomes);
                touchedKnownItems |= Replace(player.m_uniques, stored.Uniques);
                touchedKnownItems |= Replace(player.m_shownTutorials, stored.ShownTutorials);
                if (stored.KnownStations != null) {
                    player.m_knownStations.Clear();
                    foreach (KeyValuePair<string, int> station in stored.KnownStations) {
                        player.m_knownStations[station.Key] = station.Value;
                    }
                    touchedKnownItems = true;
                }
                if (stored.KnownTexts != null) {
                    // Not a plain clear-and-repopulate. Known texts are where some mods keep per-player
                    // progression (EpicMMO's level and experience), and progression rides FULL saves only -
                    // CharacterDeltaTracker never carries it - so the stored copy is current only as of the
                    // last full save. Replacing a mod's key from it rolled that mod's progress back to the
                    // last full save on every single join. Compendium entries are still replaced outright;
                    // pass-through keys keep the live player's own value. See CompatKnownTexts.
                    compat.CompatKnownTexts.ApplyToPlayer(stored.KnownTexts, player.m_knownTexts);
                    touchedKnownItems = true;
                }
                // Recipes last, and then rebuilt below. Vanilla derives most of this list from the materials
                // and stations set above and re-derives it on every inventory change, so writing it first
                // would be undone within seconds; writing it at all is still needed for the build pieces and
                // the recipes granted directly, which nothing re-derives.
                touchedKnownItems |= Replace(player.m_knownRecipes, stored.KnownRecipes);
            }

            if (TrophiesEnabled) {
                Replace(player.m_trophies, stored.Trophies);
            }

            if (touchedKnownItems) {
                player.UpdateKnownRecipesList();
                player.UpdateAvailablePiecesList();
                // Rediscovery queues an unlock popup per piece. Vanilla clears the queue for the same reason
                // after resetting known items for an item set (ItemSets.TryGetSet).
                if (MessageHud.instance != null) { MessageHud.instance.ClearUnlockQueue(); }
            }

            if (StatsEnabled && profile != null && stored.Stats != null) {
                ApplyStats(stored.Stats, profile);
            }

            if (SpawnEnabled && profile != null && ZNet.instance != null && stored.Spawn != null) {
                SpawnPoints spawn = stored.Spawn;
                profile.SetHomePoint(new UnityEngine.Vector3(spawn.HomeX, spawn.HomeY, spawn.HomeZ));
                if (spawn.HaveCustomSpawn) {
                    profile.SetCustomSpawnPoint(new UnityEngine.Vector3(spawn.SpawnX, spawn.SpawnY, spawn.SpawnZ));
                } else {
                    profile.ClearCustomSpawnPoint();
                }
            }

            Logger.LogDebug($"Applied the server's stored progression to {characterName}.");
        }

        // Returns whether anything was written, so the caller only pays for the recipe rebuild when it has to.
        private static bool Replace(HashSet<string> live, List<string> stored) {
            if (stored == null) { return false; }
            live.Clear();
            foreach (string value in stored) {
                if (!string.IsNullOrEmpty(value)) { live.Add(value); }
            }
            return true;
        }

        private static void ApplyStats(Dictionary<string, StatBucket> stored, PlayerProfile profile) {
            foreach (KeyValuePair<string, StatBucket> entry in stored) {
                if (entry.Value == null) { continue; }
                if (!TryBucketIndex(entry.Key, out int index)) { continue; }
                if (index >= profile.m_playerStats.Length) { continue; }
                PlayerProfile.PlayerStats live = profile.m_playerStats[index];
                if (live == null) { continue; }

                StatBucket bucket = entry.Value;
                // Counters absent from the record are zero there, and the server's copy is the one that
                // counts - so the live bucket is reset before the stored values go in, rather than merged
                // onto. Without this a client could keep any counter it simply never reported.
                if (bucket.Stats != null) {
                    foreach (PlayerStatType type in new List<PlayerStatType>(live.m_stats.Keys)) {
                        live.m_stats[type] = 0f;
                    }
                    foreach (KeyValuePair<string, float> stat in bucket.Stats) {
                        // An unrecognised name is a counter this build of the game no longer has (or does not
                        // have yet). Skipped rather than guessed at - the alternative is writing somebody's
                        // kill count onto whatever now sits at that position.
                        if (!TryStatType(stat.Key, out PlayerStatType type)) { continue; }
                        live.m_stats[type] = stat.Value;
                    }
                }

                CopyInto(live.m_knownWorlds, bucket.KnownWorlds);
                CopyInto(live.m_knownWorldKeys, bucket.KnownWorldKeys);
                CopyInto(live.m_knownCommands, bucket.KnownCommands);
                CopyInto(live.m_itemPickupStats, bucket.ItemPickup);
                CopyInto(live.m_itemCraftStats, bucket.ItemCraft);
                CopyInto(live.m_pickableStats, bucket.Pickable);
                CopyInto(live.m_foodEatenStats, bucket.FoodEaten);
                CopyInto(live.m_piecesPlacedStats, bucket.PiecesPlaced);

                if (bucket.EnemyStats != null && live.m_enemyStats != null) {
                    for (int k = 0; k < live.m_enemyStats.Length; k++) {
                        if (live.m_enemyStats[k] == null) { continue; }
                        bucket.EnemyStats.TryGetValue(((KillModifiers)k).ToString(), out Dictionary<string, float> table);
                        CopyInto(live.m_enemyStats[k], table);
                    }
                }
            }
        }

        private static void CopyInto(Dictionary<string, float> live, Dictionary<string, float> stored) {
            if (live == null) { return; }
            live.Clear();
            if (stored == null) { return; }
            foreach (KeyValuePair<string, float> entry in stored) { live[entry.Key] = entry.Value; }
        }

        // ---------------------------------------------------------------------------------------------
        // Clamp: live above the record goes back down
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Lowers any statistic the live profile holds above the server's copy, the way
        /// PreventExternalSkillRaises handles a skill. A counter the server has no record of counts as zero.
        ///
        /// Runs after <see cref="Apply"/> rather than instead of it: Apply restores what was lost, this
        /// removes what was gained elsewhere, and between them the live profile ends up equal to the record.
        /// A null record means the section was never tracked, and nothing is lowered.
        /// </summary>
        internal static void ClampStats(Progression stored, string characterName) {
            if (!StatsEnabled || stored == null || stored.Stats == null) { return; }
            PlayerProfile profile = Game.instance != null ? Game.instance.GetPlayerProfile() : null;
            if (profile == null) { return; }

            int lowered = 0;
            for (int i = 0; i < DifficultyBuckets && i < profile.m_playerStats.Length; i++) {
                PlayerProfile.PlayerStats live = profile.m_playerStats[i];
                if (live == null) { continue; }
                stored.Stats.TryGetValue(((DifficultyRequirement)i).ToString(), out StatBucket bucket);
                Dictionary<string, float> ceiling = bucket?.Stats;

                foreach (PlayerStatType type in new List<PlayerStatType>(live.m_stats.Keys)) {
                    float held = live.m_stats[type];
                    if (held <= 0f) { continue; }
                    float allowed = 0f;
                    if (ceiling != null) { ceiling.TryGetValue(type.ToString(), out allowed); }
                    if (held <= allowed) { continue; }
                    live.m_stats[type] = allowed;
                    lowered++;
                }
            }
            if (lowered > 0) {
                Logger.LogInfo($"Lowered {lowered} statistic(s) for {characterName} that were above the levels this server holds.");
            }
        }

        // ---------------------------------------------------------------------------------------------
        // New characters
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// A character this server has never seen keeps nothing it arrived knowing, for whichever sections
        /// are being tracked. Applied to the live player; <see cref="NewCharacterRules"/> does the same to
        /// the record so the two agree.
        ///
        /// Deliberately NOT called for known items and trophies - NewCharacterClearKnownRecipes already owns
        /// that decision and runs vanilla's own reset, and having two settings clear the same lists would
        /// mean an admin who turned one off still lost them to the other.
        /// </summary>
        internal static void ResetForNewCharacter(string characterName) {
            if (!StatsEnabled) { return; }
            if (ValConfig.NewCharacterClearPlayerStats == null || !ValConfig.NewCharacterClearPlayerStats.Value) { return; }
            // Same guard as KnownRecipes: on a listen host or in singleplayer every character reads as new
            // the first time it is loaded with the mod installed, and zeroed statistics cannot be given back.
            if (CharacterManager.ThisMachineIsAuthority()) { return; }
            if (!CharacterManager.ConfirmedNewCharacter()) {
                Logger.LogWarning($"Not clearing statistics for {characterName}: the server has not confirmed this is a new character, and zeroed statistics cannot be given back.");
                return;
            }

            PlayerProfile profile = Game.instance != null ? Game.instance.GetPlayerProfile() : null;
            if (profile == null) { return; }
            for (int i = 0; i < DifficultyBuckets && i < profile.m_playerStats.Length; i++) {
                PlayerProfile.PlayerStats live = profile.m_playerStats[i];
                if (live == null) { continue; }
                foreach (PlayerStatType type in new List<PlayerStatType>(live.m_stats.Keys)) { live.m_stats[type] = 0f; }
                live.m_knownWorlds.Clear();
                live.m_knownWorldKeys.Clear();
                live.m_knownCommands.Clear();
                live.m_itemPickupStats.Clear();
                live.m_itemCraftStats.Clear();
                live.m_pickableStats.Clear();
                live.m_foodEatenStats.Clear();
                live.m_piecesPlacedStats.Clear();
                if (live.m_enemyStats == null) { continue; }
                foreach (Dictionary<string, float> table in live.m_enemyStats) { table?.Clear(); }
            }
            Logger.LogInfo($"New character {characterName}: cleared the statistics they arrived with.");
        }

        // ---------------------------------------------------------------------------------------------

        private static bool TryBucketIndex(string name, out int index) {
            index = -1;
            if (string.IsNullOrEmpty(name)) { return false; }
            if (!Enum.TryParse(name, false, out DifficultyRequirement difficulty)) { return false; }
            if (difficulty == DifficultyRequirement.Count) { return false; }
            index = (int)difficulty;
            return index >= 0;
        }

        private static bool TryStatType(string name, out PlayerStatType type) {
            type = PlayerStatType.None;
            if (string.IsNullOrEmpty(name)) { return false; }
            // Case sensitive on purpose: these are enum member names written by this mod, not admin input, so
            // a mismatch is a game update rather than a typo and should not be papered over.
            if (!Enum.TryParse(name, false, out type)) { return false; }
            return type != PlayerStatType.None && type != PlayerStatType.Count;
        }
    }
}
