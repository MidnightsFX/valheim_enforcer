using HarmonyLib;
using System.Collections.Generic;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.character {

    /// <summary>
    /// NewCharacterResetMapExploration: a character joining this server for the first time starts with a blank map
    /// of it.
    ///
    /// Valheim keeps one map per world in the player profile, keyed by the world's id, so the only ways to arrive
    /// with this server's map already uncovered are to have played a copy of this world somewhere else, or to be a
    /// character an admin reset by deleting their save. Neither was explored here.
    ///
    /// Client side by necessity - the map lives in the player's own profile and never reaches the server, so there
    /// is nothing for the server to check. It is also the one join rule with no undo: a stripped item is recorded
    /// and can be handed back, a wiped map is simply gone. That is why it acts only on a definite answer that the
    /// character is new, never on the "no answer yet, assume new" that the item rules accept.
    /// </summary>
    internal static class MapExploration {

        // The character whose reset is waiting on the map to load, or null. Minimap reads the profile's map lazily,
        // on its first Update after the world generator exists, and nothing guarantees that has happened by the
        // time a join is validated - a reset done before that load would be quietly undone by it.
        private static string pendingFor;

        private static bool Enabled {
            get { return ValConfig.NewCharacterResetMapExploration != null && ValConfig.NewCharacterResetMapExploration.Value; }
        }

        /// <summary>Join, new character. Wipes the map now if it has loaded, otherwise as soon as it does.</summary>
        internal static void ResetForNewCharacter(string characterName) {
            if (!Enabled) { return; }

            // ResolveSessionCharacter reads "the server has not answered yet" as new, which is the safe direction for
            // items. It is not the safe direction for a map, so only a definite answer counts here: this machine owns
            // the store, or the server explicitly said it holds no save.
            bool confirmedNew = CharacterManager.ThisMachineIsAuthority()
                || CharacterManager.ServerCharacter == CharacterManager.ServerCharacterState.ServerHasNone;
            if (!confirmedNew) {
                Logger.LogWarning($"Not resetting the map for {characterName}: the server has not confirmed this is a new character, and a wiped map cannot be given back.");
                return;
            }

            Minimap map = Minimap.instance;
            if (map == null) { return; }
            if (!map.m_hasGenerated) {
                Logger.LogDebug($"The map has not loaded yet; resetting it for {characterName} once it does.");
                pendingFor = characterName;
                return;
            }
            Reset(map, characterName);
        }

        /// <summary>The session ended. A reset still waiting belongs to it and must not land on the next one.</summary>
        internal static void CancelPending() {
            pendingFor = null;
        }

        private static void Reset(Minimap map, string characterName) {
            pendingFor = null;

            // Clears both exploration layers - what the player uncovered and what a cartography table shared with
            // them - and repaints the fog to match.
            map.Reset();

            // Saved pins only: those are the ones that came out of the profile. The rest - spawn point, other players,
            // pings, events - are rebuilt by the minimap from live state, and it keeps its own references to them,
            // which ClearPins would destroy out from under it.
            List<Minimap.PinData> saved = new List<Minimap.PinData>();
            foreach (Minimap.PinData pin in map.m_pins) {
                if (pin.m_save) { saved.Add(pin); }
            }
            foreach (Minimap.PinData pin in saved) {
                if (pin == map.m_deathPin) { map.m_deathPin = null; }
                map.RemovePin(pin);
            }

            // Nothing is written to the profile here: every profile save reads the live minimap
            // (Game.SavePlayerProfile -> Minimap.SaveMapData), so the next one - at the latest, logout - carries this.
            Logger.LogInfo($"New character {characterName}: reset their map of this world and removed {saved.Count} saved pin(s).");
        }

        [HarmonyPatch(typeof(Minimap), "LoadMapData")]
        public static class ResetAfterMapLoad {
            [HarmonyPostfix]
            private static void Postfix(Minimap __instance) {
                if (!Enabled || pendingFor == null || __instance == null) { return; }
                Reset(__instance, pendingFor);
            }
        }
    }
}
