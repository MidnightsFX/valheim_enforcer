# Valheim Enforcer

Lightweight mod synchronization and server-side character progression for Valheim.

A drop-in, no-maintenance solution for people who would rather play than configure. Install it on the server and on your clients and it works: **every mod the server loads becomes a required mod**, and **characters are saved on the server** rather than trusted from the player's own machine. All of it is configurable, and the server always decides.

[![discord logo](https://i.imgur.com/uE6umQE.png)](https://discord.gg/Dmr9PQTy9m) [![github logo](https://i.imgur.com/lvbP5OF.png)](https://github.com/MidnightsFX/valheim_enforcer)

**[Full documentation](https://github.com/MidnightsFX/valheim_enforcer/blob/master/docs/README.md)** — every feature, setting and command explained.

---

## Install

1. Install on the **server** and on every **client**. Both sides need it; only the server's copy decides anything.
2. Start the server.

That is the whole setup. There is no mod list to write — the server publishes the plugins it loaded and requires them of everyone. Characters are already server-side.

Requires BepInEx, Jotunn and YamlDotNet (handled for you by any mod manager).

> **Already have players?** Anyone joining for the first time since you installed this has no save yet and counts as a **new character**. Check the [new-character rules](https://github.com/MidnightsFX/valheim_enforcer/blob/master/docs/character-progression.md#new-characters) first.
>
> **Coming from ServerCharacters?** [Import your saves](https://github.com/MidnightsFX/valheim_enforcer/blob/master/docs/migration.md) so nobody loses anything. The two mods cannot run at the same time.

---

## Features

### On by default

**Mod enforcement** — every client is checked at connect against the mods this server allows, and is told exactly what to fix when it does not match. Optional per-mod lists for required, optional, admin-only and server-only mods; strict version pinning; SHA256 file verification that catches a mod recompiled with the version string untouched; an allowlist for BepInEx patchers; and per-connection attestation so a mod report cannot be a replayed canned answer.
[Full docs →](https://github.com/MidnightsFX/valheim_enforcer/blob/master/docs/mod-enforcement.md)

**Character progression** — items, skills and custom data live on the server. Untracked items are confiscated and kept so you can hand them back, skills raised elsewhere are clamped and every reduction is recorded so it can be undone, and a character whose local save was lost gets its progress back. Optionally holds Forsaken Power, eaten food, the map, known recipes, known texts, trophies, statistics and spawn point too; optionally hands new characters a starter kit or limits an account to one character. `NewCharacterClearKnownTexts` (on) has a first-time joiner forget the compendium entries it read somewhere this server never saw, which vanilla's own recipe reset leaves untouched; `KnownTextPassthroughPrefixes` (`EpicMMOSystem`) leaves known-text keys a mod owns to that mod, so EpicMMO's level, experience and attribute points are neither tracked nor overwritten.
[Full docs →](https://github.com/MidnightsFX/valheim_enforcer/blob/master/docs/character-progression.md)

**Cheat detection** — clients are checked against a catalog of known cheat tools across running processes, DLLs injected into the game, open window titles, the managed assemblies loaded into the game, and proxy DLLs dropped beside `valheim.exe`. The last two are what see an injected cheat menu, which is not a mod and so has no plugin file to hash and nothing in the declared mod list. Log, kick or ban; dedicated game-cheating loaders and menus are banned on sight, and the *server* makes that call from its own catalog. Only matched entries ever leave the player's machine.
[Full docs →](https://github.com/MidnightsFX/valheim_enforcer/blob/master/docs/cheat-detection.md)

**Player activity audit** — the evidence layer under everything else. What a player is carrying, what they gained and lost, what they took from or put into chests, graves, ships and carts, and a rolling summary of the damage they are dealing. Seven days on disk, searchable with `grep`, downloadable to your own machine. It records and reports only — it never kicks, bans or confiscates.
[Full docs →](https://github.com/MidnightsFX/valheim_enforcer/blob/master/docs/player-audit.md)

### Off until you switch them on

**Structure validation** — catches a client spawning world-generation geometry (dungeon rooms, dvergr towns, ruins) instead of building, and pieces whose health has been set above what the prefab allows, which is how an indestructible structure is made. Blueprint and bulk-build mods cannot trip it, by design. `enforcer-structures-scan` finds what is already in your world.
[Full docs →](https://github.com/MidnightsFX/valheim_enforcer/blob/master/docs/structure-validation.md)

**Network integrity** — guards on the vanilla RPCs the server otherwise relays without looking at them. Binds chat messages to the name the server holds, refuses the player-teleport message from non-admins, filters mass object deletion down to what the sender owns, drops impossible damage values, and optionally re-decides PvP and restricts global keys. **These read packets the server already holds, so there is nothing for a client to lie about.**
[Full docs →](https://github.com/MidnightsFX/valheim_enforcer/blob/master/docs/network-integrity.md)

**Item origins** — reports equipment that appears with no crafter and that nothing in this world drops, sells or spawns, and equipment crafted by a player id nobody here has ever been. Only looks at items that have just appeared, so existing characters are never re-examined. Warns, never confiscates.
[Full docs →](https://github.com/MidnightsFX/valheim_enforcer/blob/master/docs/item-origins.md)

**Save archives** — rolling compressed archives holding the world save **and** the character saves from the same moment. The game's own backups keep two uncompressed copies of the world only, and none of your players' characters. Configurable history, size ceiling and destination.
[Full docs →](https://github.com/MidnightsFX/valheim_enforcer/blob/master/docs/save-archives.md)

**Emergency crash recovery** — each player holds a sealed, signed snapshot of their own character that only the server can open. After an unclean shutdown the server asks for them back and adopts the ones that verify and are newer. Narrow window, every restore logged, and an honest note about the item duplication it can cause.
[Full docs →](https://github.com/MidnightsFX/valheim_enforcer/blob/master/docs/crash-recovery.md)

**Discord notifications** — joins, leaves, startup, shutdown, saves, cheat bans and refused connections. Each category can post to a channel of its own, and every message is a template you can rewrite, role pings included. Paste in a webhook URL and it starts posting; nothing else needs configuring.
[Full docs →](https://github.com/MidnightsFX/valheim_enforcer/blob/master/docs/discord.md)

---

## Console commands

Run them from the server console, or from a connected admin's client — a dedicated server is administered entirely from in-game. Type `enforcer-help` for the list, or `enforcer-help items` for one area. Tab completion works past the first argument, through the account ids the server actually has and then that account's characters.

| Command | What it does |
| --- | --- |
| `enforcer-help` | Lists the commands, grouped by area |
| `enforcer-whoami` | Whether the server treats you as an admin, and what to fix if it does not |
| `enforcer-player-list` | Every account with a save, and the characters under it |
| `enforcer-items-list` / `-return` / `-clear` | See, hand back, or discard confiscated items |
| `enforcer-skills-list` / `-restore` / `-clear` | See, undo, or forget the skill reductions this mod made |
| `enforcer-progress-show` / `-clear` | What synced progression the server holds for a character |
| `enforcer-loadout-list` / `-apply` | Starter kits, and giving one to an existing character |
| `enforcer-audit-inventory` | What a character is carrying, and who crafted it |
| `enforcer-audit-history` | Timeline of what a player gained, lost, took and stored |
| `enforcer-audit-damage` | Live damage summary for everyone currently fighting |
| `enforcer-audit-available` / `-download` | What history the server holds, and keeping a copy of it |
| `enforcer-structures-scan` | Finds cheat-placed structures already in the world |
| `enforcer-item-origins` | The equipment this world has no uncrafted route to |
| `enforcer-trust` | Which players have tripped which network guards |
| `enforcer-archive-list` / `-now` / `-prune` | Save archives on disk, taking one, applying rotation |
| `enforcer-recovery-status` / `-open` / `-close` / `-key-rotate` | Crash recovery window and key |
| `enforcer-characters-import` | Imports saves from ServerCharacters |
| `enforcer-notify-test` | Previews a Discord message |
| `enforcer-memory` | What the mod is holding, and the world's object counts |

Everything except `enforcer-help` and `enforcer-whoami` needs admin rights on the server, which the server checks itself — a client that claims to be an admin is refused and told so.

---

## Troubleshooting: your admins stopped being admins

A Valheim update changed the spelling `adminlist.txt` requires, and it is now the **only** spelling accepted for an account whose id is a number:

| Platform | Line to write |
| --- | --- |
| Steam | `V_<steamid>` |
| Nintendo Switch 2 | `N_<in-game id>` |
| Xbox | `X_<in-game id>` |
| PlayStation | `S_<in-game id>` |
| GameCenter | `A_<in-game id>` |

A bare `76561198…`, or the older `Steam_76561198…`, is no longer honoured. **The game says nothing when this happens** — the file still looks exactly as correct as it always did, and every admin on the server simply stops being one. If admin commands stopped working after an update and you changed nothing, this is why.

Run **`enforcer-whoami`** and it prints the exact line to write for your account. It works whether or not you are an admin, from the console or from chat as `/enforcer-whoami`, and only the server answers — so it reports the id that actually decides things rather than what your client believes. It also catches the other two cases: an id not in the file at all, and a line with stray whitespace or a byte order mark, which Valheim does not trim and which costs you admin over one trailing space.

`adminlist.txt` is re-read within about ten seconds of being saved, so there is no need to restart the server to test a fix.

[More on administration →](https://github.com/MidnightsFX/valheim_enforcer/blob/master/docs/administration.md)

---

## What this mod can and cannot do

Valheim is client authoritative, and the checks here fall into two kinds that fail very differently. Checks that **ask the client about itself** — what mods it runs, what is in its inventory, what is running on that machine — can be lied to by a client that can already cheat; what they change is the cost, from "edit one file and rebuild" to "reverse engineer and patch the enforcer". Checks the **server makes for itself** — the sender of every message verified against the connection it arrived on, the join rules re-run server-side, and the network guards reading packets the server already holds — have nothing for a client to lie about.

The full statement of both is in the [documentation](https://github.com/MidnightsFX/valheim_enforcer/blob/master/docs/cheat-detection.md#what-this-can-and-cannot-do).

---

## Roadmap

Not yet implemented, but planned:

- Automatic mod suggestions and download links for clients that are missing mods or have the wrong versions
- Platform-ID based "moderator" mod list, so server owners can give mod permissions to specific players without making them admins

---

Got a bug to report or just want to chat about the mod? Drop by the [Discord](https://discord.gg/Dmr9PQTy9m) or [GitHub](https://github.com/MidnightsFX/valheim_enforcer).
