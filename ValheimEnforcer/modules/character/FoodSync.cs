using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;
using ValheimEnforcer.common;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.modules.character {

    /// <summary>
    /// PreventExternalFoodChanges: holds a character's eaten foods to what they last had on this server.
    ///
    /// Vanilla keeps the foods a player has eaten, and how much burn time each has left, in the player profile - which
    /// travels with the character into every world. So a player can log out, eat three servings of food this server
    /// has not reached yet in a solo world, and walk straight back in with them; or simply top their food back up
    /// for free. The save records the foods instead, and a join puts back exactly what was recorded:
    ///  - a returning character has their foods restored to the ones, and the burn time, they left with;
    ///  - a new character has whatever they arrived with cleared (see NewCharacterRules).
    ///
    /// In a save, null is a third answer - never tracked - handled the same way as the Forsaken Power: the join adopts
    /// the live foods as the baseline rather than stripping them. See Character.Foods.
    /// </summary>
    internal static class FoodSync {

        internal static bool Tracked {
            get { return ValConfig.PreventExternalFoodChanges != null && ValConfig.PreventExternalFoodChanges.Value; }
        }

        /// <summary>
        /// The live foods to record, or null when they are not being tracked. Null rather than the live value so a save
        /// written with the setting off looks exactly like one written before it existed, and turning it on later
        /// adopts the foods the character has then instead of restoring whatever was recorded last time.
        /// </summary>
        internal static List<PackedFood> Capture(Player player) {
            if (!Tracked || player == null) { return null; }
            List<PackedFood> foods = new List<PackedFood>();
            foreach (Player.Food food in player.GetFoods()) {
                if (food == null || string.IsNullOrEmpty(food.m_name)) { continue; }
                foods.Add(new PackedFood { Name = food.m_name, Time = food.m_time });
            }
            return foods;
        }

        /// <summary>
        /// Whether a delta flush has a food change worth sending on its own: something was eaten, or a food ran out.
        /// Drain alone is not - it would make every flush a send - and rides along with the next real change.
        /// </summary>
        internal static bool ChangedSince(List<PackedFood> current, List<PackedFood> baseline) {
            if (current == null) { return false; }
            if (baseline == null) { return true; }
            return current.Count != baseline.Count || PackedFood.Exceeds(current, baseline);
        }

        /// <summary>
        /// Join, returning character: put back the foods the save recorded, burn time included. Whatever the
        /// character ate, or let run out, since they left is undone.
        ///
        /// A save with no foods recorded predates tracking and has nothing to put back, so the live foods are adopted
        /// as the baseline instead. The alternative - reading "nothing recorded" as "no food" - would strip every
        /// existing player's food the moment an admin turned this on.
        /// </summary>
        internal static void RestoreOnJoin(Player player, DataObjects.Character saved) {
            if (!Tracked || player == null || saved == null) { return; }
            List<PackedFood> live = Capture(player);
            if (saved.Foods == null) {
                Logger.LogInfo($"No foods recorded for {saved.Name} yet; adopting their current ones ({Describe(live)}) as the baseline.");
                saved.Foods = live;
                return;
            }
            if (PackedFood.Same(live, saved.Foods)) { return; }
            Logger.LogInfo($"Restoring the foods recorded for {saved.Name}: {Describe(live)} -> {Describe(saved.Foods)}.");
            SetLive(player, saved.Foods);
        }

        /// <summary>Join, new character: clear whatever foods they arrived with.</summary>
        internal static void StripLive(Player player, string characterName) {
            if (player == null || player.GetFoods().Count == 0) { return; }
            Logger.LogInfo($"New character {characterName}: clearing the foods they arrived with ({Describe(Capture(player))}).");
            SetLive(player, new List<PackedFood>());
        }

        /// <summary>
        /// The server reconciled a save and sent it back: bring the live foods into line with the record. Only ever
        /// takes food away - a record captured a few seconds ago has slightly more burn time than the live foods, and
        /// handing that back would be a small top-up on every push. A null record says nothing either way.
        /// </summary>
        internal static void ApplyRecord(Player player, DataObjects.Character record, string reason) {
            if (!Tracked || player == null || record == null || record.Foods == null) { return; }
            List<PackedFood> live = Capture(player);
            if (!PackedFood.Exceeds(live, record.Foods)) { return; }
            Logger.LogInfo($"{reason}: setting foods from {Describe(live)} to {Describe(record.Foods)}.");
            SetLive(player, record.Foods);
        }

        private static void SetLive(Player player, List<PackedFood> foods) {
            List<Player.Food> live = player.GetFoods();
            live.Clear();
            foreach (PackedFood packed in foods) {
                if (packed == null || string.IsNullOrEmpty(packed.Name) || packed.Time <= 0f) { continue; }
                // Resolved the same way Player.Load resolves a saved food, and skipped the same way when a mod that
                // added it is no longer installed.
                GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(packed.Name) : null;
                ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                if (drop == null) {
                    Logger.LogWarning($"Not restoring food {packed.Name}: no item prefab by that name exists.");
                    continue;
                }
                live.Add(new Player.Food { m_name = packed.Name, m_item = drop.m_itemData, m_time = packed.Time });
            }
            // What EatFood itself calls: derives each food's health, stamina and eitr from its burn time and resizes
            // the bars to match, now rather than on the next food tick. It also takes the tick's one second of burn
            // time, exactly as eating does.
            player.UpdateFood(0f, forceUpdate: true);
        }

        private static string Describe(List<PackedFood> foods) {
            if (foods == null || foods.Count == 0) { return "none"; }
            List<string> parts = new List<string>();
            foreach (PackedFood food in foods) {
                if (food == null) { continue; }
                parts.Add($"{food.Name} {Mathf.RoundToInt(food.Time)}s");
            }
            return string.Join(", ", parts.ToArray());
        }

        // Eating does not always touch the inventory - a feast is eaten off its table (Feast.OnInteract) - and the
        // delta tracker only wakes on Inventory.Changed. Without this a feast would not reach the server until the
        // next full save, and a crash in between would restore the food the player had before it.
        [HarmonyPatch(typeof(Player), nameof(Player.EatFood))]
        public static class TrackFoodEaten {
            [HarmonyPostfix]
            private static void Postfix(Player __instance, bool __result) {
                if (!__result || !Tracked) { return; }
                if (__instance == null || __instance != Player.m_localPlayer) { return; }
                // Until the join is validated there is no baseline to compare against, and the join decides the foods
                // itself.
                if (!CharacterManager.JoinValidationComplete || CharacterManager.PlayerCharacter == null) { return; }
                CharacterDeltaTracker.MarkBaselineDirty();
            }
        }
    }
}
