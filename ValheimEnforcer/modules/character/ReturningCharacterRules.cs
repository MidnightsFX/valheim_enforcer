using System.Collections.Generic;
using ValheimEnforcer.common;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.modules.character {

    /// <summary>
    /// The server's own copy of the join validation for a RETURNING character - the counterpart to
    /// <see cref="NewCharacterRules"/>, which only covers a character the server has never seen.
    ///
    /// On join the client validates its live inventory against the stored character
    /// (<see cref="CharacterManager.LoadAndValidatePlayer"/>): items the stored save does not account for are
    /// confiscated, skills above the stored level are clamped, custom data, the Forsaken Power and foods are reset
    /// to the stored copy. That is
    /// exactly the enforcement a modified client skips, then uploads the un-validated result as the new
    /// authoritative save. This re-runs the same decisions on the server, against the stored character, on the
    /// first full save of the session - so the documented Player Sync rules hold regardless of what the client
    /// ran.
    ///
    /// Pure data: no Player, no Unity, no ZNet, so it is safe on the CharacterStore worker thread. Item matching
    /// reuses <see cref="DataObjects.Character.RemoveFromPlayerItems"/> so the server and client agree on what
    /// "the same item" means (value equality, with the durability/slot tolerance and the fuzzy fallback).
    ///
    /// Restoration of missing items is deliberately NOT done here: it is a client-side action on a live
    /// inventory, and a returning player ending up with fewer items than the server holds is not an exploit -
    /// it is the honest client's own job, and the server has nothing to add items into from a worker thread.
    /// </summary>
    internal static class ReturningCharacterRules {

        /// <summary>Immutable snapshot of the settings, taken on the main thread (the worker must never read a
        /// ConfigEntry).</summary>
        internal sealed class Policy {
            internal bool RemoveUntracked;      // RemoveNontrackedItemsFromJoiningPlayers
            internal bool ClampSkills;          // PreventExternalSkillRaises
            internal bool ResetCustomData;      // PreventExternalCustomDataChanges
            internal bool ResetGuardianPower;   // PreventExternalForsakenPowerChanges
            internal bool ResetFoods;           // PreventExternalFoodChanges
            internal bool LenientDirtyRemoval;  // ItemRemovalForDirtyReconnection
            internal bool RecordReductions;     // RecordSkillReductions
            internal bool ResetKnownItems;      // SyncKnownItems
            internal bool ResetTrophies;        // SyncTrophies
            internal bool ReconcileStats;       // SyncPlayerStats
            internal bool ResetSpawn;           // SyncSpawnPoint

            internal bool AnyEnabled {
                get {
                    return RemoveUntracked || ClampSkills || ResetCustomData || ResetGuardianPower || ResetFoods
                           || ResetKnownItems || ResetTrophies || ReconcileStats || ResetSpawn;
                }
            }
        }

        /// <summary>Main thread only. Reads the current settings into a Policy.</summary>
        internal static Policy Current() {
            return new Policy {
                RemoveUntracked = ValConfig.RemoveNontrackedItemsFromJoiningPlayers.Value,
                ClampSkills = ValConfig.PreventExternalSkillRaises.Value,
                ResetCustomData = ValConfig.PreventExternalCustomDataChanges.Value,
                ResetGuardianPower = ValConfig.PreventExternalForsakenPowerChanges.Value,
                ResetFoods = ValConfig.PreventExternalFoodChanges.Value,
                LenientDirtyRemoval = ValConfig.ItemRemovalForDirtyReconnection.Value,
                RecordReductions = ValConfig.RecordSkillReductions.Value,
                ResetKnownItems = ProgressionSync.KnownItemsEnabled,
                ResetTrophies = ProgressionSync.TrophiesEnabled,
                ReconcileStats = ProgressionSync.StatsEnabled,
                ResetSpawn = ProgressionSync.SpawnEnabled,
            };
        }

        internal sealed class Result {
            internal int ItemsConfiscated;
            internal int SkillsClamped;
            internal bool CustomDataReset;
            internal bool GuardianPowerReset;
            internal bool FoodsReset;
            internal bool KnownItemsReset;
            internal bool TrophiesReset;
            internal int StatsReconciled;
            internal bool SpawnReset;

            internal bool Changed {
                get {
                    return ItemsConfiscated > 0 || SkillsClamped > 0 || CustomDataReset || GuardianPowerReset
                           || FoodsReset || KnownItemsReset || TrophiesReset || StatsReconciled > 0 || SpawnReset;
                }
            }

            internal string Describe() {
                List<string> parts = new List<string>();
                if (ItemsConfiscated > 0) { parts.Add($"{ItemsConfiscated} item(s) confiscated"); }
                if (SkillsClamped > 0) { parts.Add($"{SkillsClamped} skill(s) clamped"); }
                if (CustomDataReset) { parts.Add("custom data reset"); }
                if (GuardianPowerReset) { parts.Add("forsaken power reset"); }
                if (FoodsReset) { parts.Add("foods reset"); }
                if (KnownItemsReset) { parts.Add("known recipes and materials reset"); }
                if (TrophiesReset) { parts.Add("trophies reset"); }
                if (StatsReconciled > 0) { parts.Add($"{StatsReconciled} statistic(s) reconciled"); }
                if (SpawnReset) { parts.Add("spawn point reset"); }
                return parts.Count == 0 ? "nothing to do" : string.Join(", ", parts.ToArray());
            }
        }

        /// <summary>
        /// Reconciles an uploaded character <paramref name="incoming"/> to the server's <paramref name="stored"/>
        /// copy. Mutates <paramref name="incoming"/> in place (its item list is trimmed, confiscations appended,
        /// skills clamped, custom data reset) and returns what changed. Confiscations and skill reductions are
        /// recorded on the incoming character, whose ConfiscatedItems and SkillReductions lists are the
        /// server-owned ones by the time this runs.
        /// </summary>
        internal static Result Apply(DataObjects.Character incoming, DataObjects.Character stored, Policy policy) {
            Result result = new Result();
            if (incoming == null || stored == null || policy == null) { return result; }

            // Item removal, matching the client's join-time confiscation. Skipped on a lenient dirty reconnect,
            // exactly as the client skips it, so a crash victim is not stripped of items gained in the unsaved
            // window. The dirty flag is read from the STORED character - the same value the client saw.
            bool skipRemovalForDirty = stored.LastDisconnect == DisconnectionState.DirtyDisconnect
                                       && policy.LenientDirtyRemoval;
            if (policy.RemoveUntracked && !skipRemovalForDirty && incoming.PlayerItems != null) {
                // A COPY of the stored items, consumed one entry per matched incoming item (a multiset match)
                // without disturbing the real stored list. Strict value equality (PackedItem.Equals - name,
                // stack, quality, variant, world level, crafter, custom data) on purpose, NOT the fuzzy fallback
                // in Character.RemoveFromPlayerItems: that fallback ignores quality, which would let an item
                // upgraded elsewhere pass here even though the client's own join check (ValidateItems) confiscates
                // it. Durability is excluded from equality by design (it drains continuously and would false-
                // positive on legitimate wear), matching how the item is tracked everywhere else.
                List<PackedItem> baseline = stored.PlayerItems != null
                    ? new List<PackedItem>(stored.PlayerItems)
                    : new List<PackedItem>();

                List<PackedItem> kept = new List<PackedItem>();
                foreach (PackedItem item in incoming.PlayerItems) {
                    if (item == null) { continue; }
                    int match = baseline.IndexOf(item); // value equality via IEquatable<PackedItem>
                    if (match >= 0) {
                        baseline.RemoveAt(match);
                        kept.Add(item);
                    } else {
                        result.ItemsConfiscated++;
                        incoming.AddConfiscatedItem(item, "Returning character, item not accounted for by the stored save");
                    }
                }
                incoming.PlayerItems = kept;
            }

            // Skill clamp: no skill may exceed the stored level. This is the returning-character equivalent of
            // the client's PreventExternalSkillRaises pass; SkillClamp's per-delta bound is a separate, looser
            // guard applied everywhere.
            if (policy.ClampSkills && incoming.SkillLevels != null && stored.SkillLevels != null) {
                List<Skills.SkillType> keys = new List<Skills.SkillType>(incoming.SkillLevels.Keys);
                foreach (Skills.SkillType skill in keys) {
                    if (!stored.SkillLevels.TryGetValue(skill, out float storedLevel)) { continue; }
                    // An admin's restore raises the ceiling: the level they granted arrives from the client
                    // looking exactly like an external gain, and must not be taken straight back.
                    float ceiling = storedLevel;
                    if (stored.PendingSkillRestores != null
                        && stored.PendingSkillRestores.TryGetValue(skill, out float pending) && pending > ceiling) {
                        ceiling = pending;
                    }
                    float reported = incoming.SkillLevels[skill];
                    if (SkillReductions.IsAbove(reported, ceiling)) {
                        // The server's own record of the lowering. On an honest client the join clamp already ran
                        // and recorded it, and the reported level equals the stored one, so this never fires; it
                        // fires - and records - for the client that skipped the clamp.
                        if (policy.RecordReductions) {
                            incoming.AddSkillReduction(skill, reported, ceiling, "Returning character: above the stored level");
                        }
                        incoming.SkillLevels[skill] = ceiling;
                        result.SkillsClamped++;
                    }
                }
            }

            // Custom data reset to the stored copy (a detached copy, so the two do not alias). The stored
            // save may predate pass-through handling and still carry a compat mod's inventory backup; shed
            // it so the reset cannot write it back into the incoming save. The incoming side was already
            // stripped at ingestion.
            if (policy.ResetCustomData) {
                Dictionary<string, string> storedCopy = PackedItem.SnapshotCustomData(stored.PlayerCustomData);
                compat.CompatCustomData.StripPassthroughKeys(storedCopy);
                if (!CustomDataEquals(incoming.PlayerCustomData, storedCopy)) {
                    incoming.PlayerCustomData = storedCopy;
                    result.CustomDataReset = true;
                }
            }

            // Forsaken Power reset to the one this character last had selected here. A stored null is a save from
            // before tracking was on, with nothing to reset to, so the incoming power is adopted - the same call the
            // client makes (ForsakenPower.RestoreOnJoin), so the two sides agree on the baseline.
            if (policy.ResetGuardianPower && stored.GuardianPower != null && incoming.GuardianPower != stored.GuardianPower) {
                incoming.GuardianPower = stored.GuardianPower;
                result.GuardianPowerReset = true;
            }

            // Foods reset to what this character last had here, but only when the upload holds more than that - a food
            // the stored save does not have, or more burn time on one. The client restores the stored foods exactly on
            // join and they only drain from there, so an honest first save always holds the same or less, and
            // resetting it anyway would hand back the few seconds it drained. A stored null predates tracking and has
            // nothing to reset to, matching the client's FoodSync.RestoreOnJoin.
            if (policy.ResetFoods && PackedFood.Exceeds(incoming.Foods, stored.Foods)) {
                incoming.Foods = PackedFood.Copy(stored.Foods);
                result.FoodsReset = true;
            }

            ApplyProgression(incoming, stored, policy, result);

            return result;
        }

        /// <summary>
        /// The progression counterpart, and the server's own copy of what ProgressionSync does on the client.
        ///
        /// Only the first full save of a session reaches this, which is what makes replacement the right
        /// answer rather than a merge: at that moment an honest client is holding exactly what the server
        /// pushed it moments earlier on join, so replacing changes nothing for it. Everything discovered
        /// later in the session arrives through an ordinary save that this never sees, and is kept.
        ///
        /// A null section on the stored side is untracked, not empty, and is left alone - the same rule as
        /// the Forsaken Power and foods above, and for the same reason: a setting switched on today must not
        /// wipe every character that was saved before it.
        /// </summary>
        private static void ApplyProgression(DataObjects.Character incoming, DataObjects.Character stored,
                                             Policy policy, Result result) {
            Progression storedProgress = stored.Progress;
            if (storedProgress == null) { return; }
            if (!policy.ResetKnownItems && !policy.ResetTrophies && !policy.ReconcileStats && !policy.ResetSpawn) { return; }
            if (incoming.Progress == null) { incoming.Progress = new Progression(); }
            Progression incomingProgress = incoming.Progress;

            if (policy.ResetKnownItems) {
                bool changed = false;
                if (NeedsReplace(incomingProgress.KnownRecipes, storedProgress.KnownRecipes)) {
                    incomingProgress.KnownRecipes = new List<string>(storedProgress.KnownRecipes);
                    changed = true;
                }
                if (NeedsReplace(incomingProgress.KnownMaterials, storedProgress.KnownMaterials)) {
                    incomingProgress.KnownMaterials = new List<string>(storedProgress.KnownMaterials);
                    changed = true;
                }
                if (NeedsReplace(incomingProgress.KnownBiomes, storedProgress.KnownBiomes)) {
                    incomingProgress.KnownBiomes = new List<string>(storedProgress.KnownBiomes);
                    changed = true;
                }
                if (NeedsReplace(incomingProgress.Uniques, storedProgress.Uniques)) {
                    incomingProgress.Uniques = new List<string>(storedProgress.Uniques);
                    changed = true;
                }
                if (NeedsReplace(incomingProgress.ShownTutorials, storedProgress.ShownTutorials)) {
                    incomingProgress.ShownTutorials = new List<string>(storedProgress.ShownTutorials);
                    changed = true;
                }
                if (NeedsReplaceStations(incomingProgress.KnownStations, storedProgress.KnownStations)) {
                    incomingProgress.KnownStations = new Dictionary<string, int>(storedProgress.KnownStations);
                    changed = true;
                }
                // Against a stripped copy of the stored side, the same way custom data is handled above.
                // Ingestion sheds pass-through keys from every save it writes, but a save written before that
                // rule existed still carries them - and replacing the incoming list from it wholesale would
                // copy them straight back in and keep doing so on every join.
                Dictionary<string, string> storedTexts = storedProgress.KnownTexts;
                if (storedTexts != null) {
                    storedTexts = PackedItem.SnapshotCustomData(storedTexts);
                    compat.CompatKnownTexts.StripPassthroughKeys(storedTexts);
                }
                if (NeedsReplaceTexts(incomingProgress.KnownTexts, storedTexts)) {
                    incomingProgress.KnownTexts = new Dictionary<string, string>(storedTexts);
                    changed = true;
                }
                result.KnownItemsReset = changed;
            }

            if (policy.ResetTrophies && NeedsReplace(incomingProgress.Trophies, storedProgress.Trophies)) {
                incomingProgress.Trophies = new List<string>(storedProgress.Trophies);
                result.TrophiesReset = true;
            }

            if (policy.ReconcileStats && storedProgress.Stats != null) {
                result.StatsReconciled = ReconcileStats(incomingProgress, storedProgress);
            }

            if (policy.ResetSpawn && storedProgress.Spawn != null && !SpawnEquals(incomingProgress.Spawn, storedProgress.Spawn)) {
                incomingProgress.Spawn = CopySpawn(storedProgress.Spawn);
                result.SpawnReset = true;
            }
        }

        // Counters go both ways, unlike every other rule here, and each direction has its own reason. Above
        // the stored value is a gain from somewhere this server did not see, exactly like a skill. Below it
        // is a client under-reporting - vanilla's counters never decrease, so there is no honest way to
        // report less - and accepting that would quietly erase the history the server is holding on the
        // player's behalf. Either way the stored value is the answer.
        private static int ReconcileStats(Progression incoming, Progression stored) {
            int changed = 0;
            if (incoming.Stats == null) { incoming.Stats = new Dictionary<string, StatBucket>(); }
            foreach (KeyValuePair<string, StatBucket> bucket in stored.Stats) {
                if (bucket.Value == null) { continue; }
                if (!incoming.Stats.TryGetValue(bucket.Key, out StatBucket reported) || reported == null) {
                    incoming.Stats[bucket.Key] = bucket.Value;
                    changed++;
                    continue;
                }
                if (!BucketEquals(reported, bucket.Value)) {
                    incoming.Stats[bucket.Key] = bucket.Value;
                    changed++;
                }
            }
            // A bucket the server has never held is a difficulty this character has not played here.
            List<string> extra = new List<string>();
            foreach (string key in incoming.Stats.Keys) {
                if (!stored.Stats.ContainsKey(key)) { extra.Add(key); }
            }
            foreach (string key in extra) {
                incoming.Stats.Remove(key);
                changed++;
            }
            return changed;
        }

        // Whether the incoming list has to be replaced with the stored one. A null stored side is untracked
        // and never replaces anything. Order is ignored: both sides come out of a HashSet on the client,
        // which does not promise one.
        private static bool NeedsReplace(List<string> incoming, List<string> stored) {
            return stored != null && !SetEquals(incoming, stored);
        }

        private static bool NeedsReplaceStations(Dictionary<string, int> incoming, Dictionary<string, int> stored) {
            if (stored == null) { return false; }
            if (incoming == null || incoming.Count != stored.Count) { return true; }
            foreach (KeyValuePair<string, int> entry in stored) {
                if (!incoming.TryGetValue(entry.Key, out int level) || level != entry.Value) { return true; }
            }
            return false;
        }

        private static bool NeedsReplaceTexts(Dictionary<string, string> incoming, Dictionary<string, string> stored) {
            if (stored == null) { return false; }
            if (incoming == null || incoming.Count != stored.Count) { return true; }
            foreach (KeyValuePair<string, string> entry in stored) {
                if (!incoming.TryGetValue(entry.Key, out string text) || text != entry.Value) { return true; }
            }
            return false;
        }

        private static bool SetEquals(List<string> a, List<string> b) {
            int acount = a == null ? 0 : a.Count;
            if (acount != (b == null ? 0 : b.Count)) { return false; }
            if (acount == 0) { return true; }
            HashSet<string> set = new HashSet<string>(b);
            foreach (string value in a) {
                if (!set.Contains(value)) { return false; }
            }
            return true;
        }

        private static bool BucketEquals(StatBucket a, StatBucket b) {
            return FloatsEqual(a.Stats, b.Stats)
                   && FloatsEqual(a.KnownWorlds, b.KnownWorlds)
                   && FloatsEqual(a.KnownWorldKeys, b.KnownWorldKeys)
                   && FloatsEqual(a.KnownCommands, b.KnownCommands)
                   && FloatsEqual(a.ItemPickup, b.ItemPickup)
                   && FloatsEqual(a.ItemCraft, b.ItemCraft)
                   && FloatsEqual(a.Pickable, b.Pickable)
                   && FloatsEqual(a.FoodEaten, b.FoodEaten)
                   && FloatsEqual(a.PiecesPlaced, b.PiecesPlaced)
                   && EnemiesEqual(a.EnemyStats, b.EnemyStats);
        }

        private static bool EnemiesEqual(Dictionary<string, Dictionary<string, float>> a,
                                         Dictionary<string, Dictionary<string, float>> b) {
            int acount = a == null ? 0 : a.Count;
            if (acount != (b == null ? 0 : b.Count)) { return false; }
            if (acount == 0) { return true; }
            foreach (KeyValuePair<string, Dictionary<string, float>> entry in a) {
                if (!b.TryGetValue(entry.Key, out Dictionary<string, float> other)) { return false; }
                if (!FloatsEqual(entry.Value, other)) { return false; }
            }
            return true;
        }

        private static bool FloatsEqual(Dictionary<string, float> a, Dictionary<string, float> b) {
            int acount = a == null ? 0 : a.Count;
            if (acount != (b == null ? 0 : b.Count)) { return false; }
            if (acount == 0) { return true; }
            foreach (KeyValuePair<string, float> entry in a) {
                if (!b.TryGetValue(entry.Key, out float other) || other != entry.Value) { return false; }
            }
            return true;
        }

        private static bool SpawnEquals(SpawnPoints a, SpawnPoints b) {
            if (a == null || b == null) { return a == b; }
            return a.HaveCustomSpawn == b.HaveCustomSpawn
                   && a.SpawnX == b.SpawnX && a.SpawnY == b.SpawnY && a.SpawnZ == b.SpawnZ
                   && a.HomeX == b.HomeX && a.HomeY == b.HomeY && a.HomeZ == b.HomeZ;
        }

        private static SpawnPoints CopySpawn(SpawnPoints source) {
            return new SpawnPoints {
                HaveCustomSpawn = source.HaveCustomSpawn,
                SpawnX = source.SpawnX, SpawnY = source.SpawnY, SpawnZ = source.SpawnZ,
                HomeX = source.HomeX, HomeY = source.HomeY, HomeZ = source.HomeZ,
            };
        }

        private static bool CustomDataEquals(Dictionary<string, string> a, Dictionary<string, string> b) {
            int acount = a == null ? 0 : a.Count;
            int bcount = b == null ? 0 : b.Count;
            if (acount != bcount) { return false; }
            if (acount == 0) { return true; }
            foreach (KeyValuePair<string, string> kvp in a) {
                if (!b.TryGetValue(kvp.Key, out string other) || other != kvp.Value) { return false; }
            }
            return true;
        }
    }
}
