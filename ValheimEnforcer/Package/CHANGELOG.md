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