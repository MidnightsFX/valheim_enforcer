using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.character {

    /// <summary>
    /// NewCharacterClearKnownTexts: a character joining this server for the first time forgets the runestone
    /// and lore texts it read somewhere else - and, with them, any per-player progression a mod keeps in the
    /// same dictionary.
    ///
    /// This exists because vanilla's own reset does not cover it. Player.ResetCharacterKnownItems - the
    /// resetknownitems console command, and what KnownRecipes runs - clears m_knownRecipes, m_knownStations,
    /// m_knownMaterial and m_trophies, and nothing else. m_knownTexts survives it untouched, and nothing else
    /// on the new-character path touches it either: newCharacterClearCustomData clears m_customData,
    /// NewCharacterRules only rewrites the record, and ProgressionSync.Apply is skipped for a new character
    /// by definition.
    ///
    /// For vanilla that gap is nearly invisible - a first-time joiner arriving with a runestone already read
    /// is nobody's problem. It stops being invisible when a mod parks progression there, which EpicMMO does:
    /// its level, experience and attribute points are one m_knownTexts entry each. A first-time joiner whose
    /// items, skills, recipes and custom data were all reset therefore walked in at whatever level they had
    /// reached somewhere else, and was the one thing the new-character rules could not reach.
    ///
    /// A separate setting from NewCharacterClearKnownRecipes rather than part of it, because the two answer
    /// different questions and an admin may well want one without the other - and because this one reaches
    /// mod data, which deserves to be switched off in one obvious place if it goes wrong.
    ///
    /// Client side by necessity - all of this lives in the player's own profile and never reaches the server,
    /// so there is nothing for the server to check. And like the map it has no undo, which is why it acts
    /// only on a definite answer that the character is new. See MapExploration.
    /// </summary>
    internal static class KnownTexts {

        private static bool Enabled {
            get { return ValConfig.NewCharacterClearKnownTexts != null && ValConfig.NewCharacterClearKnownTexts.Value; }
        }

        /// <summary>Join, new character. Call alongside the other new-character progression resets.</summary>
        internal static void ResetForNewCharacter(Player player, string characterName) {
            if (!Enabled || player == null || player.m_knownTexts == null) { return; }

            // Joining a server only, exactly as in KnownRecipes: singleplayer and a listen host's own
            // character also read as new whenever this machine has no local save for them, which is every
            // character the first time it is loaded with the mod installed.
            if (CharacterManager.ThisMachineIsAuthority()) { return; }

            if (!CharacterManager.ConfirmedNewCharacter()) {
                Logger.LogWarning($"Not clearing known texts for {characterName}: the server has not confirmed this is a new character, and a forgotten level cannot be given back.");
                return;
            }

            if (player.m_knownTexts.Count == 0) { return; }

            // Everything goes, pass-through prefixes included. CompatKnownTexts governs what is enforced
            // between joins; what a character may bring in with it on its very first one is this rule's
            // question, and the answer is the same as it is for items, skills and custom data.
            player.m_knownTexts.Clear();

            Logger.LogInfo($"New character {characterName}: cleared the runestone texts and mod-owned progression they arrived with.");
        }
    }
}
