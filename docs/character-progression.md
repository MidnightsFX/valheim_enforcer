# Character Progression

**On by default.** The server keeps its own copy of each character's items, skills and custom data, hands back what is missing, and takes away what it never saw. A character is therefore a character *on this server* rather than a file on the player's machine that the server accepts on trust.

- [How it works](#how-it-works)
- [The join rules](#the-join-rules)
- [Settings](#settings)
- [Server-Synced Progression](#server-synced-progression) — map, recipes, trophies, statistics, spawn point
- [One Character Per Account](#one-character-per-account)
- [Starter Loadouts](#starter-loadouts)
- [Restoring confiscated items](#restoring-confiscated-items)
- [Restoring skills](#restoring-skills)

---

## How it works

Each character has a save on the server at `BepInEx/config/ValheimEnforcer/Characters/<PlatformID>/<Name>.yaml`. It holds their items, skill levels, per-player custom data, anything this mod has confiscated from them, and a record of every skill this mod has lowered.

Three things keep it current:

| | What it is |
| --- | --- |
| **Deltas** | The client sends an incremental update when its inventory, skills or custom data actually change, rate-limited by `CharacterDeltaTracker`. An idle player sends nothing |
| **Full saves** | The server asks each connected player for a complete character save every `FullSyncPullIntervalMinutes`. The requests are spread one player at a time across the whole interval (`FullSyncSpreadAcrossInterval`, on), so a large server is never parsing and rewriting everybody's save in the same minute |
| **Join reconciliation** | On arrival the stored copy and the arriving character are compared, and the join rules below decide the result |

**The server re-runs the rules itself.** The client applies them too, but the client is what you are defending against, so `ServerSideJoinEnforcement` (on) re-applies item confiscation, skill clamping, custom-data reset, Forsaken Power and food resets to the first full save a returning player uploads each session. A brand new character's first save is held to the new-character rules the same way. Turning off one of the underlying rules turns off its server-side copy too.

**A dirty reconnect is treated gently.** After a crash or a timeout the server's copy can be up to one delta window stale, so item confiscation still runs but item and skill *restoration* is skipped — otherwise a player would be handed back items they had since consumed, or skill levels they had since lost to a death. `ItemRemovalForDirtyReconnection` and `ItemReturnForDirtyReconnection` change either half.

## The join rules

**A returning character** is reconciled against the stored copy:

- Items the server has no record of are confiscated, and kept so an admin can hand them back ([Restoring confiscated items](#restoring-confiscated-items))
- Items the server holds that the character is missing are given back
- A skill above the level the server holds is put back down to it; a skill *below* it is raised back up, which is what brings a character back after its local save was deleted and recreated under the same name
- Every skill this mod lowers is written into the save with the level it came from, so it can be undone ([Restoring skills](#restoring-skills))
- Optionally: the Forsaken Power and the eaten food they left with are put back, so neither can be changed in a solo world

**A character joining this server for the first time** — one the server has no save for at all — is handled separately. By default it forgets the recipes and build pieces it discovered elsewhere, forgets its known texts, and has its custom data cleared. Optionally it can also have its items stripped to an allowlist, its skills zeroed, its map of this world wiped, its Forsaken Power cleared, and its statistics reset. If [Starter Loadouts](#starter-loadouts) are on, the kit is handed over *after* all of that has run.

Note that on a server that already has players, anyone joining for the first time since ValheimEnforcer was installed has no save yet and counts as new. Coming from ServerCharacters? [Import the saves first](migration.md).

## Settings

All of these are server-side and synced to admins, so an admin can change them in-game and the server stays the authority. They live in the `Player Sync` section unless noted.

### Items

| Setting | Default | What it does |
| --- | --- | --- |
| `RemoveNontrackedItemsFromJoiningPlayers` | `true` | Confiscates items the server has no record of from a joining character |
| `AddMissingItemsFromPlayerServerSave` | `true` | Gives back items the server holds that the character arrived without |
| `ValidateItemCustomData` | `true` | Validates the custom data mods attach to items |
| `ValidateItemDurability` | `true` | Validates item durability |
| `ItemValidationDurabilityAllowedVariance` | `10` | How far durability may differ before it counts as a mismatch |
| `ConfiscateUnidentifiableItems` | `false` | *(Advanced)* An item whose prefab does not resolve — usually a modded item — is left alone and logged by default. Enable to confiscate it instead. A confiscated item with no prefab name can never be returned |

### Skills

| Setting | Default | What it does |
| --- | --- | --- |
| `PreventExternalSkillRaises` | `true` | Skill levels above what the server holds are put back down to it |
| `RestoreSkillsFromPlayerServerSave` | `true` | Skill levels below what the server holds are raised back to it. Never lowers anything |
| `RecordSkillReductions` | `true` | Records every skill this mod lowers — from what, to what, when and why — so it can be undone |

### Custom data

| Setting | Default | What it does |
| --- | --- | --- |
| `PreventExternalCustomDataChanges` | `true` | Tracks per-player custom data. Note that custom data can be large and affects how other mods behave |
| `newCharacterClearCustomData` | `true` | A character joining for the first time has its custom data cleared |
| `PassthroughCompatModCustomData` | `true` | *(Advanced)* Leaves inventory-describing custom data owned by slot mods (ExtraSlots and its CustomSlots addon, EquipmentAndQuickSlots including its 2.x legacy keys, InventorySlots) to those mods. Those mods keep a serialized backup of every slot item and restore it whenever a character loads with empty slots; tracking it and re-applying a stale copy duplicated slot gear on every death. Disable only to restore the old behaviour |

### Forsaken Power, food, map, recipes and known texts

| Setting | Default | What it does |
| --- | --- | --- |
| `PreventExternalForsakenPowerChanges` | `false` | Records the Forsaken Power a character selected here and puts it back on join, so one picked up elsewhere cannot be brought in |
| `NewCharacterClearForsakenPower` | `false` | A character joining for the first time has their Forsaken Power cleared |
| `PreventExternalFoodChanges` | `false` | Records the foods a character has eaten and how long each has left, and puts those exact foods back on join |
| `NewCharacterResetMapExploration` | `false` | A character joining for the first time gets a blank map of this world — explored ground, cartography reveals and saved pins |
| `NewCharacterClearKnownRecipes` | `true` | A character joining for the first time forgets the recipes, build pieces, materials, crafting stations and trophies it discovered elsewhere |
| `NewCharacterClearKnownTexts` | `true` | A character joining for the first time forgets its known texts — the runestone and lore entries of the compendium, and any per-player progression a mod keeps in the same dictionary. EpicMMO keeps a character's level, experience and attribute points there, so with this on a first-time joiner starts at level 1. Vanilla's own recipe reset does not cover known texts, which is why this is a setting of its own |

A character whose save predates one of these being switched on has nothing recorded for it: they keep what they arrive with on their next join and are tracked from then on. **Switching one on strips nobody.**

The map, recipe and known-text wipes only happen once the server has confirmed it holds no save for that character, because unlike an item none of them can be handed back. All three are carried out by the client, because none of them ever reaches the server — unless [Server-Synced Progression](#server-synced-progression) is on, which is what gives the server somewhere to keep them.

### New characters

| Setting | Default | What it does |
| --- | --- | --- |
| `NewCharactersRemoveExtraItems` | `false` | A character joining for the first time has everything confiscated except the starting items below |
| `NewCharacterStartingItems` | `ArmorRagsChest,ArmorRagsLegs,Torch` | Comma-separated prefab names a brand new character may arrive holding. Anything else is confiscated, as is any item above quality 1. Matched exactly and case-insensitively, not as substrings, so `Torch` does not also permit `TorchMist`. Leave it empty to allow nothing |
| `NewCharacterSetSkillsToZero` | `false` | A character joining for the first time has its skills zeroed |

### Enforcement and reconnects

| Setting | Default | What it does |
| --- | --- | --- |
| `ServerSideJoinEnforcement` | `true` | The server re-applies the join rules to the first full save a returning player uploads, instead of trusting the client to have done it. Inert if none of the underlying rules are on |
| `ItemRemovalForDirtyReconnection` | `false` | Skip confiscation when the last disconnect was dirty, so crash victims keep items gained in the unsaved window |
| `ItemReturnForDirtyReconnection` | `false` | Restore missing items and lowered skills on a dirty reconnect too. Off by default, to avoid duping items consumed — or handing back skill lost to a death — in the unsaved window |
| `SavePlayerStatusEffectsOnLogout` | `true` | Save active character effects on logout and reapply them on login |
| `InitialCharacterSyncWaitSeconds` | `10` | How long a joining client waits for the server's answer about its stored character before giving up. The character is treated as **new** when no answer arrives — the local save on the joining machine is never used as the baseline. `0` never waits |

### Storage and timing

| Setting | Section | Default | What it does |
| --- | --- | --- | --- |
| `InternalStorageMode` | Advanced | `false` | Store character data inside the world file instead of in `Characters/`, making the world portable without carrying the config with it. Maps are not stored in this mode — see [Server-Synced Progression](#server-synced-progression) |
| `CharacterDeltaTracker` | Advanced | `15` | Minimum seconds between incremental updates. A rate limit, not a polling interval: an idle player sends nothing |
| `FullSyncPullIntervalMinutes` | Advanced | `25` | How often the server asks connected players for a full character save |
| `FullSyncSpreadAcrossInterval` | Advanced | `true` | Ask one player at a time, evenly spread across `FullSyncPullIntervalMinutes`, instead of everybody at the end of it. Each player is still asked exactly as often; what goes away is the burst of work once per interval. Turn it off to go back to waves |
| `FullSyncMaxConcurrentPlayers` | Advanced | `5` | Only used with `FullSyncSpreadAcrossInterval` off: how many players are asked at once, with larger counts staggered into successive waves. Lower it on a constrained upload or VPS host |
| `CharacterWriteIntervalSeconds` | Advanced | `0` | When above `0`, a character changed by an incremental update is written to disk at most this often rather than after every update. Full saves, deaths, a player joining or leaving, admin commands and a shutdown still write at once, so only the routine updates in between are delayed. The cost: a server that dies without shutting down loses up to this many seconds of them. Worth setting on a busy server — `30` to `60` — where rewriting a whole character for every player every few seconds is the mod's largest source of memory churn. Not used with `InternalStorageMode` |
| `FastCharacterWriter` | Advanced | `true` | Write character saves with a purpose-built writer instead of the general YAML serializer. Same file, same layout, read by the same reader and interchangeable with saves written the other way, for a small fraction of the memory. It checks itself against the serializer at startup and hands everything back to it for the session if the two disagree, saying so in the log |
| `BinaryDeltaUpdates` | Advanced | `true` | The server tells connecting clients it accepts incremental updates in a compact binary form, and clients that understand send that instead of YAML. Same content; far cheaper for the server to read. Clients on a build from before this existed keep sending YAML and are handled as before |
| `CharacterCacheIdleMinutes` | Advanced | `30` | How long a character stays parsed in memory after it was last touched — see [Memory](administration.md#memory) |

---

## Server-Synced Progression

Off by default. Items and skills have always been held server-side; this extends that to the rest of a character's progress — the part Valheim keeps in the player profile rather than in anything the server ever sees:

- the **map** of this world: explored ground, what a cartography table revealed, and saved pins
- **known recipes**, build pieces, materials, crafting stations, discovered biomes, runestone texts and permanent unlocks
- **trophies**
- **statistics** — the counters behind the in-game Statistics panel and the post-1.0 achievement system
- the **spawn point**, meaning the bed a character has claimed here

Two problems, one answer. A player can edit any of it offline and arrive with a fully uncovered map or every recipe already known, and there is no way to check that without a copy to check against. And a corrupted local character file loses all of it for good — the one kind of loss the mod could previously do nothing about, because it never held the data.

With `EnableProgressionSync` on and at least one `Sync*` setting beside it, the server keeps its own copy, hands it back on join, and **its copy is the one that counts**: anything above what the server has seen is replaced, and anything lost is restored. This is the same model `PlayerItems` and `SkillLevels` have always used.

Turning a `Sync*` setting on **strips nobody**. A character saved before it was enabled has nothing recorded for that section, and "nothing recorded" is deliberately different from "knows nothing": their next join adopts what they arrive with, and they are tracked from then on.

### Progression sync settings

| Setting | Default | What it does |
| --- | --- | --- |
| `EnableProgressionSync` | `false` | Master switch. Everything below is inert until this is on |
| `SyncMapExploration` | `false` | Map, cartography reveals and saved pins |
| `SyncKnownItems` | `false` | Recipes, build pieces, materials, crafting stations, biomes, runestone texts, unlocks |
| `SyncTrophies` | `false` | The trophy list, kept separate because vanilla files trophies by prefab and everything else by name |
| `SyncPlayerStats` | `false` | The statistics counters |
| `SyncSpawnPoint` | `false` | The claimed bed, and the point the game falls back to when it is gone |
| `NewCharacterClearPlayerStats` | `false` | A character joining for the first time starts with zeroed statistics |
| `MapSyncIntervalMinutes` | `15` | *(Advanced)* How often a client may upload its map |
| `KnownTextPassthroughPrefixes` | `EpicMMOSystem` | *(Advanced)* Comma separated key prefixes in a character's known texts left to the mod that owns them instead of being tracked and enforced. Empty tracks known texts in full |
| `EpicMMOKnownTextCompat` | `true` | *(Advanced)* Only does anything when EpicMMO is installed. Makes the new-character known-text reset land before EpicMMO reads the level out of that dictionary at spawn, so the level it publishes to the world is the reset one. Turn it off to leave EpicMMO entirely alone |

### Progression sync commands

- `enforcer-progress-show <accountId> <characterName>` — what the server holds, section by section. Reads only.
- `enforcer-progress-clear <accountId> <characterName> <map|items|stats|spawn|all> confirm` — deletes part of it. There is no undo and no confiscation record: unlike an item, a forgotten recipe cannot be handed back.

### What this does and does not prove

**Statistics are progress that survives a lost save, not a cheat-proof record.** The server restores them and refuses a counter reported above the value it holds, the same way `PreventExternalSkillRaises` handles a skill. It has no way to verify that any individual increment was earned honestly, and it never will — the increments happen on the client. Do not read "server-synced achievements" as "achievements are now cheat-proof".

**Steam and Xbox achievement unlocks are not touched.** Valheim does not store them in the save at all; it hands them to the platform, which owns them per account. Only the statistics behind them are stored here.

**Everything in this group is enforced on the client.** All of it hangs off a live `Player` and the local player profile, neither of which exists on a dedicated server, so unlike the item and skill rules there is no second copy running server-side to catch a modified client mid-session. What the server does have is the record, and it re-decides the first save of every session against it — so a client that skips the join-time reconciliation has its upload reconciled anyway.

### Things worth knowing about progression sync

- **The map is stored beside the save, not inside it.** A fully explored map is 8.4 MB before compression; compressed it is usually tens of kilobytes and can reach a megabyte. It lives at `Characters/<PlatformID>/<Name>.map` and costs that much disk per character per world.
- **Uploads are paced.** Building the upload means rebuilding and compressing that 8.4 MB package on the player's own machine, so a client sends its map at most once every `MapSyncIntervalMinutes`, once more at logout, and not at all when nothing has been explored since the last one.
- **Maps are not stored in `InternalStorageMode`.** That mode puts the character record in a ZDO string inside the world file, which is no place for a megabyte per player. The two together leave the map untracked and say so in the log; everything else in this group still works.
- **A server that holds no map does not wipe yours.** The first time you switch `SyncMapExploration` on, the server holds nothing for anybody, and treating that as "you have explored nothing" would erase every connected player's map at once. Wiping a new character's map is a separate, narrower decision that `NewCharacterResetMapExploration` already owns.
- **Mods that store progression in known texts are left to themselves.** Valheim's known texts are the compendium — the runestone and lore entries a character has read — but nothing namespaces the keys, so some mods park per-player progression there. EpicMMO is the case this ships for: a character's level, experience and attribute points are one entry each, all prefixed `EpicMMOSystem`. Progression is captured on full saves only, never on deltas, so the server's copy is current only as of the last one; enforcing such a key against it would roll the mod's progress back to that point on every join. Any key matching `KnownTextPassthroughPrefixes` is therefore never stored in a server save and never overwrites the live player's own value. This is the same bargain `PassthroughCompatModCustomData` strikes for slot mods, and it comes with the same caveat: what the server does not hold, it cannot restore or enforce.
- **EpicMMO is nudged when a new character is reset.** EpicMMO does not read its level out of the known texts when something asks for it — it reads them once in a `Game.SpawnPlayer` patch and publishes what it found to the player's ZDO and custom data, which is where its own monster scaling reads a player's level from. That patch runs at a Harmony priority above anything this mod uses, so on its own the reset left a first-time joiner correctly at level 1 while the world went on scaling monsters to the level they walked in with for the rest of the session. With `EpicMMOKnownTextCompat` on, the reset is done ahead of every spawn patch whenever the server has already confirmed the character is new, so EpicMMO reads the reset state itself; when the join had to wait for that answer there is nothing left to get in front of, and EpicMMO is asked to read again afterwards instead. Nothing here changes *what* is reset — only when, and who is told.
- **Pass-through does not exempt a new character.** `NewCharacterClearKnownTexts` clears the lot, prefixes included. Pass-through decides what is enforced between joins; what a character may bring in on its very first one is a separate question, and the answer there is the same as it is for items, skills and custom data.
- **Known recipes are mostly derived.** Vanilla rediscovers a recipe the moment the player holds its materials and has seen its crafting station, so the materials and stations are what is really being held; the recipe list is rebuilt from them on arrival.
- **`NewCharacterClearPlayerStats` cannot be undone**, and the counters it zeroes are per character rather than per world — so it discards a record of everything that character has done anywhere, not just here.


## One Character Per Account

Off by default. Set `EnforceCharacterLimit` to `true` and an account may only join with a character this server already has a save for — anyone else is turned away at the connect handshake and told which character to come back as. Nothing about this is retroactive punishment: **every character an account already has stays playable**, so switching it on locks nobody out. It only stops the *next* new character.

There is no separate list to maintain. The characters an account "has" are exactly the saves under `BepInEx/config/ValheimEnforcer/Characters/<PlatformID>/`, which the mod already writes on the first join. So a brand new player joins normally, that character becomes theirs, and a second one is refused. Run `enforcer-player-list` to see who has what.

**Giving someone a fresh start** is deleting their character's `.yaml` from that folder while they are offline. The slot frees itself; the next character they connect with takes it.

### Character limit settings

| Setting | Default | What it does |
| --- | --- | --- |
| `EnforceCharacterLimit` | `false` | Master switch. Everything below is inert until this is on |
| `MaxCharactersPerAccount` | `1` | How many characters an account may have. Accounts already over it keep what they have |
| `CharacterLimitExemptAccounts` | *(empty)* | Comma-separated account ids allowed any number of characters |
| `CharacterLimitExemptAdmins` | `false` | Whether being on the adminlist is itself an exemption |
| `NotifyCharacterRejected` | `true` | Post refused joins to [Discord](discord.md), if a webhook is configured |

Exemptions are deliberately independent of admin rights — an exempt account does not need to be an admin, and an admin is not exempt unless you list them or turn `CharacterLimitExemptAdmins` on. Ids go in either form: `Steam_76561198012345678` or the bare `76561198012345678`. Note that this setting syncs to connected clients like every other server setting, so the ids in it are visible to players; if that matters for your server, the alternative is editing it in the config file with the list left empty in-game.

### Things worth knowing about the character limit

- **Identity is the character name.** It is the only thing about a character the server learns during the handshake. A player who deletes "Bjorn" locally and makes a new "Bjorn" gets past the check — though since this mod pushes the saved Bjorn's items and skills back on join, it is a poor way to get a clean slate.
- **The save holds the slot, not the player.** Delete someone's save while they still have that character locally and it counts as new again next time they join.
- If the server cannot read its character folder at all, joins are **allowed** and a warning is logged. A disk problem should not lock out your playerbase.
- On a player-hosted (listen) server the host never goes through the connect handshake, so the host's own account is not checked. Dedicated servers check everyone.
- Enforcement is tied to the game's network version. If Valheim ships a new one, the rule stops applying until the mod is rebuilt against it — the check goes quiet rather than guessing at a changed wire format.


## Starter Loadouts

Off by default. A kit — items, skill levels, a head start on what they know, and where they wake up — handed to a character joining this server for the first time.

Kits live in `BepInEx/config/ValheimEnforcer/Loadouts.yaml`, which is created with a commented example in it. `DefaultStarterLoadout` picks the one new characters get. The file is re-read while the server is running.

```yaml
loadouts:
  starter:
    description: A hood, a cloak and something to eat
    items:
      - prefabName: HelmetLeather
        quality: 1
        equipped: true
      - prefabName: CapeDeerHide
        equipped: true
      - prefabName: CookedMeat
        stack: 5
    skills:
      Run: 10
      Swim: 10
    knownMaterials:
      - $item_wood
      - $item_stone
    haveSpawnPoint: false
```

### How it fits with the new-character rules

The kit is granted **after** the new-character rules have stripped whatever the character arrived carrying. That ordering is the whole of the interaction:

- Nothing in a loadout needs listing in `NewCharacterStartingItems` as well. The allowlist decides what a character may *keep*; a loadout is what the server *hands over*, and the strip has already finished by then.
- A loadout may grant an **upgraded** item even though the allowlist refuses one that turns up on its own — because the server is the one giving it.
- Skills are only ever **raised**. A loadout that could lower one would be a second, quieter copy of `NewCharacterSetSkillsToZero`.

Both sides run it: the server writes the kit into the stored record, and the client puts the same items in the player's hands. The same split every other join rule uses.

### Loadout settings

| Setting | Default | What it does |
| --- | --- | --- |
| `EnableStarterLoadouts` | `false` | Master switch |
| `DefaultStarterLoadout` | *(empty)* | Which loadout new characters get. Empty means none |

### Loadout commands

- `enforcer-loadout-list [name]` — the kits in the file, and which one is live. With a name, what is in it.
- `enforcer-loadout-apply <accountId> <characterName> <name> confirm` — add a kit to a character the server already has a save for: a starter kit for somebody who joined before you set one up, or a replacement for a player who lost something.

### Things worth knowing about loadouts

- **`knownMaterials`, `knownRecipes` and the spawn point need progression sync.** They are only applied when `SyncKnownItems` / `SyncSpawnPoint` are on, because those are what give the server somewhere to record them. `enforcer-loadout-list <name>` says so against each line when they are off.
- **A prefab name that does not exist is named in the log and skipped**, not a reason to refuse the join. A typo is the most likely thing to be wrong with a hand-written loadout.
- **A `Loadouts.yaml` that will not parse keeps the previously loaded kits** rather than emptying them, and says so. Nothing in this file can make the server *less* strict, so there is no reason to fail closed.
- Items are added at full durability and are not marked as cheated, so they do not put a character out of the running for the game's own achievements.


## Restoring confiscated items
Someone brought on their priceless Epicloot Askavin cloak? Some Prestine +InfinitePower Jewels? You can restore confiscated items!

There are two ways to do so. 
1. In-Game commands
	- Run `enforcer-player-list` to get the player's account ID and character name	
	- Run `enforcer-items-list AcountID999999 CharacterName` to see what they lost
	- Run `enforcer-items-return AcountID999999 CharacterName prefabName` (just want it all back? use 'all' as the prefab). If they are online the items go straight into their hands; if they are not, they go into their save and are handed over on their next join. Either way the command tells you which of those happened.
1. Manual config file edits.
	- Ensure the player is offline (server can be running) 
	- If you are unsure about the player's account ID, run `enforcer-player-list` in-game to get the player's account ID and character name
	- Move any item listed under `confiscatedItems` to the `playerItems` list in the player's save file. Player save files are located in `BepInEx\config\ValheimEnforcer\Characters\<PlatformID>\playername.yaml` on the server.

## Restoring skills

Two of the Player Sync rules lower a skill: `PreventExternalSkillRaises` clamps a returning character back to the level the server has for them, and `NewCharacterSetSkillsToZero` zeroes a first-time character. Both are right in the case they exist for, and both are occasionally wrong — a save that went stale over a crash, or a player treated as new because the mod was installed after they were. So every skill this mod lowers is written into the character's save with the level it was lowered from, the level it was lowered to, when, and why (`RecordSkillReductions`, on by default), and there are commands to act on that record.

- Run `enforcer-player-list` to get the player's account ID and character name
- Run `enforcer-skills-list AcountID999999 CharacterName` to see what was lowered, and from what
- Run `enforcer-skills-restore AcountID999999 CharacterName Swords,Bows` (or `all`) to put them back. Each skill goes back to the highest level it was recorded being lowered from, and a skill the player has since levelled past is left alone — a restore never lowers anything. If they are online it is applied straight away; if not, on their next join. Either way the command tells you which.
- Run `enforcer-skills-clear AcountID999999 CharacterName all` to forget records without restoring anything, for a reduction that was deserved.

Things worth knowing:

- A character whose skills arrive *below* the stored levels has them raised on join (`RestoreSkillsFromPlayerServerSave`, on by default), the same way missing items are handed back — so deleting and recreating a character locally does not lose the progress the server holds. Like the item restore it is skipped on a dirty reconnect unless `ItemReturnForDirtyReconnection` is on.
- Only reductions this mod makes are recorded. The skill loss on death is the game's own, and a reported level the game could never produce (above 100, negative) is corrected without a record, because there is nothing valid to put it back to.
- A restore waits on the server, as `pendingSkillRestores` in the save, until the player's client is seen holding the level. So one sent to a player who disconnects that instant, or who is on an older build of the mod, is applied on a later join rather than lost. `enforcer-skills-list` shows what is still waiting.
- The record sits in the save file under `skillReductions`, so the manual route works here too: while the player is offline, add the skill and the level you want under `pendingSkillRestores` and delete the record.

---

[← All documentation](README.md)
