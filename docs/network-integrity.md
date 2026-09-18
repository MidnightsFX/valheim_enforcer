# Network Integrity

Off by default. Set `EnableRpcGuards` to `true` and the server starts checking the vanilla network messages that it otherwise relays without looking at them.

These are a different kind of check from everything else in this mod. Cheat detection and mod verification ask the client about itself and have to take the answer on trust; these guards read the packet the server already has in its hands, so **there is nothing for a client to lie about**. A patched Enforcer gets past the first kind and not past this one.

Each guard covers one vanilla RPC that validates nothing:

| Guard | The RPC | What a client can do with it today | What the guard does |
| --- | --- | --- | --- |
| `GuardChatSenderName` | `ChatMessage` | The display name travels *inside* the message, written by the sender. Talk as any player, or as an admin | Rewrites the name to the one the server holds. The message still arrives |
| `GuardPlayerTeleportRpc` | `RPC_TeleportPlayer` | Teleports whoever *receives* it, and can be addressed to everybody at once | Refused for non-admins |
| `GuardZdoDestruction` | `DestroyZDO` | Names a list of objects and the server deletes every one. No ownership check, no limit | Keeps the ids the sender owns or is near, drops the rest |
| `GuardDamageRpc` | `RPC_Damage` | Damage is applied from numbers the attacker's client wrote. One-shot anything | Drops hits that are NaN, infinite, negative or above `MaxAllowedHitDamage` |
| `GuardPvpDamage` | `RPC_Damage` | The hit's ignore-PvP flag is written by the attacker, so setting it kills players who never opted in | Re-decides it on the server, where the flag carries no weight. Off by default |
| `GuardGlobalKeys` | `SetGlobalKey` | Sets any world key — every boss defeated, free building, altered damage rates | Allows only keys a loaded prefab actually sets |

Everything is refused *and logged*; `RpcGuardAction` decides whether the player is also kicked or banned, and defaults to `Log`. Run it that way for a while first — a mod doing something unusual shows up here as a refusal, and the log names it. Repeat offences from the same player collapse into one line per 30 seconds, so a script cannot flood the log.

Admins are exempt by default (`RpcGuardExemptAdmins`), because vanilla's own admin-only `recall` command sends the teleport message, and admins legitimately clean up objects they do not own. **The chat name binding ignores that exemption** — an admin has no reason to speak under another player's name, and it is the most valuable name to borrow.

## Global keys

This one is off even when `EnableRpcGuards` is on, and it is the only guard with an asymmetric failure mode: a key wrongly refused stops progression registering, quietly. Turn it on deliberately.

There is no list to maintain. Every key a client legitimately sets is written on a prefab — the key a creature sets when it dies, the one an offering bowl sets when a boss is summoned, the one a runestone sets when it is read — so the allowed set is read out of the prefabs this world actually loaded, the same way structure validation reads buildable pieces out of piece tables. A modded boss with its own key is covered without anyone listing it. `activeBosses` and `AshlandsOcean` are set from code rather than a prefab field and are built in.

`AllowedClientGlobalKeys` is the escape hatch: comma-separated key names, added on top of what the scan found. Matched on the key name alone, so `activeBosses` also permits `activeBosses 2`. When a legitimate key is refused, the log names it — that is what you paste in here.

Removal is judged separately. Nothing in normal gameplay removes a global key; only console commands and world setup do. `BlockClientGlobalKeyRemoval` (on) refuses removal from non-admins outright rather than checking it against the allowlist, since otherwise a client could erase the very keys it is allowed to set.

Nothing is filtered until the prefab scan has succeeded, and keys the server sets itself are never filtered.

## Client Contradictions

Off by default. Set `ReportClientContradictions` to `true` and the server ties the two halves of this mod together.

Every player who gets past the join gate is, by construction, running only mods this server approved - that is what the gate is for. So when one of the guards above refuses something they sent, an approved mod set has produced traffic the server does not sanction, and the guard is the half of that sentence that **cannot be forged**. The report names the player, the guard, and the mod list they claimed at join, which is what makes it something a moderator can act on rather than two unrelated log lines.

**It does not decide who lied.** A guard trip cannot tell "the client lied about its mods" apart from "a mod you really did approve legitimately sends this", and nothing can work that out in general. Where the inference *is* tight - the client declared only mods this server itself requires, and the server offers no optional mods - the report says so explicitly, because there nothing they declared can account for it.

| Setting | Default | Effect |
| --- | --- | --- |
| `ReportClientContradictions` | `false` | Correlate declarations with guard trips |
| `ContradictionThreshold` | `2` | How many **distinct** guards before the action applies |
| `ContradictionAction` | `Log` | What happens then. Separate from `RpcGuardAction` on purpose |

Distinct guards rather than total trips: one player hitting the same guard four hundred times is a single behaviour the guard already stopped, while tripping the teleport, global-key and object-destroy guards once each is a toolkit. Run `enforcer-trust` to see who has tripped what before acting.

Only *enforced* refusals count. A correction a guard made quietly - a rebound chat name - is not evidence, because an ordinary chat mod that decorates names produces exactly the same thing on the wire.

## Things worth knowing

- **`GuardDamageRpc` is a sanity bound, not a damage model.** Deciding whether a *plausible* hit was earned would mean modelling every weapon, skill, buff and world modifier on the server, and getting that wrong deletes real combat. It catches the class that matters — non-finite values that corrupt a health bar for good, and the absurd totals used to one-shot players and bosses. Raise `MaxAllowedHitDamage` if a mod on your server legitimately hits harder than the 5000 default.
- **`GuardZdoDestruction` filters, it does not drop.** A legitimate batch and a hostile id can arrive in the same packet, and cancelling the whole thing would leave destroyed objects alive on the server. Ownership is the real test; `ZdoDestroyProximityMetres` is the tolerance for the moment where a client has claimed something and the server has not caught up yet.
- **`GuardPvpDamage` is off even with the guards on.** Vanilla already refuses player-on-player damage to somebody with PvP off — but it skips that check whenever the hit's ignore-PvP flag is set, and the attacker writes that flag. This re-decides it server side. Self-damage is always allowed, since standing in your own fire is exactly the case vanilla sets the flag for. The cost: an area-effect prefab that sets the flag deliberately loses its pass-through, and a PvP arena mod may rely on it. Watch the log first.
- **Chat name mismatches are never kicked.** The rewrite is the whole fix, and a chat mod that decorates names looks identical on the wire. It is logged as a correction and left there.


---

[← All documentation](README.md)
