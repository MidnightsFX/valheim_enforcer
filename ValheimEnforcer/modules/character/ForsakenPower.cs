using HarmonyLib;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.character {

    /// <summary>
    /// Holds a character's Forsaken Power - the guardian power selected at a boss stone - to what they chose on
    /// this server.
    ///
    /// Vanilla keeps the selected power in the player profile, which travels with the character into every world.
    /// Selecting one needs only the boss's trophy on the stone in the world you are standing in, so a power this
    /// server has never unlocked can be picked up in a solo world in a couple of minutes and carried straight back
    /// in. Two settings close that, split the same way skills are:
    ///  - PreventExternalForsakenPowerChanges records the power in the character save and puts it back on join;
    ///  - NewCharacterClearForsakenPower clears it on a character's first join (see NewCharacterRules).
    ///
    /// The value is the status effect name vanilla hands to Player.SetGuardianPower ("GP_Eikthyr"), or empty for no
    /// power. In a save, null is a third answer - never tracked - see Character.GuardianPower.
    ///
    /// Selecting a power is entirely client side (ItemStand.DelayedPowerActivation sets it with no RPC), so the
    /// server has nothing to validate a change against. What it can hold is the record, which is what a join
    /// restores from and what ReturningCharacterRules reconciles to.
    /// </summary>
    internal static class ForsakenPower {

        internal static bool Tracked {
            get { return ValConfig.PreventExternalForsakenPowerChanges != null && ValConfig.PreventExternalForsakenPowerChanges.Value; }
        }

        /// <summary>
        /// The live power to record, or null when it is not being tracked. Null rather than the live value so a save
        /// written with the setting off looks exactly like one written before it existed, and turning it on later
        /// adopts the power the character has then instead of restoring whatever was recorded last time.
        /// </summary>
        internal static string Capture(Player player) {
            if (!Tracked || player == null) { return null; }
            return player.GetGuardianPowerName() ?? "";
        }

        /// <summary>
        /// Join, returning character: put back the power the save recorded.
        ///
        /// A save with no power recorded predates tracking and has nothing to put back, so the live power is adopted
        /// as the baseline instead. The alternative - reading "nothing recorded" as "no power" - would strip the
        /// legitimately chosen power from every existing player the moment an admin turned this on.
        /// </summary>
        internal static void RestoreOnJoin(Player player, DataObjects.Character saved) {
            if (!Tracked || player == null || saved == null) { return; }
            string live = player.GetGuardianPowerName() ?? "";
            if (saved.GuardianPower == null) {
                Logger.LogInfo($"No Forsaken Power recorded for {saved.Name} yet; adopting their current one ({Describe(live)}) as the baseline.");
                saved.GuardianPower = live;
                return;
            }
            if (live == saved.GuardianPower) { return; }
            Logger.LogInfo($"Restoring the Forsaken Power recorded for {saved.Name}: {Describe(live)} -> {Describe(saved.GuardianPower)}.");
            player.SetGuardianPower(saved.GuardianPower);
        }

        /// <summary>Join, new character: clear whatever power they arrived with.</summary>
        internal static void StripLive(Player player, string characterName) {
            if (player == null) { return; }
            string live = player.GetGuardianPowerName();
            if (string.IsNullOrEmpty(live)) { return; }
            Logger.LogInfo($"New character {characterName}: clearing the Forsaken Power they arrived with ({live}).");
            player.SetGuardianPower("");
        }

        /// <summary>
        /// The server reconciled a save and sent it back: bring the live power into line with the record. A null
        /// record power says nothing either way and leaves the live one alone.
        /// </summary>
        internal static void ApplyRecord(Player player, DataObjects.Character record, string reason) {
            if (!Tracked && !ValConfig.NewCharacterClearForsakenPower.Value) { return; }
            if (player == null || record == null || record.GuardianPower == null) { return; }
            string live = player.GetGuardianPowerName() ?? "";
            if (live == record.GuardianPower) { return; }
            Logger.LogInfo($"{reason}: setting the Forsaken Power from {Describe(live)} to {Describe(record.GuardianPower)}.");
            player.SetGuardianPower(record.GuardianPower);
        }

        private static string Describe(string power) {
            return string.IsNullOrEmpty(power) ? "none" : power;
        }

        // Selecting a power mid-session changes nothing in the inventory, and the delta tracker only wakes on
        // Inventory.Changed. Without this the new power would not reach the server until the next full save - up to
        // FullSyncPullIntervalMinutes away - and a crash in between would restore the old one on the next join.
        [HarmonyPatch(typeof(Player), nameof(Player.SetGuardianPower))]
        public static class TrackGuardianPowerChange {
            [HarmonyPostfix]
            private static void Postfix(Player __instance, string name) {
                if (!Tracked) { return; }
                if (__instance == null || __instance != Player.m_localPlayer) { return; }
                // Until the join is validated there is no baseline to compare against, and the join decides the power
                // itself. Player.Load also lands here on every spawn, with the unchanged power.
                if (!CharacterManager.JoinValidationComplete || CharacterManager.PlayerCharacter == null) { return; }
                if ((name ?? "") == CharacterManager.PlayerCharacter.GuardianPower) { return; }
                CharacterDeltaTracker.MarkBaselineDirty();
            }
        }
    }
}
