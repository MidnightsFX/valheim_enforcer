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
- Optional per-mod lists for required, optional, admin-only and server-only mods
- Optional SHA256 file verification of client plugin DLLs, so a recompiled mod is rejected even when its version string is untouched

Nothing needs configuring for the default behaviour — every mod the server loads becomes a required mod. [Mod List](#mod-list) covers the file for when you want something else.

### Mod List

The mod list lives in `BepInEx/config/ValheimEnforcer/Mods.yaml`. Both sides need the mod installed, but only the **server's** copy decides anything: the only thing a server reads out of a client is the list of plugins that client actually loaded.

**You do not have to write this file.** Install the mod, start the server, and every plugin the server loaded is now required of everyone. The rest of this section is for when you want something other than "everybody runs exactly what the server runs".

The file is regenerated at startup and re-read within `ConfigPollIntervalSeconds` (30 by default) of being edited, so you can change it on a running server. Comments you write on their own line are kept across those rewrites and stay attached to the entry below them; a comment sharing a line with a value is not, since that line gets rewritten from scratch.

#### The five lists

| List | Who fills it in | Client has the mod | Client does not |
| --- | --- | --- | --- |
| `activeMods` | Generated, every start | — | — |
| `requiredMods` | Auto-populated, then yours | allowed | **rejected** |
| `optionalMods` | You | allowed | allowed |
| `adminOnlyMods` | You | admins only, everyone else **rejected** | allowed |
| `serverOnlyMods` | You | **rejected** | allowed |

Every list is keyed by the mod's BepInEx plugin GUID — `Azumatt.AzuCraftyBoxes`, not `AzuCraftyBoxes`. It is the `GUID` in the plugin's `BepInPlugin` attribute, and the surest place to read it off is the server's `LogOutput.log`, where BepInEx lists each plugin as it loads. A mod that appears in none of the lists is rejected.

`activeMods` is what this machine loaded. It is rebuilt from the running plugins on every start and never read back out of the file, so editing it does nothing. That is deliberate: it is also the list each side reports about itself during the handshake, and a list taken from a text file is a list a player can type whatever they like into.

`serverOnlyMods` is for mods the server runs and nobody else needs — a map generator, a backup tool, a Discord bridge. It keeps them out of `requiredMods` without demanding them of anyone. It is **not** the list for client-side mods: a client that installs a server-only mod is rejected for it, because that mod is on no list that permits it. Client-side mods belong in `optionalMods`.

#### An entry

```yaml
requiredMods:
  Azumatt.AzuCraftyBoxes:
    pluginID: Azumatt.AzuCraftyBoxes
    version: 1.8.13
    name: AzuCraftyBoxes
    enforceVersion: true
```

| Field | What it does |
| --- | --- |
| `pluginID` | The plugin GUID again. The key above it is what lookups actually use |
| `version` | The version to compare against, kept current for you |
| `name` | Human-readable label, for logs and the disconnect screen |
| `enforceVersion` | When `true`, a client's version must match exactly. Defaults to `false` |
| `acceptedHashes`, `hashSource`, `thunderstorePackage`, `hashEnforcement` | File verification — see [Mod File Verification](#mod-file-verification) |

Version comparison is an exact string match, so `1.0` and `1.0.0` count as a mismatch. Fields sitting at their default are not written out, which is why most entries are three lines. If you find a `versionStrictness` field in an older file, it does nothing and can be deleted.

#### What happens when someone connects

| Situation | Result |
| --- | --- |
| Missing a mod from `requiredMods` | Rejected, and told which |
| Running a mod that is on no list | Rejected as a non-allowed mod |
| Version differs where `enforceVersion` is set | Rejected as a version mismatch |
| Running an `adminOnlyMods` mod without being an admin | Rejected |

The client runs the same comparison against the server's list and shows the result in the connection error window, but that is only feedback for the player — the server decides, from its own file. With Discord notifications enabled, a rejection is posted with the offending mods listed.

#### Handled for you

| What | Controlled by |
| --- | --- |
| `activeMods` rebuilt from the plugins actually loaded | always |
| Any loaded plugin not already on a list is added to `requiredMods`, with `enforceVersion: false` | `AutoAddModsToRequired` *(on)* |
| A mod's `version` is corrected in whichever list holds it when you update the mod | always |
| The SHA256 of every plugin the server loads is recorded as its accepted hash | `RecordHashesForLoadedMods` *(on)* |
| Mods pinned with a `thunderstorePackage` are downloaded and hashed | `ResolveThunderstoreHashes` *(off)* |
| The file is rewritten with all of the above | `UpdateLoadedModsOnStartup` *(on)* |
| Edits are picked up without a restart | `ConfigPollIntervalSeconds` *(30)* |

Updating a mod on the server therefore needs no edit here at all — the version follows it, in whichever list you put it in.

#### What you write yourself

- Membership of `optionalMods`, `adminOnlyMods` and `serverOnlyMods`. Nothing is ever added to these automatically; move an entry out of `requiredMods` by hand.
- `enforceVersion: true`. Auto-added mods are always written with it off, so a client that is a patch version behind is not locked out of a server that never asked for exact versions.
- `thunderstorePackage`, `hashEnforcement`, and any `Manual` hash.

#### Settings

All of these are server-side and synced to admins, so an admin can change them in-game and the server stays the authority.

| Setting | Section | Default | Effect |
| --- | --- | --- | --- |
| `AutoAddModsToRequired` | Mods | `true` | Adds any loaded plugin that is on no list to `requiredMods`. Turn it off to curate the file by hand — mods you have not listed are then rejected rather than adopted |
| `UpdateLoadedModsOnStartup` | Mods | `true` | Writes version corrections, auto-added mods and recorded hashes back to the file. With it off, all of that still applies for the session but nothing is saved |
| `HashEnforcement` | Mods | `WhenKnown` | File verification mode — see [Mod File Verification](#mod-file-verification) |
| `RecordHashesForLoadedMods` | Mods | `true` | Records the hash of every plugin this machine loads. Needs `UpdateLoadedModsOnStartup` to reach disk |
| `ResolveThunderstoreHashes` | Mods | `false` | Downloads and hashes mods pinned with a `thunderstorePackage`. Off by default because it makes outbound requests |
| `ConfigPollIntervalSeconds` | Advanced | `30` | How often the file is checked for edits |
| `HashComputeTimeoutSeconds` | Advanced | `30` | Safety valve for a stalled disk during startup hashing, not a tuning knob |
| `ThunderstoreMaxArchiveMB` | Advanced | `128` | Largest package the resolver will download; bigger ones are skipped and logged |

`Discord.NotifyWrongMods` (on) posts a message naming the mods whenever a player is rejected for a mismatch.

#### Recipes

**Lock the pack to exact versions.** Set `enforceVersion: true` on every entry you care about. There is no global switch — it is per mod on purpose, so one mod that is fussy about its version does not force the whole list to be.

**Let players use a client-side mod.** Move its entry from `requiredMods` to `optionalMods`, or add it there if the server does not run it. They can then connect with or without it.

**Give admins a tool nobody else may run.** Put it in `adminOnlyMods`. Admin status is read from the server's admin list at connect time, so no client can claim it.

**Stop a server-side mod being demanded of clients.** Move it to `serverOnlyMods`. Note that this also means no one may connect *with* it.

**Require a mod the server does not run.** Add it to `requiredMods` by hand with its GUID, version and name. To verify the file as well, give it a `thunderstorePackage` and turn on `ResolveThunderstoreHashes`.

### Mod File Verification

Version checks only compare the version string a client declares, so somebody who downloads a mod, edits the numbers and rebuilds it — keeping the version the same — passes. File verification closes that by comparing a SHA256 of the DLL each plugin was actually loaded from.

`HashEnforcement` (server config, `Mods` section) controls it:

| Value | Server has a hash for the mod | No hash, required/admin mod | No hash, optional mod |
| --- | --- | --- | --- |
| `Off` | not checked | not checked | not checked |
| `WhenKnown` *(default)* | **enforced** | allowed | allowed |
| `Strict` | **enforced** | **rejected** | allowed |

`WhenKnown` means turning this on breaks nothing: only mods you have actually pinned are enforced. `Strict` is for a fully pinned server and deliberately fails loudly when a required mod has no hash on file.

Any mod in `Mods.yaml` can override the server setting with `hashEnforcement: Off | WhenKnown | Strict`. The usual setup is `WhenKnown` globally with `hashEnforcement: Strict` on the handful of mods that actually affect balance.

#### Getting hashes on file

- **Mods the server loads** pin themselves. `RecordHashesForLoadedMods` (on by default) writes the hash of every plugin the server runs into `Mods.yaml` at startup.
- **Client-only mods** — a UI or QoL plugin the server never loads — need one of:
  - **By hand.** Put the SHA256 in `acceptedHashes` and set `hashSource: Manual`. `Get-FileHash -Algorithm SHA256 <file>.dll` produces it. Nothing else ever overwrites a `Manual` entry.
  - **From Thunderstore.** Set `thunderstorePackage: Owner-ModName-Version` and enable `ResolveThunderstoreHashes`. The server downloads that package, hashes the DLLs inside it in memory, records them and discards the download. It re-downloads only when you change the pinned version. Only `thunderstore.io` and its CDN are ever contacted — arbitrary download URLs are not supported on purpose.

```yaml
requiredMods:
  shudnal.ExtraSlots:
    pluginID: shudnal.ExtraSlots
    version: 1.1.20
    name: Extra Slots
    thunderstorePackage: shudnal-ExtraSlots-1.1.20
    hashEnforcement: Strict
```

#### Things worth knowing

- Recorded hashes are sent to clients on purpose, so the disconnect screen can name the mod that failed. They are not secrets — anyone can download the package and hash it themselves.
- Plugins loaded from memory rather than from a file (BepInEx ScriptEngine, in-game plugin loaders) cannot be verified. They report as `dynamic` and will be rejected once the server enforces that mod. The client logs a warning about this at startup, before you try to connect.
- Under `Strict`, enforcement is deferred for mods whose `thunderstorePackage` has not resolved yet, but only until the first resolve pass after server start finishes. That window is bounded and logged; it exists so a restart does not lock everyone out for the few seconds the downloads take.
- BepInEx *patchers* (`BepInEx/patchers/`) are not plugins and are not covered by any of this.

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

*Disclaimer: Valheim is client authoratative and without extremely invasive measures, cheating cannot be fully prevented. Process-name detection in particular is a speed bump rather than a wall — renaming Cheat Engine is a documented feature of the tool, and trainer executables are renameable by design. The module and window checks exist because they survive a rename, but a client that can cheat can also lie about what it is running. The same applies to mod file verification: the hash is computed and reported by the client, so it stops a recompiled mod, not a patched enforcer. What it changes is the cost — from "edit one file and rebuild" to "reverse engineer and patch the anti-cheat", which is a real barrier to the people who actually do the former and none at all to the people who can do the latter.*

### Server Management

Add the mod to your server and to your clients — both sides must run it. Setting up the mod lists is optional; every mod the server loads is required automatically. See [Mod List](#mod-list) for the file itself, the other three lists, and what is kept up to date for you.

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
