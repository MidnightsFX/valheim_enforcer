# Player Activity Audit

Off by default. Every other feature in this mod answers "was a specific rule broken". This one answers the question you actually get asked: *what has this player been doing?*

Somebody reports that Bjorn emptied the guild chest and is one-shotting trolls. By the time you log in the chest is empty, the items are spread across four other players, and nothing anywhere says who took what. The audit is already running by the time that happens, so there is something to look at — which is the whole reason it is on by default rather than waiting to be switched on.

**It records and reports only.** It never kicks, bans, confiscates, or blocks a single packet. It is not a detector and it makes no accusations — it is the evidence layer the detectors sit on top of.

## What it records

| | Where it comes from | Can a modified client avoid it? |
| --- | --- | --- |
| Items gained and lost | The character sync the server already receives | Yes — a client that stops reporting stops producing these |
| Containers opened, taken from, stored into | The chest's own world data, read on the server | **No** |
| Damage dealt | The damage message the server relays | **No** |

The two halves are worth having together precisely because they fail differently. A player who gains a sword with no matching container loss, or who deals damage while reporting no inventory change at all, has produced a contradiction — and the two server-observed signals are the ones that cannot be talked out of.

Containers means chests, ships, carts **and graves**, so somebody looting a grave that is not theirs lands in the same record.

## Reading it

| Command | What it answers |
| --- | --- |
| `enforcer-audit-inventory <account> <character>` | What are they carrying, and who crafted it? |
| `enforcer-audit-history <account> <character> [minutes] [filter]` | How did they get it? |
| `enforcer-audit-damage [character]` | What are they doing with it, right now? |
| `enforcer-audit-available <account> <character>` | What history does the server still have? |
| `enforcer-audit-download <account> <character> [days]` | Give me a copy to keep. |

A worked example — a report comes in about `Bjorn`:

```
enforcer-player-list                                  # find their account id
enforcer-audit-inventory 76561198012345678 Bjorn      # what they have, and which items have no crafter
enforcer-audit-history 76561198012345678 Bjorn 120    # the last two hours of how it got there
enforcer-audit-damage Bjorn                           # what they are hitting things for
enforcer-audit-download 76561198012345678 Bjorn 7     # keep a copy before it ages out
```

`enforcer-audit-history` takes a filter — `all` (the default), `items`, `containers` or `damage` — and a window in minutes.

## Where it lives

One file per UTC day in `BepInEx/config/ValheimEnforcer/Audit/`, one event per line, written by a background thread so nothing touches the disk on the main thread. The lines are JSON, which is also valid YAML, so `grep container-took audit-2026-08-30.yaml` is a perfectly good way to use this feature and needs no tooling at all:

```
{"t": "2026-08-30T14:04:00Z", "acct": "76561198012345678", "character": "Bjorn", "kind": "container-took", "prefab": "Wood", "qty": 47, "quality": 1, "container": "piece_chest_wood", "pos": "1234/31/-987"}
```

`AuditRetentionDays` (7) decides how long they stay. Expired files are deleted one per five-minute pass, so a server that has been offline for a month drains its backlog gradually rather than stalling on one enormous sweep.

`enforcer-audit-download` sends a player's events to the machine you typed the command on, under `BepInEx/config/ValheimEnforcer/AuditDownloads/`. **That copy is yours and the retention window does not reach it** — which is the point when you need to keep something past seven days, and worth knowing when you did not mean to.

## Settings

| Setting | Default | What it does |
| --- | --- | --- |
| `EnableAuditLog` | `true` | The only switch. Records items gained and lost, container takes and stores, and damage dealt. Turning it back on takes a restart |
| `AuditRetentionDays` | `7` | How many days to keep |
| `AuditDamageWindowSeconds` | `60` | Length of the rolling damage window |
| `AuditHighDamageThreshold` | `200` | Single hit above this gets written down |
| `AuditHighDamagePerWindow` | `5000` | Window total above this gets written down |
| `AuditMaxDownloadDays` | `7` | Largest range one download may ask for |
| `AuditExemptAdmins` | `false` | Whether admins are left out |
| `AuditFlushIntervalSeconds` | `30` | Advanced. How often the buffer is written |
| `AuditContainerTrackingLimit` | `5000` | Advanced. How many chests it remembers the contents of |

## Things worth knowing

- **You are keeping a log of what your players do, and you are doing it by default.** Seven days of item movements and chest access, on disk, with an admin able to take a copy. It is on out of the box because an audit you had to know to switch on beforehand is no use on the day you need it — but that makes it a decision you should make deliberately rather than one you inherit. Tell your players if that is how you run your server, and set `EnableAuditLog` to `false` if it is not.
- **`AuditExemptAdmins` is off by default, unlike every other admin exemption here.** The others exist because those features *punish*, and an admin using devcommands should not be kicked for it. This one only records, so exempting admins buys nothing and puts the blind spot in exactly the accounts that can do the most damage and are the most worth impersonating.
- **Item timestamps are when the server heard, not when it happened.** The client batches inventory changes over a couple of seconds before sending them. It is also a *net* diff: picking something up and dropping it again inside one window produces no entry at all. The container half has neither limitation.
- **The damage figures are what the attacking client claimed**, before the victim applies armour, resistances and difficulty scaling. That is the right number for spotting the impossible and the wrong one for comparing builds. Only a player's own hits count — their tamed wolf's do not.
- **`AuditHighDamageThreshold` will fire on legitimate late-game hits.** It sits deliberately far below `MaxAllowedHitDamage`, which drops impossible packets; this one only notes hits that are *suspicious*, which is the range a careful cheater actually works in. It is a thing to look at, not a verdict.
- **There is no history before the server started recording.** Nothing is backfilled, so a day you spent with the audit off stays a blank. The whole value of this feature is that it is already running when you find out you needed it, which is why it does not wait to be asked.
- **Turning it off mid-session works; turning it back on needs a restart.** Switching it off takes effect on the next packet. Switching it on again starts the item and damage halves immediately, but not the container half — that one has to watch every object a client replicates, so its hook is only installed at startup and is left off entirely when the audit was off at boot. The server log says so if it happens.


---

[← All documentation](README.md)
