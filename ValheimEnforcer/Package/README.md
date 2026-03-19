# Valheim Enforcer
Valheim Enforcer is a lightweight Mod Synchronization, and Server sided character progression enforce tool.

## Beta!
This mod is still in active development. While the core features of mod version checking and server progress saving work and have been tested.
There are still lots of unkowns which might contain bugs.

## Features

Server saved character progression lock. All of the following features are configurable (server authoratative).
- Character progress is saved on the server
- Prevents characters from bringing untracked items onto the server
- Prevents characters from raising skills externally

Mod Enforcement. All of the following features are configurable (server authoratative).
- All mods are checked on connection, allows strict version enforcement
- Prevents users connecting with mods not listed
- Optional configuration for requiredMods, optionalMods and adminOnlyMods

## Planned Features
- Retrieve item command for admins which gives players the specified item

### Server Management

Add the mod, setup your required mod list, optional mods and admin mod lists.
Your clients and server must both run the mod.

#### Restoring user Items
Someone brought on their priceless Epicloot Askavin cloak? Some Jewels? You can restore confiscated items!

Note: All commands require `devcommands` as such they require admin on the server.

There are two ways to do so. 
1. In-Game commands
	- Run `list-confiscated AcountID999999 CharacterName` (Not sure about what the players account ID is? Run `list-players`)
	- Run `retrieve-confiscated AcountID999999 CharacterName prefabName` (just want it all back? use 'all' as the prefab)
	- Give the items to said player
1. Manual config file edits.
