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