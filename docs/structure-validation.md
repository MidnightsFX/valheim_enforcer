# Structure Validation

Off by default. Set `EnableStructureValidation` to `true` and the server starts checking the objects clients create, instead of taking every one of them on trust.

It is the answer to a specific report: large structures appearing on a server that show no "Crafted by" on hover, cannot be destroyed, and flattened the terrain where they landed. All three are the same thing — somebody spawning **world-generation geometry**. A dvergr archway, a crypt room and a stone ruin are ordinary prefabs with ordinary health; what they are not is anything a player can build. There is no craftsman on them because nobody crafted them.

Valheim gives the server nothing to work with here. There is no "place piece" message — a client instantiates the object locally and its data arrives in the same stream as everything else, which the game accepts without checking the prefab, the position, or a single value in it. So this checks it.

## The two checks

| Check | What it looks at | Setting |
| --- | --- | --- |
| Non-buildable structure | A client creates something that is in no build menu | `DetectNonBuildableStructures` |
| Excessive health | A client sets a piece's health above what its prefab allows | `DetectExcessiveStructureHealth` |

**Blueprint mods are safe, and not because of an allowlist.** What makes a prefab placeable is being in a piece table, and *every* build path uses those tables — the hammer, the hoe, the cultivator, and every blueprint, bulk-build or planned-piece mod, because they all place out of the same menus. Mods register their own pieces into those tables too, so a server's custom content is covered without anybody listing it. A prefab with no table entry is one no build tool can reach.

**Repairing is never flagged.** Valheim has no invulnerability flag; an unbreakable piece is just an absurd number in the health field. The ceiling is the prefab's own maximum, including any increase from a world modifier, and a full repair writes exactly that.

**Nobody is blamed for somebody else's structure.** Ownership of an object moves to whichever player is nearest, every couple of seconds. Health that was already too high before a client wrote to it is attributed to no one, so walking past a cheated structure — or hitting it — cannot get an innocent player reported. `enforcer-structures-scan` is how those get found.

There is a second door: `SpawnObject`, a routed message nothing in the game ever sends, which asks the server to instantiate any prefab by hash — a creature or an item as easily as a structure. `BlockSpawnObjectRPC` (on) refuses every one of them and, by default, posts the block to your moderation channel; it follows `StructureValidationAction` for what happens to the player, which defaults to `Log`, so out of the box it blocks and reports without kicking or banning. A structure spawned this way is reported as a structure detection either way. Turn it off to fall back to refusing only non-buildable structures through `SpawnObject`, if a mod on your server legitimately uses the call.

## Settings

| Setting | Default | What it does |
| --- | --- | --- |
| `EnableStructureValidation` | `false` | Master switch. Everything below is inert until this is on |
| `DetectNonBuildableStructures` | `true` | The build-menu check |
| `BlockSpawnObjectRPC` | `true` | Refuse every client-sent `SpawnObject` RPC (all prefabs, not just structures) and post it to the moderation channel |
| `DetectExcessiveStructureHealth` | `true` | The health-ceiling check |
| `StructureValidationAction` | `Log` | What happens to the player: `Log`, `Kick` or `Ban`. Detections are logged and posted to Discord regardless |
| `RemoveDetectedStructures` | `false` | Whether the structure itself is deleted |
| `StructureValidationExemptAdmins` | `true` | Whether the adminlist is exempt |
| `StructureHealthAllowedMultiplier` | `1` | Headroom on the health ceiling, for mods that raise piece health at runtime rather than on the prefab |
| `IgnoredStructurePrefabs` | *(empty)* | Prefab names never flagged, matched as a substring |

`RemoveDetectedStructures` is deliberately a separate switch from the action, and starts off. Run with it off first and read the log for a few days: a wrong detection that only writes a line costs you nothing, and a wrong detection that deletes something costs a player their build. `IgnoredStructurePrefabs` is the fix when you find one — reach for it rather than turning the whole feature off.

**Admins are exempt by default**, unlike every other exemption in this mod. Spawning a non-buildable prefab is what `devcommands` is *for*, and an admin decorating with it should not have to know this feature exists. Set it to `false` to hold admins to the same rule as everyone else.

## Finding what is already there

The live check only sees a structure as it arrives, which is no help to a server that was hit last month. `enforcer-structures-scan` walks every object in the world and reports the ones that look placed rather than generated — prefab, coordinates, health and crafter — grouped by prefab so the shape of it is visible at a glance.

```
enforcer-structures-scan scan
enforcer-structures-scan scan dvergrtown
enforcer-structures-scan remove confirm dvergrtown
```

`scan` changes nothing. Removal needs the word `confirm` typed out, because it is the one thing here that cannot be undone. Both forms take an optional prefab filter, matched as a substring, which is how you act on one finding out of a long report. It runs from the server console or from a connected **admin's** client, and non-admins are refused server side.

The scan runs both checks whatever your `Detect*` settings say — you asked for a picture of the world, so you get the whole picture. It works in slices across frames, so a large world does not stall the server while it runs.

**It never touches generated content.** Real dungeons and ruins are non-buildable structures too, so anything inside a zone the world generated a location into is excluded, and the report says how many were skipped that way. The cost is stated plainly: a structure spawned right next to real ruins is excluded along with them. Missing one is recoverable and deleting a dungeon is not — and the live check catches that case anyway, wherever it happens.

Removal also refuses to delete more than 500 objects at once without a prefab filter. A number that large means this server's content classifies differently from vanilla's, not that somebody placed five hundred structures by hand.

## Things worth knowing

- **The terrain is not put back.** The flattening arrives as separate objects from the structure, so removing the structure leaves the ground as the cheat left it. Re-terraforming is still yours to do.
- **What is detected is a *structure*.** Something with no piece component at all — scenery, a plant, a creature — is outside the first check on purpose. Requiring one is what keeps tombstones, dropped items, arrows and animals out of a detector that can delete things.
- **Detections name the connection, not the character.** A character name is whatever a client says it is, and the crafter field on a cheated piece is empty by definition. Structures found by a scan are reported with no player at all, because nothing durable records who created an object.
- **A world-generated piece can be damaged.** Locations spawn with their pieces pre-damaged, which is below the ceiling and never flagged.


---

[← All documentation](README.md)
