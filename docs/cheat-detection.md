# Cheat Detection

**On by default.** Clients are checked against a catalog of known cheat tools, and the *server* decides what happens — a client only ever reports what it saw.

- Automatic log, kick or ban for common cheating utilities
- Injected cheat menus — ValheimTooler, Valheaven — are detected even though they are not mods, and are always auto-banned
- Proxy-DLL loaders are caught by where they were loaded from, which survives renaming and rebuilding the tool
- Optional [Discord notification](discord.md) whenever a player is banned for cheating, routable to a staff-only channel

## The five vectors


| Vector | What it looks at | Why it exists |
| --- | --- | --- |
| Process | Names of running programs, including ones run as administrator and background services | Catches the tool while it is open |
| Module | DLLs loaded into Valheim itself | Sees a cheat that already injected and then closed its launcher, and survives renaming the tool |
| Window | Window classes and titles | Catches tools renamed to dodge the process check (a "Cheat Engine" window title does not change when you rename the exe) |
| Assembly | Namespaces of the managed types loaded into the game | The only thing that sees a cheat menu which is not a mod — no plugin file to hash, not in the declared mod list, and nothing on screen outside the game |
| Proxy | Where a system DLL was loaded *from* | Catches a loader dropped beside `valheim.exe`, whatever it is called and whatever it is built from |

### Injected menus, and why the other vectors miss them

The current generation of Valheim cheat menus are not mods and not memory editors. They arrive as a
**proxy DLL** in the Valheim folder, next to `valheim.exe` — Windows resolves a DLL from the
application directory before `System32`, so a local copy of a system DLL gets loaded into the game
ahead of the real one, does as it likes, and forwards the genuine exports on. It then boots a managed
assembly into the game, sharing BepInEx's Harmony when BepInEx is present and loading its own when it
is not.

Nothing else this mod does can see that. There is no file in `BepInEx/plugins` to hash, so
[mod file verification](mod-enforcement.md) never looks at it; it is not chainloaded, so it is not in
the mod list the client declares at join; it has no process of its own; and because it draws its menu
inside the game it has no window either. Two things do give it away, and both are properties of what
it *is* rather than what it is called:

- **The assembly it loads.** Its own types are in this process, and the namespace they sit in cannot
  be changed by renaming the file. Assemblies are inspected as they load and on a periodic sweep, so
  a menu that arrives mid-session — after mod validation has already passed — is still caught.
- **The proxy DLL itself.** The name is no help: `version.dll`, `winmm.dll` and the rest are genuine
  Windows DLLs the game legitimately loads. The path is the whole signal. The real one always resolves
  out of the Windows directory; a copy beside the game does not.

The catch is that a mod manager's loader is the same shape. BepInEx's own doorstop is a proxy DLL in
the game folder; so is the one Vortex installs, and so is x360ce. Past the names, *nothing about
where a file sits tells a legitimate loader from a cheat one* — they live in the same folder, beside
the same `BepInEx`. So the file itself is hashed, and the hash is asked first:

1. **A build known to be a cheat** is reported under that tool's own name, which for a dedicated
   cheat means an auto-ban. This is asked before every exemption below it, and cannot be waived by
   any of them — `DetectProxyLoaders` is the only way past it.
2. **A hash in `AllowedProxyLoaderHashes`** is the admin vouching for one specific file. Silent.
   This is the only thing that grants trust: there is no shipped list of builds to treat as good,
   deliberately. Pre-trusting a loader ships the evasion with it — stock Doorstop is legitimate and
   loads whatever its ini names, so exempting its hash exempts anything it is pointed at.
3. **An allowlisted name**, or a folder named in `IgnoredCheatProcesses`, is silent.
4. **BepInEx's doorstop** — a `winhttp.dll` beside the files Doorstop ships — is silent. Only under
   that name: every client here has BepInEx in the game root, so a doorstop file sitting next to a
   `version.dll` says nothing at all about the `version.dll`, which is the name Valheaven uses.
5. **Anything else** is judged on its name. The graphics names — `dxgi`, `d3d9`/`10`/`11`/`12`,
   `ddraw`, `opengl32` — are how ReShade, Special K and ENB install, so those are *low confidence*:
   reported and logged, never enforced on their own. The rest say a loader is installed, not which
   one, and follow `ActionOnDetection` — a legitimate loader nobody has vouched for included.

When Valheim's own runtime lists processes, it silently leaves out anything it is not allowed to open. That covers every program started as administrator and every background service, roughly a third of what runs on a typical desktop. On Windows the process scan therefore reads names from a system snapshot, which needs no access to the processes themselves. `ScanElevatedProcesses` (Advanced, on) switches back to the old list.

## What is detected by default

Detected by default: **WeMod / Wand / Infinity** (the app, its auxiliary service, the `TrainerHost` injector, and the trainer DLLs it loads into the game), **Cheat Engine** (including the `magic-engine` fork and injected speedhack/DBK modules), **ArtMoney** (SE and Pro), **PLITCH**, **Speed Gear**, **Squalr**, **WPE Pro**, generic trainers such as FLiNG and Cheat Happens, the injected menus — **ValheimTooler** and **Valheaven** (a.k.a. ValheimAdminMenu) — and the loaders used to deliver Valheim cheats: **ValHack**, **Valheim Mod Menu**, **SharpMonoInjector**, **Xenos**, **Extreme Injector**, and unrecognised proxy DLLs.

Tools with no purpose other than cheating (the menus, loaders and injectors above) are banned on sight. Everything else follows `ActionOnDetection`, which defaults to `Kick`. The auto-ban decision is made by the *server* from its own catalog — a client only ever reports what it saw, so a tampered client cannot get another player banned.

Some signatures are *low confidence*: Cheat Engine's `TfrmMain`/`TfrmMemView` classes are Delphi's default names for forms called `frmMain`/`frmMemView`, and plenty of legitimate Delphi software carries them; a graphics proxy DLL is how ReShade and Special K install. A low-confidence sighting is reported and shows up in the server log marked `(weak)`, but it is **never** kicked or banned on its own, regardless of `ActionOnDetection` — enforcement requires a strong signal (process name, injected module, window title, injected assembly, or a non-graphics proxy DLL).

Window *titles* are ignored on windows that display content rather than run it — browsers and Electron apps, UWP frames, File Explorer, and terminals. A YouTube tab titled "cheat engine tutorial", a Discord channel discussing ArtMoney, or a folder named after a tool will not match, and because those windows are skipped outright, browser tab titles are never sent to the server.

## Privacy and false positives

**Privacy:** only matched entries are sent to the server. A player's full process list never leaves their machine.

**False positives:** generic framework window classes are logged but never enforced, and browser/Explorer/terminal titles are not matched at all (see above), so neither a Delphi utility in the tray nor a YouTube tab about a cheat tool can get anyone kicked. **ReShade, Special K and ENB** install as a graphics proxy DLL, which is the same mechanism a loader uses — they are reported as low confidence and never enforced on their own, and BepInEx's own `winhttp.dll` doorstop is recognised and not reported at all. Any **other injector**, legitimate or not, lands in the enforceable tier: a mod manager's loader under another name (Vortex and older or hand-assembled BepInEx packs use `version.dll`), x360ce as `xinput1_3.dll`, a profiler's bootstrap. That is on purpose — at that point only the hash distinguishes it from Valheaven, and an unidentified injector is not owed the benefit of the doubt. Vouch for the specific ones your server runs into; see the troubleshooting note below. Developer tools that also read game memory — x64dbg, Process Hacker / System Informer, HxD, ReClass.NET, Frida, Fiddler — are deliberately **not** detected by default, because modders and streamers use them routinely. Add them to `AdditionalCheatProcesses` if your server wants them treated as cheats. `Aurora`, `Process Lasso`, `AutoHotkey`, and overlay tools like MSI Afterburner and OBS are excluded on purpose and are not recommended additions; see the config file comments for the reasoning. If something legitimate trips a detection, add it to `IgnoredCheatProcesses` — with the one exception that a proxy DLL belongs in `AllowedProxyLoaderHashes` instead.

### A proxy DLL got my player kicked

The server log line for the detection carries the hash of the file that did it:

```
Cheat detection from Bjorn (…): tools: Proxy loader (version.dll) [proxy: version.dll sha256=a31f… at C:\…\Valheim]
  ^ an unidentified proxy DLL - not a build known to be a cheat: …
```

Satisfy yourself about the file — a mod manager's loader and a cheat's loader are the same shape, so
this is a judgement about *that* file, not about the name. Then put the `sha256=` value in
`AllowedProxyLoaderHashes` and have the player rejoin.

Use that rather than `IgnoredCheatProcesses`. A hash exempts the one file and leaves the check
working against anything else that turns up under the same name later; the name exempts every
`version.dll` on every client forever, which is a door Valheaven walks straight through. The setting
takes effect on the client's next module scan, which is one tick in three (≈90 seconds at defaults),
so rejoining is the reliable path.

## What this can and cannot do

*Disclaimer: Valheim is client authoratative and without extremely invasive measures, cheating cannot be fully prevented. Process-name detection in particular is a speed bump rather than a wall — renaming Cheat Engine is a documented feature of the tool, and trainer executables are renameable by design. The module, window-title, assembly and proxy checks exist because they survive a rename: the proxy check in particular keys on where a file was loaded from rather than on anything the author chooses, so it does not go stale when the tool is rebuilt. But every one of them runs on the client and is reported by the client, and a client that can cheat can also lie about what it is running. The same applies to mod file verification: the hash is computed and reported by the client, so it stops a recompiled mod, not a patched enforcer. What it changes is the cost — from "edit one file and rebuild" to "reverse engineer and patch the anti-cheat", which is a real barrier to the people who actually do the former and none at all to the people who can do the latter.*

*What the server does refuse to take on trust is anything it can decide for itself. The sender of every network message is verified against the connection it arrived on, so a modified client cannot act as another player — it cannot run an admin's commands, get someone else banned, or write to another account's character. A character save or inventory delta is only ever accepted for the account and character the connection joined as. The join rules (item confiscation, skill clamping, custom-data reset) are re-run on the server for returning characters, not just applied on the client, and a first save from a brand-new character is held to the new-character rules server-side. These are the parts a client cannot lie its way past; the caveats above are about the parts — what mods it runs, what it has in its inventory this instant — that it still can. [Network Integrity](network-integrity.md) extends the same principle to the vanilla RPCs the server relays: chat names, player teleports, mass object deletion, damage values and global keys are all checked against what the server itself knows, so no amount of patching the client gets past them.*


## Hardening against injected menus

Run **`enforcer-harden`** on the server console. It reports which of these are off and what each one leaves open, and changes nothing.

Most of what a cheat menu actually *does* already runs into code the server runs for itself — and most of that ships **off**, because each switch has a cost to somebody. Worth going through deliberately if a menu has turned up on your server:

| What the menu does | What stops it | Ships |
| --- | --- | --- |
| Item, creature and boss spawner | `BlockSpawnObjectRPC` — `ZNetScene.SpawnObject` has no legitimate caller anywhere in the game | off, needs `EnableStructureValidation` |
| Placing world-generation geometry | `DetectNonBuildableStructures` | off, needs `EnableStructureValidation` |
| Million-damage weapons, one-hit kills | `GuardDamageRpc` + `MaxAllowedHitDamage` | off, needs `EnableRpcGuards` |
| Free building, altered damage rates, self-granted boss kills | `GuardGlobalKeys` | off, needs `EnableRpcGuards` |
| Teleporting other players | `GuardPlayerTeleportRpc` | off, needs `EnableRpcGuards` |
| Terrain brush, mass deletion | `GuardZdoDestruction` | off, needs `EnableRpcGuards` |
| Resized inventory | [`DetectInventoryGrid`](item-origins.md#impossible-inventory-slots) | **on** |
| Spawned gear | [`DetectItemOrigins`](item-origins.md) | off |
| Skipping the join rules client-side | `ServerSideJoinEnforcement` | **on** |

Two of these are worth singling out. **`BlockSpawnObjectRPC`** is the highest-value switch on the list: nothing in vanilla Valheim ever sends `SpawnObject` from a client, so there is no legitimate traffic for it to refuse. And **`ContradictionAction`** (default `Log`) is what turns the rest into consequences — each guard above records the connection that tripped it, and a client tripping several distinct guards is a toolkit rather than a coincidence. Read [`enforcer-trust`](network-integrity.md#client-contradictions) before raising it.

One thing a menu advertises that is *not* a gap: Valheaven claims "anti-cheat scrubbing" that cleans the vanilla "gotten by cheated means" flags before they are written. It can also stamp a plausible crafter on a spawned item, which defeats the no-crafter check by design. What it cannot invent is a crafter this server has actually seen — `DetectUnknownCrafterIds` compares against `PlayerIds.yaml`, which is built from players who really joined.

## Settings

| Setting | Default | What it does |
| --- | --- | --- |
| `EnableCheatDetection` | `true` | Master switch. Everything below is inert until this is on |
| `ActionOnDetection` | `Kick` | What the server does: `Log`, `Kick` or `Ban`. Dedicated game-cheating tools are auto-banned regardless, and low-confidence sightings are logged only regardless |
| `DetectInjectedCheatAssemblies` | `true` | Detects injected cheat menus — ValheimTooler, Valheaven — by the namespace of the types they load, which survives renaming, including assemblies injected mid-session. Always auto-banned |
| `DetectValheimTooler` | `true` | Includes ValheimTooler in the assembly scan. Split out because it predates the catalog and some servers had already turned it off. Requires `DetectInjectedCheatAssemblies` |
| `DetectProxyLoaders` | `true` | Detects a system DLL loaded from the game folder rather than from Windows — the shape of every proxy loader, including Valheaven's `version.dll`. Every flagged file is hashed, and the hash decides first. BepInEx's doorstop is recognised; the graphics names (ReShade, Special K) are logged only. Anything else is enforced unless an admin has vouched for its hash. Requires `ScanLoadedModules` |
| `DetectCheatTools` | `true` | The built-in catalog: WeMod/Wand, ArtMoney, PLITCH, Speed Gear, Squalr, WPE Pro, and the injectors and loaders used to deliver Valheim cheats |
| `DetectCheatEngine` | `true` | Cheat Engine — process names, window titles, and injected speedhack/DBK modules |
| `DetectGenericTrainers` | `true` | Any running process whose executable name contains "trainer". Catches FLiNG, MrAntiFun and Cheat Happens without listing each one |
| `ScanLoadedModules` | `true` | Scan the native DLLs loaded into the game process. The only way to see a cheat that injected and then closed its launcher, and what carries the proxy-loader check |
| `ScanWindowTitles` | `true` | Scan open window classes and titles. Catches a tool renamed to dodge the process check |
| `ScanElevatedProcesses` | `true` | *(Advanced)* Read process names from a system snapshot rather than the runtime's own list, so programs running as administrator and background services are not invisible |
| `AdditionalCheatProcesses` | *(empty)* | Comma-separated process names to treat as cheats on top of the catalog |
| `IgnoredCheatProcesses` | *(empty)* | Comma-separated process, module or window names never flagged. Overrides everything else except a proxy DLL whose build is known to be a cheat. An entry containing `\` or `/` is also matched against the folder a proxy DLL was loaded from |
| `AllowedProxyLoaderHashes` | *(empty)* | Comma-separated SHA256 hashes of proxy DLLs to treat as legitimate. The right escape hatch for a `DetectProxyLoaders` false positive: it exempts one file rather than blinding a name. The hash is printed in the detection's log line |
| `ScanIntervalSeconds` | `30` | *(Advanced)* Seconds between scan ticks. The process, module and window scans are staggered across successive ticks so their cost never lands on the same frame, so each individual scan runs every three intervals. Injected-assembly detection is event-driven and not affected |

`Discord.NotifyCheaterBanned` (on) posts a message whenever a player is banned for cheating. It names the account behind the ban, so it is worth routing to `WebhookUrlModeration` and a staff-only channel — see [Discord Notifications](discord.md).

---

[← All documentation](README.md)
