# Server Management

Add the mod to your server and to your clients — both sides must run it. Setting up the mod lists is optional; every mod the server loads is required automatically. See [Mod List](mod-enforcement.md#mod-list) for the file itself, the other three lists, and what is kept up to date for you.

## Console Commands

Type `enforcer-help` for the list, or `enforcer-help items` for one area of it. Every command names what it did when it finishes — how many characters it found, how many items it moved, how many objects it deleted — so you never have to go and read a log to find out whether it worked.

| Command | What it does |
| --- | --- |
| `enforcer-help` | Lists the commands, grouped by area |
| `enforcer-whoami` | Says whether the server treats you as an admin, and what to fix if it does not |
| `enforcer-player-list` | Every account with a save, and the characters under it |
| `enforcer-items-list` | What has been confiscated from one character |
| `enforcer-items-return` | Gives confiscated items back |
| `enforcer-items-clear` | Deletes confiscated items for good |
| `enforcer-skills-list` | Every skill this mod has lowered for one character, and what it was lowered from |
| `enforcer-skills-restore` | Puts lowered skills back to where they were |
| `enforcer-skills-clear` | Forgets recorded skill reductions without restoring them |
| `enforcer-characters-import` | Imports saves from ServerCharacters ([details](migration.md)) |
| `enforcer-notify-test` | Previews a Discord message ([details](discord.md)) |
| `enforcer-structures-scan` | Finds cheat-placed structures ([details](structure-validation.md)) |
| `enforcer-audit-inventory` | What a character is carrying, and who crafted it ([details](player-audit.md)) |
| `enforcer-audit-history` | Timeline of what a player gained, lost, took and stored |
| `enforcer-audit-damage` | Live damage summary for everyone currently fighting |
| `enforcer-audit-available` | What audit history the server still holds for a player |
| `enforcer-audit-download` | Saves a player's history to your own machine |
| `enforcer-memory` | Memory use: what the mod is holding, and the world's object counts ([details](#memory)) |

Everything except `enforcer-help` and `enforcer-whoami` needs admin rights on the server. Those two run for anybody, because the person who needs them most is the one being refused everything else; see [When the server does not think you are an admin](#when-the-server-does-not-think-you-are-an-admin). If you would rather ordinary players could not even see the command list, set `AllowPublicDiagnosticCommands` (Advanced) to false and both go back to being admin-only.

They run from the server console and from a connected admin's client alike. From a client, the server does the work and its output comes back into the console you typed in — so a dedicated server, which has no console of its own, is administered entirely from in-game. The server checks admin status itself on arrival, so a client that lies about being one is refused and told so.

Tab completion works past the first argument: tab through the account ids the server actually has, then through that account's characters. `EnableTerminalColors` (on) colours the output by severity and is a local setting, so it is yours rather than the server's.

Older command names — `Enforcer-List-Players`, `Enforcer-Return-Confiscated` and the rest — still work and are listed beside their replacement in `enforcer-help`.

## Memory

The character sync keeps a parsed copy of each character's save in memory while that character is being played, so incremental updates apply without re-reading the file. The file is always the authority: every update reaches it within about a second (or within `CharacterWriteIntervalSeconds`, for routine updates, if you have set one), and a copy that has gone idle is dropped and read back on the next update. Memory therefore follows who is playing now, not everyone who has joined since the last restart.

| Setting | Section | Default | Effect |
| --- | --- | --- | --- |
| `CharacterCacheIdleMinutes` | Advanced | `30` | How long a character stays in memory after it was last touched. Keep it above `FullSyncPullIntervalMinutes`, or players who are online but idle are re-read after every periodic pull. `0` keeps every character until restart |
| `MemoryReportIntervalMinutes` | Advanced | `0` | Writes the `enforcer-memory` summary to the server log this often. `0` is off |
| `CharacterWriteIntervalSeconds` | Advanced | `0` | The setting that matters most for memory *churn* on a busy server, as opposed to memory held — see [Storage and timing](character-progression.md#storage-and-timing) |

`enforcer-memory` shows the process working set and managed heap, what the mod is holding (characters, audit buffers, per-player tables) and the world's object counts. The last two lines are the game's own: how many live and destroyed objects it remembers, and the per-connection tables it keeps of which objects each player has been sent. Both grow with uptime and neither is something this mod changes; they are shown so a server whose memory climbs can tell the two apart.

## adminlist.txt changed format — old files grant nobody admin

A Valheim update changed the spelling `adminlist.txt` requires, and it is now the **only** spelling accepted for an account whose id is a number:

| Platform | Line to write |
| --- | --- |
| Steam | `V_<steamid>` |
| Nintendo Switch 2 | `N_<in-game id>` |
| Xbox | `X_<in-game id>` |
| PlayStation | `S_<in-game id>` |
| GameCenter | `A_<in-game id>` |

A bare `76561198…`, or the older `Steam_76561198…`, is no longer honoured. The game says nothing when this happens — the file still looks exactly as correct as it always did, and every admin on the server simply stops being one. If admin commands stopped working after an update and you changed nothing, this is why.

On consoles the number is not your platform account id either: the game derives the in-game id from it, so you cannot work the line out by hand. Run `enforcer-whoami` and it prints the exact line for you.

## When the server does not think you are an admin

Every command above except `enforcer-help` and `enforcer-whoami` is refused with "Only server admins can run …" when the server does not recognise you. Run `enforcer-whoami` — it works whether or not you are an admin, from the console or from chat as `/enforcer-whoami`:

```
enforcer-whoami
```

Only the server answers. It reports the id it actually sees for your connection — which is the one thing that decides anything, and is not always the same string your client knows itself by — whether it treats that id as an admin, how many entries in the list use the pre-update spelling, and where the file lives. Your own client is deliberately not asked: it holds a copy of the admin list from the moment you connected and a synced admin flag from just after login, and either can be out of date, so quoting them beside the real answer would only give you two things to believe.

When the answer is no, it tells you which case you are in:

- **Your id is in the file in the pre-update spelling.** The most likely one right now, and the one that looks like a mystery: the file plainly contains your SteamID and the server still says no. It names that line and prints the `V_…` text to replace it with.
- **Your id is not in the file at all.** It prints the exact line to add.
- **The line has stray whitespace or a byte order mark.** Valheim compares the text exactly as written and does not trim, so one trailing space costs you admin. The command counts the lines this applies to.

`adminlist.txt` is re-read within about ten seconds of being saved, so there is no need to restart the server to test a fix. Neither command reveals anything about other players: `enforcer-whoami` reports how many entries the admin list holds and how many are broken, but never names an admin other than you.

Because a client can be wrong about its own standing — Valheim sends the admin list once when you connect, and Jotunn syncs its admin flag once after login — this mod no longer lets your own client refuse a command outright. If it thinks you are not an admin it says so and asks the server anyway, and the refusal, if there is one, comes from the side that actually decides.


---

[← All documentation](README.md)
