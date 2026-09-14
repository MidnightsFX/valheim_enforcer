using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.character {

    /// <summary>
    /// NewCharacterClearKnownRecipes: a character joining this server for the first time forgets every recipe and
    /// build piece they discovered somewhere else.
    ///
    /// Vanilla does not remember recipes on their own. A recipe is discovered the moment the player knows its
    /// materials and crafting station (Player.UpdateKnownRecipesList), and it re-runs that check on every inventory
    /// change - so clearing only the recipe list would have them all back within seconds. What gets cleared is
    /// vanilla's own "known items" set (Player.ResetCharacterKnownItems, the same thing the resetknownitems console
    /// command runs): recipes, materials, crafting stations and trophies. Discovery then restarts from what the
    /// character is actually carrying after the join rules have run.
    ///
    /// Client side by necessity - all of this lives in the player's own profile and never reaches the server, so
    /// there is nothing for the server to check. And like the map it has no undo, which is why it acts only on a
    /// definite answer that the character is new. See MapExploration.
    /// </summary>
    internal static class KnownRecipes {

        private static bool Enabled {
            get { return ValConfig.NewCharacterClearKnownRecipes != null && ValConfig.NewCharacterClearKnownRecipes.Value; }
        }

        /// <summary>Join, new character. Call after the new-character item rules, so rediscovery starts from what
        /// they kept.</summary>
        internal static void ResetForNewCharacter(Player player, string characterName) {
            if (!Enabled || player == null) { return; }

            // Joining a server only. Singleplayer and a listen host's own character also count as new whenever this
            // machine has no local save for them, which is every character the first time it is loaded with the mod
            // installed - and this setting is on by default, so without this a player opening their own world with a
            // server's modpack would lose every recipe they have.
            if (CharacterManager.ThisMachineIsAuthority()) { return; }

            if (!CharacterManager.ConfirmedNewCharacter()) {
                Logger.LogWarning($"Not clearing known recipes for {characterName}: the server has not confirmed this is a new character, and forgotten recipes cannot be given back.");
                return;
            }

            player.ResetCharacterKnownItems();

            // Rediscover from the inventory they hold now. AddKnownItem is what vanilla's inventory-changed handler
            // runs per item; without it nothing they carry counts as a known material until the next change.
            foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItems()) {
                player.AddKnownItem(item);
            }
            player.UpdateKnownRecipesList();
            player.UpdateAvailablePiecesList();

            // Rediscovering what they are holding queues an unlock popup for each piece of it. Vanilla clears the
            // queue for the same reason after resetting known items for an item set (ItemSets.TryGetSet).
            if (MessageHud.instance != null) {
                MessageHud.instance.ClearUnlockQueue();
            }

            Logger.LogInfo($"New character {characterName}: cleared the recipes, materials, crafting stations and trophies they arrived knowing.");
        }
    }
}
