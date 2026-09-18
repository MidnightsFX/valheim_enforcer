**0.28.0**
---
```
- Fixes Mods.yaml losing optionalMods, adminOnlyMods and serverOnlyMods on a restart.
    - A file that fails to parse is now copied to Mods.yaml.unreadable-<date>-<time>.bak before anything is
      written, and the error names the line and column and what did load. Previously it logged "fix the file and
      restart" and then published an empty file over the top of it, which made that impossible.
    - A duplicate top-level key is now reported with its line number instead of being silently resolved
      last-wins, which quietly discarded whichever copy came first and then wrote the loss to disk.
- Fixes a NullReferenceException on every connecting client when any mod entry was written with no settings under
  it, and the same entry then being named twice in the rejection text. An entry with nothing under it now reads as
  listing the mod with no options set, which is what it looks like it means.
- Adds keepWhenUnloaded on a mod entry (default false): keeps a requiredMods entry out of the reach of
  RemoveUnloadedModsFromRequired, for a mod required of clients that the server does not run itself. Corrects that
  setting's description and the README, both of which said it was off by default when it has been on.
- Removes the versionStrictness field, which was read by nothing. A versionStrictness line in an existing file is
  ignored and disappears on the next rewrite.
- Adds Starter Loadouts (Player Sync, EnableStarterLoadouts, default off): a kit of items, skill levels, known
  materials and a spawn point handed to a character joining this server for the first time. The kits are written
  in Loadouts.yaml beside the other config files, created with a commented example in it and re-read while the
  server is running; DefaultStarterLoadout picks the one new characters get.
    - Granted AFTER the new-character rules have stripped what the character arrived with, which is what keeps the
      two from fighting: nothing in a loadout needs listing in NewCharacterStartingItems as well, and a loadout may
      hand over an upgraded item even though the allowlist refuses one that turns up on its own.
    - Skills are only ever raised, never lowered. Items arrive at full durability and are not marked cheated, so
      they do not put a character out of the running for the game's own achievements.
    - knownMaterials, knownRecipes and the spawn point need SyncKnownItems / SyncSpawnPoint, which are what give
      the server somewhere to record them; enforcer-loadout-list says so against each line when they are off.
    - A prefab name that does not exist is named in the log and skipped rather than failing the join, and a
      Loadouts.yaml that will not parse keeps the kits already loaded rather than emptying them.
    - Adds enforcer-loadout-list [name] and enforcer-loadout-apply <accountId> <characterName> <name> confirm, the
      second for giving a kit to a character the server already has a save for.
- Adds Emergency Crash Recovery (Crash Recovery, EnableCrashRecovery, default off): every few minutes each
  connected player is handed a sealed snapshot of their own character. Server still verifies and wins if the
  clients character is tampered with.
    - CrashRecoveryAutoRestore (default on) is a separate switch from the master one, so snapshots can be kept and
      distributed without the server ever acting on one unasked. CrashRecoveryPushIntervalMinutes (default 10)
      bounds how much a crash can cost and is also the bandwidth knob. CrashRecoveryKeepBlobs (default 3) bounds
      what a client keeps per world.
    - Adds enforcer-recovery-status, enforcer-recovery-open, enforcer-recovery-close and
      enforcer-recovery-key-rotate.
    - It cannot help with a lost or corrupted LOCAL character file - the client cannot read its own snapshot 
      which is what progression sync is for. This is exlusively to handle server-crash recovery.
- Adds Save Archives (Backups, EnableSaveArchives, default off): rolling, compressed archives holding the world save
  and the character saves from the same moment.
    - An archive is only ever taken just after a world save FINISHES.
    - SaveArchiveIntervalMinutes (default 120) is a minimum gap, not a schedule
    - SaveArchiveKeepCount (default 5) and SaveArchiveMaxTotalMB (default 0, no ceiling) decide what is kept, 
      oldest deleted first.
    - SaveArchiveIncludeCharacters (default on), SaveArchiveIncludeConfig (default on), SaveArchiveCompression
      (Fastest / Optimal / NoCompression) and SaveArchivePath (empty = BepInEx/config/ValheimEnforcer/Archives).
    - Adds enforcer-archive-list, enforcer-archive-now [save] and enforcer-archive-prune
    - This does drastically increase storage size of the world on disk.
- Adds Server-Synced Progression (Player Sync, EnableProgressionSync, default off)
    - SyncMapExploration (default off): Syncs the players map to the server
    - SyncKnownItems (default off): recipes, build pieces, materials, crafting stations, biomes, runestone texts and
      permanent unlocks.
    - SyncTrophies (default off): Syncs trophy list
    - SyncPlayerStats (default off): Syncs player statistics, these are also used for achievements
    - SyncSpawnPoint (default off): the bed a character has claimed here, and the fallback point.
    - NewCharacterClearPlayerStats (default off): a character joining for the first time gets their stats cleared
    - MapSyncIntervalMinutes (Advanced, default 15): how often a client may upload its map. This is mildly expensive.
    - Adds enforcer-progress-show <accountId> <characterName>, which reports what the server holds section by section
    - Adds enforcer-progress-clear <accountId> <characterName> <map|items|stats|spawn|all> allows deleting saved character progress
- Slight memory optimization when Gzipping contents such as large save data
```

**0.27.1**
---
```
    - Adds an Admin override config (NOT SERVER SYNCED, must be set on the server itself)
```

**0.27.0**
---
```
- Adds ModValidationExemptAdmins (Mods, default off): anyone on the server's adminlist may connect with any mods at
  all - missing required mods, mods the server does not allow, mismatched versions, modified files, unlisted BepInEx
  patchers, and a missing or wrong attestation even under Require. It also covers an admin running no ValheimEnforcer
  at all, who is otherwise refused for never sending a mod list. Meant for testing a mod against the live server
  without editing Mods.yaml first.
    - The checks still run and their result still goes to the server log, so you can read what the admin was
      carrying; only the rejection is skipped.
    - The Discord mod-mismatch notification is not sent for an exempt admin - nobody was turned away.
    - An exempt admin whose list failed gets no client-contradiction declaration on file, since that feature reasons
      from a premise this setting sets aside.
    - The oversize mod-list guard is not part of the exemption.
    - enforcer-whoami reports when it is on.
- Reduces player memory footprint
    - Adds CharacterCacheIdleMinutes (Advanced, default 30): a character untouched for this long is dropped from
      memory and read back from its file on the next update. 0 keeps everything until restart, as before.
- Adds enforcer-memory (Diagnostics): process and managed heap size, what the mod is holding, and the world's
  object counts including the game's own per-peer object tables. MemoryReportIntervalMinutes (Advanced, default
  0 = off) writes the same summary to the server log on a schedule.
```

**0.26.0**
---
```
- Fixes incorrect default coloring of player left message
- Adds PreventExternalFoodChanges (Player Sync, default off). The character save now records the foods a character has
  eaten and how long each has left, and puts exactly those back when they join - so food eaten in a solo world or on
  another server, or a free top-up, cannot be walked in.
    - A character joining for the first time has all of their food cleared.
    - A save written before the setting was on has no foods recorded. That character keeps what they arrive with on
      their next join and is tracked from then on, so switching this on strips nobody.
    - Re-applied server side on the first save of each session when ServerSideJoinEnforcement is on.
- Adds NewCharacterClearKnownRecipes (Player Sync, default on): a character joining a server for the first time forgets
  the recipes and build pieces they discovered elsewhere, along with the materials, crafting stations and trophies
  that discover them. Only once the server confirms it holds no save for the character, and never in singleplayer.
- Moves the list of loaded mods out of Mods.yaml into ServerActiveMods.yaml, beside it. Entries are sorted and written
  exactly like a Mods.yaml entry, ready to copy into a list. The file is deleted and rewritten every start, never read,
  and not synced.
    - An existing Mods.yaml drops its activeMods section on the next rewrite, and its header banner has the activeMods
      line swapped for one pointing at the new file. Comments are unaffected.
- Adds RemoveUnloadedModsFromRequired (Mods, default true): at startup, removes requiredMods entries for mods the server
  does not have loaded, so an uninstalled mod stops being demanded of clients.
- Fixes adminOnlyMods being required of admins. A mod on both adminOnlyMods and requiredMods - which is what adding a mod
  the server loads to adminOnlyMods usually leaves behind - was treated as required: every client, admins included, had
  to install it, and non-admins were let in with it. adminOnlyMods now wins, so the mod is optional for admins and
  refused to everyone else. A startup log line names any mod on both lists.
- Adds enforcer-skills-list, enforcer-skills-restore and enforcer-skills-clear, and RecordSkillReductions (Player Sync,
  default on). Every skill this mod lowers - a returning character clamped back to the stored level, a new character's
  skills set to zero - is now recorded in the character's save with the level it was lowered from and to, when and why,
  and an admin can put it back.
    - Restore puts each skill back to the highest level it was recorded being lowered from, and never lowers one. A
      player who is online gets it straight away; one who is offline gets it on their next join, and a restore that
      does not reach a player is applied on a later join rather than lost.
    - The game's own skill loss on death is not recorded, and neither is the correction of a reported level the game
      could never produce.
- Adds RestoreSkillsFromPlayerServerSave (Player Sync, default on): a returning character whose skills arrive below the
  levels the server holds has them raised on join, the way missing items are handed back - so a character recreated
  after its local save was deleted keeps the progress the server holds instead of pushing blank skills up as the new
  record. Skipped on a dirty reconnect unless ItemReturnForDirtyReconnection is on.
```

**0.25.0**
---
```
- Adds full support for crossplay exclusive servers
```

**0.24.0**
---
```
- Adds PreventExternalForsakenPowerChanges (Player Sync, default off). The character save now records the Forsaken Power a
  character has selected and puts it back when they join, so a power picked up in a solo world or on another
  server - one this server may never have unlocked at its boss stones - cannot be walked in.
    - A power selected at a boss stone here is saved as normal, mid-session included.
    - A save written before the setting was on has no power recorded. That character keeps the power they arrive
      with on their next join and is tracked from then on, so switching this on strips nobody's existing power.
    - Re-applied server side on the first save of each session when ServerSideJoinEnforcement is on.
- Adds NewCharacterClearForsakenPower (Player Sync, default off): a character joining for the first time has their Forsaken
  Power cleared, on the client at join and again server side on their first save.
- Adds NewCharacterResetMapExploration (Player Sync, default off): a character joining for the first time has their map of
  this world wiped - explored areas, cartography table data and saved pins.
- Improves WeMod detection
```

**0.23.2**
---
```
- Adds enforcer-whoami: says whether the server treats you as an admin and, when it does not, what to change.
    - Runs for anybody, admin or not. The person who needs to ask why they are not an admin is by definition
      the person every other command is refusing, so locking the answer behind the same check left an operator
      whose id is misspelled in adminlist.txt with nothing to go on from in-game.
    - Distinguishes "your id was never added" from "your id is in the file but has a trailing space or a byte
      order mark on it", which look identical from in-game and are the usual cause. Prints the exact line to
      add or replace.
- enforcer-help also runs without admin now, which is what the documentation already claimed.
- Adds AllowPublicDiagnosticCommands (Advanced, on): set it false to make those two admin-only again.
- Handles the adminlist.txt format change. A Valheim update made the one-letter platform prefix - V_ Steam,
  N_ Nintendo, X_ Xbox, S_ PlayStation, A_ GameCenter - the ONLY spelling accepted for a numeric account id:
  ZNet.ListContainsId computes the old answer and then overwrites it with a lookup of the filtered form
  alone. Every adminlist.txt written before that update silently stopped granting anybody admin, and nothing
  in the game says so.
    - enforcer-whoami prints the exact line to write, including for console accounts, where the in-game id is
      derived from the platform id and cannot be worked out by hand.
```

**0.23.1**
---
```
- Console commands no longer flagged as cheats, but do require admin
- Prepatchers detected on the server stay in activePatchers and allowedPatchers when Mods.yaml is re-read.
  Previously only startup did that, so an edit to the file replaced the detected list with whatever the file
  said and dropped the server's own patchers out of the allowlist until the next restart.
```

**0.23.0**
---
```
- Deep North update
```

**0.22.0**
---
```
- Adds the Player Activity Audit (EnableAuditLog, on by default): a record of what players do, stored and monitored server side.
    - Item gains and losses
    - Container takes and stores
    - A rolling window of damage dealt per player
    - One file per UTC day under BepInEx/config/ValheimEnforcer/Audit, kept for 7 days (AuditRetentionDays) by default
    - EnableAuditLog is the only switch: AuditItemChanges, AuditContainerAccess and AuditDamage are gone. Three of the four ways to configure it produced a record with a hole in it that nothing in the output announced.
    - Container auditing has to watch every object a client replicates, so its hook is only installed when EnableAuditLog is on at startup. Turning the audit on in a running server takes a restart; turning it off takes effect immediately.
    - Trimming the container snapshot table no longer copies and sorts the whole table with a comparison delegate, which it did on the main thread inside the packet loop.
- Adds five commands: enforcer-audit-inventory, -history, -damage, -available and -download.
- Resolves the peer behind a connection from a cached map instead of walking the peer list, so routed RPC sender verification (EnforceRoutedRpcSender) and ZDOData inspection no longer cost more per packet as the server fills up.
- Server-side death recording no longer inspects every object a client replicates. It now looks only at the objects a packet newly created, once the packet is done, so a server running it with structure validation off - the default - carries no hook on ZDO deserialization at all.
    - The excessive-health check still needs that hook, since it compares against the value an object held before the client's write. It is now only installed when EnableStructureValidation and DetectExcessiveStructureHealth are both on at startup, so turning either on in a running server takes a restart before that one check begins working. The log says so if it happens.
```

**0.21.0**
---
```
- Improves scanning performance and prevents main thread hitches related to these scans
- Adds Network Integrity: server-side validation of the vanilla routed RPCs the server relays without ever
  looking at them, configurable EnableRpcGuards (off by default)
- Adds BepInEx patcher validation (ValidatePatchers, off by default)
- Adds per-connection session handshakes
- Adds client contradiction reporting (ReportClientContradictions, off by default), which ties what a client
  declared at join to what the guards later catch it doing
- Adds item origin detection (DetectItemOrigins, off by default), which monitors users aquiring items that are not fully valid
   - Admins are exempt by default (ItemOriginExemptAdmins); IgnoredItemOriginPrefabs disables the check for certain items
```

**0.20.1**
---
```
- Improve item compatibility storage of Extraslots, ExtraCustomSlots, Equipment & Quickslots, and InventorySlots
    - Configurable through PassthroughCompatModCustomData (default on). If disabled, you must disable your inventory mod from restoring backup items
```

**0.20.0**
 ---
 ```
 - Routed RPC sender verification (EnforceRoutedRpcSender, Advanced, on by default). Valheim's routed RPC
   carries a sender id the sending client writes, which the server now validates, this is applied to every registered RPC.
    - Client-side messages from the server are now verified before being accepted.
 - Character names and account ids are validated before being used as save-file paths, and a connection
   whose character name is not a safe file name is refused at the handshake with a clear reason.
 - A player's death is now recorded server-side (from the grave the client creates), so a client that skips
   its own death handling can no longer keep its pre-death inventory.
 - Skill levels reported by a client are clamped to the game's valid 0-100 range before being stored.
 - Adds structure validation: server-side detection of clients placing structures no build tool can
   place, and of pieces whose health is above what their prefab allows
    - Configurable EnableStructureValidation (off by default) to enable this functionality
    - StructureValidationAction determines the automated response to a player triggering this
    - Admins are exempt by default
 - Blocks ZNetScene's SpawnObject RPC (BlockSpawnObjectRPC, on by default), an unused routed call that
   otherwise lets any client have the server instantiate any prefab by hash (creatures and items included,
   not just structures). A block posts to the moderation Discord channel by default and follows
   StructureValidationAction (default Log, so it reports without kicking or banning)
 - Adds Enforcer-Scan-Structures, which allows finding existing structures like this
 - Adds a structureFlagged Discord notification, routed to the moderation webhook
 - Console commands improvements
    - Commands now provide a summary back of what their action taken or result was
    - Output goes to the console you typed in, including when the server ran the command for you.
    - Renamed to enforcer-<area>-<verb>: enforcer-player-list, enforcer-items-list/-return/-clear,
      enforcer-characters-import, enforcer-notify-test, enforcer-structures-scan. Every old name still
      works and is shown beside its replacement in the new enforcer-help
    - Adds enforcer-help, and enforcer-items-list
    - Tab completion now works past the first argument, and offers the account ids and character names
      the server actually has
    - Adds EnableTerminalColors (on, local) to colour command output by severity
    - Fixes clearing confiscated items doing nothing at all when the player was offline
    - Naming a character the server has no save for now says so
- Fixes first-join allowing items on in a specific scenario
 ```

**0.19.0**
 ---
 ```
 - Reduces false positive kicks for having applications which could be cheat-engines running
    - Generic window classes are now a low-confidence signal: the sighting is still reported and
      logged on the server
 - Kicks and bans from cheat reports now target the reporting connection's platform ID instead of
   the player name it self-reported, so a crafted report cannot hit another player and duplicate
   character names cannot misfire
 - A wrong mod version is now reported as a version mismatch instead of a modified file
    - This covers the case where enforceVersion is off and the recorded hash is what caught it
    - Version mismatches now name both versions - "com.example.Mod (needs 1.4.2, has 1.3.0)" - on the
      disconnect screen, in the server log and in the Discord {versionMismatches} field
 ```

**0.18.0**
 ---
 ```
 - Discord notifications can now be split across channels
    - WebhookUrlPlayerActivity, WebhookUrlServerStatus, WebhookUrlModeration and WebhookUrlModMismatch
      each take a webhook of their own; any left empty falls back to WebhookUrl as before
 - Every notification is now a template you can edit, in config/ValheimEnforcer/Notifications.yaml
    - Each entry is the literal message body posted to Discord, anything Discord accepts works, including
      author/footer/thumbnail/image
    - Deleting a key deletes that part of the message: drop "timestamp" and no date stamp is sent,
      drop "embeds" and it becomes a plain text post. Nothing is added back for you
    - A 'content' line is the only place a mention pings - use it for role alerts
    - Placeholders like {player}, {playerId}, {reason} and {missingMods}; a mod mismatch also exposes
      its missing/extra/version/hash lists separately instead of one block of prose
 - Adds a world save notification (NotifyWorldSaved, off by default - the autosave is every ~20 minutes)
 - Adds ServerLabel, exposed to templates as {server}, for several servers sharing one channel
 - Adds Enforcer-Test-Notification, which posts any event with sample data so a template can be
   previewed without waiting for the real thing
 ```

**0.17.0**
 ---
 ```
 - Adds character import from the ServerCharacters mod (ImportServerCharacters, off by default)
    - Reads the character files ServerCharacters leaves behind and turns them into enforcer saves, so
      migrating players keep their inventory and skills instead of being confiscated on first join
    - Item quality, variants, crafter names and mod item data (EpicLoot and friends) come across intact,
      as do modded skills
    - Runs once at server start, or on demand with Enforcer-Import-ServerCharacters, which has a dryrun
    - Existing characters are never overwritten unless 'force' is given
 ```

**0.16.0**
 ---
 ```
 - Adds an optional one-character-per-account rule (EnforceCharacterLimit, off by default)
    - An account may only join with a character the server already has a save for, up to
      MaxCharactersPerAccount; anything else is refused at the connect handshake
    - Characters that already exist are never affected, so enabling it locks out no current player
    - Refused players are told which character to rejoin with, instead of a generic connection error
    - CharacterLimitExemptAccounts allows specific accounts any number of characters, whether or not
      they are admins; CharacterLimitExemptAdmins extends that to the whole adminlist
 ```

**0.15.0**
 ---
 ```
 - Adds file verification of client plugin DLLs at connect time
    - New HashEnforcement setting: Off / WhenKnown (default) / Strict, overridable per mod in Mods.yaml
    - Mods the server loads pin themselves; client-only mods pin by hand or from a thunderstorePackage
 - Fixes a mod with the wrong version being reported as both a version mismatch and a non-allowed mod
 - Comments in Mods.yaml now survive the startup rewrite, which used to delete them - a note stays
   attached to the entry it was written above
 - Documents the mod list in the README: the five lists, how an entry is structured, what is kept up
   to date for you and what you have to write yourself
 ```

**0.14.1**
 ---
 ```
 - Update Jotunn version
 ```

**0.14.0**
 ---
 ```
 - Greatly expands cheat tool detection
    - Detects the loaders used to deliver Valheim cheats, these are banned on sight
    - generic trainers are also detected, with a configurable moderation action (default ban)
 - Ban reasons and Discord notifications now name the specific tool and how it was found
 ```

**0.13.0**
 ---
 ```
 - Fixes item duplication on death edgecases
 - Improves compatibility with death mods that change what happens to items on death
 - Inventory changes are now tracked as they happen instead of being polled on a timer
    - An idle player sends nothing at all; CharacterDeltaTracker is now a rate limit (default 15s, was a 60s poll)
    - Singleplayer and listen-host sessions now keep their character save current mid-session
 - Fixes restored items being dropped on the ground when the player had room for them
 - Singleplayer fixed skill progress earned during a session being rolled back on death
 - Status effects can no longer carry across a death
 ```

**0.12.0**
 ---
 ```
 - Improves multiplayer disconnect saving for extremely large character saves
 - Allows server admin editing of save files to be hot-reloaded (please ensure the player you are editing is logged off first)
 ```

**0.11.1**
 ---
 ```
 - Improves accuracy of saves in singleplayer games
 ```

**0.11.0**
 ---
 ```
 - Server-side character saves and delta updates are now written off the main thread
    - Full/delta saves are deserialized, serialized and written on a background worker with an in-memory cache
    - Repeated writes to the same character are coalesced, so a burst of saves (e.g. every client on a "save player profiles" broadcast) can no longer stall the server or time players out
    - Internal storage mode keeps its existing behavior (registry writes must stay on the main thread)
 - Full character saves are now pulled by the server instead of riding the world/profile autosave
    - The server asks connected players for a full save every FullSyncPullIntervalMinutes (default 25)
    - No more than FullSyncMaxConcurrentPlayers upload at once (default 5); larger player counts are staggered into waves so incoming saves never spike bandwidth
    - Removes the client-side full-save timer and the Player.Save trigger; routine changes still stream up incrementally via CharacterDeltaTracker, and join/logout still push a full save
 ```

**0.10.1**
 ---
 ```
 - Forward leads character saves to ensure first round of delta saves are not discarded
 ```

**0.10.0**
 ---
 ```
 - Anti-Cheat now enabled by default
 - ValheimTooler detection reworked to be more flexible
	- A confirmed ValheimTooler detection is always auto-banned (when cheat detection is enabled)
 - Discord notification when a player is banned for cheat usage (NotifyCheaterBanned, default on, requires seperate webhook)
 - Cheat Engine process scan throttled
	- ScanIntervalSeconds default raised to 30 (now only affects the Cheat Engine check)
- Added another user to the global ban list
 ```

**0.9.1**
 ---
 ```
 - Admin only mods now strongly restricted to admins
 ```

**0.9.0**
 ---
 ```
 - Added Automatic ban list, built in known-banned
 - Added discord notifications (server side) [Configurable!]
	- Notify on player join
	- Notify on player leave
	- Notify on server start
	- Notify on server shutdown
	- Notify on mod mismatch
 ```

**0.8.2**
 ---
 ```
 - Configurable save sync intervals for full saves and delta saves
 - Last disconnect status tracked
	- Allows reduction in strictness of item confiscation
 - Added a confiscated timestamp
 - Improved item return logic to drop items on the ground if the player does not have room for it
 ```

**0.8.1**
 ---
 ```
 - Null check for status effects which no longer exist when adding to character
 - Improves Item return RPC logic to deal with partially valid clients
 - Improves compatibility with some custom status effects and saved custom data
 ```

**0.8.0**
 ---
 ```
 - Improved Item, skill, status effect, and custom data consistency
 - Added a catchall to persist character data when exiting without saving
 ```

**0.7.3**
 ---
 ```
 - Polling filewatcher for better server side support with unix/hybrid storage (default check interval is 30s, configurable)
 ```

**0.7.2**
 ---
 ```
 - Adds support for status effect tracking between sessions (configurable)
	- Status effects (such as poison) will now be applied when you log back in, with their previous durations etc
	- No more save scumming for a 60s poison tick
	- On the plus side, your rested buff now stays between play sessions!
 ```

**0.7.1**
 ---
 ```
 - Adds a very small amount of variance allowed for float rounding when validating item durability
 - Adds extra details to the confiscation reason
 ```

**0.7.0**
 ---
 ```
 - Added a confiscation reason field on items confiscated, field is optional but will be set for all confiscated items
 - Removed redundant NewCharacterSkillsCleared setting (replaced by NewCharacterSetSkillsToZero)
	- Set NewCharacterSetSkillsToZero default to false
 - Added CheatDetector module (in testing, disabled by default)
	- Client-side scanning for ValheimTooler (loaded assemblies) and Cheat Engine (process name, window class, injected speedhack/DBK modules, debugger, time-drift speedhack)
	- New Anti-Cheat config section; default ActionOnDetection=Log
	- Detections reported to server via new VENFORCE_CHEAT RPC
 ```

**0.6.4**
 ---
 ```
 - Cache busting between player sessions
 - Fixes character switching allowances for local only usage
 - Add Extraslots compatability (restores items to the correct slots for characters with extraslots)
 - Restores equipped status of items when they are returned to the player
 ```

**0.6.3**
 ---
 ```
 - Explicitly requires yaml.net
 ```

**0.6.2**
 ---
 ```
 - Improves item durability save bounding
 ```

**0.6.1**
 ---
 ```
 - Adds item durability validation (configurable through ValidateItemDurability setting, default on)
 ```

**0.6.0**
 ---
 ```
 - Improves custom data validation
 - Enables Enforcer- commands for admins to retrieve confiscated items
	- List player saves
	- List confiscated items for a player
	- Retrieve confiscated items (give to admin) from a player save
	- Retrieve confiscated items (give to player) from a player save
- Optional (disabled by default) portable mode which stores all data inside the world
 ```

**0.5.5**
 ---
 ```
 - Enforce quality and custom data consistency for all characters, including new characters on first load
 - Added extra safety checks for player data settings
 ```

**0.5.4**
 ---
 ```
 - Defaults to enforcing mod versions for active mods
 - Automatically updates mod versions in all lists when the mod is updated on the server
 - Fixes inconsistent server save IDs when recieving data from the client
 ```

**0.5.3**
 ---
 ```
 - Fixes character fallback logic to more consistently select a non-mutating ID, prefers steamID and playfabID
 ```

**0.5.2**
 ---
 ```
 - Fixes skill removal for new chracters on first load
 ```

**0.5.1**
 ---
 ```
 - Fixes player custom data loading for new characters on first init
 ```

**0.5.0**
 ---
 ```
 - Initial public beta
 ```