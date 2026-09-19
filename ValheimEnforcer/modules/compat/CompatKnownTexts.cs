using System;
using System.Collections.Generic;
using System.Linq;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.modules.compat {
    /// <summary>
    /// Known-text keys owned by mods that keep per-player progression there, and so must not be enforced
    /// against the server's copy between joins.
    ///
    /// Vanilla's m_knownTexts is the compendium: the runestone and lore texts a character has read. It is a
    /// plain Dictionary&lt;string, string&gt; on the Player, saved into the .fch beside m_customData, and nothing
    /// in the game namespaces its keys - so it is a tempting place for a mod to park a handful of numbers.
    /// EpicMMO does exactly that. Level, experience and every attribute point live in one flat entry each,
    /// all prefixed with its plugin name (EpicMMOSystem_LevelSystem_Level, _CurrentExp, _TotalExp, and one
    /// per attribute), plus EpicMMOSystem_friend_list; it hides them from the compendium UI with a
    /// Player.GetKnownTexts postfix that filters on that same prefix.
    ///
    /// That matters here because ProgressionSync enforces m_knownTexts wholesale - the stored copy replaces
    /// the live one on join - and progression rides FULL saves only. CharacterDeltaTracker never carries it,
    /// so the server's copy is current only as of the last full save, and enforcing a mod's key against it
    /// rolls back everything earned since. For an item that is the point; for a level counter the mod
    /// rewrites continuously it is just loss.
    ///
    /// The rule that follows is the one CompatCustomData already established: a key matched here is left
    /// entirely to the mod that owns it. It is never captured into the tracked character, never written into
    /// a server save, and never applied back onto a live player, whose own value always wins.
    ///
    /// What this deliberately does NOT cover is the new-character reset. A character arriving for the first
    /// time forgets what it learned elsewhere, mod progression included - see KnownTexts.ResetForNewCharacter.
    /// Pass-through governs ongoing enforcement, not the question of what a character may bring in with it,
    /// and the same asymmetry already holds for custom data: PassthroughCompatModCustomData exempts the
    /// ExtraSlots backup from tracking while newCharacterClearCustomData still clears it.
    ///
    /// Everything here must be safe off the main thread: the CharacterStore worker runs these checks through
    /// full-save ingestion. That is why the config value is mirrored into a volatile array refreshed from the
    /// main thread instead of the ConfigEntry being read directly.
    /// </summary>
    internal static class CompatKnownTexts {

        // Matched by prefix rather than by exact key, because these keys are composed rather than fixed -
        // EpicMMO builds one per attribute out of an enum name - and because a mod that namespaces its keys
        // by plugin name (the convention EpicMMO's own compendium filter relies on) is covered whole by a
        // single entry. Admin-configurable for the same reason CompatCustomData's list is not: the set of
        // mods doing this is open-ended, and an admin should not need a new build to add one.
        //
        // Replaced wholesale rather than mutated, so a worker thread that read the array a moment ago keeps
        // reading a consistent snapshot. Defaults to the shipped value so anything running before config
        // binding behaves like a default installation.
        private static volatile string[] prefixes = ParsePrefixes(DefaultPrefixes);

        internal const string DefaultPrefixes = "EpicMMOSystem";

        // The raw string the current array was parsed from, so a config reload that changes nothing does not
        // allocate. Only ever touched on the main thread, in RefreshPrefixes.
        private static string parsedFrom = DefaultPrefixes;

        /// <summary>Main thread only. Re-reads the config value; wired to its SettingChanged so a config
        /// reload or a server sync takes effect immediately.</summary>
        internal static void RefreshPrefixes() {
            string raw = ValConfig.KnownTextPassthroughPrefixes == null
                ? DefaultPrefixes
                : ValConfig.KnownTextPassthroughPrefixes.Value ?? "";
            if (raw == parsedFrom) { return; }
            prefixes = ParsePrefixes(raw);
            parsedFrom = raw;
        }

        private static string[] ParsePrefixes(string raw) {
            if (string.IsNullOrEmpty(raw)) { return new string[0]; }
            return raw.Split(',').Select(entry => entry.Trim()).Where(entry => entry.Length > 0).ToArray();
        }

        /// <summary>Whether this known-text key belongs to a mod rather than to the compendium. An empty
        /// prefix list turns pass-through off entirely, which is why no separate boolean exists.</summary>
        internal static bool IsPassthroughKey(string key) {
            if (string.IsNullOrEmpty(key)) { return false; }
            string[] current = prefixes;
            for (int i = 0; i < current.Length; i++) {
                if (key.StartsWith(current[i], StringComparison.Ordinal)) { return true; }
            }
            return false;
        }

        /// <summary>Removes pass-through keys from a tracked or stored known-text dictionary, in place.
        /// Null-safe, and a no-op when no prefixes are configured.</summary>
        internal static void StripPassthroughKeys(IDictionary<string, string> knownTexts) {
            if (knownTexts == null || knownTexts.Count == 0 || prefixes.Length == 0) { return; }
            List<string> doomed = null;
            foreach (string key in knownTexts.Keys) {
                if (!IsPassthroughKey(key)) { continue; }
                if (doomed == null) { doomed = new List<string>(); }
                doomed.Add(key);
            }
            if (doomed == null) { return; }
            foreach (string key in doomed) {
                knownTexts.Remove(key);
            }
        }

        /// <summary>
        /// A detached snapshot of live known texts with pass-through keys removed - the capture-side
        /// counterpart of <see cref="ApplyToPlayer"/>. Every site that records live known texts into the
        /// tracked character goes through this.
        /// </summary>
        internal static Dictionary<string, string> SnapshotForTracking(Dictionary<string, string> live) {
            Dictionary<string, string> snapshot = PackedItem.SnapshotCustomData(live);
            StripPassthroughKeys(snapshot);
            return snapshot;
        }

        /// <summary>
        /// Brings a live player's known texts into line with a stored copy, IN PLACE. The stored copy decides
        /// every compendium entry; pass-through keys are never taken from it, and whatever the player
        /// currently holds for them - the freshest truth, since the owning mod rewrites it as they play - is
        /// left exactly where it is.
        ///
        /// In place rather than by assignment, unlike CompatCustomData.ApplyToPlayer: that one replaces a
        /// dictionary the enforcer's own Player.Load postfix already reassigns, whereas nothing here owns
        /// m_knownTexts, and a mod holding a reference to it would quietly lose every later write.
        /// </summary>
        internal static void ApplyToPlayer(Dictionary<string, string> stored, IDictionary<string, string> live) {
            if (stored == null || live == null) { return; }

            List<string> doomed = null;
            foreach (string key in live.Keys) {
                if (IsPassthroughKey(key)) { continue; }
                if (doomed == null) { doomed = new List<string>(); }
                doomed.Add(key);
            }
            if (doomed != null) {
                foreach (string key in doomed) { live.Remove(key); }
            }

            foreach (KeyValuePair<string, string> text in stored) {
                // A stored pass-through key can only be a stale copy written before this rule existed, or by
                // an admin who has since added the prefix. Either way the live value is the authority.
                if (IsPassthroughKey(text.Key)) { continue; }
                live[text.Key] = text.Value;
            }
        }
    }
}
