# Valheim Enforcer
Valheim Enforcer is a lightweight Mod Synchronization, and Server sided character progression enforce tool.

This mod is designed to be a drop-in, no maintenance solution for those who are wary of configuration, or those that would rather spend time playing than configuring.

By default this mod will enforce character server saves and require clients to only connect with mods that are installed on the server. All of this is configurable.

## Feature Roadmap
The following features are not yet implemented but currently planned:

- Automatic kick/ban for players detected with cheat engines
- Automatic Mod suggestions for clients that are missing mods or have incorrect versions
- Platform ID based 'Moderator' mod list that allows server owners to easily give mod permissions to specific players without making them admins


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

### Server Management

Add the mod, setup your required mod list, optional mods and admin mod lists (setup is OPTIONAL, mods loaded by the server will automatically be required).
Your clients and server must both run the mod.

#### Restoring user Items
Someone brought on their priceless Epicloot Askavin cloak? Some Prestine +InfinitePower Jewels? You can restore confiscated items!

Note: All commands require `devcommands` as such they require admin on the server.

There are two ways to do so. 
1. In-Game commands
	- Run `enforcer-list-players` to get the player's account ID and character name	
	- Run `enforcer-list-confiscated AcountID999999 CharacterName` (Not sure about what the players account ID is? Run `list-players`)
	- Run `enforcer-retrieve-confiscated AcountID999999 CharacterName prefabName` (just want it all back? use 'all' as the prefab)
	- Give the items to said player
1. Manual config file edits.
	- Ensure the player is offline (server can be running) 
	- If you are unsure about the player's account ID, run `enforcer-list-players` in-game to get the player's account ID and character name
	- Move any item listed under `confiscatedItems` to the `playerItems` list in the player's save file. Player save files are located in `BepInEx\config\ValheimEnforcer\Characters\<PlatformID>\playername.yaml` on the server.
