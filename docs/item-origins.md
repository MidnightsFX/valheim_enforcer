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

# Impossible inventory slots

**On by default** (`DetectInventoryGrid`), and separate from everything above: this one is not about where an item came from, it is about where it is *sitting*.

Every item records the grid cell it occupies. An item in column 11 of an eight-column inventory is not suspicious, it is impossible — the only way to produce one is to have resized the grid, which is a feature of the injected cheat menus and of nothing else. Valheaven announces its own grid in the client log as it changes it: `Inv 12x8`, then `10x8`, then `8x8` as one player walked it back.

**Width is the check. Height is not.** Vanilla builds the player inventory eight columns wide and offers no way to change that — `Player.SetInventorySize` takes *rows* only. Rows are genuinely mod territory:

- vanilla itself sells rows at the trader, up to nine, recorded in the player's `invrows` key
- **ExtraSlots** and **AzuExtendedPlayerInventory** both add rows
- **EquipmentAndQuickSlots** writes the height directly to fit its visible *and hidden* slot rows

Every one of those leaves the width at eight. So `MaxInventoryWidth` ships at `8`, which is correct for vanilla and for all three of those mods, and `MaxInventoryHeight` ships at `0`, meaning rows are not checked at all. Set it only if you know what your own stack produces — count the rows on a fully equipped player and add a margin. A vanilla server can use `9`.

ExtraSlots places its equipment slots outside the ordinary grid flow, so a cell it claims is never reported even when the plain bounds would reject it.

| Setting | Default | What it does |
| --- | --- | --- |
| `DetectInventoryGrid` | `true` | Master switch for the check |
| `MaxInventoryWidth` | `8` | *(Advanced)* Columns a player inventory may have. `0` disables the column check |
| `MaxInventoryHeight` | `0` | *(Advanced)* Rows a player inventory may have. `0`, the default, does not check rows |
| `InventoryGridExemptAdmins` | `true` | Whether admins are exempt |

Like the origin checks, this **warns and never confiscates**, and only items that have just appeared are examined. It does one thing more: it records a contradiction against the connection, so it counts toward `ContradictionThreshold` alongside the [network guards](network-integrity.md#client-contradictions) and shows up in `enforcer-trust`.

### What it cannot see

A client that clamps the grid position before sending defeats this directly. It also cannot see a resize that stays inside legal bounds — the `8x8` the player above settled on is eight columns and eight rows, and vanilla sells nine rows, so that shape is a legal vanilla inventory and no bounds check of any kind will report it. Only the `12x8` and `10x8` phases were ever catchable this way.

What a clamping client gives up is the slots themselves: the server stores the positions it was told, so an item clamped on the way out is an item in a different place when the character comes back.

---

[← All documentation](README.md)
