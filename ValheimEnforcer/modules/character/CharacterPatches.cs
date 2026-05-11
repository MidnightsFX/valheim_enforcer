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

        [HarmonyPatch(typeof(Game), nameof(Game.SpawnPlayer))]
        public static class LoadAndValidatePlayerPatch {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.First)]
            private static void PlayerSpawn(Game __instance) {
                CharacterManager.LoadAndValidatePlayer(Player.m_localPlayer);
            }
        }

        [HarmonyPatch(typeof(Game), nameof(Game.Logout))]
        public static class ClearPlayerCharacterOnLogout {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            private static void Postfix() {
                if (CharacterManager.PlayerCharacter != null) {
                    Logger.LogDebug($"Clearing selected save profile for {CharacterManager.PlayerCharacter.Name} on logout.");
                    CharacterManager.PlayerCharacter = null;
                }
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

        // Move this off to its own repeating process? Recieve a unique seed from the server to offset save timer to prevent congestion?
        [HarmonyPatch(typeof(Player), nameof(Player.Save))]
        public static class SaveSync {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.High)]
            private static void PlayerSave(Player __instance) {
                if (__instance == null || SceneManager.GetActiveScene().name.Equals("main") == false) { return; }
                string playerID = "";
                string PlayerName = "";
                DataObjects.Character savableChar = null;
                if (CharacterManager.PlayerCharacter != null) {
                    savableChar = CharacterManager.PlayerCharacter;
                    playerID = CharacterManager.PlayerCharacter.HostID;
                    PlayerName = CharacterManager.PlayerCharacter.Name;
                } else {
                    playerID = CharacterManager.GetPlayerID(__instance);
                    PlayerName = __instance.GetPlayerName();
                }
                Logger.LogDebug($"Saving character for player {PlayerName} with id {playerID}");

                if (CharacterManager.PlayerCharacter == null) {
                    savableChar = ValConfig.LoadCharacterFromSave(playerID, PlayerName);
                }

                if (savableChar == null) {
                    Logger.LogWarning($"Attempted to save character for player {PlayerName} with ID {playerID} but no existing character data was found. Creating new character data.");
                    savableChar = new DataObjects.Character() {
                        Name = PlayerName,
                        HostID = playerID,
                        SkillLevels = __instance.GetSkills().GetSkillList().ToDictionary(skill => skill.m_info.m_skill, skill => skill.m_level),
                        ConfiscatedItems = null,
                    };
                    // Add all of the players current items
                    foreach (ItemDrop.ItemData item in __instance.GetInventory().GetAllItems().ToList()) {
                        savableChar.AddItemToPlayerItems(item);
                    }
                    if (ValConfig.PreventExternalCustomDataChanges.Value) {
                        savableChar.PlayerCustomData = __instance.m_customData;
                    }
                    if (ValConfig.SavePlayerStatusEffectsOnLogout.Value) {
                        savableChar.ActiveCharacterEffects.Clear();
                        foreach (StatusEffect se in __instance.GetSEMan().GetStatusEffects()) {
                            Logger.LogDebug($"Saving active status effect: {se.name}");
                            if (savableChar.ActiveCharacterEffects.ContainsKey(se.name)) {
                                savableChar.ActiveCharacterEffects[se.name] = new PackedStatusEffect(se);
                            } else {
                                savableChar.ActiveCharacterEffects.Add(se.name, new PackedStatusEffect(se));
                            }
                        }
                    }
                } else {
                    Logger.LogDebug($"Existing character data found for player {PlayerName} with ID {playerID}. Updating character data with current player information.");
                    savableChar.SkillLevels = __instance.GetSkills().GetSkillList().ToDictionary(skill => skill.m_info.m_skill, skill => skill.m_level);
                    Logger.LogDebug($"Updated player skills for {PlayerName} with ID {playerID}.");
                    if (ValConfig.PreventExternalCustomDataChanges.Value) {
                        savableChar.PlayerCustomData = __instance.m_customData;
                        Logger.LogDebug("Updated player custom data.");
                    }
                    savableChar.PlayerItems.Clear();
                    // Add all of the players current items
                    foreach (ItemDrop.ItemData item in __instance.GetInventory().GetAllItems().ToList()) {
                        savableChar.AddItemToPlayerItems(item);
                    }
                    Logger.LogDebug($"Updated player Items for {PlayerName} with ID {playerID}.");

                    if (ValConfig.SavePlayerStatusEffectsOnLogout.Value) {
                        savableChar.ActiveCharacterEffects.Clear();
                        foreach (StatusEffect se in __instance.GetSEMan().GetStatusEffects()) {
                            Logger.LogDebug($"Saving active status effect: {se.name}");
                            if (savableChar.ActiveCharacterEffects.ContainsKey(se.name)) {
                                savableChar.ActiveCharacterEffects[se.name] = new PackedStatusEffect(se);
                            } else {
                                savableChar.ActiveCharacterEffects.Add(se.name, new PackedStatusEffect(se));
                            }
                        }
                        Logger.LogDebug("Updated player active status effects.");
                    }
                }

                if (savableChar == null) {
                    Logger.LogWarning("Savable character was null, not sending network updates.");
                    return;
                }

                ValConfig.WritePlayerCharacterToSave(playerID, savableChar);

                if (ZNet.instance != null && ZNet.instance.GetServerPeer() != null) {
                    Logger.LogDebug("Sending updated character data to server.");
                    ValConfig.CharacterSaveRPC.SendPackage(ZNet.instance.GetServerPeer().m_uid, ValConfig.SendCharacterAsZpackage(savableChar));
                }
            }
        }

        [HarmonyPatch]
        public static class TrackInventoryAddItem {

            static Inventory playerInventory;

            [HarmonyPatch(typeof(Player), nameof(Player.SetLocalPlayer))]
            static void Postfix() {
                playerInventory = Player.m_localPlayer.GetInventory();
            }

            [HarmonyPrefix]
            [HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem), new[] { typeof(ItemDrop.ItemData) })]
            static void AddItemItemDropItemDataPrefix(ItemDrop.ItemData item, out DataObjects.PackedItem __state) {
                // Building the packed item data MUST happen in a prefix, as the item is deleted when added to the inventory
                __state = (item?.m_dropPrefab != null) ? ItemDeltaTracker.BuildPackedItem(item) : null;
            }

            [HarmonyPostfix]
            [HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem), new[] { typeof(ItemDrop.ItemData) })]
            static void AddItemItemDropItemData(Inventory __instance, bool __result, DataObjects.PackedItem __state) {
                // Item was not added, delta tracking is suppressed, or player is not fully initialized yet, so skip tracking.
                if (__result == false || CharacterManager.SuppressDeltaTracking || Player.m_localPlayer == null || playerInventory == null) { return; }
                // Only track changes to the local player's inventory
                if (playerInventory != __instance) { return; }
                CharacterManager.PendingItemDeltas.Add(new DataObjects.ItemDelta {
                    Op = DataObjects.ItemDeltaChangeType.Added, Item = __state,
                });
                Logger.LogDebug($"Delta enqueued: Added {__state.prefabName} x{__state.m_stack}");
            }

            [HarmonyPrefix]
            [HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem), new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int) })]
            static void AddItemAtpositionPrefix(ItemDrop.ItemData item, out DataObjects.PackedItem __state) {
                __state = (item?.m_dropPrefab != null) ? ItemDeltaTracker.BuildPackedItem(item) : null;
            }

            [HarmonyPostfix]
            [HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem), new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int) })]
            static void AddItemAtpositionPostfix(Inventory __instance, bool __result, DataObjects.PackedItem __state) {
                if (!__result || __state == null) return;
                if (CharacterManager.SuppressDeltaTracking) return;
                if (Player.m_localPlayer == null) return;
                if (!ReferenceEquals(__instance, Player.m_localPlayer.GetInventory())) return;
                CharacterManager.PendingItemDeltas.Add(new DataObjects.ItemDelta {
                    Op = DataObjects.ItemDeltaChangeType.Added,
                    Item = __state,
                });
                Logger.LogDebug($"Delta enqueued: Added (pos) {__state.prefabName} x{__state.m_stack}");
            }


            [HarmonyPrefix]
            [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), new[] { typeof(ItemDrop.ItemData) })]
            static void RemoveItemPrefix(ItemDrop.ItemData item, out DataObjects.PackedItem __state) {
                __state = (item?.m_dropPrefab != null) ? ItemDeltaTracker.BuildPackedItem(item) : null;
            }

            [HarmonyPostfix]
            [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), new[] { typeof(ItemDrop.ItemData) })]
            static void RemoveItemPostfix(Inventory __instance, DataObjects.PackedItem __state) {
                if (__state == null) return;
                if (CharacterManager.SuppressDeltaTracking) return;
                if (Player.m_localPlayer == null) return;
                if (!ReferenceEquals(__instance, Player.m_localPlayer.GetInventory())) return;
                CharacterManager.PendingItemDeltas.Add(new DataObjects.ItemDelta {
                    Op = DataObjects.ItemDeltaChangeType.Removed,
                    Item = __state,
                });
                Logger.LogDebug($"Delta enqueued: Removed {__state.prefabName} x{__state.m_stack}");
            }
        }
    }
}
