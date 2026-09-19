# Ban Network

Servers running ValheimEnforcer can share the bans they issue. You publish what you catch, you receive
what everyone else caught, and **you decide what to act on** — the network is a source of information,
never an instruction.

Off by default. It makes outbound network requests and it involves publishing accusations about real
accounts, so nothing happens until you switch it on twice: once to receive, once to publish.

---

## Read this before you turn it on

**Account ids are hashed, and the hashing is not anonymity.** Your server sends a raw platform id over
HTTPS; the service hashes it and stores only the digest. That keeps the database from being a
directly greppable list of SteamIDs, and that is the entire claim. The salt ships inside this mod and
the identifier space is small, so anyone who cares can reverse a digest in seconds. Nothing about the
hashing makes reporting private.

**Reporting a player is publishing an accusation about them.** It goes to every other server on the
network, along with your reason text and — unless you turn it off — the character name they last used.
Treat it the way you would treat posting it publicly, because functionally that is what it is.

**Any approved server can download the whole list.** That is no different from server owners who trade
ban lists in plain text today, which is the thing this replaces. It does mean the list is not a secret.

**There is no appeal process in version one.** If someone is listed unfairly, the operator can retract
an entry and the retraction reaches every server within the hour — but there is no form, no queue and
no adjudication. Your own `Overrides.yaml` is the only mechanism that works immediately, and it only
works on your server. This is a real gap and it is named here rather than discovered later.

---

## Getting a key

Registration is reviewed by a person. There is no way to request a key from in-game, and the mod
contains no registration code at all.

1. Apply through the registration form or the Discord ticket, whichever the network operator runs.
2. Someone reads your application and approves it. You are handed a key, once.
3. Paste it into `BepInEx/config/ValheimEnforcer/BanNetwork/api.key`, on its own line, and save.
4. Run [`enforcer-ban-network-status`](#commands).

The key lives in a file rather than a config setting on purpose: a server setting is pushed to every
connecting client, and even a local one would sit in `ValheimEnforcer.cfg` — the file people paste
wholesale into support threads and which Configuration Manager shows to any in-game admin. The key is
never written to the log, whole or partial. Only the short server id is.

Saving a new key takes effect immediately; it also clears a rejected state and any backoff, so you do
not have to restart to test a fix.

## What it does

Every hour your server fetches entries added since its last fetch, and stores them in
`BanNetwork/NetworkBans.yaml`. When somebody connects, their account is checked against that list.

An entry is only **acted on** if both of these are true:

- it carries a category you listed in `BanNetworkEnforceCategories`, and
- at least `BanNetworkMinReporters` different servers have independently reported it.

Anything else is **advisory**: recorded, visible in `enforcer-ban-list network`, logged when that
player joins, and otherwise ignored. That is the setting to reach for first — widen your categories
once you know what you would have caught.

`BanNetworkEnforceCategories` defaults to `cheating` alone, and that is not timidity. Cheating has a
machine test behind it. `toxic` is one stranger's judgement about another stranger, and auto-enforcing
it across a network of people you have never met is how you end up with a shared blacklist nobody can
appeal.

## Publishing your own bans

Two separate switches, because taking the list and contributing to it are different decisions:

- `BanNetworkReportBans` — publish bans an admin typed.
- `BanNetworkReportAutoCheatBans` — also publish bans the mod issued by itself (cheat detections,
  structure validation, RPC guards). Off by default: these are unreviewed, by far the highest volume,
  and the likeliest source of a false positive landing on somebody else's server.

A ban marked `share: false` in `Bans.yaml` is never published whatever these say, and
`enforcer-unban` retracts your report as well as lifting the ban locally.

Reports queue in `BanNetwork/outbox.yaml` and are sent every few minutes. They survive a restart, and
a clean shutdown flushes them, so a ban issued a minute before you stop the server still gets out.

### Why your ban sometimes is not published

If the network already lists the player, `enforcer-ban` records the ban but does **not** publish it,
and tells you so. The reason is that the reporter count is only meaningful if it counts independent
sightings: a server banning somebody *because the network said to* is agreeing, not corroborating, and
counting it would turn one report into apparent consensus. If you caught them yourself, add `--share`.

Bans the mod inherited — the built-in seed, and anything migrated from the old `KnownCheaters.yaml` —
are never published for the same reason. Your server did not witness them.

## Letting somebody in

**Do not edit `NetworkBans.yaml`.** It is rewritten every hour and your changes will be lost.

```
enforcer-ban-allow 76561198012345678 Appealed to me, accepted
```

That writes `BanNetwork/Overrides.yaml`, which nothing overwrites. An override beats every list,
including the seed that ships inside the mod.

Network entries you have never seen locally show as a 32-character hash rather than an id — that is
the hashing working as intended, and it means your server genuinely does not know who they are. Paste
the hash in place of the id and the override still works:

```
enforcer-ban-allow a91f3c0e5b7d2148a91f3c0e5b7d2148 Cleared after talking to them
```

`enforcer-ban-check` takes either form and prints exactly which rule decided and why.

## Files

Everything lives under `BepInEx/config/ValheimEnforcer/`.

| File | Yours to edit? | What it is |
| --- | --- | --- |
| `Bans.yaml` | **yes** | Every ban this server issued or inherited. Comments preserved. |
| `BanNetwork/Overrides.yaml` | **yes** | Your allow/deny decisions. Never overwritten. |
| `BanNetwork/api.key` | **yes** | Your key, one line. |
| `BanNetwork/NetworkBans.yaml` | no | The cached network list. Rewritten hourly. |
| `BanNetwork/outbox.yaml` | no | Reports not sent yet. |
| `BanNetwork/state.yaml` | no | Cursor and connection state. |

`KnownCheaters.yaml` is no longer read. It is migrated into `Bans.yaml` on first run and left on disk
so you can see what carried over; delete it when you are satisfied.

## Commands

| Command | What it does |
| --- | --- |
| `enforcer-ban <id\|name> <categories> <reason> [--for 7d] [--share\|--no-share]` | Ban, with what it was for |
| `enforcer-unban <id\|name>` | Lift a ban and retract the report |
| `enforcer-ban-list [local\|network\|all] [category]` | What is banned, and what is only advisory |
| `enforcer-ban-check <id\|name\|hash>` | Which rule decides for this account, and why |
| `enforcer-ban-allow <id\|hash> <reason>` | Let them in regardless |
| `enforcer-ban-deny <id\|hash> <reason>` | Refuse them regardless |
| `enforcer-ban-override-clear <id\|hash>` | Back to the normal rules |
| `enforcer-ban-network-status` | Key state, entry counts, last and next cycle, last error |
| `enforcer-ban-network-sync [pull\|push\|both]` | Do it now; also clears a rejected key state |
| `enforcer-ban-network-purge --confirm` | Drop the cached list and rebuild from scratch |

## Settings

| Setting | Default | |
| --- | --- | --- |
| `EnableBanNetwork` | `false` | Off means no requests, no hashing, no files |
| `BanNetworkReportBans` | `false` | Publish admin-issued bans |
| `BanNetworkReportAutoCheatBans` | `false` | Also publish automatic detections |
| `BanNetworkEnforceCategories` | `cheating` | Which categories you act on |
| `BanNetworkMinReporters` | `1` | How much corroboration you want first |
| `BanNetworkAction` | `Ban` | `Ban`, `Kick` or `Log` |
| `BanNetworkSharePlayerName` | `true` | Include the last known character name |
| `BanNetworkNotifyEnforced` | `true` | Post refusals and outages to Discord |
| `BanNetworkPullIntervalMinutes` | `60` | Clamped to 15–360 |
| `BanNetworkPushIntervalMinutes` | `5` | Clamped to 1–60 |
| `BanNetworkMaxReportsPerHour` | `60` | A ceiling you put on yourself |
| `BanNetworkLogAdvisory` | `true` | Log advisory matches at join |
| `BanNetworkEndpoint` | official | https only; no query, fragment or credentials |

## When it is not working

Run `enforcer-ban-network-status`. It reports one of:

| State | Meaning |
| --- | --- |
| `off` | `EnableBanNetwork` is false |
| `no key` | Nothing in `api.key` yet |
| `malformed key` | What is in `api.key` is not a key |
| `registered, awaiting approval` | Nobody has approved you yet. It keeps checking hourly |
| `rejected` | The network does not recognise the key. **Stops trying** until you fix it |
| `revoked` / `suspended` | Access withdrawn. **Stops trying**; the note says why |
| `connected` | Working |

A rejected or revoked key stops the server calling at all, rather than retrying a dead key against a
shared service forever. Entries already pulled are kept and still enforced — losing access to the
network is not a reason to forget what it already told you. Fix the key and run
`enforcer-ban-network-sync`.

## What stops the list being poisoned

Every server on the network is approved by a person, and its reports are taken at face value after
that. What keeps one bad or broken server from doing real damage:

- **A server can only ever count once per player.** The service keeps one report per server per
  account, so reporting somebody a thousand times moves the reporter count by one.
- **Revoking a key undoes what it said.** Withdrawing a server's access also withdraws its reports and
  recalculates every account it touched; those corrections reach every server on the next pull.
- **Volume limits with automatic suspension**, which is an alert for the operator rather than a
  punishment — a suspended server keeps everything it has already pulled.
- **`BanNetworkMinReporters`**, which is yours alone. Set it to 2 and no single server can get anyone
  refused on your machine.
