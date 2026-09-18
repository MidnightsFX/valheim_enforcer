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

BepInEx's own doorstop is a `winhttp.dll` proxy, so it is recognised by the files Doorstop ships
alongside it and is not reported. The graphics names — `dxgi`, `d3d9`/`10`/`11`/`12`, `ddraw`,
`opengl32` — are how ReShade, Special K and ENB install, so a sighting there is *low confidence*:
reported and logged, never enforced on its own.

A proxy DLL that is not recognised says a loader is installed, not which one, so it follows
`ActionOnDetection`. Known builds are identified by SHA256 and reported under the tool's own name,
which for a dedicated cheat means an auto-ban.

When Valheim's own runtime lists processes, it silently leaves out anything it is not allowed to open. That covers every program started as administrator and every background service, roughly a third of what runs on a typical desktop. On Windows the process scan therefore reads names from a system snapshot, which needs no access to the processes themselves. `ScanElevatedProcesses` (Advanced, on) switches back to the old list.

## What is detected by default

Detected by default: **WeMod / Wand / Infinity** (the app, its auxiliary service, the `TrainerHost` injector, and the trainer DLLs it loads into the game), **Cheat Engine** (including the `magic-engine` fork and injected speedhack/DBK modules), **ArtMoney** (SE and Pro), **PLITCH**, **Speed Gear**, **Squalr**, **WPE Pro**, generic trainers such as FLiNG and Cheat Happens, the injected menus — **ValheimTooler** and **Valheaven** (a.k.a. ValheimAdminMenu) — and the loaders used to deliver Valheim cheats: **ValHack**, **Valheim Mod Menu**, **SharpMonoInjector**, **Xenos**, **Extreme Injector**, and unrecognised proxy DLLs.

Tools with no purpose other than cheating (the menus, loaders and injectors above) are banned on sight. Everything else follows `ActionOnDetection`, which defaults to `Kick`. The auto-ban decision is made by the *server* from its own catalog — a client only ever reports what it saw, so a tampered client cannot get another player banned.

Some signatures are *low confidence*: Cheat Engine's `TfrmMain`/`TfrmMemView` classes are Delphi's default names for forms called `frmMain`/`frmMemView`, and plenty of legitimate Delphi software carries them; a graphics proxy DLL is how ReShade and Special K install. A low-confidence sighting is reported and shows up in the server log marked `(weak)`, but it is **never** kicked or banned on its own, regardless of `ActionOnDetection` — enforcement requires a strong signal (process name, injected module, window title, injected assembly, or a non-graphics proxy DLL).

Window *titles* are ignored on windows that display content rather than run it — browsers and Electron apps, UWP frames, File Explorer, and terminals. A YouTube tab titled "cheat engine tutorial", a Discord channel discussing ArtMoney, or a folder named after a tool will not match, and because those windows are skipped outright, browser tab titles are never sent to the server.

## Privacy and false positives

**Privacy:** only matched entries are sent to the server. A player's full process list never leaves their machine.

**False positives:** generic framework window classes are logged but never enforced, and browser/Explorer/terminal titles are not matched at all (see above), so neither a Delphi utility in the tray nor a YouTube tab about a cheat tool can get anyone kicked. **ReShade, Special K and ENB** install as a graphics proxy DLL, which is the same mechanism a loader uses — they are reported as low confidence and never enforced on their own, and BepInEx's own `winhttp.dll` doorstop is recognised and not reported at all. Developer tools that also read game memory — x64dbg, Process Hacker / System Informer, HxD, ReClass.NET, Frida, Fiddler — are deliberately **not** detected by default, because modders and streamers use them routinely. Add them to `AdditionalCheatProcesses` if your server wants them treated as cheats. `Aurora`, `Process Lasso`, `AutoHotkey`, and overlay tools like MSI Afterburner and OBS are excluded on purpose and are not recommended additions; see the config file comments for the reasoning. If something legitimate trips a detection, add it to `IgnoredCheatProcesses`, which overrides everything else.

## What this can and cannot do

*Disclaimer: Valheim is client authoratative and without extremely invasive measures, cheating cannot be fully prevented. Process-name detection in particular is a speed bump rather than a wall — renaming Cheat Engine is a documented feature of the tool, and trainer executables are renameable by design. The module, window-title, assembly and proxy checks exist because they survive a rename: the proxy check in particular keys on where a file was loaded from rather than on anything the author chooses, so it does not go stale when the tool is rebuilt. But every one of them runs on the client and is reported by the client, and a client that can cheat can also lie about what it is running. The same applies to mod file verification: the hash is computed and reported by the client, so it stops a recompiled mod, not a patched enforcer. What it changes is the cost — from "edit one file and rebuild" to "reverse engineer and patch the anti-cheat", which is a real barrier to the people who actually do the former and none at all to the people who can do the latter.*

*What the server does refuse to take on trust is anything it can decide for itself. The sender of every network message is verified against the connection it arrived on, so a modified client cannot act as another player — it cannot run an admin's commands, get someone else banned, or write to another account's character. A character save or inventory delta is only ever accepted for the account and character the connection joined as. The join rules (item confiscation, skill clamping, custom-data reset) are re-run on the server for returning characters, not just applied on the client, and a first save from a brand-new character is held to the new-character rules server-side. These are the parts a client cannot lie its way past; the caveats above are about the parts — what mods it runs, what it has in its inventory this instant — that it still can. [Network Integrity](network-integrity.md) extends the same principle to the vanilla RPCs the server relays: chat names, player teleports, mass object deletion, damage values and global keys are all checked against what the server itself knows, so no amount of patching the client gets past them.*


## Settings

| Setting | Default | What it does |
| --- | --- | --- |
| `EnableCheatDetection` | `true` | Master switch. Everything below is inert until this is on |
| `ActionOnDetection` | `Kick` | What the server does: `Log`, `Kick` or `Ban`. Dedicated game-cheating tools are auto-banned regardless, and low-confidence sightings are logged only regardless |
| `DetectInjectedCheatAssemblies` | `true` | Detects injected cheat menus — ValheimTooler, Valheaven — by the namespace of the types they load, which survives renaming, including assemblies injected mid-session. Always auto-banned |
| `DetectValheimTooler` | `true` | Includes ValheimTooler in the assembly scan. Split out because it predates the catalog and some servers had already turned it off. Requires `DetectInjectedCheatAssemblies` |
| `DetectProxyLoaders` | `true` | Detects a system DLL loaded from the game folder rather than from Windows — the shape of every proxy loader, including Valheaven's `version.dll`. BepInEx's doorstop is recognised; the graphics names (ReShade, Special K) are logged only. Requires `ScanLoadedModules` |
| `DetectCheatTools` | `true` | The built-in catalog: WeMod/Wand, ArtMoney, PLITCH, Speed Gear, Squalr, WPE Pro, and the injectors and loaders used to deliver Valheim cheats |
| `DetectCheatEngine` | `true` | Cheat Engine — process names, window titles, and injected speedhack/DBK modules |
| `DetectGenericTrainers` | `true` | Any running process whose executable name contains "trainer". Catches FLiNG, MrAntiFun and Cheat Happens without listing each one |
| `ScanLoadedModules` | `true` | Scan the native DLLs loaded into the game process. The only way to see a cheat that injected and then closed its launcher, and what carries the proxy-loader check |
| `ScanWindowTitles` | `true` | Scan open window classes and titles. Catches a tool renamed to dodge the process check |
| `ScanElevatedProcesses` | `true` | *(Advanced)* Read process names from a system snapshot rather than the runtime's own list, so programs running as administrator and background services are not invisible |
| `AdditionalCheatProcesses` | *(empty)* | Comma-separated process names to treat as cheats on top of the catalog |
| `IgnoredCheatProcesses` | *(empty)* | Comma-separated process, module or window names never flagged. Overrides everything else, the proxy check included |
| `ScanIntervalSeconds` | `30` | *(Advanced)* Seconds between scan ticks. The process, module and window scans are staggered across successive ticks so their cost never lands on the same frame, so each individual scan runs every three intervals. Injected-assembly detection is event-driven and not affected |

`Discord.NotifyCheaterBanned` (on) posts a message whenever a player is banned for cheating. It names the account behind the ban, so it is worth routing to `WebhookUrlModeration` and a staff-only channel — see [Discord Notifications](discord.md).

---

[← All documentation](README.md)
