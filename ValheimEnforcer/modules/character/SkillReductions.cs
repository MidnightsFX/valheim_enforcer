using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ValheimEnforcer.common;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.modules.character {

    /// <summary>What taking records off a character's skill-reduction list actually did.</summary>
    internal sealed class SkillReductionChange {
        /// <summary>False when the server has no save for that account and character at all.</summary>
        internal bool CharacterFound;
        internal DataObjects.Character Character;
        /// <summary>Records the filter matched, already removed from the character's list.</summary>
        internal List<SkillReduction> Taken = new List<SkillReduction>();
        internal int TotalBefore;
        internal int Remaining;

        internal string Describe() {
            return string.Join(", ", Taken
                .GroupBy(record => record.Skill)
                .OrderBy(group => group.Key.ToString())
                .Select(group => $"{group.Key} x{group.Count()}"));
        }
    }

    /// <summary>
    /// The record of every skill this mod has forcibly lowered, and the means of putting one back.
    ///
    /// Two of the Player Sync rules lower a skill: PreventExternalSkillRaises clamps a returning character to the
    /// level the server holds, and NewCharacterSetSkillsToZero zeroes a first-time character. Both are right in
    /// the case they exist for and both are occasionally wrong - a save that went stale over a crash, a player
    /// treated as new because the mod was installed after they were - and until now the level a skill was lowered
    /// from was simply gone. Each lowering is now written into the character's save as a
    /// <see cref="SkillReduction"/> (RecordSkillReductions), listed by enforcer-skills-list, and undone by
    /// enforcer-skills-restore, which puts each skill back to the highest level it was recorded being lowered from.
    ///
    /// The shape follows <see cref="ConfiscatedItems"/> deliberately, because it is the same problem: the record
    /// is server-owned and merged by id, the commands edit the stored save, and an online player is told over an
    /// RPC so their tracked copy cannot re-report what the admin just removed. The one addition is
    /// <see cref="DataObjects.Character.PendingSkillRestores"/>: a skill cannot be dropped into a save the way an
    /// item can, so the restore is left pending on the server until the client is seen holding the level - on the
    /// next join for an offline player, straight away for an online one.
    /// </summary>
    internal static class SkillReductions {

        internal static bool Enabled {
            get { return ValConfig.RecordSkillReductions != null && ValConfig.RecordSkillReductions.Value; }
        }

        // How far above the stored level a skill has to sit before that counts as a gain. A level that has been
        // through the general YAML serializer - a YAML delta, or any save written with FastCharacterWriter off -
        // comes back rounded to seven significant digits, so the live value it was rounded from reads as "above
        // the stored level" by a millionth on the next join. On one real server 35 of 39 recorded reductions were
        // exactly that, the same four skills on the same character join after join. A level moves in whole steps
        // (progress toward the next one is held separately), so nothing worth taking back is this small.
        internal const float LevelTolerance = 0.001f;

        /// <summary>Whether <paramref name="level"/> exceeds <paramref name="ceiling"/> by more than rounding.
        /// NaN is never above anything, as with the plain comparison this replaces.</summary>
        internal static bool IsAbove(float level, float ceiling) {
            return level > ceiling + LevelTolerance;
        }

        /// <summary>Client side (main thread): write down a reduction, if the setting says to. The server-side
        /// rules read the same setting through their Policy snapshot instead.</summary>
        internal static void Record(DataObjects.Character character, Skills.SkillType skill, float from, float to, string reason) {
            if (!Enabled || character == null) { return; }
            character.AddSkillReduction(skill, from, to, reason);
        }

        /// <summary>The records as stored, without changing anything. <paramref name="character"/> is null when
        /// the save could not be read.</summary>
        internal static List<SkillReduction> Peek(string account, string name, out DataObjects.Character character) {
            character = ValConfig.LoadCharacterFromSave(account, name);
            if (character?.SkillReductions == null) { return new List<SkillReduction>(); }
            return new List<SkillReduction>(character.SkillReductions);
        }

        /// <summary>
        /// Removes matching records from the stored character and hands them back, leaving the loaded character
        /// on the result for the caller to finish with. Nothing is written until <see cref="Persist"/>.
        /// </summary>
        /// <param name="skills">null means every record.</param>
        internal static SkillReductionChange Take(string account, string name, List<Skills.SkillType> skills) {
            SkillReductionChange change = new SkillReductionChange();
            DataObjects.Character character = ValConfig.LoadCharacterFromSave(account, name);
            if (character == null) { return change; }

            change.CharacterFound = true;
            change.Character = character;
            List<SkillReduction> records = character.SkillReductions ?? new List<SkillReduction>();
            change.TotalBefore = records.Count;

            List<SkillReduction> kept = new List<SkillReduction>();
            foreach (SkillReduction record in records) {
                if (record == null) { continue; }
                if (skills == null || skills.Contains(record.Skill)) {
                    change.Taken.Add(record);
                } else {
                    kept.Add(record);
                }
            }
            character.SkillReductions = kept.Count == 0 ? null : kept;
            change.Remaining = kept.Count;
            return change;
        }

        /// <summary>
        /// Writes the edited character back. Paired with a store invalidation for the same reason
        /// <see cref="ConfiscatedItems.Persist"/> is: this bypasses the async store, and a cached copy left behind
        /// would be rewritten from pre-edit state on the next delta.
        /// </summary>
        internal static void Persist(string account, string name, DataObjects.Character character) {
            if (character == null) { return; }
            ValConfig.WritePlayerCharacterToSave(account, character);
            CharacterStore.Invalidate(account, name);
        }

        /// <summary>
        /// Drops records matching an admin's restore or clear from the character this client is tracking. The
        /// in-memory copy is pushed back on every full save, so without this the records the admin just removed
        /// would be re-appended.
        /// </summary>
        internal static int ClearTrackedLocally(List<Skills.SkillType> skills) {
            DataObjects.Character tracked = CharacterManager.PlayerCharacter;
            if (tracked?.SkillReductions == null || tracked.SkillReductions.Count == 0) { return 0; }
            int before = tracked.SkillReductions.Count;
            if (skills == null) {
                tracked.SkillReductions = null;
                return before;
            }
            tracked.SkillReductions.RemoveAll(record => record != null && skills.Contains(record.Skill));
            int after = tracked.SkillReductions.Count;
            if (after == 0) { tracked.SkillReductions = null; }
            return before - after;
        }

        /// <summary>
        /// The level each skill goes back to: the highest it was recorded being lowered from, bounded to what the
        /// game can hold. A record whose "from" is not a usable level (NaN from a crafted payload, or nothing above
        /// zero) contributes nothing.
        /// </summary>
        internal static Dictionary<Skills.SkillType, float> RestoreTargets(List<SkillReduction> records) {
            Dictionary<Skills.SkillType, float> targets = new Dictionary<Skills.SkillType, float>();
            if (records == null) { return targets; }
            foreach (SkillReduction record in records) {
                if (record == null || !(record.From > 0f)) { continue; } // also rejects NaN
                float level = Math.Min(record.From, SkillClamp.MaxSkillLevel);
                if (!targets.TryGetValue(record.Skill, out float current) || level > current) {
                    targets[record.Skill] = level;
                }
            }
            return targets;
        }

        /// <summary>
        /// Raises a live player's skills to the given levels. Raise only: a skill already at or above its target is
        /// left alone, so a restore can never lower anything, and the progress accumulated toward the next level is
        /// kept. Returns how many skills were changed.
        /// </summary>
        internal static int ApplyToLivePlayer(Player player, Dictionary<Skills.SkillType, float> levels, string reason) {
            if (player == null || levels == null || levels.Count == 0) { return 0; }
            int raised = 0;
            Skills skills = player.GetSkills();
            foreach (KeyValuePair<Skills.SkillType, float> target in levels) {
                if (!(target.Value > 0f)) { continue; }
                float level = Math.Min(target.Value, SkillClamp.MaxSkillLevel);
                Skills.Skill skill = FindOrCreateSkill(skills, target.Key);
                if (skill == null) {
                    Logger.LogWarning($"{reason}: this client has no definition for the {target.Key} skill, so it cannot be restored to {Level(level)}; skipping it.");
                    continue;
                }
                if (skill.m_level >= level) { continue; }
                Logger.LogInfo($"{reason}: raising {target.Key} from {Level(skill.m_level)} to {Level(level)} for {player.GetPlayerName()}.");
                skill.m_level = level;
                raised++;
            }
            return raised;
        }

        /// <summary>
        /// The live entry for a skill, created when the player has none yet - which is every skill on a character
        /// recreated from scratch, the case the join restore exists for. Only created when this client has a
        /// definition for it: the game walks its skill list assuming every entry has one, so a skill from a mod this
        /// client is not running is left alone and reported rather than added without.
        /// </summary>
        private static Skills.Skill FindOrCreateSkill(Skills skills, Skills.SkillType type) {
            foreach (Skills.Skill existing in skills.GetSkillList()) {
                if (existing?.m_info != null && existing.m_info.m_skill == type) { return existing; }
            }
            if (skills.GetSkillDef(type) == null) { return null; }
            return skills.GetSkill(type);
        }

        /// <summary>
        /// Join, returning character: raise every live skill that sits below the level the record holds. The skill
        /// counterpart of AddMissingItemsFromPlayerServerSave, and what brings a character back after its local save
        /// was deleted and recreated - the server still holds their progress, and without this the recreated
        /// character's blank skills would be pushed up as the new record on the first save. Raise only, and the
        /// record itself is untouched: it already says what the live skills now say.
        /// </summary>
        internal static void RestoreOnJoin(Player player, DataObjects.Character saved) {
            if (player == null || saved?.SkillLevels == null || saved.SkillLevels.Count == 0) { return; }
            int raised = ApplyToLivePlayer(player, saved.SkillLevels, "Join restore (below the stored level)");
            if (raised > 0) {
                Logger.LogInfo($"Restored {raised} skill(s) for {saved.Name} that arrived below the level the server holds.");
            }
        }

        /// <summary>
        /// Join, returning character: apply whatever an admin restored while this player was away. Runs before
        /// the external-skill-raise clamp and lifts the record's own level with it, so the clamp does not read the
        /// restored level as an external gain. The pending entries are dropped from this copy; on a dedicated
        /// server the server keeps its own until the save that follows confirms them.
        /// </summary>
        internal static void ApplyPendingOnJoin(Player player, DataObjects.Character saved) {
            if (player == null || saved?.PendingSkillRestores == null || saved.PendingSkillRestores.Count == 0) { return; }
            if (saved.SkillLevels == null) { saved.SkillLevels = new Dictionary<Skills.SkillType, float>(); }

            foreach (KeyValuePair<Skills.SkillType, float> pending in saved.PendingSkillRestores) {
                if (!(pending.Value > 0f)) { continue; }
                float level = Math.Min(pending.Value, SkillClamp.MaxSkillLevel);
                if (!saved.SkillLevels.TryGetValue(pending.Key, out float recorded) || recorded < level) {
                    saved.SkillLevels[pending.Key] = level;
                }
            }
            int raised = ApplyToLivePlayer(player, saved.PendingSkillRestores, "Admin restore (applied on join)");
            Logger.LogInfo($"Applied {saved.PendingSkillRestores.Count} pending skill restore(s) for {saved.Name} on join; {raised} skill(s) were below the restored level and raised.");
            saved.PendingSkillRestores = null;
        }

        /// <summary>
        /// Parses the wire form of a skill filter back into the list Take expects. 'all' becomes null, which is
        /// how "every record" is spelled throughout this file. An unparseable name is skipped rather than refused:
        /// the server validated the filter before sending it.
        /// </summary>
        internal static List<Skills.SkillType> ParseFilter(string filter) {
            if (string.IsNullOrEmpty(filter) || string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase)) { return null; }
            List<Skills.SkillType> skills = new List<Skills.SkillType>();
            foreach (string token in filter.Split(',')) {
                if (TryParseSkill(token, out Skills.SkillType skill)) { skills.Add(skill); }
            }
            return skills;
        }

        /// <summary>A skill by name, case-insensitively, or by number - which is how a skill another mod added
        /// appears in a save, since it has no name in the game's own list.</summary>
        internal static bool TryParseSkill(string token, out Skills.SkillType skill) {
            skill = default;
            token = token?.Trim();
            if (string.IsNullOrEmpty(token)) { return false; }
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)) {
                skill = (Skills.SkillType)number;
                return true;
            }
            return Enum.TryParse(token, true, out skill) && Enum.IsDefined(typeof(Skills.SkillType), skill);
        }

        /// <summary>Levels are floats that can carry the fraction a death leaves behind; two decimals is plenty.</summary>
        internal static string Level(float level) {
            return level.ToString("0.##", CultureInfo.InvariantCulture);
        }

        internal static string Describe(Dictionary<Skills.SkillType, float> levels) {
            if (levels == null || levels.Count == 0) { return "nothing"; }
            return string.Join(", ", levels
                .OrderBy(kvp => kvp.Key.ToString())
                .Select(kvp => $"{kvp.Key} -> {Level(kvp.Value)}"));
        }
    }
}
