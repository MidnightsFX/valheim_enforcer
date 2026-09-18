# Migrating from ServerCharacters

Coming from [ServerCharacters](https://thunderstore.io/c/valheim/p/Smoothbrain/ServerCharacters/)? Valheim Enforcer can read the character files it leaves behind, so your players keep their inventories and skills instead of having everything confiscated on their first join.

**The two mods cannot run at the same time.** They both take over character saving and would fight over every profile, so Enforcer declares ServerCharacters incompatible. Be aware of how BepInEx enforces that: it refuses to load **Enforcer**, not ServerCharacters. A server with both installed runs with no Enforcer at all — no mod enforcement, no character sync, no anti-cheat — and the only sign is a line in the BepInEx log. So the order matters:

1. Stop the server.
2. Uninstall ServerCharacters. **Leave its character files alone** — they are what gets imported.
3. Set `ImportServerCharacters = true` in `ValheimEnforcer.cfg`.
4. Start the server and read the log. It reports how many characters were imported, skipped or unreadable.
5. Optionally set it back to `false`. Leaving it on is harmless — characters that already have a save are skipped, so the pass does nothing on later starts.

Want to look before you leap? With the server running, an admin can use `enforcer-characters-import dryrun`, which reports exactly what it would do and writes nothing. `enforcer-characters-import import` runs it on demand, and adding `force` overwrites saves that already exist (normally they are left alone).

The importer only ever **reads** ServerCharacters' files. Nothing is moved, renamed or deleted, so your old setup stays intact if you want to go back.

## What comes across

Inventory (including item quality, variants, crafter names and the custom data mods like EpicLoot attach to items), skill levels, and per-player custom data.

Food, guardian power, known recipes/stations/materials, trophies, map data and spawn points do **not** come across — Enforcer's character store does not model them. In practice players do not notice: ServerCharacters also writes each player's own local character file, so all of that is still on their machine. What the server needs is only enough to recognise their stuff and stop confiscating it.

The exception is a player who has lost their local character file. Under ServerCharacters the server copy was fully authoritative and could restore everything; here they would come back with their items and skills but not their recipes or map. That is a difference between how the two mods store characters, not something the import can fix.

## Things worth knowing

- Files are found automatically in the game's own character folder, which is where ServerCharacters puts them and which follows Valheim's `-savedir`. Only set `ServerCharactersImportPath` if you moved them somewhere else.
- Backups are ignored on purpose — the `backups` folder, `.fch.old`, and `*_backup_*` files. A hardcore character that died is left dead.
- The character name is taken from inside the profile, not the file name. ServerCharacters lowercases the file name, and its own code misreads names containing an underscore.
- A corrupt or truncated file is skipped and reported rather than half-imported, and a file written by a newer version of Valheim than this build understands is skipped rather than guessed at.
- If the import cannot read something, the affected player simply joins as if they were new. It never blocks a connection.


---

[← All documentation](README.md)
