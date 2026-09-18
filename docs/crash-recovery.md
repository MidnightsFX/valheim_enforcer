# Emergency Crash Recovery

Off by default. Every few minutes the server hands each connected player a **sealed snapshot of their own character** — encrypted and signed with a key that never leaves the server, so it is opaque to whoever is holding it and useless anywhere else. If this server then crashes and comes back from an older save, it asks for those snapshots back and adopts the ones that verify and are genuinely newer than what it holds.

The problem it solves is the one case the character store cannot cover on its own: when the *server* loses data, the server has no copy of what it lost. Every connected client does, because the server sent it to them moments before — and handing that back is only safe if the server can prove the thing it is being handed is its own and is newer than what it has.

## Read this before switching it on

**Restoring a character can duplicate items.** The world and the character store are separate saves and roll back independently. Putting a character back to a state newer than the world can re-introduce items whose source in the world — the chest they came out of, the vein they were mined from — has itself rolled back. That is inherent to restoring a character, not a fault in how it is done here. It is why restoration only ever happens inside a narrow window after an unclean shutdown, and why `CrashRecoveryAutoRestore` is a separate switch you can turn off.

**This does not protect a player's own save.** The client cannot read its own snapshot, so there is nothing in it for the client. A lost or corrupted *local* character file is what [Server-Synced Progression](character-progression.md#server-synced-progression) covers.

## How it decides

A snapshot is adopted only when **all** of the following hold. Each failure is a distinct line in the server log.

1. The recovery **window** is open — the previous run left no clean-shutdown marker, or an admin opened one by hand.
2. The signature verifies under this server's current key. A blob sealed under an older key is reported as such; one that has been altered is reported as tampering. They are different answers on purpose.
3. The account and character it is sealed for match the peer that sent it, as the *server* resolved them.
4. The world id matches this world.
5. Its **sequence number is strictly greater** than the one on the save held here. Every save the server accepts bumps that number, and a client never sets it — so a replayed old snapshot is refused rather than rolling somebody backwards.

The window also closes early the moment this server writes a save of its own: once that has happened, the server's copy is current and a snapshot from before the crash is no longer newer than reality, whatever its number says.

## The key

Generated on first use into `BepInEx/config/ValheimEnforcer/recovery.key`. It is a **file, not a setting** — every ordinary setting here is pushed to clients by the config sync, and a key pushed to the clients it is meant to be opaque to would make the feature decorative.

Keep it with your backups. Without it, snapshots cannot be opened. Losing it is survivable — every outstanding snapshot is simply refused, which is the safe direction — but it is also unrecoverable.

`enforcer-recovery-key-rotate confirm` replaces it and makes every snapshot every client is holding permanently unopenable. It refuses to run while a recovery window is open.

## Settings

| Setting | Default | What it does |
| --- | --- | --- |
| `EnableCrashRecovery` | `false` | Master switch. Everything below is inert until this is on |
| `CrashRecoveryAutoRestore` | `true` | Whether a verified, newer snapshot is adopted on its own inside the window |
| `CrashRecoveryWindowMinutes` | `30` | How long after an unclean shutdown snapshots are accepted |
| `CrashRecoveryPushIntervalMinutes` | `10` | How often each player is sent a fresh snapshot — this is the bound on how much a crash can cost, and the bandwidth knob |
| `CrashRecoveryKeepBlobs` | `3` | How many characters' snapshots a client keeps per world |

## Commands

- `enforcer-recovery-status` — whether the window is open, why, how long it has left, and which key is in use.
- `enforcer-recovery-open confirm` — open a window by hand, for a rollback the server could not have noticed: a restore from a backup, or a host reverted underneath the game. Connected players are asked straight away; anyone else is asked when they join.
- `enforcer-recovery-close` — shut it now.
- `enforcer-recovery-key-rotate confirm` — replace the key.

## Things worth knowing

- **The first session after you switch this on never opens a window.** The marker that answers "did the last run stop cleanly" is only written while the feature is on, so the first start finds none and reads that as a first run rather than a crash. Every start after that answers properly.
- **The clean-shutdown marker is written after the character store has flushed**, never before — a marker claiming a clean stop while a save was still queued would close the window over exactly the data it exists to recover.
- **A hard kill leaves no marker, which is the point.** `kill -9`, a power cut and a host that vanished all look the same to the next start, and all open a window.
- **An unclean shutdown while hosting a different world does not open a window.** Whatever went wrong belongs to that world, not this one.
- **Every restore is logged loudly**, with the sequence it replaced, when it was sealed, and a reminder about the duplication risk. A restore that happened quietly would be worse than none.
- **The snapshot holds the character, not the map.** A server rollback does not touch a client's own map — the client has been holding it all along — so sealing a megabyte of it into every snapshot would be bandwidth spent recovering something that was never lost.
- Snapshots live on the client at `BepInEx/config/ValheimEnforcer/Recovery/<world id>/<character>.vebak`. They are safe to delete; the next push replaces them.


---

[← All documentation](README.md)
