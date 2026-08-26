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
    /// confiscated, skills above the stored level are clamped, custom data is reset to the stored copy. That is
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
            internal bool LenientDirtyRemoval;  // ItemRemovalForDirtyReconnection

            internal bool AnyEnabled {
                get { return RemoveUntracked || ClampSkills || ResetCustomData; }
            }
        }

        /// <summary>Main thread only. Reads the current settings into a Policy.</summary>
        internal static Policy Current() {
            return new Policy {
                RemoveUntracked = ValConfig.RemoveNontrackedItemsFromJoiningPlayers.Value,
                ClampSkills = ValConfig.PreventExternalSkillRaises.Value,
                ResetCustomData = ValConfig.PreventExternalCustomDataChanges.Value,
                LenientDirtyRemoval = ValConfig.ItemRemovalForDirtyReconnection.Value,
            };
        }

        internal sealed class Result {
            internal int ItemsConfiscated;
            internal int SkillsClamped;
            internal bool CustomDataReset;

            internal bool Changed {
                get { return ItemsConfiscated > 0 || SkillsClamped > 0 || CustomDataReset; }
            }

            internal string Describe() {
                List<string> parts = new List<string>();
                if (ItemsConfiscated > 0) { parts.Add($"{ItemsConfiscated} item(s) confiscated"); }
                if (SkillsClamped > 0) { parts.Add($"{SkillsClamped} skill(s) clamped"); }
                if (CustomDataReset) { parts.Add("custom data reset"); }
                return parts.Count == 0 ? "nothing to do" : string.Join(", ", parts.ToArray());
            }
        }

        /// <summary>
        /// Reconciles an uploaded character <paramref name="incoming"/> to the server's <paramref name="stored"/>
        /// copy. Mutates <paramref name="incoming"/> in place (its item list is trimmed, confiscations appended,
        /// skills clamped, custom data reset) and returns what changed. Confiscations are recorded on the
        /// incoming character, whose ConfiscatedItems list is the server-owned one by the time this runs.
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
                    if (incoming.SkillLevels[skill] > storedLevel) {
                        incoming.SkillLevels[skill] = storedLevel;
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
