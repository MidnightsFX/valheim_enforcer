# ValheimEnforcer ModSync

An add-on for [ValheimEnforcer](https://thunderstore.io/c/valheim/p/MidnightMods/ValheimEnforcer/). When a
ValheimEnforcer server turns you away for your mods, and its admin has chosen to offer fixes, this lets the server
fix them for you instead of you working through the list by hand.

ValheimEnforcer works out what the server needs. This add-on does the installing. Without it, ValheimEnforcer only
shows you the list of what to change.

## What it does

When an offering server refuses your connection, the connection error window gets a **Fix mods** button. It opens
the plan:

- **Install or update**: a required mod you are missing, or a mod whose version or file the server refuses. Your old
  copy is turned off and the version the server pinned is installed to `BepInEx/plugins/Owner-ModName`, where
  r2modman and Gale put it.
- **Turn off**: a mod on none of the server's lists. It is renamed to `.old`, never deleted.
- **Not fixed automatically**: anything that cannot be pinned, with the reason.
- **Left alone**: admin-only mods, since your game cannot know whether you are an admin.

Nothing happens until you press **Install and restart**. The packages are downloaded and checked first. Then
Valheim closes, a small helper program applies the changes once the game has exited, and starts it again. When you
joined the server by its address, the game rejoins it.

## What it will and will not do

- **The server never sends code.** Its mod list names Thunderstore packages and the file hashes it accepts. This
  add-on downloads those exact versions from `thunderstore.io` itself, and refuses the whole batch if any plugin
  does not hash to what the server recorded.
- **It never touches** BepInEx patchers, BepInEx core files, ValheimEnforcer, Jotunn or itself. A package
  containing patchers or core files is refused; install it with your mod manager.
- **A mod's dependencies are not followed.** Only what is on the server's lists is installed.
- **Your mod manager is not told.** Gale and r2modman do not know about files written behind their back, so a
  replaced mod still shows its old version there, and a profile sync may undo the change.
- **Replaced mod folders are kept** in `BepInEx/cache/ValheimEnforcer/modsync/replaced`, the last five runs, so you
  can put one back by hand. What the helper did is written to your log on the next start.
- Windows, Linux (including Proton) and macOS (Intel build, which runs under Rosetta on Apple Silicon).

## Setting

`AllowServerModInstalls` under `[Client config]` in `BepInEx/config/MidnightsFX.ValheimEnforcer.ModSync.cfg`. On by
default. Turned off, the window still shows you the plan, and installs nothing.

Servers that offer fixes accept players who carry this add-on. Servers that do not offer them treat it like any
other mod they have not listed, and their refusal names it.

## Installing

Download `ValheimEnforcerModSync-<version>.zip` from the
[GitHub releases](https://github.com/MidnightsFX/valheim_enforcer/releases), then either:

- **Gale or r2modman**: import it as a local mod into the profile you play with, or
- **By hand**: unzip it, and copy the `plugins` folder's contents into `BepInEx/plugins/ValheimEnforcerModSync`.

It needs ValheimEnforcer 0.37.0 or later. It keeps working across ValheimEnforcer updates until ValheimEnforcer
changes the interface the two share; it then logs that it needs updating and stays off.
