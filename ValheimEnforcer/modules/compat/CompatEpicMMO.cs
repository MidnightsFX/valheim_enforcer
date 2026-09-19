using BepInEx;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using ValheimEnforcer.modules.character;

namespace ValheimEnforcer.modules.compat {

    /// <summary>
    /// EpicMMO-specific compatibility for the new-character known-text reset.
    ///
    /// <see cref="KnownTexts.ResetForNewCharacter"/> empties the dictionary EpicMMO keeps a character's
    /// level, experience and attribute points in, so a first-time joiner arrives at level 1. That is the
    /// right answer, but on its own it lands too late to be the whole answer, because EpicMMO does not read
    /// those keys when something asks for the level - it reads them once at spawn and publishes the result
    /// where the rest of the world can see it:
    ///
    ///   Game.SpawnPlayer postfix (EpicMMOSystem.SetZDOLevel) -> LevelSystem.getLevel() off m_knownTexts
    ///     -> m_customData["epicmmolevel"], zdo.Set("EpicMMOSystem_level"), ForceSendZDO
    ///
    /// and DataMonsters / MonsterDeath_Path scale monsters off that ZDO value, not off the dictionary. That
    /// postfix is [HarmonyPriority(998)]. Harmony's highest NAMED priority, Priority.First, is 800, and the
    /// enforcer's join validation runs at exactly that - so EpicMMO reads and publishes the incoming level
    /// before a single new-character rule has had a turn. The character was correctly reset to level 1 and
    /// the world went on scaling monsters to the level they walked in with, for the rest of the session.
    ///
    /// Two halves, because the enforcer cannot always answer early enough to use the first:
    ///
    ///  - BEFORE, when the answer is already in. <see cref="ResetKnownTextsBeforeModsRead"/> is a
    ///    Game.SpawnPlayer postfix above every other one, which does the reset there. EpicMMO's postfix then
    ///    reads an empty dictionary and publishes level 1 by itself, with no further help from us - the
    ///    honest fix, and the only one that is correct for every other key EpicMMO derives from that read.
    ///
    ///  - AFTER, when it was not. A join usually waits on the server's answer about whether this character is
    ///    new (JoinGate), and the reset that follows it is several frames past every spawn postfix. There is
    ///    nothing to get in front of by then, so <see cref="Resync"/> has EpicMMO read again instead.
    ///
    /// Reflection rather than a reference, and every call wrapped: EpicMMO is a soft dependency that most
    /// servers running this mod do not have, and a version that moves these types must not take a join down
    /// with it. Everything here is inert when EpicMMO is not installed, and when
    /// ValConfig.EpicMMOKnownTextCompat is off.
    /// </summary>
    internal static class CompatEpicMMO {

        /// <summary>ModGUID from EpicMMO's own Plugin.cs - Author + "." + ModName.</summary>
        internal const string PluginGUID = "WackyMole.EpicMMOSystem";

        // EpicMMO mirrors the level into player custom data as well as the ZDO, and does it with
        // Add-if-missing rather than a plain assignment - so it will never correct an entry that is already
        // there. Resync drops it and lets EpicMMO's own code put the reset value back.
        private const string LevelMirrorCustomDataKey = "epicmmolevel";

        // EpicMMOSystem.SetZDOLevel.Postfix: reads the level out of m_knownTexts and republishes it to the
        // ZDO and the custom-data mirror. Invoked rather than reimplemented so the key names, and whatever
        // else it comes to publish, stay EpicMMO's to decide.
        private static MethodInfo publishLevel;
        // EpicMMOSystem.MyUI.updateExpBar: the level and experience the player is looking at.
        private static MethodInfo refreshExpBar;

        /// <summary>Whether EpicMMO is loaded on this machine and the entry points below were found.</summary>
        internal static bool Detected { get; private set; }

        /// <summary>Detected, and the admin has not switched the compatibility off.</summary>
        internal static bool Enabled {
            get {
                return Detected
                       && (ValConfig.EpicMMOKnownTextCompat == null || ValConfig.EpicMMOKnownTextCompat.Value);
            }
        }

        /// <summary>
        /// Called from <see cref="ModCompatability.CheckModCompat"/>, before the enforcer's own patches are
        /// applied. Resolving the entry points here rather than lazily keeps the failure at startup, where an
        /// admin can see it in the log, instead of mid-join.
        /// </summary>
        internal static bool Detect(Dictionary<string, BaseUnityPlugin> plugins) {
            Detected = false;
            publishLevel = null;
            refreshExpBar = null;

            if (plugins == null || !plugins.TryGetValue(PluginGUID, out BaseUnityPlugin plugin) || plugin == null) {
                return false;
            }

            try {
                Assembly assembly = plugin.GetType().Assembly;
                // Both are public statics taking no arguments, in EpicMMO's single root namespace.
                publishLevel = AccessTools.Method(assembly.GetType("EpicMMOSystem.SetZDOLevel"), "Postfix");
                refreshExpBar = AccessTools.Method(assembly.GetType("EpicMMOSystem.MyUI"), "updateExpBar");
            } catch (Exception e) {
                Logger.LogWarning($"EpicMMO was found but could not be inspected, so the known-text compatibility is off for this session: {e.Message}");
                return false;
            }

            // The level republish is the part that matters; the exp bar is cosmetic and its absence is not
            // worth refusing the whole thing over.
            if (publishLevel == null) {
                Logger.LogWarning("EpicMMO is installed, but EpicMMOSystem.SetZDOLevel.Postfix was not found - it has probably been renamed in a newer version. A first-time joiner's level will still be reset, but the world may go on scaling monsters to the level they arrived with until they respawn or reconnect. The known-text compatibility is off for this session.");
                return false;
            }

            Detected = true;
            Logger.LogInfo("EpicMMO detected; known-text compatibility for new characters is active.");
            return true;
        }

        /// <summary>
        /// Has EpicMMO read the (now empty) known texts again and republish what it derives from them.
        ///
        /// Only for the case where the reset could not be done before EpicMMO's own spawn postfix - see the
        /// class note. Called with the reset already applied, so everything EpicMMO reads here is the reset
        /// state; it is the same work its postfix does at spawn, done a second time against better input.
        /// </summary>
        internal static void Resync(Player player, string characterName) {
            if (!Enabled || player == null) { return; }

            try {
                // Before the republish, because EpicMMO only ADDS this key when it is missing: left in place
                // it would keep the level the character walked in with, in the one place other mods read a
                // player's EpicMMO level from without going through EpicMMO.
                if (player.m_customData != null) { player.m_customData.Remove(LevelMirrorCustomDataKey); }

                publishLevel.Invoke(null, null);
                // Cosmetic, and deliberately after: the bar should show what was just published.
                if (refreshExpBar != null) { refreshExpBar.Invoke(null, null); }

                Logger.LogInfo($"New character {characterName}: had EpicMMO re-read its progression after the known-text reset, so the level it publishes matches the one the character now has.");
            } catch (Exception e) {
                // Never fatal. The character is still correctly reset; what may be stale is the copy of the
                // level EpicMMO published at spawn, and that is fixed by the next respawn or reconnect.
                Logger.LogWarning($"Could not have EpicMMO re-read its progression for {characterName} after the known-text reset: {e.Message}. Their level is reset, but monsters may scale to the level they arrived with until they respawn or reconnect.");
            }
        }
    }

    /// <summary>
    /// The "before" half. See <see cref="CompatEpicMMO"/>.
    /// </summary>
    [HarmonyPatch(typeof(Game), nameof(Game.SpawnPlayer))]
    internal static class ResetKnownTextsBeforeModsRead {

        // Above every other postfix on this method, which is the whole point of the patch existing
        // separately from the join validation below it. Harmony runs postfixes in descending priority, and
        // EpicMMO's is 998 - already above Priority.First (800), the highest named value and the one the
        // enforcer's own Game.SpawnPlayer postfix uses. int.MaxValue rather than 999 so this keeps working if
        // EpicMMO, or anything else that reads known texts at spawn, raises its own.
        [HarmonyPostfix]
        [HarmonyPriority(int.MaxValue)]
        private static void Postfix() {
            if (!CompatEpicMMO.Enabled) { return; }

            Player player = Player.m_localPlayer;
            if (player == null) { return; }

            // Only the join spawn, never a respawn. Game.SpawnPlayer builds a new Player on every death too,
            // and without this the reset would empty the compendium - and EpicMMO's progression with it -
            // every time a first-session player died. This is the same latch LoadAndValidatePlayerPatch uses
            // to tell a join from a respawn, read before that patch gets its turn: both are false on the join
            // spawn and JoinValidationComplete is true on every one after it.
            if (CharacterManager.JoinValidationComplete || CharacterManager.JoinValidationPending) { return; }

            // Checked here rather than left to ResetForNewCharacter, which would log a warning about it. A
            // "no" at this point is the ordinary case, not a problem: the server's answer about whether this
            // character is new usually arrives during the connect handshake but is not promised to, and when
            // it has not, the join waits for it (JoinGate) and CompatEpicMMO.Resync covers what follows.
            if (!CharacterManager.ConfirmedNewCharacter()) { return; }

            KnownTexts.ResetForNewCharacter(player, player.GetPlayerName());
        }
    }
}
