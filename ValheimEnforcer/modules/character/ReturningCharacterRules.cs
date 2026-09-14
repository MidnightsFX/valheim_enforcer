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

            internal bool AnyEnabled {
                get { return RemoveUntracked || ClampSkills || ResetCustomData || ResetGuardianPower || ResetFoods; }
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
            };
        }

        internal sealed class Result {
            internal int ItemsConfiscated;
            internal int SkillsClamped;
            internal bool CustomDataReset;
            internal bool GuardianPowerReset;
            internal bool FoodsReset;

            internal bool Changed {
                get { return ItemsConfiscated > 0 || SkillsClamped > 0 || CustomDataReset || GuardianPowerReset || FoodsReset; }
            }

            internal string Describe() {
                List<string> parts = new List<string>();
                if (ItemsConfiscated > 0) { parts.Add($"{ItemsConfiscated} item(s) confiscated"); }
                if (SkillsClamped > 0) { parts.Add($"{SkillsClamped} skill(s) clamped"); }
                if (CustomDataReset) { parts.Add("custom data reset"); }
                if (GuardianPowerReset) { parts.Add("forsaken power reset"); }
                if (FoodsReset) { parts.Add("foods reset"); }
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
                    if (reported > ceiling) {
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

            return result;
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
