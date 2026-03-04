# Valheim Enforcer
Valheim Enforcer is a lightweight Mod Synchronization, and Server sided character progression enforce tool.

## Features

Server saved character progression lock. All of the following features are configurable (server authoratative).
- Character progress is saved on the server
- Prevents characters from bringing untracked items onto the server
- Prevents characters from raising skills externally

- 
Mod Enforcement. All of the following features are configurable (server authoratative).
- All mods are checked on connection, allows strict version enforcement
- Prevents users connecting with mods not listed
- Optional configuration for requiredMods, optionalMods and adminOnlyMods


### Server Management

Add the mod, setup your required mod list, optional mods and admin mod lists.
Your clients and server must both run the mod.

#### Restoring user Items
Someone brought on their priceless Epicloot Askavin cloak? Some Jewels? You can restore confiscated items!

There are two ways to do so. 
1. In-Game commands
	- Run `list-confiscated AcountID999999 CharacterName` (Not sure about what the players account ID is? Run `list-players`)
	- Run ``
1. Manual config file edits.
