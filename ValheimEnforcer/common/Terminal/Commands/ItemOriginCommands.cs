using System;
using System.Collections.Generic;
using ValheimEnforcer.modules.worldintegrity;

namespace ValheimEnforcer.common {
    internal static partial class TerminalManager {

        private static void RegisterItemOriginCommands() {
            _ = new EnforcerCommand("enforcer-item-origins",
                "Format: [prefabFilter] Shows which equipment this world says can only be crafted, which is the set DetectUncraftedEquipment reports on when it turns up with no crafter. The first question after a surprising detection is always 'why is this on the list', and this is where the answer is. Server admins only.",
                ItemOrigins, CommandArea.Structures,
                serverAuthoritative: true, requiresAdmin: true,
                aliases: "Enforcer-List-Item-Origins");
        }

        private static void ItemOrigins(EnforcerCommandArgs args) {
            if (!ItemOriginIndex.EnsureBuilt()) {
                args.Output.Warning("The item origin index is not built yet. It builds on first use once the world and every mod's content are loaded.");
                return;
            }

            string filter = args.Length > 1 ? args.Args[1] : null;
            List<string> craftOnly = ItemOriginIndex.CraftOnlyEquipment();

            int shown = 0;
            foreach (string prefab in craftOnly) {
                if (!string.IsNullOrEmpty(filter) && prefab.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                args.Output.Detail($"  {prefab}", log: false);
                shown++;
            }

            if (shown == 0) {
                args.Output.Info(string.IsNullOrEmpty(filter)
                    ? "No equipment in this world is craft-only, which is unusual - check that the content finished loading."
                    : $"No craft-only equipment matches '{filter}'. An item that is NOT on this list is one the world drops, sells or spawns, so having no crafter is expected for it and it is never reported.");
                return;
            }

            args.Output.Info($"{shown} craft-only equipment prefab(s){(string.IsNullOrEmpty(filter) ? "" : $" matching '{filter}'")}, out of {craftOnly.Count} in total.");
            args.Output.Info($"Known player ids on file: {KnownPlayerIds.Count()}. Anything crafted by an id outside that set is reported when DetectUnknownCrafterIds is on.");
        }
    }
}
