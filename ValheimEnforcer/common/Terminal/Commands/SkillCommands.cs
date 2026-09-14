using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ValheimEnforcer.modules.character;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.common {
    internal static partial class TerminalManager {

        private const string SkillUsage = "Format: <accountId> <characterName> <all|skill,skill>";

        private static void RegisterSkillCommands() {
            _ = new EnforcerCommand("enforcer-skills-list",
                "Lists every skill this mod has lowered for one character - the level it was lowered from and to, when and why - and any restore waiting to be applied, without changing anything. Format: <accountId> <characterName>. eg: enforcer-skills-list 76561198012345678 Bjorn",
                SkillsList, CommandArea.Skills, AccountThenCharacter,
                serverAuthoritative: true, requiresAdmin: true);

            _ = new EnforcerCommand("enforcer-skills-restore",
                $"Puts a character's lowered skills back to the highest level each was recorded being lowered from - straight away if they are online, on their next join if not. Never lowers a skill. {SkillUsage}. eg: enforcer-skills-restore 76561198012345678 Bjorn all",
                SkillsRestore, CommandArea.Skills, AccountThenCharacterThenSkill,
                serverAuthoritative: true, requiresAdmin: true);

            _ = new EnforcerCommand("enforcer-skills-clear",
                $"Forgets recorded skill reductions without restoring anything. {SkillUsage}. eg: enforcer-skills-clear 76561198012345678 Bjorn Swords",
                SkillsClear, CommandArea.Skills, AccountThenCharacterThenSkill,
                serverAuthoritative: true, requiresAdmin: true);
        }

        // Argument 3 completes on the skills that actually have a record for the character already typed, plus 'all'.
        private static List<string> AccountThenCharacterThenSkill(string[] input) {
            if (input.Length == 4) { return TerminalArgs.RecordedSkills(input); }
            return AccountThenCharacter(input);
        }

        private static void SkillsList(EnforcerCommandArgs args) {
            string usage = "Format: enforcer-skills-list <accountId> <characterName>";
            if (!ReadTarget(args, usage, out string account, out string name)) { return; }

            List<SkillReduction> records = SkillReductions.Peek(account, name, out DataObjects.Character character);
            if (character == null) {
                args.Output.Error($"Could not read the save for {name} under account {account}.");
                return;
            }

            // The listing itself is for the person who asked; only the summary is worth a log line.
            foreach (SkillReduction record in records.OrderBy(record => record.Time)) {
                args.Output.Detail($"  {record.Skill}: {SkillReductions.Level(record.From)} -> {SkillReductions.Level(record.To)} on {record.Time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC - {record.Reason}", log: false);
            }
            if (character.PendingSkillRestores != null && character.PendingSkillRestores.Count > 0) {
                args.Output.Warning($"  Restore waiting to be applied on their next join: {SkillReductions.Describe(character.PendingSkillRestores)}", log: false);
            }

            if (records.Count == 0) {
                args.Output.Info($"{name} has no recorded skill reductions.");
                return;
            }
            args.Output.Info($"{name} has {records.Count} recorded skill reduction(s). Put them back with enforcer-skills-restore {account} {name} all, or forget them with enforcer-skills-clear.");
        }

        private static void SkillsClear(EnforcerCommandArgs args) {
            string usage = "Format: enforcer-skills-clear <accountId> <characterName> <all|skill,skill>";
            if (!ReadTarget(args, usage, out string account, out string name)) { return; }
            if (!args.ReadSkillFilter(2, usage, out string raw, out List<Skills.SkillType> skills)) { return; }

            SkillReductionChange change = SkillReductions.Take(account, name, skills);
            if (!change.CharacterFound) {
                args.Output.Error($"Could not read the save for {name} under account {account}.");
                return;
            }
            if (change.TotalBefore == 0) {
                args.Output.Info($"{name} had no recorded skill reductions, so nothing was cleared.");
                return;
            }
            if (change.Taken.Count == 0) {
                args.Output.Warning($"Nothing matched '{raw}'. {name} still has {change.Remaining} recorded reduction(s) - run enforcer-skills-list to see them.");
                return;
            }

            SkillReductions.Persist(account, name, change.Character);

            // Whoever holds this character also holds their own copy of the records and pushes it back on the
            // next full save, so that copy has to be cleared too or the entries reappear.
            string reach = TellClientAboutSkillRecords(account, name, raw, skills);
            args.Output.Info($"Forgot {change.Taken.Count} recorded reduction(s) for {name} ({change.Describe()}). {change.Remaining} left. {reach}");
        }

        private static void SkillsRestore(EnforcerCommandArgs args) {
            string usage = "Format: enforcer-skills-restore <accountId> <characterName> <all|skill,skill>";
            if (!ReadTarget(args, usage, out string account, out string name)) { return; }
            if (!args.ReadSkillFilter(2, usage, out string raw, out List<Skills.SkillType> skills)) { return; }

            SkillReductionChange change = SkillReductions.Take(account, name, skills);
            if (!change.CharacterFound) {
                args.Output.Error($"Could not read the save for {name} under account {account}.");
                return;
            }
            if (change.TotalBefore == 0) {
                args.Output.Info($"{name} has no recorded skill reductions, so there is nothing to restore.");
                return;
            }
            if (change.Taken.Count == 0) {
                args.Output.Warning($"Nothing matched '{raw}'. {name} still has {change.Remaining} recorded reduction(s) - run enforcer-skills-list to see them.");
                return;
            }

            // A skill the player has since levelled past its recorded level needs nothing; say so rather than
            // silently doing nothing about it. The stored level can lag the live one by a delta window, which is
            // fine: the client's own apply is raise-only too.
            Dictionary<Skills.SkillType, float> targets = SkillReductions.RestoreTargets(change.Taken);
            foreach (Skills.SkillType skill in targets.Keys.ToList()) {
                if (change.Character.SkillLevels != null
                    && change.Character.SkillLevels.TryGetValue(skill, out float stored) && stored >= targets[skill]) {
                    args.Output.Detail($"  {skill} is already at {SkillReductions.Level(stored)}, at or above the {SkillReductions.Level(targets[skill])} it was lowered from; nothing to restore.", log: false);
                    targets.Remove(skill);
                }
            }

            if (targets.Count == 0) {
                SkillReductions.Persist(account, name, change.Character);
                string reachNothing = TellClientAboutSkillRecords(account, name, raw, skills);
                args.Output.Info($"Nothing to restore for {name}: every matching skill is already at or above the level it was lowered from. {change.Taken.Count} record(s) forgotten, {change.Remaining} left. {reachNothing}");
                return;
            }

            // A listen host is not one of its own peers, so an admin restoring their own character would fall
            // through to the offline path and leave a restore pending on a save they are not about to reload.
            if (IsLocalCharacter(name)) {
                int raised = SkillReductions.ApplyToLivePlayer(Player.m_localPlayer, targets, "Admin restore");
                SkillReductions.Persist(account, name, change.Character);
                SkillReductions.ClearTrackedLocally(skills);
                // Rewrites the save from the tracked copy, which now carries the raised levels and no records.
                CharacterManager.SavePlayerCharacter(Player.m_localPlayer);
                args.Output.Info($"Restored {raised} of your skill(s) ({SkillReductions.Describe(targets)}). {change.Remaining} recorded reduction(s) left.");
                return;
            }

            // Left pending on the server whether they are online or not. The client applies it - now, over the
            // RPC, or on its next join - and the server drops each entry once a save shows the skill at the
            // restored level, so a restore that does not reach the player this time is applied next time rather
            // than lost.
            foreach (KeyValuePair<Skills.SkillType, float> target in targets) {
                change.Character.AddPendingSkillRestore(target.Key, target.Value);
            }
            SkillReductions.Persist(account, name, change.Character);

            ZNetPeer peer = ValConfig.GetPeerByPlatformID(account);
            if (peer == null) {
                args.Output.Info($"{name} is offline. {targets.Count} skill(s) will be restored on their next join ({SkillReductions.Describe(targets)}). {change.Remaining} recorded reduction(s) left.");
                return;
            }
            SendSkillRestore(peer, raw, targets);
            args.Output.Info($"Sent a restore of {targets.Count} skill(s) to {name} ({SkillReductions.Describe(targets)}); their client is applying it now. {change.Remaining} recorded reduction(s) left.");
        }

        /// <summary>
        /// Brings the client's tracked copy of the records into line with an edit the server just made: the
        /// listen host's own character directly, a connected player over the RPC, and an offline one not at all -
        /// the save is what counts and it is written. Returns the sentence to append to the command's result.
        /// </summary>
        private static string TellClientAboutSkillRecords(string account, string name, string raw, List<Skills.SkillType> skills) {
            if (IsLocalCharacter(name)) {
                SkillReductions.ClearTrackedLocally(skills);
                return "Your own tracked copy was updated too.";
            }
            ZNetPeer peer = ValConfig.GetPeerByPlatformID(account);
            if (peer == null) { return "They are offline; the save is what counts and it is written."; }
            SendSkillRestore(peer, raw, null);
            return "Their client was updated too.";
        }

        // The filter travels in its typed form and is re-parsed on the client, the same way the confiscation
        // clear does; the levels, when there are any, as the same YAML map the character save uses.
        private static void SendSkillRestore(ZNetPeer peer, string filter, Dictionary<Skills.SkillType, float> levels) {
            ZPackage package = new ZPackage();
            package.Write(filter);
            package.Write(levels == null || levels.Count == 0 ? "" : DataObjects.yamlserializer.Serialize(levels));
            ValConfig.SkillRestoreRPC.SendPackage(peer.m_uid, package);
        }
    }
}
