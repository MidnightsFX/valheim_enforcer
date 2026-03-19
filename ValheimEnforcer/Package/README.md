# Valheim Enforcer
Valheim Enforcer is a lightweight Mod Synchronization, and Server sided character progression enforce tool.

This mod is designed to be a drop-in, no maintenance solution for those who are wary of configuration, or those that would rather spend time playing than configuring.

By default this mod will enforce character server saves and require clients to only connect with mods that are installed on the server. All of this is configurable.

## Beta!
This mod is still in active development. While the core features of mod (version checking and server progress saving) work and have been tested, there are still lots of unkowns which might contain bugs.

Got a bug to report or just want to chat about the mod? Drop by the discord or github.

[![discord logo](https://i.imgur.com/uE6umQE.png)](https://discord.gg/Dmr9PQTy9m) [![github logo](https://i.imgur.com/lvbP5OF.png)](https://github.com/MidnightsFX/valheim_enforcer)


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
