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

                if (CharacterManager.JoinValidationComplete) {
                    CharacterManager.RebaselineFromLiveInventory(player);
                } else if (CharacterManager.JoinValidationPending) {
                    // A deferred validation is already waiting on the server's answer; it will run for this
                    // player when the answer lands.
                } else if (CharacterManager.ShouldWaitForServerCharacter()) {
                    JoinGate.BeginDeferredJoinValidation(player);
                } else {
                    CharacterManager.LoadAndValidatePlayer(player);
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
                CharacterManager.ResetServerCharacterState();
                CharacterDeltaTracker.StopWatching();
            }
        }

        // The reset that cannot be missed. Game.Logout above covers a menu logout, but a failed handshake, a
        // dropped connection during loading, and a listen host returning to the menu and hosting again all
        // reach a new session without it - and a stale "the server already answered" from the previous session
        // would let the next one skip straight past the check. A session cannot receive the character RPC
        // before its own ZNet.Start, so resetting here is always safe and always early enough.
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Start))]
        public static class ResetServerCharacterStateOnConnect {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static void Prefix() {
                CharacterManager.ResetServerCharacterState();
                // A shutdown that armed the latch but never reached SavePlayerProfile (vanilla skipped it)
                // must not leave it armed for the next session.
                finalSavePending = false;
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
        public static class ResetServerCharacterStateOnShutdown {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            private static void Postfix() {
                CharacterManager.ResetServerCharacterState();
            }
        }

        // Player.Load runs *inside* Game.SpawnPlayer, so this fires before the SpawnPlayer postfix that does the
        // rest of the join validation - which means it cannot be deferred to wait for the server's answer the
        // way LoadAndValidatePlayerPatch can. It has to decide now, and it decides the safe way: with no known
        // character, a joining player's custom data is cleared rather than kept. If the server's character does
        // arrive afterwards, LoadAndValidatePlayer puts the stored custom data back.
        [HarmonyPatch(typeof(Player))]
        public static class LoadPlayerCustomData {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.First)]
            [HarmonyPatch(nameof(Player.Load))]
            static void Postfix(Player __instance) {
                if (!ValConfig.PreventExternalCustomDataChanges.Value) { return; }
                // GetPlayerID reads the player list off ZNet, and this runs early enough that neither is
                // guaranteed to exist yet (character selection, or the main menu preview player).
                if (ZNet.instance == null || SceneManager.GetActiveScene().name.Equals("main") == false) { return; }

                string playerID;
                string PlayerName;
                if (CharacterManager.PlayerCharacter != null) {
                    playerID = CharacterManager.PlayerCharacter.HostID;
                    PlayerName = CharacterManager.PlayerCharacter.Name;
                } else {
                    playerID = CharacterManager.GetPlayerID(__instance);
                    PlayerName = __instance.GetPlayerName();
                }
                if (string.IsNullOrEmpty(playerID)) { return; }

                // Routed through ResolveSessionCharacter so this shares the single rule about when the local
                // save file may be trusted. It is exactly this call that used to load a solo world's save on a
                // first join and reinstate its custom data - the ExtraSlots backup from an unrelated world that
                // kept turning up in server saves.
                DataObjects.Character savableChar = CharacterManager.ResolveSessionCharacter(playerID, PlayerName, out bool isNewCharacter);

                if (isNewCharacter) {
                    if (ValConfig.newCharacterClearCustomData.Value && __instance.m_customData != null) {
                        Logger.LogInfo($"New character {PlayerName}: clearing custom data carried in from elsewhere.");
                        __instance.m_customData.Clear();
                    }
                } else if (savableChar != null) {
                    // Copy rather than alias: sharing one dictionary with the tracked character is what stopped
                    // the delta tracker from ever detecting a custom data change.
                    //
                    // ApplyToPlayer rather than a plain snapshot, and this is the site that matters most:
                    // this postfix runs at Priority.First, BEFORE ExtraSlots' own Player.Load postfix decides
                    // whether to restore its inventory backup. Installing the tracked copy of that backup
                    // here is what used to resurrect a dead player's extra-slot gear - vanilla's death save
                    // had just correctly emptied the live backup, this overwrote it with the stale pre-death
                    // one, and ExtraSlots then restored a full set of gear that was also lying in the
                    // tombstone. Pass-through keys keep the live (profile-fresh) value instead.
                    __instance.m_customData = modules.compat.CompatCustomData.ApplyToPlayer(savableChar.PlayerCustomData, __instance.m_customData);
                    Logger.LogDebug("Set player custom data.");
                }
            }
        }

        // NOTE: full character saves are not triggered by the vanilla Player.Save itself. Persistence map:
        //  - End-of-session: SaveSyncForShutdown (Game.Shutdown prefix) arms the latch and marks the session
        //    clean; CaptureAfterProfileSave (Game.SavePlayerProfile postfix) writes the full save recorded
        //    Clean. Game.Shutdown is the choke point both exit paths funnel through — menu logout
        //    (Game.Logout -> ContinueLogout -> Shutdown) AND quit-to-desktop / Alt+F4
        //    (Game.OnApplicationQuit -> Shutdown, which never calls Game.Logout) — and it reaches
        //    SavePlayerProfile before it tears the scene and the connection down, so the save still sends.
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

        // Armed by the Game.Shutdown prefix, consumed by the Game.SavePlayerProfile postfix below. The two are
        // split because the end-of-session save has to be taken AFTER vanilla has flushed the profile, not
        // before — see CaptureAfterProfileSave.
        private static bool finalSavePending = false;

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
                // The save itself is deferred to the SavePlayerProfile postfix, which this same Shutdown call
                // is about to reach. Taking it here read the player one flush too early.
                finalSavePending = true;
            }
        }

        // Where a full character save is actually taken.
        //
        // Mods do not keep Player.m_customData current. The established pattern is to serialize into it from a
        // Player.Save prefix, so between one profile save and the next the dictionary describes a past state.
        // EpicLoot's adventure data (bounty progress, claimed rewards, treasure maps) is written exactly that
        // way, and vanilla's own save interval is twenty minutes. Reading the dictionary at any other moment
        // captures that stale past, and because LoadPlayerCustomData re-imposes the tracked copy on the next
        // Player.Load, the stale past then overwrites the player's real one: completed bounties reverted to
        // in-progress on the next login, permanently, because their targets were already dead.
        //
        // Game.SavePlayerProfile is the one point where that is not true. It calls PlayerProfile.SavePlayerData
        // -> Player.Save, so by the time a postfix runs every such mod has just flushed. Both jobs hang off it:
        //  - the end-of-session save, which Game.Shutdown reaches here BEFORE ZNetScene.Shutdown() and
        //    ZNet.Shutdown(), so the server peer is still live and FinalSaveRpc can still send synchronously;
        //  - the periodic profile autosave, which only wakes the delta tracker. That is deliberate: full saves
        //    stay on the server's own schedule (FullSyncScheduler), and all this has to do is let a
        //    custom-data-only change reach the delta stream at a moment when the dictionary is honest. The
        //    tracker's dirty flag is otherwise raised only by Inventory.Changed, so a change that never touches
        //    the inventory was never sampled at all — the same reason ForsakenPower and FoodSync nudge it.
        [HarmonyPatch(typeof(Game), nameof(Game.SavePlayerProfile))]
        public static class CaptureAfterProfileSave {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(bool isFromRpc) {
                if (Player.m_localPlayer == null) { return; } // dedicated server

                // Mirror every reason vanilla's own body returned without saving the character. Saving when it
                // did not is the same divergence this patch exists to avoid, just in the other direction; the
                // pending latch is left armed so a later profile save still takes the end-of-session copy.
                if (SaveSystem.HasSessionFlag(SaveSystemSessionFlags.DontSaveCharacter)) { return; }
                if (isFromRpc && ZNet.instance != null && ZNet.instance.HardSaveBlock()) { return; }

                if (!finalSavePending) {
                    // Routine profile save: the custom data is current as of right now, so let the delta
                    // tracker pick it up on its next tick.
                    CharacterDeltaTracker.MarkBaselineDirty();
                    return;
                }

                finalSavePending = false;
                // Never let this throw. An exception out of a postfix propagates into Game.Shutdown, and that
                // means no ZNet shutdown and - since it unwinds ContinueLogout too - no return to the menu:
                // the logout button simply stops working. Losing the enforcer's copy of one save is the far
                // smaller failure.
                try {
                    CharacterManager.SavePlayerCharacter(Player.m_localPlayer);
                } catch (Exception e) {
                    Logger.LogError($"Final character save failed during shutdown; letting the game shut down anyway: {e}");
                }
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
