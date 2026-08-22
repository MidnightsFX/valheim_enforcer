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
    /// and everything it is carrying was granted by something this server never saw. The three NewCharacter*
    /// settings say how much of that to keep. Those rules are applied twice, deliberately:
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
            internal bool ConfiscateUnidentifiable;
            internal HashSet<string> StartingPrefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>False when every rule is off, in which case there is nothing to apply and the server
            /// side never needs to be told about a first save at all.</summary>
            internal bool AnyEnabled {
                get { return ZeroSkills || StripItems || ClearCustomData; }
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
                ConfiscateUnidentifiable = ValConfig.ConfiscateUnidentifiableItems.Value,
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
            internal bool EffectsCleared;

            internal bool Changed {
                get { return ItemsRemoved > 0 || SkillsZeroed > 0 || CustomDataCleared || EffectsCleared; }
            }

            internal string Describe() {
                List<string> parts = new List<string>();
                if (ItemsRemoved > 0) { parts.Add($"{ItemsRemoved} item(s) confiscated"); }
                if (SkillsZeroed > 0) { parts.Add($"{SkillsZeroed} skill(s) zeroed"); }
                if (CustomDataCleared) { parts.Add("custom data cleared"); }
                if (EffectsCleared) { parts.Add("status effects cleared"); }
                return parts.Count == 0 ? "nothing to do" : string.Join(", ", parts.ToArray());
            }
        }

        /// <summary>
        /// Applies the policy to a character. Pure data - safe to call from the CharacterStore worker thread.
        ///
        /// <paramref name="recordConfiscation"/> decides whether stripped items are written into the
        /// character's confiscated list. It exists so an item is recorded exactly once: whichever side
        /// actually removes the item from the save records it, and the other side reconciles a live inventory
        /// against the result without recording anything. Two recordings would mean two confiscation entries
        /// with two ids for one item, and an admin returning it would hand back two.
        /// </summary>
        internal static Result Apply(DataObjects.Character character, Policy policy, bool recordConfiscation) {
            Result result = new Result();
            if (character == null || policy == null) { return result; }

            if (policy.ZeroSkills && character.SkillLevels != null) {
                foreach (Skills.SkillType skill in character.SkillLevels.Keys.ToList()) {
                    if (character.SkillLevels[skill] == 0) { continue; }
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
                    if (recordConfiscation) {
                        character.AddConfiscatedItem(item, ReasonFor(item));
                    }
                }
                character.PlayerItems = kept;
            }

            // A character that arrives buffed was buffed somewhere else. Cleared whenever any rule is on,
            // rather than under a fourth setting: there is no coherent policy where the items and skills a
            // solo world granted are removed but the food and rested bonuses it granted are kept.
            if (policy.AnyEnabled && character.ActiveCharacterEffects != null && character.ActiveCharacterEffects.Count > 0) {
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
