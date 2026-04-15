**0.6.5**
 ---
 ```
 - Removed redundant NewCharacterSkillsCleared setting (replaced by NewCharacterSetSkillsToZero)
	- Set NewCharacterSetSkillsToZero default to false
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