using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine.SceneManagement;
using ValheimEnforcer.common;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.modules.character {
    internal static class CharacterPatches {

        // Game.SpawnPlayer instantiates a brand new Player prefab every time, so this fires on the initial join
        // AND on every respawn (post-death, and the second spawn SkipIntro produces). Only the first spawn of a
        // session is a join: that is the one that gets confiscation and item restoration validated against the
        // save. Every later spawn adopts the live inventory as the new baseline instead, because vanilla and any
        // installed death mod - not the enforcer - decide what a player keeps through a death.
        [HarmonyPatch(typeof(Game), nameof(Game.SpawnPlayer))]
        public static class LoadAndValidatePlayerPatch {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.First)]
            private static void PlayerSpawn(Game __instance) {
                Player player = Player.m_localPlayer;
                if (player == null) { return; }

                if (!CharacterManager.JoinValidationComplete) {
                    CharacterManager.LoadAndValidatePlayer(player);
                } else {
                    CharacterManager.RebaselineFromLiveInventory(player);
                }

                // Each spawn is a new Player with a new Inventory instance, so the change subscription that keeps
                // the baseline current has to be re-pointed at it.
                CharacterDeltaTracker.WatchInventory(player);
            }
        }

        // Game.Logout is the end of a session for both a menu logout and a dropped connection (Game.FixedUpdate
        // calls Logout when the connection status goes bad), so it is where the once-per-session join latch and
        // the inventory subscription are torn down. Quit-to-desktop ends the process, so it needs no reset.
        [HarmonyPatch(typeof(Game), nameof(Game.Logout))]
        public static class ClearPlayerCharacterOnLogout {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            private static void Postfix() {
                if (CharacterManager.PlayerCharacter != null) {
                    Logger.LogDebug($"Clearing selected save profile for {CharacterManager.PlayerCharacter.Name} on logout.");
                    CharacterManager.PlayerCharacter = null;
                }
                CharacterManager.LogoutInProgress = false;
                CharacterManager.JoinValidationComplete = false;
                CharacterDeltaTracker.StopWatching();
            }
        }

        [HarmonyPatch(typeof(Player))]
        public static class LoadPlayerCustomData {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.First)]
            [HarmonyPatch(nameof(Player.Load))]
            static void Postfix(Player __instance) {
                string playerID;
                string PlayerName;
                DataObjects.Character savableChar = null;
                if (CharacterManager.PlayerCharacter != null) {
                    savableChar = CharacterManager.PlayerCharacter;
                    playerID = CharacterManager.PlayerCharacter.HostID;
                    PlayerName = CharacterManager.PlayerCharacter.Name;
                } else {
                    playerID = CharacterManager.GetPlayerID(__instance);
                    PlayerName = __instance.GetPlayerName();
                }
                if (CharacterManager.PlayerCharacter == null) {
                    savableChar = ValConfig.LoadCharacterFromSave(playerID, PlayerName);
                }

                if (savableChar == null) {
                    if (ValConfig.PreventExternalCustomDataChanges.Value) {
                        if (ValConfig.newCharacterClearCustomData.Value) { __instance.m_customData.Clear(); }
                    }
                } else {
                    if (ValConfig.PreventExternalCustomDataChanges.Value) {
                        __instance.m_customData = savableChar.PlayerCustomData;
                        Logger.LogDebug("Set player custom data.");
                    }
                }
            }
        }

        // NOTE: full character saves are no longer triggered by the vanilla Player.Save (world/profile
        // autosave). Persistence map:
        //  - End-of-session: SaveSyncForShutdown (Game.Shutdown prefix) writes a full save recorded Clean.
        //    Game.Shutdown is the choke point both exit paths funnel through — menu logout
        //    (Game.Logout -> ContinueLogout -> Shutdown) AND quit-to-desktop / Alt+F4
        //    (Game.OnApplicationQuit -> Shutdown, which never calls Game.Logout).
        //  - Mid-session: routine changes are picked up by CharacterDeltaTracker, which watches the player's
        //    inventory for changes rather than polling. On networked clients they stream up incrementally and
        //    the server also pulls periodic full saves on its own schedule (FullSyncScheduler); both are
        //    recorded DirtyDisconnect. In singleplayer the same watcher keeps the local save current.
        //  - Join: LoadAndValidatePlayer still pushes a full save directly. It runs once per session only -
        //    see JoinValidationComplete.
        //  - Death: ClearTrackedItemsOnDeath pushes a full save with an emptied item list immediately, and the
        //    respawn re-baselines from the live player instead of running the join pipeline.

        // Drain the async character persistence store before the server stops so no queued save is lost.
        // No-op on clients (the store is only used server-side, in disk storage mode).
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
        public static class FlushCharacterStoreOnShutdown {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static void Prefix(ZNet __instance) {
                if (__instance != null && __instance.IsServer()) {
                    CharacterStore.Shutdown();
                }
            }
        }

        [HarmonyPatch(typeof(Game), nameof(Game.Shutdown))]
        public static class SaveSyncForShutdown {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static void PlayerSave(Game __instance, bool saveWorld) {
                // Shutdown can run twice (e.g. Logout then OnApplicationQuit); vanilla ignores the second
                // call via m_shuttingDown and so do we.
                if (__instance.m_shuttingDown) { return; }
                // Stay in lockstep with vanilla: when it skips SavePlayerProfile (disk-space decline) the
                // enforcer save must stay equally stale, or the two stores diverge and cause false
                // confiscation/restoration on the next join.
                if (!saveWorld) { return; }
                if (Player.m_localPlayer == null) { return; } // dedicated server
                // Mark the shutdown so this save — and any save the vanilla Shutdown body triggers afterwards —
                // records the character as cleanly disconnected. Reset by ClearPlayerCharacterOnLogout on a
                // return-to-menu, and by LoadAndValidatePlayer on the next spawn.
                CharacterManager.LogoutInProgress = true;
                CharacterManager.SavePlayerCharacter(Player.m_localPlayer);
            }
        }

        // Death handling. By the time this runs vanilla has moved the inventory into the tombstone, applied the
        // skill penalty and removed every status effect. The tracked item list is cleared here rather than
        // snapshotted, so it can never be replayed back into the inventory on respawn - that replay was the
        // duplication bug, where the grave held one copy and the join-time item restore handed back a second.
        // Whatever the player actually ends up holding (vanilla keeps quest items; death mods such as Deathlink
        // may hand items back later, on their own schedule) is picked up by CharacterDeltaTracker.
        // Priority.Last so other mods' OnDeath postfixes, which may still be moving items around, run first.
        [HarmonyPatch(typeof(Player), "OnDeath")]
        public static class ClearTrackedItemsOnDeath {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Player __instance) {
                if (__instance == null || __instance != Player.m_localPlayer) { return; }
                // Vanilla's OnDeath body no-ops for a non-owner; stay in lockstep so a remote death never
                // rewrites the local player's save.
                if (__instance.m_nview == null || !__instance.m_nview.IsOwner()) { return; }
                CharacterManager.ClearTrackedItemsForDeath(__instance);
            }
        }
    }
}
