# Valheim Enforcer — Documentation

Full reference for every feature. If you just want the short version, the [project README](../README.md) summarises what each feature does.

**The short version of the whole mod:** install it on the server and on your clients, start the server, and it already enforces the mod list and server-side characters. Everything else on this page is optional and off until you switch it on.

---

## Feature pages

| Page | Default | What it covers |
| --- | --- | --- |
| [Mod Enforcement](mod-enforcement.md) | **on** | The four mod lists, file verification, BepInEx patchers, attestation, admin bypass |
| [Character Progression](character-progression.md) | **on** | Server-side items and skills, the join rules, synced map/recipes/stats, character limits, starter kits, restoring what was taken |
| [Cheat Detection](cheat-detection.md) | **on** | Scanning clients for known cheat tools, and what that is and is not worth |
| [Player Activity Audit](player-audit.md) | **on** | What a player is carrying, how they got it, and what they are hitting things for |
| [Structure Validation](structure-validation.md) | off | Catching world-generation geometry spawned as if it were built, and indestructible pieces |
| [Network Integrity](network-integrity.md) | off | Guards on the vanilla RPCs the server otherwise relays unchecked |
| [Item Origins](item-origins.md) | off | Equipment that appears with no crafter and no route into this world |
| [Ban Network](ban-network.md) | off | Sharing bans with other servers, and deciding which of theirs to act on |
| [Save Archives](save-archives.md) | off | Rolling compressed archives of the world **and** the character saves together |
| [Emergency Crash Recovery](crash-recovery.md) | off | Sealed character snapshots held by clients, adopted back after an unclean shutdown |
| [Discord Notifications](discord.md) | off | Joins, bans, refusals and server status, with fully rewritable messages |
| [Migrating from ServerCharacters](migration.md) | — | Importing existing characters so nobody loses anything |
| [Server Management](administration.md) | — | Console commands, memory, and why your admins stopped being admins |

## Where to start

**A new server.** Install it, start it, done. Every mod the server loads becomes required and characters are already server-side. Read [Mod Enforcement](mod-enforcement.md#mod-list) when you want something other than "everybody runs exactly what the server runs".

**An existing server with players.** Anyone joining for the first time since you installed this has no save yet and counts as a **new character**, so check the new-character rules in [Character Progression](character-progression.md#new-characters) before your players arrive. Coming from ServerCharacters, [import first](migration.md).

**A server that has already been cheated on.** [`enforcer-structures-scan`](structure-validation.md#finding-what-is-already-there) finds structures that were placed rather than generated. The [audit log](player-audit.md) is already recording, so there is something to read for anything that happened since you installed.

**Locked out of your own commands?** A Valheim update changed the spelling `adminlist.txt` requires, and every admin on servers with an old file silently stopped being one. [Here is the fix](administration.md#adminlisttxt-changed-format--old-files-grant-nobody-admin).

## Config sections

Settings are grouped into these sections in `BepInEx/config/ValheimEnforcer.cfg` and in Configuration Manager. Server settings are synced to admins, so an admin can change them in-game and the server stays the authority.

| Section | Covered by |
| --- | --- |
| `Mods` | [Mod Enforcement](mod-enforcement.md#settings) |
| `Player Sync` | [Character Progression](character-progression.md#settings) |
| `Anti-Cheat` | [Cheat Detection](cheat-detection.md#settings) |
| `Audit` | [Player Activity Audit](player-audit.md#settings) |
| `World Integrity` | [Structure Validation](structure-validation.md#settings), [Item Origins](item-origins.md) |
| `Network Integrity` | [Network Integrity](network-integrity.md) |
| `Ban Network` | [Ban Network](ban-network.md#settings) |
| `Backups` | [Save Archives](save-archives.md#settings) |
| `Crash Recovery` | [Emergency Crash Recovery](crash-recovery.md#settings) |
| `Discord` | [Discord Notifications](discord.md#settings) — **not** synced to clients, because a webhook URL is a password |
| `Advanced` | Timing and memory knobs, listed with the feature each one belongs to |

Files this mod writes live beside the config, in `BepInEx/config/ValheimEnforcer/`:

| File | What it is |
| --- | --- |
| `Mods.yaml` | The four mod lists. Regenerated at startup, re-read while running |
| `ServerActiveMods.yaml` | Every plugin this machine loaded, written so entries can be copied out. Never read |
| `Notifications.yaml` | The literal Discord message bodies |
| `Bans.yaml` | Every ban this server issued or inherited. Yours to edit |
| `BanNetwork/` | The shared ban list, your overrides, and your API key. Only `Overrides.yaml` and `api.key` are yours to edit |
| `Loadouts.yaml` | Starter kits |
| `Characters/<PlatformID>/` | One save per character, plus `.map` files when map sync is on |
| `Audit/` | One file per UTC day, one event per line |
| `Archives/` | Save archives, when enabled |
| `recovery.key` | The crash-recovery key. **Keep it with your backups** |

## What this mod can and cannot do

Worth reading before you rely on any of it. Valheim is client authoritative, and the checks here fall into two kinds that fail very differently:

- **Checks that ask the client about itself** — what mods it runs, what is in its inventory, what programs are open on that machine. A client that can cheat can also lie about these. They raise the cost from "edit one file" to "reverse engineer and patch the enforcer", which stops the people who do the former and nobody who can do the latter.
- **Checks the server makes for itself** — the sender of every message against the connection it arrived on, the join rules re-run server-side, and the [network guards](network-integrity.md) reading packets the server already holds. **There is nothing for a client to lie about in these.**

The full statement of both, and what is deliberately not attempted, is in [Cheat Detection](cheat-detection.md#what-this-can-and-cannot-do).

---

Got a bug to report or just want to chat about the mod? Drop by the [Discord](https://discord.gg/Dmr9PQTy9m) or [GitHub](https://github.com/MidnightsFX/valheim_enforcer).
