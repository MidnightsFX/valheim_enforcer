using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.character {

    /// <summary>
    /// EnableCharacterEnforcement: one switch over every Player Sync rule that takes something away from a
    /// character or puts something back, for an admin who wants all of it off without hunting down each
    /// setting.
    ///
    /// Off does not mean untracked. Characters keep being saved on the server exactly as before; what stops is
    /// the reconciling. That is the difference from removing the mod: with this off the saves stay current, so
    /// switching it back on starts from where every character actually is, with no CatchupOverwriteOnJoin.
    ///
    /// Where the gate sits, so that nothing is left enforcing:
    ///  - a returning character is taken exactly as its player brings it. The server answers its connect with
    ///    the same CATCHUP payload CatchupOverwriteOnJoin uses (ValConfig.SendSavedCharacter), which arms
    ///    neither first-save rule set and has the client adopt its live player (CharacterManager.CatchUpFromLivePlayer).
    ///    A character this machine holds itself - singleplayer, a listen host's own - never gets that answer, so
    ///    the join path makes the same call for it directly. A peer armed before the switch went off has its
    ///    first save taken as it stands (ValConfig.PersistReceivedCharacterYaml);
    ///  - a new character keeps everything it arrives with: <see cref="NewCharacterRules.Current"/> comes back
    ///    with every rule off, on both sides, and the client-only resets (map, recipes, known texts,
    ///    statistics, custom data) each check this;
    ///  - nothing removes an item from a live player: <see cref="CharacterManager.ReconcilePlayerToCharacter"/>
    ///    returns before it validates anything.
    ///
    /// Deliberately outside it: starter loadouts and the character limit, which have master switches of their
    /// own and are not enforcement against what a character brought; admin commands, which are an admin acting
    /// rather than a rule; and SkillClamp's 0-100 bound, which only refuses values the game cannot produce.
    /// </summary>
    internal static class CharacterEnforcement {

        /// <summary>
        /// Whether the Player Sync rules apply. True while the setting is unbound, matching its default, so
        /// anything that runs before config binding behaves like a default installation.
        /// </summary>
        internal static bool Enabled {
            get { return ValConfig.EnableCharacterEnforcement == null || ValConfig.EnableCharacterEnforcement.Value; }
        }
    }
}
