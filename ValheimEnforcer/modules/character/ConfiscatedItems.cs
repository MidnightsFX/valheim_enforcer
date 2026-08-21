using System;
using System.Collections.Generic;
using System.Linq;
using ValheimEnforcer.common;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.modules.character {

    /// <summary>What taking items off a character's confiscated list actually did.</summary>
    internal sealed class ConfiscationChange {
        /// <summary>False when the server has no save for that account and character at all.</summary>
        internal bool CharacterFound;
        internal DataObjects.Character Character;
        /// <summary>Entries the filter matched, already removed from the character's confiscated list.</summary>
        internal List<PackedItem> Taken = new List<PackedItem>();
        internal int TotalBefore;
        internal int Remaining;

        internal string Describe() {
            return string.Join(", ", Taken
                .GroupBy(item => item.prefabName)
                .Select(group => $"{group.Key} x{group.Sum(item => item.m_stack)}"));
        }
    }

    /// <summary>
    /// Reads and edits the confiscated-item list on a stored character.
    ///
    /// Split out of the old CommandHelpers, which reported through the log instead of returning anything, so
    /// a command could not tell its caller what had happened. Two bugs came with that: filtering to specific
    /// prefabs removed the entries in memory and never wrote the file, so a targeted clear silently did
    /// nothing at all; and a missing character save went straight into a null dereference rather than a
    /// message. Both are fixed here, and every path now reports what it did.
    /// </summary>
    internal static class ConfiscatedItems {

        /// <summary>The confiscated list as stored, without changing anything.</summary>
        internal static List<PackedItem> Peek(string account, string name, out bool characterFound) {
            DataObjects.Character character = ValConfig.LoadCharacterFromSave(account, name);
            characterFound = character != null;
            if (character == null || character.ConfiscatedItems == null) { return new List<PackedItem>(); }
            return new List<PackedItem>(character.ConfiscatedItems);
        }

        /// <summary>
        /// Removes matching entries from the character's confiscated list and hands them back, leaving the
        /// loaded character on the result so the caller can decide what happens next - dropped for a clear,
        /// moved into the player's items for a return. Nothing is written until Persist is called.
        /// </summary>
        /// <param name="prefabs">null means every confiscated entry.</param>
        internal static ConfiscationChange Take(string account, string name, List<string> prefabs) {
            ConfiscationChange change = new ConfiscationChange();
            DataObjects.Character character = ValConfig.LoadCharacterFromSave(account, name);
            if (character == null) { return change; }

            change.CharacterFound = true;
            change.Character = character;
            if (character.ConfiscatedItems == null) { character.ConfiscatedItems = new List<PackedItem>(); }
            change.TotalBefore = character.ConfiscatedItems.Count;

            if (prefabs == null) {
                change.Taken = new List<PackedItem>(character.ConfiscatedItems);
                character.ConfiscatedItems.Clear();
            } else {
                // Partitioned in one pass rather than filtered and then removed by value. Two confiscated
                // entries that differ only in reason, timestamp or id compare equal (see PackedItem), so any
                // removal keyed on equality decides the fate of both together.
                List<PackedItem> kept = new List<PackedItem>();
                foreach (PackedItem item in character.ConfiscatedItems) {
                    if (item != null && prefabs.Any(prefab => string.Equals(prefab, item.prefabName, StringComparison.OrdinalIgnoreCase))) {
                        change.Taken.Add(item);
                    } else {
                        kept.Add(item);
                    }
                }
                character.ConfiscatedItems = kept;
            }

            change.Remaining = character.ConfiscatedItems.Count;
            return change;
        }

        /// <summary>
        /// Writes the edited character back. Always paired with a store invalidation: this write bypasses the
        /// async character store, so a cached copy left behind would be rewritten from pre-edit state on the
        /// next delta and bring the entries straight back.
        /// </summary>
        internal static void Persist(string account, string name, DataObjects.Character character) {
            if (character == null) { return; }
            ValConfig.WritePlayerCharacterToSave(account, character);
            CharacterStore.Invalidate(account, name);
        }

        /// <summary>
        /// Drops confiscated entries matching an admin's clear from the character this client is tracking.
        /// The in-memory copy is what gets pushed back to the server, so without this the entries the admin
        /// just removed would be re-appended on this session's next full push.
        /// </summary>
        internal static int ClearTrackedLocally(List<string> prefabs) {
            DataObjects.Character tracked = CharacterManager.PlayerCharacter;
            if (tracked?.ConfiscatedItems == null || tracked.ConfiscatedItems.Count == 0) { return 0; }

            int before = tracked.ConfiscatedItems.Count;
            if (prefabs == null) {
                tracked.ConfiscatedItems.Clear();
            } else {
                tracked.ConfiscatedItems.RemoveAll(item => item != null
                    && prefabs.Any(prefab => string.Equals(prefab, item.prefabName, StringComparison.OrdinalIgnoreCase)));
            }
            return before - tracked.ConfiscatedItems.Count;
        }

        /// <summary>
        /// Parses the wire form of an item filter back into the list Take expects. 'all' becomes null, which
        /// is how "everything" is spelled throughout this file.
        /// </summary>
        internal static List<string> ParseFilter(string filter) {
            if (string.IsNullOrEmpty(filter) || string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase)) {
                return null;
            }
            return filter.Split(',').Select(entry => entry.Trim()).Where(entry => entry.Length > 0).ToList();
        }
    }
}
