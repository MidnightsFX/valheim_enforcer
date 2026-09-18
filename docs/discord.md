# Discord Notifications

Paste a webhook URL into `Discord.WebhookUrl` and the server starts posting: who joined, who left and whether their save was up to date, who was turned away and why, and when the server came up or went down. Nothing else needs configuring.

Everything below is for when you want more than that — a channel per kind of message, different wording, a role ping when somebody gets banned.

## One channel or several

Every category falls back to `WebhookUrl`, so a category URL is only worth setting when you want that traffic somewhere else.

| Setting | Covers |
| --- | --- |
| `WebhookUrl` | Everything, unless a category below overrides it |
| `WebhookUrlPlayerActivity` | Joins and leaves |
| `WebhookUrlServerStatus` | Startup, shutdown, world saves |
| `WebhookUrlModeration` | Cheat bans, character-limit rejections, structure detections |
| `WebhookUrlModMismatch` | Connections refused over mods |

The usual split is join/leave into a busy activity channel, moderation into somewhere only staff can read — those messages name the account behind a ban — and mod mismatches into wherever players ask for help, since the message already lists what they need to fix.

Leaving `WebhookUrl` empty and setting only one category is fine: that category posts and nothing else does.

## Settings

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
| `NotifyStructureFlagged` | `true` | Structure validation caught a player placing something invalid. At most one post per player per minute, however many objects were involved |

These are deliberately **not** synced to clients — a webhook URL is a password in URL form, and syncing it would hand it to everyone who connects. Edit them in the config file or in Configuration Manager on the server.

## Rewriting the messages

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

## Removing things

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

## Placeholders

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
| `structureFlagged` | `{player}` `{playerId}` `{prefab}` `{position}` `{reason}` `{creator}` `{health}` `{count}` `{action}` |

`{summary}` on a mod mismatch is the whole rejection written out as prose, which is what the default shows. The lists beside it are the same information split up, for when you want to say something specific — ping the mod team only when `{hashMismatches}` is involved, or post nothing but `{missingMods}` in a support channel. `{versionMismatches}` names both versions per mod — `com.example.Mod (needs 1.4.2, has 1.3.0)` — and a wrong version lands there even when the file check is what caught it, so `{hashMismatches}` only ever holds a file that fails at the version the server expects.

`{statusColor}` is green after a clean logout and amber after a crash or timeout. The default `playerLeft` uses it as its colour, which is how one template covers both.

On `structureFlagged`, one message covers the whole batch — a cheat tool drops a village in a second, and a post per piece would walk the webhook into Discord's rate limiter. `{count}` is how many objects were involved and `{prefab}`, `{position}`, `{health}` and `{creator}` describe the first of them; the server log has the rest.

Run `enforcer-notify-test playerJoined` to post any event with stand-in data and see the result. It works from the server console or from a connected **admin's** client — in that case the server does the posting and reports back into your console, since the webhook URL is never sent to clients. It ignores the `Notify*` switches but still needs a webhook. `enforcer-notify-test list` names the events.

Non-admins are refused server side, so the command is not a way for a player to make your server post to Discord. There is a short cooldown between tests, which keeps a stuck key from walking the webhook into Discord's rate limiter and silencing the real notifications along with it.

## Things worth knowing

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


---

[← All documentation](README.md)
