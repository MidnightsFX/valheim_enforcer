# Item Origins

Off by default. Set `DetectItemOrigins` to `true` and the server watches the items appearing in players' inventories for gear nobody ever made.

A crafted item records who made it. An item conjured with `give`, devcommands or a mod menu records nobody, because only the crafting path ever writes that field. Two checks follow from that:

| Check | What it reports |
| --- | --- |
| `DetectUncraftedEquipment` | Equipment with no crafter, that nothing in this world drops, sells or spawns |
| `DetectUnknownCrafterIds` | Equipment crafted by a player id nobody on this server has ever reported |

**Having no crafter is not suspicious on its own.** Every loot drop, chest item, boss drop and trader purchase in the game has none, so flagging that alone would report a Dvergr circlet and a fishing rod bought from Haldor. The server therefore works out, from the prefabs this world actually loaded, which equipment has *no uncrafted route at all* - reading drop tables, creature drops, pickables and trader stock - and only reports that. A modded boss with a custom weapon drop is covered without anyone listing it. Run `enforcer-item-origins` to see the list; the answer to "why was this flagged" is always in it.

The second check exists because somebody who spawns gear and then thinks to stamp a crafter on it has to invent a number, and an invented one belongs to no player here. Ids are collected from connected players into `PlayerIds.yaml`. **Trading is unaffected**: the question is whether the id is *known*, not whether it is *yours*, so a sword one player made and gave to another is fine because its maker is on file.

**Only items that have just appeared are examined**, never whole inventories. A character carrying gear from before you enabled this is never re-examined, so there is no migration, no grace period and no one-time baseline to sit through.

This **warns and never confiscates**, on purpose.

## Things worth knowing

- **The crafter id comes from the client.** It arrives inside the item payload the client wrote, so a client that thinks to set the field to a plausible value defeats both checks. What this catches is items that were *spawned* - which is how the tools actually in circulation hand out gear, none of them bothering with a crafter. It is a good detector of careless cheating, not an item-integrity guarantee.
- **Expect some noise from `DetectUnknownCrafterIds` at first.** An item crafted by somebody who has not joined since you switched it on has a crafter the registry has not met yet. Nothing is reported at all until the registry has somebody in it, and it fills as people play.
- **`IgnoredItemOriginPrefabs` is the escape hatch** for a mod that hands out gear by a route the prefab scan cannot see - a quest reward written in code, an item granted by a script. Reach for it rather than turning the feature off.
- Admins are exempt by default (`ItemOriginExemptAdmins`), because spawning items is an ordinary thing to do with devcommands.


---

[← All documentation](README.md)
