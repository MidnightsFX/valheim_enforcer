using System;
using System.Collections.Generic;
using System.Linq;
using ValheimEnforcer.common;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.modules.character {

    /// <summary>
    /// What "this character has never been here before" means, in one place.
    ///
    /// A character arriving for the first time may have been played anywhere - a solo world, another server -
    /// and everything it is carrying was granted by something this server never saw. The NewCharacter* settings
    /// say how much of that to keep. Those rules are applied twice, deliberately:
    ///
    ///  - on the client, at join, because that is the only place with a live Player whose inventory and skills
    ///    can actually be changed; and
    ///  - on the server, on the first save it ever stores for the character, because the client is the thing
    ///    being defended against and a modified one simply would not run the first copy.
    ///
    /// So the rules live here as a pure transformation of a <see cref="DataObjects.Character"/>: no Player, no
    /// Unity API, no ZNet, no ObjectDB. That is what lets the server run them on the CharacterStore worker
    /// thread, and it is what keeps the two copies from drifting into disagreeing about what a new character
    /// is allowed to have.
    /// </summary>
    internal static class NewCharacterRules {

        /// <summary>
        /// An immutable snapshot of the settings, taken on the main thread.
        ///
        /// The CharacterStore worker must never read a ConfigEntry itself - the config can be reloaded from
        /// disk by the file watcher at any moment, and BepInEx makes no thread-safety promise about that. So
        /// the main thread captures the policy when it hands work off, and the worker only ever reads this.
        /// </summary>
        internal sealed class Policy {
            internal bool ZeroSkills;
            internal bool StripItems;
            internal bool ClearCustomData;
            internal bool ClearGuardianPower;
            internal bool ClearFoods;
            internal bool ConfiscateUnidentifiable;
            internal bool RecordReductions;     // RecordSkillReductions
            internal HashSet<string> StartingPrefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>False when every rule is off, in which case there is nothing to apply and the server
            /// side never needs to be told about a first save at all.</summary>
            internal bool AnyEnabled {
                get { return ZeroSkills || StripItems || ClearCustomData || ClearGuardianPower || ClearFoods; }
            }
        }

        /// <summary>Main thread only. Reads the current settings into a Policy.</summary>
        internal static Policy Current() {
            return new Policy {
                ZeroSkills = ValConfig.NewCharacterSetSkillsToZero.Value,
                StripItems = ValConfig.NewCharactersRemoveExtraItems.Value,
                // Nested under PreventExternalCustomDataChanges the same way the client's clear always has
                // been: an admin who is not tracking custom data at all has not asked us to police it.
                ClearCustomData = ValConfig.PreventExternalCustomDataChanges.Value && ValConfig.newCharacterClearCustomData.Value,
                ClearGuardianPower = ValConfig.NewCharacterClearForsakenPower.Value,
                // One setting both tracks foods and clears a new character's: food eaten anywhere but here is exactly
                // what tracking exists to keep out.
                ClearFoods = ValConfig.PreventExternalFoodChanges.Value,
                ConfiscateUnidentifiable = ValConfig.ConfiscateUnidentifiableItems.Value,
                RecordReductions = ValConfig.RecordSkillReductions.Value,
                StartingPrefabs = StartingPrefabs(),
            };
        }

        /// <summary>
        /// Whether a brand new character may keep this item. Quality is part of the question: a starting item
        /// is a starting item at quality 1, and an upgraded one was upgraded somewhere else.
        ///
        /// An item with no resolvable prefab name is never allowed. This is the one place fail-closed is
        /// clearly right - a character that has never played here has no legitimate untrackable inventory.
        /// </summary>
        internal static bool IsStartingItem(Policy policy, string prefabName, int quality) {
            if (policy == null) { return true; }
            if (string.IsNullOrEmpty(prefabName)) { return false; }
            if (quality > 1) { return false; }
            return policy.StartingPrefabs.Contains(prefabName);
        }

        /// <summary>What a run of <see cref="Apply"/> actually did, for the log line.</summary>
        internal sealed class Result {
            internal int ItemsRemoved;
            internal int SkillsZeroed;
            internal bool CustomDataCleared;
            internal bool GuardianPowerCleared;
            internal bool FoodsCleared;
            internal bool EffectsCleared;

            internal bool Changed {
                get { return ItemsRemoved > 0 || SkillsZeroed > 0 || CustomDataCleared || GuardianPowerCleared || FoodsCleared || EffectsCleared; }
            }

            internal string Describe() {
                List<string> parts = new List<string>();
                if (ItemsRemoved > 0) { parts.Add($"{ItemsRemoved} item(s) confiscated"); }
                if (SkillsZeroed > 0) { parts.Add($"{SkillsZeroed} skill(s) zeroed"); }
                if (CustomDataCleared) { parts.Add("custom data cleared"); }
                if (GuardianPowerCleared) { parts.Add("forsaken power cleared"); }
                if (FoodsCleared) { parts.Add("foods cleared"); }
                if (EffectsCleared) { parts.Add("status effects cleared"); }
                return parts.Count == 0 ? "nothing to do" : string.Join(", ", parts.ToArray());
            }
        }

        /// <summary>
        /// Applies the policy to a character. Pure data - safe to call from the CharacterStore worker thread.
        ///
        /// <paramref name="record"/> decides whether stripped items are written into the character's
        /// confiscated list, and zeroed skills into its skill-reduction record. It exists so each is recorded
        /// exactly once: whichever side actually removes the item (or zeroes the skill) in the save records it,
        /// and the other side reconciles a live player against the result without recording anything. Two
        /// recordings would mean two confiscation entries with two ids for one item, and an admin returning it
        /// would hand back two.
        /// </summary>
        internal static Result Apply(DataObjects.Character character, Policy policy, bool record) {
            Result result = new Result();
            if (character == null || policy == null) { return result; }

            if (policy.ZeroSkills && character.SkillLevels != null) {
                foreach (Skills.SkillType skill in character.SkillLevels.Keys.ToList()) {
                    float level = character.SkillLevels[skill];
                    if (level == 0) { continue; }
                    if (record && policy.RecordReductions) {
                        character.AddSkillReduction(skill, level, 0, "New character: skills set to zero");
                    }
                    character.SkillLevels[skill] = 0;
                    result.SkillsZeroed++;
                }
            }

            if (policy.ClearCustomData && character.PlayerCustomData != null && character.PlayerCustomData.Count > 0) {
                character.PlayerCustomData.Clear();
                result.CustomDataCleared = true;
            }

            if (policy.StripItems && character.PlayerItems != null) {
                List<PackedItem> kept = new List<PackedItem>();
                foreach (PackedItem item in character.PlayerItems) {
                    if (item == null) { continue; }
                    if (IsStartingItem(policy, item.prefabName, item.m_quality)) {
                        kept.Add(item);
                        continue;
                    }
                    result.ItemsRemoved++;
                    if (record) {
                        character.AddConfiscatedItem(item, ReasonFor(item));
                    }
                }
                character.PlayerItems = kept;
            }

            // Only a record that tracks the power can carry one to clear. With PreventExternalForsakenPowerChanges
            // off the field is null, and the live power is cleared on the client regardless - see
            // CharacterManager.BuildNewCharacter.
            if (policy.ClearGuardianPower && !string.IsNullOrEmpty(character.GuardianPower)) {
                character.GuardianPower = "";
                result.GuardianPowerCleared = true;
            }

            // Tracking is on, so the record should say "no food" rather than "not tracked". A save that arrives with
            // no foods at all under this policy came from a client that is not running the rule, and is cleared too -
            // left null, the character's next join would adopt whatever they are carrying.
            if (policy.ClearFoods && (character.Foods == null || character.Foods.Count > 0)) {
                character.Foods = new List<PackedFood>();
                result.FoodsCleared = true;
            }

            // A character that arrives buffed was buffed somewhere else. Cleared whenever an item, skill or
            // custom data rule is on, rather than under a setting of its own: there is no coherent policy where
            // the items and skills a solo world granted are removed but the rested and mead bonuses it granted
            // are kept. The Forsaken Power and food rules are deliberately not among them - each answers a narrower
            // question, and switching one on by itself should not start stripping status effects. (Eaten food is
            // not a status effect; FoodsCleared above covers it.)
            if ((policy.ZeroSkills || policy.StripItems || policy.ClearCustomData) && character.ActiveCharacterEffects != null && character.ActiveCharacterEffects.Count > 0) {
                character.ActiveCharacterEffects.Clear();
                result.EffectsCleared = true;
            }

            return result;
        }

        private static string ReasonFor(PackedItem item) {
            if (string.IsNullOrEmpty(item.prefabName)) {
                return "New character, item has no resolvable prefab";
            }
            if (item.m_quality > 1) {
                return $"New character, item upgraded elsewhere (quality {item.m_quality})";
            }
            return "New character, non-starter item";
        }

        // Parsed once and rebuilt only when the setting actually changes - the same shape as
        // AccountCharacterLimit.ExemptIds. The set is replaced wholesale rather than mutated, so a worker
        // thread holding a Policy that references an older one keeps reading a consistent snapshot.
        private static string startingRaw;
        private static HashSet<string> startingParsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static HashSet<string> StartingPrefabs() {
            string raw = ValConfig.NewCharacterStartingItems?.Value ?? "";
            if (raw != startingRaw) {
                startingParsed = new HashSet<string>(
                    raw.Split(',').Select(entry => entry.Trim()).Where(entry => entry.Length > 0),
                    StringComparer.OrdinalIgnoreCase);
                startingRaw = raw;
            }
            return startingParsed;
        }
    }
}
