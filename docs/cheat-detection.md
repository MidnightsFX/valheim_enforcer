# Cheat Detection

**On by default.** Clients are checked against a catalog of known cheat tools, and the *server* decides what happens — a client only ever reports what it saw.

- Automatic log, kick or ban for common cheating utilities
- ValheimTooler is detected even when injected mid-session (after mod validation) and is always auto-banned
- Optional [Discord notification](discord.md) whenever a player is banned for cheating, routable to a staff-only channel

## The three vectors


| Vector | What it looks at | Why it exists |
| --- | --- | --- |
| Process | Names of running programs, including ones run as administrator and background services | Catches the tool while it is open |
| Module | DLLs loaded into Valheim itself | Sees a cheat that already injected and then closed its launcher, and survives renaming the tool |
| Window | Window classes and titles | Catches tools renamed to dodge the process check (a "Cheat Engine" window title does not change when you rename the exe) |

When Valheim's own runtime lists processes, it silently leaves out anything it is not allowed to open. That covers every program started as administrator and every background service, roughly a third of what runs on a typical desktop. On Windows the process scan therefore reads names from a system snapshot, which needs no access to the processes themselves. `ScanElevatedProcesses` (Advanced, on) switches back to the old list.

## What is detected by default

Detected by default: **WeMod / Wand / Infinity** (the app, its auxiliary service, the `TrainerHost` injector, and the trainer DLLs it loads into the game), **Cheat Engine** (including the `magic-engine` fork and injected speedhack/DBK modules), **ArtMoney** (SE and Pro), **PLITCH**, **Speed Gear**, **Squalr**, **WPE Pro**, generic trainers such as FLiNG and Cheat Happens, and the loaders used to deliver Valheim cheats — **ValheimTooler**, **ValHack**, **Valheim Mod Menu**, **SharpMonoInjector**, **Xenos** and **Extreme Injector**.

Tools with no purpose other than cheating (the loaders and injectors above) are banned on sight. Everything else follows `ActionOnDetection`, which defaults to `Kick`. The auto-ban decision is made by the *server* from its own catalog — a client only ever reports what it saw, so a tampered client cannot get another player banned.

Some window signatures are *low confidence*: Cheat Engine's `TfrmMain`/`TfrmMemView` classes are Delphi's default names for forms called `frmMain`/`frmMemView`, and plenty of legitimate Delphi software carries them. A low-confidence sighting is reported and shows up in the server log marked `(weak)`, but it is **never** kicked or banned on its own, regardless of `ActionOnDetection` — enforcement requires a strong signal (process name, injected module, or window title).

Window *titles* are ignored on windows that display content rather than run it — browsers and Electron apps, UWP frames, File Explorer, and terminals. A YouTube tab titled "cheat engine tutorial", a Discord channel discussing ArtMoney, or a folder named after a tool will not match, and because those windows are skipped outright, browser tab titles are never sent to the server.

## Privacy and false positives

**Privacy:** only matched entries are sent to the server. A player's full process list never leaves their machine.

**False positives:** generic framework window classes are logged but never enforced, and browser/Explorer/terminal titles are not matched at all (see above), so neither a Delphi utility in the tray nor a YouTube tab about a cheat tool can get anyone kicked. Developer tools that also read game memory — x64dbg, Process Hacker / System Informer, HxD, ReClass.NET, Frida, Fiddler — are deliberately **not** detected by default, because modders and streamers use them routinely. Add them to `AdditionalCheatProcesses` if your server wants them treated as cheats. `Aurora`, `Process Lasso`, `AutoHotkey`, and overlay tools like MSI Afterburner and OBS are excluded on purpose and are not recommended additions; see the config file comments for the reasoning. If something legitimate trips a detection, add it to `IgnoredCheatProcesses`, which overrides everything else.

## What this can and cannot do

*Disclaimer: Valheim is client authoratative and without extremely invasive measures, cheating cannot be fully prevented. Process-name detection in particular is a speed bump rather than a wall — renaming Cheat Engine is a documented feature of the tool, and trainer executables are renameable by design. The module and window-title checks exist because they survive a rename, but a client that can cheat can also lie about what it is running. The same applies to mod file verification: the hash is computed and reported by the client, so it stops a recompiled mod, not a patched enforcer. What it changes is the cost — from "edit one file and rebuild" to "reverse engineer and patch the anti-cheat", which is a real barrier to the people who actually do the former and none at all to the people who can do the latter.*

*What the server does refuse to take on trust is anything it can decide for itself. The sender of every network message is verified against the connection it arrived on, so a modified client cannot act as another player — it cannot run an admin's commands, get someone else banned, or write to another account's character. A character save or inventory delta is only ever accepted for the account and character the connection joined as. The join rules (item confiscation, skill clamping, custom-data reset) are re-run on the server for returning characters, not just applied on the client, and a first save from a brand-new character is held to the new-character rules server-side. These are the parts a client cannot lie its way past; the caveats above are about the parts — what mods it runs, what it has in its inventory this instant — that it still can. [Network Integrity](network-integrity.md) extends the same principle to the vanilla RPCs the server relays: chat names, player teleports, mass object deletion, damage values and global keys are all checked against what the server itself knows, so no amount of patching the client gets past them.*


## Settings

| Setting | Default | What it does |
| --- | --- | --- |
| `EnableCheatDetection` | `true` | Master switch. Everything below is inert until this is on |
| `ActionOnDetection` | `Kick` | What the server does: `Log`, `Kick` or `Ban`. Dedicated game-cheating tools are auto-banned regardless, and low-confidence sightings are logged only regardless |
| `DetectValheimTooler` | `true` | Detects ValheimTooler by the namespace of the types it loads, which survives renaming, including assemblies injected mid-session. Always auto-banned |
| `DetectCheatTools` | `true` | The built-in catalog: WeMod/Wand, ArtMoney, PLITCH, Speed Gear, Squalr, WPE Pro, and the injectors and loaders used to deliver Valheim cheats |
| `DetectCheatEngine` | `true` | Cheat Engine — process names, window titles, and injected speedhack/DBK modules |
| `DetectGenericTrainers` | `true` | Any running process whose executable name contains "trainer". Catches FLiNG, MrAntiFun and Cheat Happens without listing each one |
| `DetectSpeedhack` | `true` | Speedhack, via Unity time against wall-clock drift |
| `ScanLoadedModules` | `true` | Scan the native DLLs loaded into the game process. The only way to see a cheat that injected and then closed its launcher |
| `ScanWindowTitles` | `true` | Scan open window classes and titles. Catches a tool renamed to dodge the process check |
| `ScanElevatedProcesses` | `true` | *(Advanced)* Read process names from a system snapshot rather than the runtime's own list, so programs running as administrator and background services are not invisible |
| `AdditionalCheatProcesses` | *(empty)* | Comma-separated process names to treat as cheats on top of the catalog |
| `IgnoredCheatProcesses` | *(empty)* | Comma-separated process names never flagged. Overrides everything else |
| `ScanIntervalSeconds` | `30` | *(Advanced)* Seconds between scan ticks. The process, module and window scans are staggered across successive ticks so their cost never lands on the same frame, so each individual scan runs every three intervals. ValheimTooler detection is event-driven and not affected |

`Discord.NotifyCheaterBanned` (on) posts a message whenever a player is banned for cheating. It names the account behind the ban, so it is worth routing to `WebhookUrlModeration` and a staff-only channel — see [Discord Notifications](discord.md).

---

[← All documentation](README.md)
