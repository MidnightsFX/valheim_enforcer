using System.Collections.Generic;
using System.Linq;
using ValheimEnforcer.modules;
using ValheimEnforcer.modules.character;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.common {
    internal static partial class TerminalManager {

        private const string ItemUsage = "Format: <accountId> <characterName> <all|prefab,prefab>";

        private static void RegisterItemCommands() {
            _ = new EnforcerCommand("enforcer-items-list",
                $"Lists the items confiscated from one character, without changing anything. {ItemUsage.Replace(" <all|prefab,prefab>", "")}. eg: enforcer-items-list 76561198012345678 Bjorn",
                ItemsList, CommandArea.Items, AccountThenCharacter,
                serverAuthoritative: true, requiresAdmin: true,
                aliases: "Enforcer-List-Confiscated");

            _ = new EnforcerCommand("enforcer-items-return",
                $"Gives confiscated items back to a player, in-hand if they are online and into their save if they are not. {ItemUsage}. eg: enforcer-items-return 76561198012345678 Bjorn all",
                ItemsReturn, CommandArea.Items, AccountThenCharacterThenFilter,
                serverAuthoritative: true, requiresAdmin: true,
                aliases: "Enforcer-Return-Confiscated");

            _ = new EnforcerCommand("enforcer-items-clear",
                $"Permanently deletes confiscated items from a character's save. {ItemUsage}. eg: enforcer-items-clear 76561198012345678 Bjorn all",
                ItemsClear, CommandArea.Items, AccountThenCharacterThenFilter,
                serverAuthoritative: true, requiresAdmin: true,
                aliases: "Enforcer-Clear-Confiscated");
        }

        // Argument 1 is an account id, argument 2 the characters saved under whatever id is already typed.
        private static List<string> AccountThenCharacter(string[] input) {
            if (input.Length <= 2) { return TerminalArgs.KnownAccounts(input); }
            if (input.Length == 3) { return TerminalArgs.KnownCharacters(input); }
            return new List<string>();
        }

        private static List<string> AccountThenCharacterThenFilter(string[] input) {
            if (input.Length == 4) { return TerminalArgs.ItemFilters(input); }
            return AccountThenCharacter(input);
        }

        /// <summary>
        /// True when the character being acted on is the one this machine is playing. Only ever true on a
        /// listen host, where the person running the command is also a player and is not in the peer list.
        /// </summary>
        private static bool IsLocalCharacter(string name) {
            return Player.m_localPlayer != null
                && string.Equals(Player.m_localPlayer.GetPlayerName(), name, System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Reads the account/character pair every item command starts with.</summary>
        private static bool ReadTarget(EnforcerCommandArgs args, string usage, out string account, out string name) {
            name = null;
            if (!args.ReadAccount(0, usage, out account)) { return false; }
            if (!args.ReadName(1, usage, out name)) { return false; }

            if (!CharacterSaves.Exists(account, name)) {
                // Distinguishing "no such character" from "nothing confiscated" is the whole difference
                // between a typo and an answer, and the old commands reported neither.
                args.Output.Error($"No save for character '{name}' under account {account}. Run enforcer-player-list to see what the server has.");
                return false;
            }
            return true;
        }

        private static void ItemsList(EnforcerCommandArgs args) {
            string usage = "Format: enforcer-items-list <accountId> <characterName>";
            if (!ReadTarget(args, usage, out string account, out string name)) { return; }

            List<PackedItem> items = ConfiscatedItems.Peek(account, name, out bool _);
            if (items.Count == 0) {
                args.Output.Info($"{name} has no confiscated items.");
                return;
            }

            foreach (IGrouping<string, PackedItem> group in items.GroupBy(item => item.prefabName).OrderBy(group => group.Key)) {
                args.Output.Detail($"  {group.Key} x{group.Sum(item => item.m_stack)}", log: false);
            }
            args.Output.Info($"{name} has {items.Count} confiscated entr{(items.Count == 1 ? "y" : "ies")}. Return them with enforcer-items-return {account} {name} all.");
        }

        private static void ItemsClear(EnforcerCommandArgs args) {
            string usage = "Format: enforcer-items-clear <accountId> <characterName> <all|prefab,prefab>";
            if (!ReadTarget(args, usage, out string account, out string name)) { return; }
            if (!args.ReadItemFilter(2, usage, out string raw, out List<string> prefabs)) { return; }

            ConfiscationChange change = ConfiscatedItems.Take(account, name, prefabs);
            if (!change.CharacterFound) {
                args.Output.Error($"Could not read the save for {name} under account {account}.");
                return;
            }
            if (change.TotalBefore == 0) {
                args.Output.Info($"{name} had no confiscated items, so nothing was deleted.");
                return;
            }
            if (change.Taken.Count == 0) {
                args.Output.Warning($"Nothing matched '{raw}'. {name} still has {change.Remaining} confiscated entr{(change.Remaining == 1 ? "y" : "ies")} - run enforcer-items-list to see them.");
                return;
            }

            ConfiscatedItems.Persist(account, name, change.Character);

            // Whoever holds this character also holds their own copy of the confiscated list and pushes it
            // back on the next full sync, so that copy has to be cleared too or the entries reappear.
            string reach;
            ZNetPeer target = ValConfig.GetPeerByPlatformID(account);
            if (IsLocalCharacter(name)) {
                // A listen host is not one of its own peers, so the lookup above cannot find them.
                ConfiscatedItems.ClearTrackedLocally(prefabs);
                reach = "Your own tracked copy was cleared too.";
            } else if (target != null) {
                ZPackage package = new ZPackage();
                package.Write(raw);
                ValConfig.ClearConfiscatedRPC.SendPackage(target.m_uid, package);
                reach = "Their client was updated too.";
            } else {
                reach = "They are offline; the save is what counts and it is written.";
            }

            args.Output.Info($"Deleted {change.Taken.Count} confiscated entr{(change.Taken.Count == 1 ? "y" : "ies")} from {name} ({change.Describe()}). {change.Remaining} left. {reach}");
        }

        private static void ItemsReturn(EnforcerCommandArgs args) {
            string usage = "Format: enforcer-items-return <accountId> <characterName> <all|prefab,prefab>";
            if (!ReadTarget(args, usage, out string account, out string name)) { return; }
            if (!args.ReadItemFilter(2, usage, out string raw, out List<string> prefabs)) { return; }

            ConfiscationChange change = ConfiscatedItems.Take(account, name, prefabs);
            if (!change.CharacterFound) {
                args.Output.Error($"Could not read the save for {name} under account {account}.");
                return;
            }
            if (change.TotalBefore == 0) {
                args.Output.Info($"{name} has no confiscated items, so there is nothing to return.");
                return;
            }
            if (change.Taken.Count == 0) {
                args.Output.Warning($"Nothing matched '{raw}'. {name} still has {change.Remaining} confiscated entr{(change.Remaining == 1 ? "y" : "ies")} - run enforcer-items-list to see them.");
                return;
            }

            // A listen host is not one of its own peers, so an admin returning items to their own character
            // would fall through to the offline path and write them into a save they are not about to
            // reload. Hand them straight over instead.
            if (IsLocalCharacter(name)) {
                foreach (PackedItem item in change.Taken) { item.AddToInventory(Player.m_localPlayer, false); }
                ConfiscatedItems.Persist(account, name, change.Character);
                if (ValConfig.InternalStorageMode.Value) { InternalDataStore.SaveAccountCharacter(change.Character); }
                ConfiscatedItems.ClearTrackedLocally(prefabs);
                args.Output.Info($"Returned {change.Taken.Count} item(s) to your inventory ({change.Describe()}). {change.Remaining} still confiscated.");
                return;
            }

            ZNetPeer target = ValConfig.GetPeerByPlatformID(account);
            if (target == null) {
                // Offline: move them into the tracked inventory so they are handed back on the next join.
                foreach (PackedItem item in change.Taken) { change.Character.PlayerItems.Add(item); }
                ConfiscatedItems.Persist(account, name, change.Character);
                if (ValConfig.InternalStorageMode.Value) { InternalDataStore.SaveAccountCharacter(change.Character); }
                args.Output.Info($"{name} is offline. Moved {change.Taken.Count} item(s) into their save ({change.Describe()}); they get them on their next join. {change.Remaining} still confiscated.");
                return;
            }

            ConfiscatedItems.Persist(account, name, change.Character);
            if (ValConfig.InternalStorageMode.Value) { InternalDataStore.SaveAccountCharacter(change.Character); }

            ZPackage items = new ZPackage();
            items.Write(DataObjects.yamlserializer.Serialize(change.Taken));
            ValConfig.ReturnConfiscatedItemsRPC.SendPackage(target.m_uid, items);
            // Push the updated character too, stripped of the confiscated list like every other client-bound
            // send, so their in-memory copy cannot re-report the entries we just gave back.
            ValConfig.CharacterSaveRPC.SendPackage(target.m_uid, ValConfig.SendCharacterToClientAsZpackage(change.Character));

            args.Output.Info($"Sent {change.Taken.Count} item(s) to {name} ({change.Describe()}). {change.Remaining} still confiscated.");
        }
    }
}
