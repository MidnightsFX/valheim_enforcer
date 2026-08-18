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
- Optionally limits each account to a single character, with an exemption list ([One Character Per Account](#one-character-per-account))
- Imports existing characters from ServerCharacters so players migrate without losing anything ([Migrating from ServerCharacters](#migrating-from-servercharacters))

Mod Enforcement. All of the following features are configurable (server authoratative).
- All mods are checked on connection, allows strict version enforcement
- Prevents users connecting with mods not listed
- Optional per-mod lists for required, optional, admin-only and server-only mods
- Optional SHA256 file verification of client plugin DLLs, so a recompiled mod is rejected even when its version string is untouched

Nothing needs configuring for the default behaviour — every mod the server loads becomes a required mod. [Mod List](#mod-list) covers the file for when you want something else.

Discord notifications. Optional, off until you paste in a webhook URL.
- Joins, leaves, server startup/shutdown/save, cheat bans, and refused connections
- Each category can post to a channel of its own, or all of them to one
- Every message is a template you can rewrite, including role pings ([Discord Notifications](#discord-notifications))

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
| Version differs where `enforceVersion` is set | Rejected as a version mismatch, naming the version to install |
| Running an `adminOnlyMods` mod without being an admin | Rejected |

The client runs the same comparison against the server's list and shows the result in the connection error window, but that is only feedback for the player — the server decides, from its own file. With [Discord notifications](#discord-notifications) enabled, a rejection is posted with the offending mods listed.

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

`Discord.NotifyWrongMods` (on) posts a message naming the mods whenever a player is rejected for a mismatch. It can go to a channel of its own, and the wording is yours to change — see [Discord Notifications](#discord-notifications).

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
- **A recorded hash pins the version too.** A different build of a mod is a different file, so a client on another version fails the file check whether or not `enforceVersion` is set on that entry. That rejection is reported as a version mismatch, naming the version to install — "modified mod files" is kept for a file whose version matches the server's and whose contents do not, which is the case where reinstalling actually helps.
- Plugins loaded from memory rather than from a file (BepInEx ScriptEngine, in-game plugin loaders) cannot be verified. They report as `dynamic` and will be rejected once the server enforces that mod. The client logs a warning about this at startup, before you try to connect.
- Under `Strict`, enforcement is deferred for mods whose `thunderstorePackage` has not resolved yet, but only until the first resolve pass after server start finishes. That window is bounded and logged; it exists so a restart does not lock everyone out for the few seconds the downloads take.
- BepInEx *patchers* (`BepInEx/patchers/`) are not plugins and are not covered by any of this.

Cheat detection (enabled by default, configurable).
- Automatic log, kick or ban for common cheating utilities
- ValheimTooler is detected even when injected mid-session (after mod validation) and is always auto-banned
- Optional [Discord notification](#discord-notifications) whenever a player is banned for cheating, routable to a staff-only channel

Clients are checked against a catalog of known cheat tools across three vectors:

| Vector | What it looks at | Why it exists |
| --- | --- | --- |
| Process | Names of running programs | Catches the tool while it is open |
| Module | DLLs loaded into Valheim itself | Sees a cheat that already injected and then closed its launcher, and survives renaming the tool |
| Window | Window classes and titles | Catches tools renamed to dodge the process check (a "Cheat Engine" window title does not change when you rename the exe) |

Detected by default: **WeMod / Wand / Infinity**, **Cheat Engine** (including the `magic-engine` fork and injected speedhack/DBK modules), **ArtMoney** (SE and Pro), **PLITCH**, **Speed Gear**, **Squalr**, **WPE Pro**, generic trainers such as FLiNG and Cheat Happens, and the loaders used to deliver Valheim cheats — **ValheimTooler**, **ValHack**, **Valheim Mod Menu**, **SharpMonoInjector**, **Xenos** and **Extreme Injector**.

Tools with no purpose other than cheating (the loaders and injectors above) are banned on sight. Everything else follows `ActionOnDetection`, which defaults to `Kick`. The auto-ban decision is made by the *server* from its own catalog — a client only ever reports what it saw, so a tampered client cannot get another player banned.

Some window signatures are *low confidence*: Cheat Engine's `TfrmMain`/`TfrmMemView` classes are Delphi's default names for forms called `frmMain`/`frmMemView`, and plenty of legitimate Delphi software carries them. A low-confidence sighting is reported and shows up in the server log marked `(weak)`, but it is **never** kicked or banned on its own, regardless of `ActionOnDetection` — enforcement requires a strong signal (process name, injected module, or window title).

Window *titles* are ignored on windows that display content rather than run it — browsers and Electron apps, UWP frames, File Explorer, and terminals. A YouTube tab titled "cheat engine tutorial", a Discord channel discussing ArtMoney, or a folder named after a tool will not match, and because those windows are skipped outright, browser tab titles are never sent to the server.

**Privacy:** only matched entries are sent to the server. A player's full process list never leaves their machine.

**False positives:** generic framework window classes are logged but never enforced, and browser/Explorer/terminal titles are not matched at all (see above), so neither a Delphi utility in the tray nor a YouTube tab about a cheat tool can get anyone kicked. Developer tools that also read game memory — x64dbg, Process Hacker / System Informer, HxD, ReClass.NET, Frida, Fiddler — are deliberately **not** detected by default, because modders and streamers use them routinely. Add them to `AdditionalCheatProcesses` if your server wants them treated as cheats. `Aurora`, `Process Lasso`, `AutoHotkey`, and overlay tools like MSI Afterburner and OBS are excluded on purpose and are not recommended additions; see the config file comments for the reasoning. If something legitimate trips a detection, add it to `IgnoredCheatProcesses`, which overrides everything else.

*Disclaimer: Valheim is client authoratative and without extremely invasive measures, cheating cannot be fully prevented. Process-name detection in particular is a speed bump rather than a wall — renaming Cheat Engine is a documented feature of the tool, and trainer executables are renameable by design. The module and window-title checks exist because they survive a rename, but a client that can cheat can also lie about what it is running. The same applies to mod file verification: the hash is computed and reported by the client, so it stops a recompiled mod, not a patched enforcer. What it changes is the cost — from "edit one file and rebuild" to "reverse engineer and patch the anti-cheat", which is a real barrier to the people who actually do the former and none at all to the people who can do the latter.*

### One Character Per Account

Off by default. Set `EnforceCharacterLimit` to `true` and an account may only join with a character this server already has a save for — anyone else is turned away at the connect handshake and told which character to come back as. Nothing about this is retroactive punishment: **every character an account already has stays playable**, so switching it on locks nobody out. It only stops the *next* new character.

There is no separate list to maintain. The characters an account "has" are exactly the saves under `BepInEx/config/ValheimEnforcer/Characters/<PlatformID>/`, which the mod already writes on the first join. So a brand new player joins normally, that character becomes theirs, and a second one is refused. Run `enforcer-list-players` to see who has what.

**Giving someone a fresh start** is deleting their character's `.yaml` from that folder while they are offline. The slot frees itself; the next character they connect with takes it.

#### Settings

| Setting | Default | What it does |
| --- | --- | --- |
| `EnforceCharacterLimit` | `false` | Master switch. Everything below is inert until this is on |
| `MaxCharactersPerAccount` | `1` | How many characters an account may have. Accounts already over it keep what they have |
| `CharacterLimitExemptAccounts` | *(empty)* | Comma-separated account ids allowed any number of characters |
| `CharacterLimitExemptAdmins` | `false` | Whether being on the adminlist is itself an exemption |
| `NotifyCharacterRejected` | `true` | Post refused joins to [Discord](#discord-notifications), if a webhook is configured |

Exemptions are deliberately independent of admin rights — an exempt account does not need to be an admin, and an admin is not exempt unless you list them or turn `CharacterLimitExemptAdmins` on. Ids go in either form: `Steam_76561198012345678` or the bare `76561198012345678`. Note that this setting syncs to connected clients like every other server setting, so the ids in it are visible to players; if that matters for your server, the alternative is editing it in the config file with the list left empty in-game.

#### Things worth knowing

- **Identity is the character name.** It is the only thing about a character the server learns during the handshake. A player who deletes "Bjorn" locally and makes a new "Bjorn" gets past the check — though since this mod pushes the saved Bjorn's items and skills back on join, it is a poor way to get a clean slate.
- **The save holds the slot, not the player.** Delete someone's save while they still have that character locally and it counts as new again next time they join.
- If the server cannot read its character folder at all, joins are **allowed** and a warning is logged. A disk problem should not lock out your playerbase.
- On a player-hosted (listen) server the host never goes through the connect handshake, so the host's own account is not checked. Dedicated servers check everyone.
- Enforcement is tied to the game's network version. If Valheim ships a new one, the rule stops applying until the mod is rebuilt against it — the check goes quiet rather than guessing at a changed wire format.

### Discord Notifications

Paste a webhook URL into `Discord.WebhookUrl` and the server starts posting: who joined, who left and whether their save was up to date, who was turned away and why, and when the server came up or went down. Nothing else needs configuring.

Everything below is for when you want more than that — a channel per kind of message, different wording, a role ping when somebody gets banned.

#### One channel or several

Every category falls back to `WebhookUrl`, so a category URL is only worth setting when you want that traffic somewhere else.

| Setting | Covers |
| --- | --- |
| `WebhookUrl` | Everything, unless a category below overrides it |
| `WebhookUrlPlayerActivity` | Joins and leaves |
| `WebhookUrlServerStatus` | Startup, shutdown, world saves |
| `WebhookUrlModeration` | Cheat bans, character-limit rejections |
| `WebhookUrlModMismatch` | Connections refused over mods |

The usual split is join/leave into a busy activity channel, moderation into somewhere only staff can read — those messages name the account behind a ban — and mod mismatches into wherever players ask for help, since the message already lists what they need to fix.

Leaving `WebhookUrl` empty and setting only one category is fine: that category posts and nothing else does.

#### Settings

| Setting | Default | What it does |
| --- | --- | --- |
| `WebhookUrl` | *(empty)* | Master switch. Empty means no notifications at all |
| `WebhookUrl…` *(the four above)* | *(empty)* | Per-category override; empty falls back to `WebhookUrl` |
| `ServerLabel` | *(empty)* | Name for this server as `{server}` in templates. Only useful when several servers share a channel |
| `NotifyServerStartup` | `true` | Server came online |
| `NotifyServerShutdown` | `true` | Server going down |
| `NotifyWorldSaved` | `false` | Every world save. **Off on purpose** — the autosave fires roughly every 20 minutes, all day |
| `NotifyPlayerJoined` | `true` | Player joined |
| `NotifyPlayerLeft` | `true` | Player left, and whether their save was current |
| `NotifyWrongMods` | `true` | Connection refused over a mod mismatch |
| `NotifyCheaterBanned` | `true` | Player banned for cheat usage |
| `NotifyCharacterRejected` | `true` | Connection refused by `EnforceCharacterLimit` |

These are deliberately **not** synced to clients — a webhook URL is a password in URL form, and syncing it would hand it to everyone who connects. Edit them in the config file or in Configuration Manager on the server.

#### Rewriting the messages

What each message looks like lives in `BepInEx/config/ValheimEnforcer/Notifications.yaml`, written on first start and re-read within `ConfigPollIntervalSeconds` of being edited. No restart. It ships with the wording this mod has always used, so it changes nothing until you edit it.

**Each entry is the message.** Not a description of one — the literal body posted to Discord, placeholders and all. Nothing is added to it and nothing is filled in for you.

```yaml
playerJoined: |
  {
    "embeds": [{
      "title": "Player Joined",
      "color": {colorGreen},
      "timestamp": "{timestamp}",
      "fields": [
        {"name": "Player", "value": "{player}", "inline": true}
      ]
    }]
  }
```

That means anything Discord accepts works — `author`, `footer`, `thumbnail`, `image`, `url`, several embeds in one post. Their [webhook reference](https://discord.com/developers/docs/resources/webhook) is the full list, and none of it needs a change to this mod.

A `content` line is the one place a mention actually pings; Discord never resolves one inside an embed:

```yaml
cheaterBanned: |
  {
    "content": "<@&123456789012345678> a player was just banned",
    "embeds": [{
      "title": "Cheater Banned",
      "color": {colorRed},
      "fields": [
        {"name": "Player", "value": "{player}", "inline": true},
        {"name": "Detected", "value": "{reason}", "inline": true}
      ]
    }]
  }
```

#### Removing things

Because the body is sent exactly as written, **anything you delete is simply not in the message**. There is no separate switch for turning a piece off.

| Delete | Effect |
| --- | --- |
| `"timestamp"` | No date stamp under the embed |
| `"title"` / `"description"` | That line is gone |
| One entry in `"fields"` | That row is gone |
| `"fields"` | No rows at all |
| `"color"` | No coloured stripe down the left edge |
| `"content"` | No plain-text line above the embed, and no pings |
| `"embeds"` | A plain-text message and nothing else — needs `"content"` to survive |
| The whole event key | The built-in default comes back next start |

So the `playerJoined` above, stripped of its timestamp, colour and title, is a bare one-line post:

```yaml
playerJoined: |
  {
    "embeds": [{
      "fields": [
        {"name": "Player", "value": "{player}", "inline": true}
      ]
    }]
  }
```

**Watch the commas.** JSON does not allow one before a `}` or a `]`, and a dangling comma is what deleting the last item in a list leaves behind. It is checked for — see below — so this costs you a log line rather than a silent outage.

To stop an event posting at all, turn off its `Notify*` setting. Emptying its template is not the way: Discord rejects a message with no content and no embeds, so the mod skips it and logs instead.

#### Placeholders

Written `{likeThis}`. Available to every event:

| Placeholder | Value |
| --- | --- |
| `{server}` | `ServerLabel` from the config, empty unless you set it |
| `{world}` | World name |
| `{onlinePlayers}` | How many are connected |
| `{timestamp}` | Current time, in the ISO-8601 form Discord's `"timestamp"` field wants |
| `{colorGreen}` `{colorAmber}` `{colorRed}` `{colorGrey}` | The numbers Discord wants for `"color"` |

Colours are offered as placeholders only so the shipped palette is convenient. `"color"` takes any number, so `"color": 3447003` is a perfectly good blue.

Then per event:

| Event | Placeholders |
| --- | --- |
| `serverStartup` `serverShutdown` `worldSaved` | *(common only)* |
| `playerJoined` | `{player}` `{playerId}` `{isAdmin}` |
| `playerLeft` | `{player}` `{playerId}` `{disconnect}` `{savedData}` `{deltaWindow}` `{statusColor}` |
| `cheaterBanned` | `{player}` `{playerId}` `{reason}` `{detections}` `{action}` |
| `characterRejected` | `{character}` `{playerId}` `{reason}` `{maxCharacters}` |
| `modMismatch` | `{player}` `{playerId}` `{summary}` `{missingMods}` `{extraMods}` `{versionMismatches}` `{adminOnlyMods}` `{hashMismatches}` `{unverifiedMods}` |

`{summary}` on a mod mismatch is the whole rejection written out as prose, which is what the default shows. The lists beside it are the same information split up, for when you want to say something specific — ping the mod team only when `{hashMismatches}` is involved, or post nothing but `{missingMods}` in a support channel. `{versionMismatches}` names both versions per mod — `com.example.Mod (needs 1.4.2, has 1.3.0)` — and a wrong version lands there even when the file check is what caught it, so `{hashMismatches}` only ever holds a file that fails at the version the server expects.

`{statusColor}` is green after a clean logout and amber after a crash or timeout. The default `playerLeft` uses it as its colour, which is how one template covers both.

Run `enforcer-test-notification playerJoined` to post any event with stand-in data and see the result. It works from the server console or from a connected **admin's** client — in that case the server does the posting and reports back into your console, since the webhook URL is never sent to clients. It ignores the `Notify*` switches but still needs a webhook. `enforcer-test-notification list` names the events.

Non-admins are refused server side, so the command is not a way for a player to make your server post to Discord. There is a short cooldown between tests, which keeps a stuck key from walking the webhook into Discord's rate limiter and silencing the real notifications along with it.

#### Things worth knowing

- **A broken template does not stop notifications.** Every template is checked when the file loads. One that is not valid JSON is reported in the log with a line and column, and that event falls back to its built-in default until you fix it — everything else keeps posting. Bad YAML around the templates keeps whatever was already loaded.
- **A broken template is never overwritten.** The fallback is in memory only; your text stays in the file exactly as you typed it, so restarting mid-edit does not cost you the version you were fixing.
- **The check is for syntax, not for Discord's rules.** It catches dangling commas, unclosed braces, unterminated strings and single quotes. It does not know that an embed title caps at 256 characters or that `"colour"` is not a field. Those come back as an HTTP status in the log.
- **Values are escaped and truncated for you.** A player called `Bj"orn` cannot break the document, and a long `{summary}` or `{extraMods}` is trimmed rather than being allowed to push the post past Discord's limits.
- **A mistyped placeholder is left visible** in the message rather than silently blanked, so `{playr}` arrives as `{playr}` and tells you what to fix.
- **`@everyone` and `@here` work in `content`.** There is no guard against it, and the event you put it on may fire far more often than you expect. Test with a role ping first.
- **Player names go to Discord** whenever notifications are on. That is the point of the feature, but worth knowing before pointing it at a public channel.
- **Comments you add are kept.** A `#` note on its own line stays with the entry below it when the mod rewrites the file. One sharing a line with a value is not, because that line gets rewritten.
- **The world-save message means the save started.** Valheim writes the world on a background thread, so nothing can honestly report the moment it finished. Skipped saves are not announced at all.
- **Turning an event off costs nothing.** The message is never built, so a server that only wants ban alerts does no work for the rest.

### Migrating from ServerCharacters

Coming from [ServerCharacters](https://thunderstore.io/c/valheim/p/Smoothbrain/ServerCharacters/)? Valheim Enforcer can read the character files it leaves behind, so your players keep their inventories and skills instead of having everything confiscated on their first join.

**The two mods cannot run at the same time.** They both take over character saving and would fight over every profile, so Enforcer declares ServerCharacters incompatible. Be aware of how BepInEx enforces that: it refuses to load **Enforcer**, not ServerCharacters. A server with both installed runs with no Enforcer at all — no mod enforcement, no character sync, no anti-cheat — and the only sign is a line in the BepInEx log. So the order matters:

1. Stop the server.
2. Uninstall ServerCharacters. **Leave its character files alone** — they are what gets imported.
3. Set `ImportServerCharacters = true` in `ValheimEnforcer.cfg`.
4. Start the server and read the log. It reports how many characters were imported, skipped or unreadable.
5. Optionally set it back to `false`. Leaving it on is harmless — characters that already have a save are skipped, so the pass does nothing on later starts.

Want to look before you leap? With the server running, an admin can use `Enforcer-Import-ServerCharacters dryrun`, which reports exactly what it would do and writes nothing. `Enforcer-Import-ServerCharacters import` runs it on demand, and adding `force` overwrites saves that already exist (normally they are left alone).

The importer only ever **reads** ServerCharacters' files. Nothing is moved, renamed or deleted, so your old setup stays intact if you want to go back.

#### What comes across

Inventory (including item quality, variants, crafter names and the custom data mods like EpicLoot attach to items), skill levels, and per-player custom data.

Food, guardian power, known recipes/stations/materials, trophies, map data and spawn points do **not** come across — Enforcer's character store does not model them. In practice players do not notice: ServerCharacters also writes each player's own local character file, so all of that is still on their machine. What the server needs is only enough to recognise their stuff and stop confiscating it.

The exception is a player who has lost their local character file. Under ServerCharacters the server copy was fully authoritative and could restore everything; here they would come back with their items and skills but not their recipes or map. That is a difference between how the two mods store characters, not something the import can fix.

#### Things worth knowing

- Files are found automatically in the game's own character folder, which is where ServerCharacters puts them and which follows Valheim's `-savedir`. Only set `ServerCharactersImportPath` if you moved them somewhere else.
- Backups are ignored on purpose — the `backups` folder, `.fch.old`, and `*_backup_*` files. A hardcore character that died is left dead.
- The character name is taken from inside the profile, not the file name. ServerCharacters lowercases the file name, and its own code misreads names containing an underscore.
- A corrupt or truncated file is skipped and reported rather than half-imported, and a file written by a newer version of Valheim than this build understands is skipped rather than guessed at.
- If the import cannot read something, the affected player simply joins as if they were new. It never blocks a connection.

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
