using Jotunn.Entities;
using Jotunn.Managers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace ValheimEnforcer.modules {
    internal static class TerminalCommands {
        internal static void AddCommands() {
            CommandManager.Instance.AddConsoleCommand(new ListPlayerConfiscatedItems());
        }

        internal class ListPlayerConfiscatedItems : ConsoleCommand {
            public override string Name => "List-Confiscated";
            public override string Help => "Format: playername eg: list-confiscated TerryTheTerrible";

            public override void Run(string[] args) {
                if (args.Length != 1) {
                    Logger.LogInfo("Target username is required. Ensure your command follows the format: list-confiscated TerryTheTerrible");
                    return;
                }
                string username = args[0];

            }
        }
    }
}
