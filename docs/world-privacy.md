# World Privacy

On by default. Two settings keep things from players that vanilla hands to any of them:

- [`HideWorldSeed`](#the-world-seed) keeps the world seed away from players. With the seed, anyone can open a seed map viewer and see every biome, boss altar, trader and dungeon before exploring any of it.
- [`BlockFreeFly`](#the-free-fly-camera) refuses the free fly camera, which passes through walls and terrain.

Admins (anyone on `adminlist.txt`) and the host of a listen server are exempt from both. Both are server authoritative: an admin can change them in-game, and clients follow the server's value.

| Setting | Default | Effect |
| --- | --- | --- |
| `HideWorldSeed` | `true` | Withhold the seed from non-admins. `false` restores vanilla behaviour |
| `BlockFreeFly` | `true` | Refuse `freefly` for non-admins. `false` restores vanilla behaviour |

## The world seed

Vanilla gives the seed out in three places, and this setting covers all three:

| Where | Who can read it in vanilla | What changes |
| --- | --- | --- |
| The join handshake | Every player who connects | The server sends `hidden` instead of the seed name. Clients never use the name; terrain is built from a separate number |
| The crossplay server listing | Anyone who can join the lobby, which happens before the password prompt and before any mod check | The lobby carries `hidden` instead |
| `printseeds` | Any player. It is not a cheat command, and it works from chat as `/printseeds` without the console enabled | Refused, with a message saying the seed is hidden on this server |

Admins and the host still get the real seed, and `printseeds` still works for them. Other mods that show the seed, such as a world info command or an admin panel's "show seed" button, show `hidden` to everyone else.

### What this does not do

The game cannot build terrain without the world's numeric seed, so that number still reaches every client. A modified client can read it and run its own copy of the world generator. This stops players *looking the seed up*. It does not stop a determined cheater from rebuilding the map. `printseeds` is refused on the client, so it keeps the number away from ordinary players. The two server-side changes are what keep the seed name off the network.

### Things worth knowing

- **A change reaches the crossplay listing at the next restart.** The listing is published once, when the server opens. Players already connected keep whatever they were sent until they rejoin.
- **Steam lobbies never published the seed.** On a Steam-only server, the handshake and `printseeds` are the only ways it got out.
- **A client-side mod that keyed its own saved data on the seed name** sees `hidden` on every server that uses this setting. Nothing in vanilla does this, and the mods found reading the name only display it.

## The free fly camera

`freefly` is vanilla's photo mode: a detached camera you fly around to take screenshots. Like `printseeds`, it is not a cheat command, so any player can run it from chat as `/freefly` without the console enabled. The camera has no collision. It goes straight through walls and terrain: into other players' bases, behind cover in PvP, and underground to see dungeons and ore.

With `BlockFreeFly` on, the command is refused with a message saying free fly is disabled on this server. If anything else switches the camera on (another mod, or the setting being turned on while someone is already flying), the player is dropped back to the normal camera on the next frame.

`ffsmooth` is left working. It only sets how smoothly the free fly camera moves, which does nothing while the camera is off.

### What this does not do

The camera belongs to the player's own game, so this is enforced there. It stops ordinary players. A modified client can switch the camera on anyway. Free fly can never show more than the area already loaded around the player, because the world loads around the character, not the camera. It cannot scout the map.
