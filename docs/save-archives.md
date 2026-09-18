# Save Archives

Off by default. Set `EnableSaveArchives` to `true` and the server keeps a rolling history of compressed archives, each holding **the world save and the character saves from the same moment**.

The game already rotates backups of its own — two of them, uncompressed, of the world only. What this adds is a longer history, compression, and the character store in the same archive. That last part is the point: every player's items, skills, confiscation record and synced progression live in `BepInEx/config/ValheimEnforcer/Characters/`, and none of it is in a vanilla backup. Restoring one of those puts the world back and leaves every character exactly as they were, which is its own kind of rollback.

**Know the cost before switching it on.** A real world file reaches 200 MB. Vanilla's own rotation already keeps around a gigabyte beside it. Each archive here is another copy on top — compressed, but still measured in hundreds of megabytes. The defaults are deliberately conservative.

## When an archive is taken

Only ever just after a world save **finishes**. `SaveArchiveIntervalMinutes` is a minimum gap rather than a schedule, so the real cadence is that interval rounded up to the next save.

This matters more than it sounds. Valheim's `SaveWorld` returns as soon as it has *started* the save — the write happens on the game's own background thread afterwards — so anything that archived when a save was requested would be zipping a half-written world. Enforcer waits for that thread to finish before it reads a single file.

The zip itself is built on a background thread, so compressing 200 MB never costs the server a frame.

## Settings

| Setting | Default | What it does |
| --- | --- | --- |
| `EnableSaveArchives` | `false` | Master switch. Everything below is inert until this is on |
| `SaveArchiveIntervalMinutes` | `120` | Shortest gap between archives. Below the server's save interval means "every save" |
| `SaveArchiveKeepCount` | `5` | How many to keep. Lowering it gives the disk back at the next archive, not one file per cycle |
| `SaveArchiveMaxTotalMB` | `0` | Ceiling on all archives together, oldest deleted first. `0` means no ceiling |
| `SaveArchiveIncludeCharacters` | `true` | Include the `Characters/` folder |
| `SaveArchiveIncludeConfig` | `true` | Include this mod's `.cfg` and YAML files |
| `SaveArchiveCompression` | `Fastest` | `Fastest`, `Optimal` or `NoCompression` |
| `SaveArchivePath` | *(empty)* | Where to write. Empty means `BepInEx/config/ValheimEnforcer/Archives` |

## Commands

- `enforcer-archive-list` — what is on disk, newest first, with sizes and the running totals for this session.
- `enforcer-archive-now [save]` — take one at the next completed save, ignoring the interval. Add `save` to start a world save immediately rather than waiting for the next one.
- `enforcer-archive-prune confirm` — apply the keep count and the size ceiling now. Without `confirm` it lists what it would delete and stops.

## What is in one

```
<world>-<yyyyMMdd-HHmmss>.zip
├── world/        the world save, whichever layout it uses
├── characters/   Characters/<PlatformID>/<Name>.yaml and .map
└── config/       ValheimEnforcer's .cfg and its YAML files
```

Both world layouts are handled — the legacy flat `<World>.fwl` + `<World>.db` pair and the current chunked directory — because the file list comes from the game's own save record rather than from a guess at the filenames.

## Things worth knowing

- **An archive is never half-written.** It is built as `.zip.tmp` and renamed on success, so a crash mid-archive leaves a temporary file that rotation ignores and a later run deletes. Nothing that rotation counts as a good archive is ever incomplete.
- **Rotation reads the timestamp out of the filename, never the file's modification time.** A restore, a copy or a sync tool rewrites mtimes, and deciding what to delete from a rewritten mtime deletes the wrong archive. A file whose name does not parse is not one of ours and is left completely alone — including anything else you keep in that folder.
- **The newest archive is never deleted** to satisfy `SaveArchiveMaxTotalMB`. Setting a ceiling smaller than a single archive keeps one rather than none.
- **A file that cannot be read does not fail the archive.** It is skipped, named in the log, and the rest is still written — an incomplete archive that says so beats no archive at all.
- **Other mods' configuration is not included.** It is not this mod's to copy around.
- Archives are not pruned at startup, only after one is written. A server that has been off for a month prunes on its first archive rather than on boot.


---

[← All documentation](README.md)
