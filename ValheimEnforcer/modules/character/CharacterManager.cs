using HarmonyLib;
using Jotunn;
using Mono.Security.Interface;
using Splatform;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using ValheimEnforcer.common;
using static ValheimEnforcer.common.DataObjects;
using static Version;

namespace ValheimEnforcer.modules.character {
    internal static class CharacterManager {
        internal static DataObjects.Character PlayerCharacter = null;
        // Set while a saving Game.Shutdown is in progress (menu logout and quit-to-desktop both funnel through
        // it — see SaveSyncForShutdown) so the end-of-session save records the character as cleanly
        // disconnected. Every other save (delta stream, the periodic full-save pull, a full-sync response)
        // happens during an active session and must record DirtyDisconnect. See SavePlayerCharacter.
        internal static bool LogoutInProgress = false;
        // Join validation (confiscation + item restore) is a once-per-session event. Vanilla re-instantiates the
        // Player prefab on every respawn, so Game.SpawnPlayer fires again after every death and after SkipIntro -
        // none of which are joins. Reset only when the session ends (ClearPlayerCharacterOnLogout), so the player
        // has to actually log out and back in before their inventory is validated against the save again.
        internal static bool JoinValidationComplete = false;
        // Set while a deferred join validation is waiting for the server's answer, so a second Game.SpawnPlayer
        // during the wait does not start a second one. See CharacterPatches.LoadAndValidatePlayerPatch.
        internal static bool JoinValidationPending = false;

        /// <summary>What the server has told us about this character, which is not the same question as
        /// "do we have a character".</summary>
        internal enum ServerCharacterState {
            /// <summary>Nothing has arrived yet. On a remote server this is a race we have lost, not a fact.</summary>
            Unknown,
            /// <summary>The server looked and holds no save for this account and character name.</summary>
            ServerHasNone,
            /// <summary>The server sent us its stored character.</summary>
            Received,
            /// <summary>The server said it HAS a character for us and we could not read what it sent. Very
            /// different from ServerHasNone: the player is a returning one whose save we cannot see, so
            /// validating against nothing would confiscate everything they own.</summary>
            Unreadable,
        }
        internal static ServerCharacterState ServerCharacter = ServerCharacterState.Unknown;

        internal static void SetPlayerCharacter(DataObjects.Character character) {
            if (character == null) { return; }
            Logger.LogDebug("Set character from Saved server data");
            PlayerCharacter = character;
            ServerCharacter = ServerCharacterState.Received;
        }

        /// <summary>Back to knowing nothing. Called at both ends of a session so a previous connection's
        /// answer can never be mistaken for this one's.</summary>
        internal static void ResetServerCharacterState() {
            ServerCharacter = ServerCharacterState.Unknown;
            JoinValidationPending = false;
            // Invalidates any JoinGate coroutine still running from the session being torn down. Coroutines
            // live on a DontDestroyOnLoad object, so nothing else stops one - and a leftover coroutine would
            // otherwise clear the NEXT session's pending flag, or run a duplicate validation against a
            // character that session had already validated.
            SessionGeneration++;
        }

        /// <summary>Bumped whenever a session begins or ends. Anything that spans frames captures it and
        /// checks it is still current before acting.</summary>
        internal static int SessionGeneration { get; private set; }

        /// <summary>
        /// Whether this join should hold off until the server's answer arrives. Only ever true on a remote
        /// server with no answer yet: a listen host answers itself, and an answer already received is final.
        /// </summary>
        internal static bool ShouldWaitForServerCharacter() {
            return !ThisMachineIsAuthority()
                && ServerCharacter == ServerCharacterState.Unknown
                && ValConfig.InitialCharacterSyncWaitSeconds != null
                && ValConfig.InitialCharacterSyncWaitSeconds.Value > 0;
        }

        /// <summary>
        /// The server sent a character it does hold and we could not parse it (a corrupt save, a truncated
        /// payload). Recorded distinctly so the join can fail OPEN. The player is not new - the server said so
        /// - and treating an unreadable answer as "no character" would strip a returning player's inventory
        /// down to starting gear over a damaged file.
        /// </summary>
        internal static void SetServerCharacterUnreadable() {
            // Only the connect-time answer can put a session into this state. VENFORCE_CHAR also carries
            // mid-session pushes (an admin returning confiscated items, a sanitized first save), and one
            // corrupt push must not tear down a session that already has a good character: Unreadable stops
            // saving for the rest of the session, so it would silently discard everything since login.
            if (ServerCharacter == ServerCharacterState.Received) {
                Logger.LogWarning("Ignoring an unreadable character push from the server; this session already has a character and keeps using it.");
                return;
            }
            Logger.LogError("The server sent a stored character that could not be read. Skipping join validation entirely this session rather than risk confiscating a returning player's inventory. The server-side save for this character likely needs an admin's attention.");
            PlayerCharacter = null;
            ServerCharacter = ServerCharacterState.Unreadable;
        }

        /// <summary>The server answered, and the answer was that it has never seen this character.</summary>
        internal static void SetServerHasNoCharacter() {
            Logger.LogInfo("Server holds no stored character for this account and character name; this is a new character here.");
            PlayerCharacter = null;
            ServerCharacter = ServerCharacterState.ServerHasNone;
        }

        /// <summary>
        /// True when this machine owns the character store: singleplayer, or a listen host. False when a
        /// remote dedicated server owns it, in which case the Characters/ folder on this disk is nothing but
        /// leftovers from solo play or from some other server, and must never seed anything.
        /// </summary>
        internal static bool ThisMachineIsAuthority() {
            return ZNet.instance != null && ZNet.instance.IsServer();
        }

        /// <summary>
        /// The one answer to "what does this session start from", and the fix for the bug this whole path
        /// exists around.
        ///
        /// The local save file is only ever consulted when this machine IS the server. On a remote server it
        /// is not evidence of anything: singleplayer writes to exactly the same
        /// Characters/&lt;account&gt;/&lt;name&gt;.yaml path, so a player who cheated items into a solo world has
        /// a local file describing that inventory sitting right there. Reading it was how a first-time joiner
        /// ended up validated against their own solo save - every item matched, nothing was confiscated, and
        /// the cheated inventory was uploaded as the server's authoritative copy.
        ///
        /// So on a remote server there are exactly two possibilities: the server sent a character, or this is
        /// a new character here. Not knowing yet resolves to "new", which is the safe direction - the join
        /// path is what enforces the new-character rules, and the server's own first-save check
        /// (FirstSaveEnforcement) is keyed off the server's lookup rather than this one, so a client that
        /// guesses wrong cannot make a returning player look new to the server.
        /// </summary>
        internal static DataObjects.Character ResolveSessionCharacter(string playerID, string playerName, out bool isNewCharacter) {
            if (PlayerCharacter != null) {
                isNewCharacter = false;
                return PlayerCharacter;
            }

            if (ThisMachineIsAuthority()) {
                DataObjects.Character local = ValConfig.LoadCharacterFromSave(playerID, playerName);
                isNewCharacter = local == null;
                if (isNewCharacter) {
                    Logger.LogInfo($"No local character save for {playerName} ({playerID}); treating as a new character.");
                }
                return local;
            }

            isNewCharacter = true;
            if (ServerCharacter == ServerCharacterState.Unknown) {
                Logger.LogWarning($"The server has not told us whether it holds a character for {playerName} ({playerID}) yet. Treating them as a new character; the local save on this machine is deliberately not used, because a solo world writes to that same file.");
            }
            return null;
        }

        /// <summary>
        /// The account this character belongs to. Every save is filed under it, on this machine and on the
        /// server, so it has to be the same string on both sides and the same string on every join.
        ///
        /// The local platform account is asked first because it is the only source that is always available: it
        /// is established at game start and depends on no network state at all. The player list is a fallback,
        /// not the primary, because it is routinely still empty at exactly the moments this is called - the
        /// first join, and the Player.Load postfix, which runs inside Game.SpawnPlayer.
        ///
        /// There is deliberately no last-resort fallback. The previous one returned the ZDOID
        /// (player.m_nview.GetZDO().m_uid), which is not an account id at all: a character filed under it is
        /// filed under a key the server will never look up again, so the player reads as brand new on every
        /// single join and has their whole inventory confiscated each time. An empty return says "I do not know
        /// who this is", and callers refuse to write a save rather than write it somewhere wrong.
        /// </summary>
        internal static string GetPlayerID(Player player) {
            string selectedID = LocalPlatformUserId();
            if (!string.IsNullOrEmpty(selectedID)) {
                return NormalizeAccountId(selectedID);
            }

            if (ZNet.instance == null) {
                Logger.LogWarning("Cannot resolve the local account id: the platform reported none and ZNet is not up yet.");
                return "";
            }

            List<ZNet.PlayerInfo> zplayerInfo = ZNet.instance.GetPlayerList();
            ZDOID localCharacterID = player?.m_nview?.GetZDO()?.m_uid ?? ZDOID.None;
            if (localCharacterID != ZDOID.None) {
                foreach (ZNet.PlayerInfo playerInfo in zplayerInfo) {
                    if (playerInfo.m_characterID == localCharacterID) {
                        selectedID = playerInfo.m_userInfo.m_id.m_userID;
                        Logger.LogDebug($"Matched local player by ZDO to account {selectedID}");
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(selectedID)) {
                string playerName = player?.GetPlayerName();
                foreach (ZNet.PlayerInfo playerInfo in zplayerInfo) {
                    if (playerInfo.m_name == playerName) {
                        selectedID = playerInfo.m_userInfo.m_id.m_userID;
                        Logger.LogDebug($"Matched player {playerName} by name to ID {selectedID}");
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(selectedID)) {
                Logger.LogError($"Failed to resolve an account id for local player {player?.GetPlayerName()}. Character data will not be tracked this session rather than be filed under a fabricated id.");
                return "";
            }
            return NormalizeAccountId(selectedID);
        }

        // The bare platform user id for whoever is signed in on this machine. Wrapped because the platform layer
        // is not guaranteed to be initialised in every host process (a dedicated server has no local user).
        private static string LocalPlatformUserId() {
            try {
                PlatformUserID local = PlatformManager.DistributionPlatform?.LocalUser?.PlatformUserID ?? default;
                return string.IsNullOrEmpty(local.m_userID) ? null : local.m_userID;
            } catch (Exception e) {
                Logger.LogDebug($"Platform layer could not supply a local user id: {e.Message}");
                return null;
            }
        }

        // Ids reach us in more than one spelling (see PlatformIds). Strip any platform prefix, and drop the
        // ':'-suffixed form some sources produce, so one account always yields one folder name.
        private static string NormalizeAccountId(string id) {
            if (string.IsNullOrEmpty(id)) { return ""; }
            if (id.Contains(":")) {
                Logger.LogDebug("Player ID contained invalid character : removing.");
                id = id.Split(':')[0];
            }
            return PlatformIds.Normalize(id);
        }

        internal static void SavePlayerCharacter(Player __instance) {
            if (__instance == null || SceneManager.GetActiveScene().name.Equals("main") == false) { return; }
            // A save marks the character Clean only when it is produced by a clean logout; every other save
            // represents an active (potentially soon-to-be-stale) session and is recorded as DirtyDisconnect.
            DataObjects.DisconnectionState lastDisconnect = LogoutInProgress ? DisconnectionState.Clean : DisconnectionState.DirtyDisconnect;
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
            if (string.IsNullOrEmpty(playerID)) {
                Logger.LogError($"Not saving character {PlayerName}: no account id could be resolved, and a save filed under a fabricated id would be lost and treated as a new character on the next join.");
                return;
            }
            Logger.LogDebug($"Saving character for player {PlayerName} with id {playerID}");

            // Same rule as the join path: on a remote server the local save file is a solo-play artefact, not
            // a baseline. Without this gate a full-sync request arriving before the join validation finished
            // would load that file and push it to the server as authoritative - the same exploit as
            // LoadAndValidatePlayer's, reached by a different route.
            if (ServerCharacter == ServerCharacterState.Unreadable) {
                Logger.LogWarning($"Not saving {PlayerName}: the server's stored character could not be read, and overwriting it with an unvalidated snapshot would destroy whatever is still recoverable from it.");
                return;
            }

            // Nothing has been validated yet this session, and on a remote server there is no trustworthy
            // baseline to fall back on. Refusing beats inventing one: the block below would otherwise build a
            // character straight from the live inventory - with none of the new-character rules applied - and
            // push it to the server as authoritative. That is reachable during the JoinGate wait, when
            // PlayerCharacter is still null, via a full-sync request or a Game.Shutdown save; a player who
            // joined and quit inside that window would leave their un-validated solo inventory as the server's
            // copy, and would read as a returning player forever after.
            if (!JoinValidationComplete && !ThisMachineIsAuthority()) {
                Logger.LogWarning($"Not saving {PlayerName}: their join has not been validated yet, so there is nothing to save that the server should trust.");
                return;
            }

            if (CharacterManager.PlayerCharacter == null) {
                savableChar = ResolveSessionCharacter(playerID, PlayerName, out _);
            }

            if (savableChar == null) {
                Logger.LogWarning($"Attempted to save character for player {PlayerName} with ID {playerID} but no existing character data was found. Creating new character data.");
                savableChar = new DataObjects.Character() {
                    Name = PlayerName,
                    HostID = playerID,
                    SkillLevels = __instance.GetSkills().GetSkillList().ToDictionary(skill => skill.m_info.m_skill, skill => skill.m_level),
                    ConfiscatedItems = null,
                    LastDisconnect = lastDisconnect
                };
                // Add all of the players current items
                foreach (ItemDrop.ItemData item in __instance.GetInventory().GetAllItems().ToList()) {
                    savableChar.AddItemToPlayerItems(item);
                }
                if (ValConfig.PreventExternalCustomDataChanges.Value) {
                    // Copy: aliasing the live dictionary makes the tracked baseline and the player the same
                    // object, so the delta tracker can never see a change (it would diff a dict against itself).
                    savableChar.PlayerCustomData = PackedItem.SnapshotCustomData(__instance.m_customData);
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
                savableChar.LastDisconnect = lastDisconnect;
                savableChar.SkillLevels = __instance.GetSkills().GetSkillList().ToDictionary(skill => skill.m_info.m_skill, skill => skill.m_level);
                Logger.LogDebug($"Updated player skills for {PlayerName} with ID {playerID}.");
                if (ValConfig.PreventExternalCustomDataChanges.Value) {
                    savableChar.PlayerCustomData = PackedItem.SnapshotCustomData(__instance.m_customData);
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

            ZNetPeer serverPeer = ZNet.instance?.GetServerPeer();
            if (serverPeer != null) {
                if (LogoutInProgress) {
                    // End-of-session save: send synchronously and flush the socket so it lands before the
                    // vanilla Game.Shutdown tears the connection down. Jotunn's paced coroutine send would be
                    // lost in the teardown here (see FinalSaveRpc).
                    Logger.LogDebug("Sending final character data to server (synchronous, logout).");
                    FinalSaveRpc.SendFinalSaveSync(serverPeer, savableChar);
                } else {
                    Logger.LogDebug("Sending updated character data to server.");
                    ValConfig.CharacterSaveRPC.SendPackage(serverPeer.m_uid, ValConfig.SendCharacterAsZpackage(savableChar));
                }
            } else if (ZNet.instance != null && ZNet.instance.IsServer()) {
                // Singleplayer / listen host: there is no server peer because we ARE the server; the local
                // write above is the authoritative save, so there is nothing to sync and no desync risk.
                Logger.LogDebug("No server peer; local write is authoritative.");
            } else {
                Logger.LogWarning("Server Disconnected, can't sync player data. This may result in desync issues.");
            }
        }

        internal static void LoadAndValidatePlayer(Player player) {
            // A fresh spawn is an active session; clear any stale logout flag so saves record DirtyDisconnect.
            LogoutInProgress = false;
            string playerID;
            string PlayerName;
            if (PlayerCharacter != null) {
                playerID = PlayerCharacter.HostID;
                PlayerName = PlayerCharacter.Name;
            } else {
                playerID = GetPlayerID(player);
                PlayerName = player.GetPlayerName();
            }
            if (string.IsNullOrEmpty(playerID)) {
                Logger.LogError($"Not validating character {PlayerName}: no account id could be resolved. Their character will not be tracked this session rather than be filed under a fabricated id.");
                return;
            }

            // Fail open: the server has a character for this player that we could not read, so we have nothing
            // to validate against and no basis to call them new. Leave their inventory and skills alone.
            if (ServerCharacter == ServerCharacterState.Unreadable) {
                Logger.LogError($"Not validating {PlayerName}: the server's stored character could not be read. Nothing will be confiscated, restored or tracked this session.");
                JoinValidationComplete = true;
                return;
            }

            Logger.LogInfo($"Player {PlayerName} with ID {playerID} validating character data.");
            DataObjects.Character savableChar = ResolveSessionCharacter(playerID, PlayerName, out bool isNewCharacter);

            if (isNewCharacter) {
                savableChar = BuildNewCharacter(player, playerID, PlayerName);
            }

            // Base enforcement runs on every join. On a *dirty* reconnect the server save can be up to one
            // delta window stale, so an admin may opt into leniency (ItemRemovalForDirtyReconnection) to avoid
            // confiscating items a crash victim legitimately gained in that window. Default keeps removal on for
            // every join — a forced-dirty disconnect cannot be used to bypass confiscation.
            bool skipRemovalForDirty = savableChar.LastDisconnect == DisconnectionState.DirtyDisconnect
                                       && ValConfig.ItemRemovalForDirtyReconnection.Value;
            if (ValConfig.RemoveNontrackedItemsFromJoiningPlayers.Value && !skipRemovalForDirty) {
                // A new character's removals were already recorded by the new-character strip, so this pass
                // must not record them a second time; for a returning character it is the only recorder.
                ReconcilePlayerToCharacter(player, savableChar, recordConfiscation: !isNewCharacter, "Join validation");
            }

            // Base restoration runs on every join. On a *dirty* reconnect the save can be stale, so restoring
            // "missing" items risks duping items the player consumed in the last (unsaved) delta window; default
            // skips restore on a dirty reconnect unless the admin opts in (ItemReturnForDirtyReconnection).
            bool suppressReturnForDirty = savableChar.LastDisconnect == DisconnectionState.DirtyDisconnect
                                          && !ValConfig.ItemReturnForDirtyReconnection.Value;
            if (ValConfig.AddMissingItemsFromPlayerServerSave.Value && !suppressReturnForDirty) {
                Logger.LogDebug("Checking to restore player items.");
                List<Tuple<string, int>> prefablist = new List<Tuple<string, int>>();
                foreach(ItemDrop.ItemData item in player.m_inventory.GetAllItems()) {
                    if (!PackedItem.TryPrefabName(item, out string heldPrefab)) { continue; }
                    prefablist.Add(new Tuple<string, int>(heldPrefab, item.m_stack));
                    }
                foreach (DataObjects.PackedItem item in savableChar.PlayerItems) {
                    if (item == null) { continue; }
                    Tuple<string, int> searcher = new Tuple<string, int>(item.prefabName, item.m_stack);
                    if (!prefablist.Contains(searcher)) {
                        Logger.LogInfo($"Adding missing item to players inventory: {item.prefabName}x{item.m_stack}");
                        item.AddToInventory(player, false);
                    }
                }
            }
            Logger.LogDebug($"Validated player items.");

            if (ValConfig.PreventExternalSkillRaises.Value) {
                player.GetSkills().GetSkillList().ForEach(skill => {
                    if (savableChar.SkillLevels.TryGetValue(skill.m_info.m_skill, out float savedLevel)) {
                        if (skill.m_level > savedLevel) {
                            Logger.LogInfo($"Removing external skill gains for {skill.m_info.m_skill} from {savedLevel} to {skill.m_level} from player {savableChar.Name}");
                            skill.m_level = savedLevel;
                        }
                    }
                });
            }
            Logger.LogDebug($"Validated player skills.");

            // Custom data is decided twice, and this is the second time. The first is the Player.Load postfix
            // (CharacterPatches.LoadPlayerCustomData), which runs inside Game.SpawnPlayer - before this - and so
            // has to fail closed and clear the data when it does not yet know the character. That is the right
            // call at that moment, but it would strand a returning player whose character only arrived
            // afterwards (a join deferred by JoinGate, or a server push). Reapplying here repairs that, and is
            // a harmless no-op when Player.Load already got it right.
            if (ValConfig.PreventExternalCustomDataChanges.Value && !isNewCharacter) {
                player.m_customData = PackedItem.SnapshotCustomData(savableChar.PlayerCustomData);
                Logger.LogDebug("Reapplied tracked custom data.");
            }

            if (ValConfig.SavePlayerStatusEffectsOnLogout.Value && savableChar.ActiveCharacterEffects != null && savableChar.ActiveCharacterEffects.Count > 0) {
                SEMan pseman = player.GetSEMan();
                foreach (KeyValuePair<string, PackedStatusEffect> kvp in savableChar.ActiveCharacterEffects) {
                    Logger.LogDebug($"Applying status effect: {kvp.Key}");
                    StatusEffect se = kvp.Value.ToStatusEffect();
                    if (se == null) { continue; }
                    pseman.AddStatusEffect(se);
                }
                savableChar.ActiveCharacterEffects.Clear();
                Logger.LogDebug("Validated saved status effects.");
            }

            PlayerCharacter = savableChar;
            PersistAndPushCharacter(playerID, savableChar);
            // Everything above is join-only enforcement. Later spawns in this session (deaths, SkipIntro) take
            // RebaselineFromLiveInventory instead - see CharacterPatches.LoadAndValidatePlayerPatch.
            JoinValidationComplete = true;
        }

        /// <summary>
        /// The server held our first save to the new-character rules and sent back what it kept. Adopt it and
        /// bring the live player into line.
        ///
        /// This is the other half of server-side enforcement. Sanitizing the stored save alone would leave the
        /// player still carrying everything, and their next delta would simply put it all back.
        ///
        /// Nothing is recorded as confiscated here: the server already recorded every removal, and recording
        /// again would file a second entry with a second id for each item, so an admin returning one would hand
        /// back two.
        /// </summary>
        internal static void ApplyServerSanitizedCharacter(DataObjects.Character sanitized) {
            if (sanitized == null) { return; }
            Logger.LogWarning($"The server applied its new-character rules to {sanitized.Name}: items and skills not in its record are being removed.");

            PlayerCharacter = sanitized;
            ServerCharacter = ServerCharacterState.Received;

            Player player = Player.m_localPlayer;
            if (player == null) {
                // Arrived before the player spawned; the join validation will run against this record.
                Logger.LogDebug("No local player yet; the sanitized character will be applied by join validation.");
                return;
            }

            ReconcilePlayerToCharacter(player, sanitized, recordConfiscation: false, "Server first-save enforcement");

            if (sanitized.SkillLevels == null) { sanitized.SkillLevels = new Dictionary<Skills.SkillType, float>(); }
            if (sanitized.PlayerItems == null) { sanitized.PlayerItems = new List<PackedItem>(); }

            foreach (Skills.Skill skill in player.GetSkills().GetSkillList()) {
                if (!sanitized.SkillLevels.TryGetValue(skill.m_info.m_skill, out float savedLevel)) { continue; }
                if (skill.m_level <= savedLevel) { continue; }
                Logger.LogInfo($"Server first-save enforcement: lowering {skill.m_info.m_skill} from {skill.m_level} to {savedLevel}.");
                skill.m_level = savedLevel;
                skill.m_accumulator = 0;
            }

            if (ValConfig.PreventExternalCustomDataChanges.Value) {
                player.m_customData = PackedItem.SnapshotCustomData(sanitized.PlayerCustomData);
            }

            // Re-baseline from what the player actually holds now, and drop the dirty flag the removals just
            // raised. Without this the client would stream Removed deltas for items the server has already
            // dropped from its copy; each would fail to match, the server would read that as drift, and it
            // would answer with a full-sync request. Self-healing but noisy, and nothing is lost by clearing:
            // BuildCharacterItemDeltas diffs the whole list against the baseline on the next real change.
            sanitized.PlayerItems.Clear();
            foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItems().ToList()) {
                sanitized.AddItemToPlayerItems(item);
            }
            CharacterDeltaTracker.ClearDirty();

            // Push the result back so the server's copy carries the fields a delta never describes (durability,
            // grid position, equipped state). This cannot loop: the sanitized save now exists, so the store's
            // first-save check reports Found and does not fire again.
            PersistAndPushCharacter(sanitized.HostID, sanitized);
        }

        /// <summary>
        /// Builds the character for somebody this store has never seen, and applies the new-character rules to
        /// both the record and the live player.
        ///
        /// Everything they are carrying and everything they know was granted somewhere this server did not
        /// see, so the record is built from the live player and then sanitised - rather than the live player
        /// being sanitised and the record built from the result - so that the exact same
        /// <see cref="NewCharacterRules"/> code decides what survives here as decides it server-side.
        /// </summary>
        private static DataObjects.Character BuildNewCharacter(Player player, string playerID, string playerName) {
            Logger.LogInfo($"Building a new character record for {playerName} ({playerID}).");

            // Clear item custom data BEFORE packing, not after. Packing copies it (PackedItem.CopyCustomData),
            // so clearing afterwards left the record claiming custom data the live item no longer had, and the
            // very first delta comparison saw every item as changed.
            if (ValConfig.ValidateItemCustomData.Value) {
                foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItems()) {
                    item.m_customData?.Clear();
                }
            }

            DataObjects.Character character = new DataObjects.Character() {
                Name = playerName,
                HostID = playerID,
                SkillLevels = player.GetSkills().GetSkillList().ToDictionary(skill => skill.m_info.m_skill, skill => skill.m_level),
            };
            foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItems().ToList()) {
                character.AddItemToPlayerItems(item);
            }
            if (ValConfig.PreventExternalCustomDataChanges.Value) {
                character.PlayerCustomData = PackedItem.SnapshotCustomData(player.m_customData);
            }

            NewCharacterRules.Policy policy = NewCharacterRules.Current();
            NewCharacterRules.Result result = NewCharacterRules.Apply(character, policy, recordConfiscation: true);
            if (result.Changed) {
                Logger.LogInfo($"New character rules applied to {playerName}: {result.Describe()}");
            }

            // Zeroing SkillLevels only rewrites the record. The live Skills object has to be told separately,
            // and it cannot be left to the PreventExternalSkillRaises clamp below: that is a different setting,
            // and with it off the delta tracker would upload the live (still raised) skills within one window
            // and the zeros would be gone.
            if (policy.ZeroSkills) {
                ZeroLiveSkills(player, character);
            }

            // The live inventory still holds everything; ReconcilePlayerToCharacter strips it down to what the
            // record kept. recordConfiscation is false because Apply above already recorded every removal -
            // recording again would file two entries, with two ids, for one item.
            ReconcilePlayerToCharacter(player, character, recordConfiscation: false, "New character");
            return character;
        }

        // Drop every live skill to the (already zeroed) tracked level.
        private static void ZeroLiveSkills(Player player, DataObjects.Character character) {
            foreach (Skills.Skill skill in player.GetSkills().GetSkillList()) {
                if (skill.m_level <= 0) { continue; }
                Logger.LogInfo($"New character: resetting {skill.m_info.m_skill} from {skill.m_level} to 0 for {character.Name}");
                skill.m_level = 0;
                skill.m_accumulator = 0;
            }
        }

        /// <summary>
        /// Brings a live player into line with a character record: removes items the record does not account
        /// for, clamps skills down to it, and adopts its custom data.
        ///
        /// Deliberately NOT run on a respawn, where a death mod may legitimately have handed items back that
        /// the record cannot know about yet.
        ///
        /// <paramref name="recordConfiscation"/> is false when the removals have already been recorded by
        /// whoever produced the record - the new-character strip, or the server's first-save enforcement.
        /// </summary>
        internal static void ReconcilePlayerToCharacter(Player player, DataObjects.Character character, bool recordConfiscation, string reasonPrefix) {
            if (player == null || character == null) { return; }

            Dictionary<ItemDrop.ItemData, ItemValidatorResult> ValidatorResults = ValidateItems(player.m_inventory.GetAllItems(), character);
            foreach (KeyValuePair<ItemDrop.ItemData, ItemValidatorResult> eval in ValidatorResults) {
                if (eval.Value.Validated) { continue; }
                Logger.LogInfo($"Removing item {PackedItem.Describe(eval.Key)}x{eval.Key.m_stack} from player {character.Name}. Validation message: {eval.Value.ValidationMessage}");
                if (recordConfiscation) {
                    character.AddConfiscatedItem(eval.Key, $"{reasonPrefix}: {eval.Value.ValidationMessage}");
                }
                player.UnequipItem(eval.Key);
                player.GetInventory().RemoveItem(eval.Key);
            }
        }

        // Write the character to the local save and, when connected to a dedicated server, push it as a full save.
        // The full push matters: BuildCharacterItemDeltas only describes the transition from the client's own
        // baseline, so a delta stream can never reconcile a server copy that has drifted away from it. The full
        // push is also the only thing that carries the fields PackedItem equality deliberately ignores -
        // durability, grid position and equipped state.
        internal static void PersistAndPushCharacter(string playerID, DataObjects.Character character) {
            if (character == null) { return; }
            ValConfig.WritePlayerCharacterToSave(playerID, character);

            ZNetPeer serverPeer = ZNet.instance?.GetServerPeer();
            if (serverPeer != null) {
                ValConfig.CharacterSaveRPC.SendPackage(serverPeer.m_uid, ValConfig.SendCharacterAsZpackage(character));
            }
        }

        // Every spawn after the session's first one is a respawn, not a join. Vanilla and any death mod have
        // already decided what the player keeps, so the live inventory is the truth - adopt it wholesale. No
        // confiscation (it would delete items a death mod legitimately returned) and no restore (the difference
        // is sitting in the tombstone, or was deliberately destroyed).
        internal static void RebaselineFromLiveInventory(Player player) {
            if (player == null) { return; }
            // A fresh spawn is an active session; clear any stale logout flag so saves record DirtyDisconnect.
            LogoutInProgress = false;

            DataObjects.Character savableChar = PlayerCharacter;
            if (savableChar == null) {
                // No tracked character (state was reset mid-session). The join pipeline bootstraps from the live
                // inventory, which is the same truth we would establish here.
                Logger.LogWarning("Respawn with no tracked character, falling back to full join validation.");
                LoadAndValidatePlayer(player);
                return;
            }

            Logger.LogInfo($"Player {savableChar.Name} respawned, re-baselining tracked state from their live inventory.");
            // Mid-session save, so it records the session as still active and potentially soon-to-be-stale.
            savableChar.LastDisconnect = DisconnectionState.DirtyDisconnect;
            savableChar.PlayerItems.Clear();
            foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItems().ToList()) {
                savableChar.AddItemToPlayerItems(item);
            }
            // Vanilla has already applied the death skill penalty and removed every status effect by this point.
            savableChar.SkillLevels = player.GetSkills().GetSkillList().ToDictionary(skill => skill.m_info.m_skill, skill => skill.m_level);
            savableChar.ActiveCharacterEffects.Clear();
            if (ValConfig.PreventExternalCustomDataChanges.Value) {
                savableChar.PlayerCustomData = PackedItem.SnapshotCustomData(player.m_customData);
            }

            PlayerCharacter = savableChar;
            PersistAndPushCharacter(savableChar.HostID, savableChar);
        }

        // Drop the tracked item list the moment the player dies. This is deliberately a clear rather than a
        // snapshot: snapshotting would race other mods' Player.OnDeath patches and bake in whatever they happened
        // to have done by that instant. Clearing is order independent - it destroys the pre-death list, which is
        // the thing that was being duplicated back into the inventory on respawn, and leaves re-population to
        // CharacterDeltaTracker, which observes the inventory instead of guessing when a death mod is finished.
        // Pushing immediately means an alt-F4 during the 10s respawn wait cannot leave the pre-death list
        // authoritative and dupe the grave on rejoin.
        internal static void ClearTrackedItemsForDeath(Player player) {
            if (player == null || PlayerCharacter == null) { return; }
            Logger.LogInfo($"Player {PlayerCharacter.Name} died, clearing tracked items pending re-enumeration.");
            // Mid-session save. Recording it dirty also means that if the player crashes out before looting their
            // grave, the next join skips the item restore by default rather than restoring against this save.
            PlayerCharacter.LastDisconnect = DisconnectionState.DirtyDisconnect;
            PlayerCharacter.PlayerItems.Clear();
            PlayerCharacter.ActiveCharacterEffects.Clear();
            PlayerCharacter.SkillLevels = player.GetSkills().GetSkillList().ToDictionary(skill => skill.m_info.m_skill, skill => skill.m_level);
            PersistAndPushCharacter(PlayerCharacter.HostID, PlayerCharacter);
        }

        // Validate Item, stacksize, custom data, and quality
        internal static Dictionary<ItemDrop.ItemData, ItemValidatorResult> ValidateItems(List<ItemDrop.ItemData> playerItems, DataObjects.Character savedChar) {
            Dictionary<ItemDrop.ItemData, ItemValidatorResult> validationResults = new Dictionary<ItemDrop.ItemData, ItemValidatorResult>();
            // A save that never had a playerItems key deserializes with the list null rather than empty.
            List<DataObjects.PackedItem> savedItems = savedChar.PlayerItems ?? new List<DataObjects.PackedItem>();
            Logger.LogInfo($"Player Items: {playerItems.Count} | SavedCharacter Items: {savedItems.Count}");
            foreach (ItemDrop.ItemData item in playerItems) {
                ValidationSummary ItemValidationSummary = new DataObjects.ValidationSummary();
                validationResults.Add(item, new ItemValidatorResult() {
                    CharacterItemRef = item,
                });
                string validationReason = "";

                // An item with no resolvable ItemDrop prefab has no identity to compare: it was never written
                // into the save (AddItemToPlayerItems skips it), so it can never match anything here either.
                // Reading m_dropPrefab.name on one used to throw and take the whole validation pass down with
                // it - which is the NullReferenceException that surfaced on modded servers. Default is to
                // leave it alone and say so; ConfiscateUnidentifiableItems flips that for strict servers.
                if (!PackedItem.TryPrefabName(item, out string itemPrefab)) {
                    bool confiscate = ValConfig.ConfiscateUnidentifiableItems.Value;
                    Logger.LogWarning($"{PackedItem.Describe(item)} in {savedChar.Name}'s inventory has no ItemDrop prefab, so it cannot be validated. {(confiscate ? "Confiscating it (ConfiscateUnidentifiableItems is on)." : "Leaving it in place.")}");
                    validationResults[item].Validated = !confiscate;
                    validationResults[item].ValidationResult = ItemValidationSummary;
                    if (confiscate) {
                        validationResults[item].ValidationMessage = "Item has no ItemDrop prefab and cannot be validated.";
                    }
                    continue;
                }
                Logger.LogDebug($"Checking player item: {itemPrefab}");

                foreach (DataObjects.PackedItem savedItem in savedItems) {
                    if (savedItem == null) { continue; }
                    if (savedItem.prefabName == itemPrefab && savedItem.m_stack == item.m_stack) {
                        ItemValidationSummary.NameAndStackMatch = true;
                        //Logger.LogDebug($"Matched {savedItem.prefabName} s:{savedItem.m_stack} q:{savedItem.m_quality} d:{savedItem.m_durability}");

                        
                        int quality = savedItem.m_quality;
                        if (quality == 0) { quality = 1; }
                        //Logger.LogDebug($"Checking Quality: {quality} == {item.m_quality}");
                        if (quality == item.m_quality) {
                            ItemValidationSummary.QualityMatch = true;
                            validationReason += $"{quality} != {item.m_quality} ";
                        }

                        // Validate item durability
                        if (ValConfig.ValidateItemDurability.Value && item.m_durability <= (savedItem.m_durability - ValConfig.ItemValidationDurabilityAllowedVariance.Value) && item.m_durability >= (savedItem.m_durability + ValConfig.ItemValidationDurabilityAllowedVariance.Value)) {
                            ItemValidationSummary.DurabilityMatch = false;
                            validationReason += $"Durability mismatch. Expected {savedItem.m_durability} got {item.m_durability} ";
                            Logger.LogDebug($"Item {itemPrefab} durability mismatch. Expected {savedItem.m_durability} got {item.m_durability} | {item.m_durability} >= {(savedItem.m_durability - ValConfig.ItemValidationDurabilityAllowedVariance.Value)} && {item.m_durability} <= {(savedItem.m_durability + ValConfig.ItemValidationDurabilityAllowedVariance.Value)}");
                        } else {
                            ItemValidationSummary.DurabilityMatch = true;
                        }

                        // Check all of the custom data
                        ItemValidationSummary.CustomDataMatch = true;
                        if (ValConfig.ValidateItemCustomData.Value) {
                            foreach (KeyValuePair<string, string> playerItemKVP in item.m_customData) {
                                if (savedItem.m_customdata.ContainsKey(playerItemKVP.Key) && savedItem.m_customdata[playerItemKVP.Key] != playerItemKVP.Value) {
                                    ItemValidationSummary.CustomDataMatch = false;
                                    validationReason += $"Custom data mismatch on key {playerItemKVP.Key}. Expected {savedItem.m_customdata[playerItemKVP.Key]} got {playerItemKVP.Value} ";
                                    Logger.LogDebug($"Item {itemPrefab} custom data mismatch on key {playerItemKVP.Key}. Expected {savedItem.m_customdata[playerItemKVP.Key]} got {playerItemKVP.Value}");
                                }
                            }
                        }

                        if (ItemValidationSummary.IsValid()) {
                            Logger.LogDebug($"Item {itemPrefab} passed validation checks against saved character data.");
                            validationResults[item].SavedItemRef = savedItem;
                            validationResults[item].Validated = true;
                            break; // if we found a match skip remaining iterations of saved items
                        }
                    }
                }

                validationResults[item].ValidationResult = ItemValidationSummary;
                if (ItemValidationSummary.IsValid() == false) {
                    validationResults[item].ValidationMessage = $"Item {itemPrefab} failed validation checks against saved character data. " +
                        $"Stack Match: {ItemValidationSummary.NameAndStackMatch}, " +
                        $"Quality Match: {ItemValidationSummary.QualityMatch}, " +
                        $"Custom Data Match: {ItemValidationSummary.CustomDataMatch}, " +
                        $"Durability Match: {ItemValidationSummary.DurabilityMatch} | " +
                        $"{validationReason}";
                }
            }

            return validationResults;
        }
    }
}
