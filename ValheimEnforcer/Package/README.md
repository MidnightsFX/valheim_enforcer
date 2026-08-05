# Valheim Enforcer
Valheim Enforcer is a lightweight Mod Synchronization, and Server sided character progression enforce tool.

This mod is designed to be a drop-in, no maintenance solution for those who are wary of configuration, or those that would rather spend time playing than configuring.

By default this mod will enforce character server saves and require clients to only connect with mods that are installed on the server. All of this is configurable.

## Feature Roadmap
The following features are not yet implemented but currently planned:

- Automatic Mod suggestions/download-links for clients that are missing mods or have incorrect versions
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

Cheat detection (enabled by default, configurable).
- Automatic log, kick or ban for common cheating utilities
- ValheimTooler is detected even when injected mid-session (after mod validation) and is always auto-banned
- Optional Discord notification whenever a player is banned for cheating

Clients are checked against a catalog of known cheat tools across three vectors:

| Vector | What it looks at | Why it exists |
| --- | --- | --- |
| Process | Names of running programs | Catches the tool while it is open |
| Module | DLLs loaded into Valheim itself | Sees a cheat that already injected and then closed its launcher, and survives renaming the tool |
| Window | Window classes and titles | Catches tools renamed to dodge the process check (Cheat Engine's `TfrmMain` window class does not change when you rename the exe) |

Detected by default: **WeMod / Wand / Infinity**, **Cheat Engine** (including the `magic-engine` fork and injected speedhack/DBK modules), **ArtMoney** (SE and Pro), **PLITCH**, **Speed Gear**, **Squalr**, **WPE Pro**, generic trainers such as FLiNG and Cheat Happens, and the loaders used to deliver Valheim cheats — **ValheimTooler**, **ValHack**, **Valheim Mod Menu**, **SharpMonoInjector**, **Xenos** and **Extreme Injector**.

Tools with no purpose other than cheating (the loaders and injectors above) are banned on sight. Everything else follows `ActionOnDetection`, which defaults to `Kick`. The auto-ban decision is made by the *server* from its own catalog — a client only ever reports what it saw, so a tampered client cannot get another player banned.

**Privacy:** only matched entries are sent to the server. A player's full process list never leaves their machine.

**False positives:** developer tools that also read game memory — x64dbg, Process Hacker / System Informer, HxD, ReClass.NET, Frida, Fiddler — are deliberately **not** detected by default, because modders and streamers use them routinely. Add them to `AdditionalCheatProcesses` if your server wants them treated as cheats. `Aurora`, `Process Lasso`, `AutoHotkey`, and overlay tools like MSI Afterburner and OBS are excluded on purpose and are not recommended additions; see the config file comments for the reasoning. If something legitimate trips a detection, add it to `IgnoredCheatProcesses`, which overrides everything else.

*Disclaimer: Valheim is client authoratative and without extremely invasive measures, cheating cannot be fully prevented. Process-name detection in particular is a speed bump rather than a wall — renaming Cheat Engine is a documented feature of the tool, and trainer executables are renameable by design. The module and window checks exist because they survive a rename, but a client that can cheat can also lie about what it is running.*

### Server Management

Add the mod, setup your required mod list, optional mods and admin mod lists (setup is OPTIONAL, mods loaded by the server will automatically be required).
Your clients and server must both run the mod.

#### Restoring user Items
Someone brought on their priceless Epicloot Askavin cloak? Some Prestine +InfinitePower Jewels? You can restore confiscated items!

Note: All commands require `devcommands` as such they require admin on the server.

There are two ways to do so. 
1. In-Game commands
	- Run `enforcer-list-players` to get the player's account ID and character name	
	- Run `enforcer-return-confiscated AcountID999999 CharacterName prefabName` (just want it all back? use 'all' as the prefab). This command will automatically give the player the items, along with update their remote save (incase they are not online).
1. Manual config file edits.
	- Ensure the player is offline (server can be running) 
	- If you are unsure about the player's account ID, run `enforcer-list-players` in-game to get the player's account ID and character name
	- Move any item listed under `confiscatedItems` to the `playerItems` list in the player's save file. Player save files are located in `BepInEx\config\ValheimEnforcer\Characters\<PlatformID>\playername.yaml` on the server.
