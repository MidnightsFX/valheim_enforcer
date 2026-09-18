# Mod Enforcement

Every client is checked at the connect handshake against the list of mods this server allows. **Nothing needs configuring for the default behaviour** — every mod the server loads becomes a required mod, and a client that does not match is refused and told exactly what to fix.

Both sides need ValheimEnforcer installed, but only the **server's** copy decides anything: the only thing a server reads out of a client is the list of plugins that client actually loaded.

- [Mod List](#mod-list) — the four lists, the file format, what is kept up to date for you
- [Mod File Verification](#mod-file-verification) — SHA256 checks that catch a recompiled mod
- [Patchers](#patchers) — the DLLs that load before any plugin
- [Attestation](#attestation) — proving a mod report was generated for this connection
- [Admin Bypass](#admin-bypass) — testing a mod against the live server

---

## Mod List

The mod list lives in `BepInEx/config/ValheimEnforcer/Mods.yaml`. Both sides need the mod installed, but only the **server's** copy decides anything: the only thing a server reads out of a client is the list of plugins that client actually loaded.

**You do not have to write this file.** Install the mod, start the server, and every plugin the server loaded is now required of everyone. The rest of this section is for when you want something other than "everybody runs exactly what the server runs".

The file is regenerated at startup and re-read within `ConfigPollIntervalSeconds` (30 by default) of being edited, so you can change it on a running server. Comments you write on their own line are kept across those rewrites and stay attached to the entry below them; a comment sharing a line with a value is not, since that line gets rewritten from scratch.

### The four lists

| List | Who fills it in | Client has the mod | Client does not |
| --- | --- | --- | --- |
| `requiredMods` | Auto-populated, then yours | allowed | **rejected** |
| `optionalMods` | You | allowed | allowed |
| `adminOnlyMods` | You | admins allowed, everyone else **rejected** | allowed, admins included |
| `serverOnlyMods` | You | **rejected** | allowed |

`adminOnlyMods` permits a mod to admins without requiring it of them — an admin can connect with or without it. It also outranks `requiredMods`: a mod on both is treated as admin-only, and the server logs which mods that applies to at startup.

Every list is keyed by the mod's BepInEx plugin GUID — `Azumatt.AzuCraftyBoxes`, not `AzuCraftyBoxes`. It is the `GUID` in the plugin's `BepInPlugin` attribute, and the surest place to read it off is the server's `LogOutput.log`, where BepInEx lists each plugin as it loads. A mod that appears in none of the lists is rejected.

`ServerActiveMods.yaml`, beside `Mods.yaml`, lists every plugin this machine loaded, sorted by GUID and written exactly like a `Mods.yaml` entry, so you can copy an entry straight into whichever list it belongs in. It is deleted and rewritten on every start, never read, and not synced to anyone, so editing it does nothing. The list each side reports about itself during the handshake is taken from the plugins actually running, never from a file, because a list taken from a text file is a list a player can type whatever they like into.

`serverOnlyMods` is for mods the server runs and nobody else needs — a map generator, a backup tool, a Discord bridge. It keeps them out of `requiredMods` without demanding them of anyone. It is **not** the list for client-side mods: a client that installs a server-only mod is rejected for it, because that mod is on no list that permits it. Client-side mods belong in `optionalMods`.

### An entry

```yaml
requiredMods:
  Azumatt.AzuCraftyBoxes:
    pluginID: Azumatt.AzuCraftyBoxes
    version: 1.8.13
    name: AzuCraftyBoxes
    enforceVersion: true
```

| Field | What it does |
| --- | --- |
| `pluginID` | The plugin GUID again. The key above it is what lookups actually use |
| `version` | The version to compare against, kept current for you |
| `name` | Human-readable label, for logs and the disconnect screen |
| `enforceVersion` | When `true`, a client's version must match exactly. Defaults to `false` |
| `keepWhenUnloaded` | When `true`, this entry is never dropped just because the server does not load the mod. Defaults to `false` — see [`RemoveUnloadedModsFromRequired`](#settings) |
| `acceptedHashes`, `hashSource`, `thunderstorePackage`, `hashEnforcement` | File verification — see [Mod File Verification](#mod-file-verification) |

The same table is written into the top of `Mods.yaml` and `ServerActiveMods.yaml`, so you do not need this page open to edit the file.

Version comparison is an exact string match, so `1.0` and `1.0.0` count as a mismatch. Fields sitting at their default are not written out, which is why most entries are three lines. If you find a `versionStrictness` field in an older file, it does nothing and can be deleted.

An empty list is written as a bare key with nothing under it:

```yaml
optionalMods:
adminOnlyMods:
```

Add entries by indenting them two spaces beneath. Older versions wrote `optionalMods: {}` instead, and adding entries under *that* is not valid YAML — if you have a file still written that way, delete the `{}` first.

### What happens when someone connects

| Situation | Result |
| --- | --- |
| Missing a mod from `requiredMods` | Rejected, and told which |
| Running a mod that is on no list | Rejected as a non-allowed mod |
| Version differs where `enforceVersion` is set | Rejected as a version mismatch, naming the version to install |
| Running an `adminOnlyMods` mod without being an admin | Rejected |

The client runs the same comparison against the server's list and shows the result in the connection error window, but that is only feedback for the player — the server decides, from its own file. With [Discord notifications](discord.md) enabled, a rejection is posted with the offending mods listed.

### Handled for you

| What | Controlled by |
| --- | --- |
| `ServerActiveMods.yaml` deleted and rewritten from the plugins actually loaded | always |
| Any loaded plugin not already on a list is added to `requiredMods`, with `enforceVersion: false` | `AutoAddModsToRequired` *(on)* |
| Any `requiredMods` entry for a mod the server does not have loaded is removed, unless it is marked `keepWhenUnloaded` | `RemoveUnloadedModsFromRequired` *(on)* |
| A mod's `version` is corrected in whichever list holds it when you update the mod | always |
| The SHA256 of every plugin the server loads is recorded as its accepted hash | `RecordHashesForLoadedMods` *(on)* |
| Mods pinned with a `thunderstorePackage` are downloaded and hashed | `ResolveThunderstoreHashes` *(off)* |
| The file is rewritten with all of the above | `UpdateLoadedModsOnStartup` *(on)* |
| The previous file is copied to `Mods.yaml.bak` before each rewrite | always |
| Edits are picked up without a restart | `ConfigPollIntervalSeconds` *(30)* |

Updating a mod on the server therefore needs no edit here at all — the version follows it, in whichever list you put it in.

### What you write yourself

- Membership of `optionalMods`, `adminOnlyMods` and `serverOnlyMods`. Nothing is ever added to these automatically; move an entry out of `requiredMods` by hand.
- `enforceVersion: true`. Auto-added mods are always written with it off, so a client that is a patch version behind is not locked out of a server that never asked for exact versions.
- `keepWhenUnloaded: true`, on any `requiredMods` entry for a mod the server does not run itself.
- `thunderstorePackage`, `hashEnforcement`, and any `Manual` hash.

### If the file will not parse

`Mods.yaml` is rewritten on startup, so a file the server cannot read is a file whose contents are at risk. Two things protect it:

- Before every rewrite, the previous file is copied to `Mods.yaml.bak`. Once per launch, so a restart cannot overwrite the backup with the file it just generated.
- If the file fails to parse, it is copied to `Mods.yaml.unreadable-<date>-<time>.bak` *first*, and the server logs an error naming the line and column that broke, along with what it managed to load. It then writes a fresh file and carries on, so the server still starts.

If your lists come back empty after a restart, look for that error in the log and for an `unreadable` file beside `Mods.yaml` — your entries are in it.

You do not need to restart to fix it. The file is re-read within `ConfigPollIntervalSeconds` of being saved, and a duplicate top-level key — the same list written twice, which used to be silently discarded — is now reported rather than quietly dropping whichever copy came first.

### Settings

All of these are server-side and synced to admins, so an admin can change them in-game and the server stays the authority.

| Setting | Section | Default | Effect |
| --- | --- | --- | --- |
| `AutoAddModsToRequired` | Mods | `true` | Adds any loaded plugin that is on no list to `requiredMods`. Turn it off to curate the file by hand — mods you have not listed are then rejected rather than adopted |
| `RemoveUnloadedModsFromRequired` | Mods | `true` | Removes `requiredMods` entries for mods the server does not have loaded, so a mod you uninstall from the server stops being demanded of clients. Only `requiredMods` is touched. Put `keepWhenUnloaded: true` on any entry you require but do not run yourself, rather than turning this off for everything |
| `UpdateLoadedModsOnStartup` | Mods | `true` | Writes version corrections, auto-added mods, removed mods and recorded hashes back to the file. With it off, all of that still applies for the session but nothing is saved |
| `HashEnforcement` | Mods | `WhenKnown` | File verification mode — see [Mod File Verification](#mod-file-verification) |
| `RecordHashesForLoadedMods` | Mods | `true` | Records the hash of every plugin this machine loads. Needs `UpdateLoadedModsOnStartup` to reach disk |
| `ResolveThunderstoreHashes` | Mods | `false` | Downloads and hashes mods pinned with a `thunderstorePackage`. Off by default because it makes outbound requests |
| `ConfigPollIntervalSeconds` | Advanced | `30` | How often the file is checked for edits |
| `HashComputeTimeoutSeconds` | Advanced | `30` | Safety valve for a stalled disk during startup hashing, not a tuning knob |
| `ThunderstoreMaxArchiveMB` | Advanced | `128` | Largest package the resolver will download; bigger ones are skipped and logged |

`Discord.NotifyWrongMods` (on) posts a message naming the mods whenever a player is rejected for a mismatch. It can go to a channel of its own, and the wording is yours to change — see [Discord Notifications](discord.md).

### Recipes

**Lock the pack to exact versions.** Set `enforceVersion: true` on every entry you care about. There is no global switch — it is per mod on purpose, so one mod that is fussy about its version does not force the whole list to be.

**Let players use a client-side mod.** Move its entry from `requiredMods` to `optionalMods`, or add it there if the server does not run it. They can then connect with or without it.

**Give admins a tool nobody else may run.** Put it in `adminOnlyMods`. Admins who do not want it can leave it uninstalled. If the server runs the mod too, it will already be in `requiredMods`; you can leave that entry, since `adminOnlyMods` wins, or delete it to keep the file tidy. Admin status is read from the server's admin list at connect time, so no client can claim it.

**Stop a server-side mod being demanded of clients.** Move it to `serverOnlyMods`. Note that this also means no one may connect *with* it.

**Stop requiring a mod you removed from the server.** Delete its entry from `requiredMods`, or leave `RemoveUnloadedModsFromRequired` on and every mod the server no longer loads is dropped from that list on the next start, apart from any marked `keepWhenUnloaded`.

**Require a mod the server does not run.** Add it to `requiredMods` by hand with its GUID, version and name, and give it `keepWhenUnloaded: true` — without that, `RemoveUnloadedModsFromRequired` drops the entry on the next start. To verify the file as well, give it a `thunderstorePackage` and turn on `ResolveThunderstoreHashes`.


## Mod File Verification

Version checks only compare the version string a client declares, so somebody who downloads a mod, edits the numbers and rebuilds it — keeping the version the same — passes. File verification closes that by comparing a SHA256 of the DLL each plugin was actually loaded from.

`HashEnforcement` (server config, `Mods` section) controls it:

| Value | Server has a hash for the mod | No hash, required/admin mod | No hash, optional mod |
| --- | --- | --- | --- |
| `Off` | not checked | not checked | not checked |
| `WhenKnown` *(default)* | **enforced** | allowed | allowed |
| `Strict` | **enforced** | **rejected** | allowed |

`WhenKnown` means turning this on breaks nothing: only mods you have actually pinned are enforced. `Strict` is for a fully pinned server and deliberately fails loudly when a required mod has no hash on file.

Any mod in `Mods.yaml` can override the server setting with `hashEnforcement: Off | WhenKnown | Strict`. The usual setup is `WhenKnown` globally with `hashEnforcement: Strict` on the handful of mods that actually affect balance.

### Getting hashes on file

- **Mods the server loads** pin themselves. `RecordHashesForLoadedMods` (on by default) writes the hash of every plugin the server runs into `Mods.yaml` at startup.
- **Client-only mods** — a UI or QoL plugin the server never loads — need one of:
  - **By hand.** Put the SHA256 in `acceptedHashes` and set `hashSource: Manual`. `Get-FileHash -Algorithm SHA256 <file>.dll` produces it. Nothing else ever overwrites a `Manual` entry.
  - **From Thunderstore.** Set `thunderstorePackage: Owner-ModName-Version` and enable `ResolveThunderstoreHashes`. The server downloads that package, hashes the DLLs inside it in memory, records them and discards the download. It re-downloads only when you change the pinned version. Only `thunderstore.io` and its CDN are ever contacted — arbitrary download URLs are not supported on purpose.

```yaml
requiredMods:
  shudnal.ExtraSlots:
    pluginID: shudnal.ExtraSlots
    version: 1.1.20
    name: Extra Slots
    thunderstorePackage: shudnal-ExtraSlots-1.1.20
    hashEnforcement: Strict
```

### Things worth knowing about file verification

- Recorded hashes are sent to clients on purpose, so the disconnect screen can name the mod that failed. They are not secrets — anyone can download the package and hash it themselves.
- **A recorded hash pins the version too.** A different build of a mod is a different file, so a client on another version fails the file check whether or not `enforceVersion` is set on that entry. That rejection is reported as a version mismatch, naming the version to install — "modified mod files" is kept for a file whose version matches the server's and whose contents do not, which is the case where reinstalling actually helps.
- Plugins loaded from memory rather than from a file (BepInEx ScriptEngine, in-game plugin loaders) cannot be verified. They report as `dynamic` and will be rejected once the server enforces that mod. The client logs a warning about this at startup, before you try to connect.
- Under `Strict`, enforcement is deferred for mods whose `thunderstorePackage` has not resolved yet, but only until the first resolve pass after server start finishes. That window is bounded and logged; it exists so a restart does not lock everyone out for the few seconds the downloads take.
- BepInEx *patchers* (`BepInEx/patchers/`) are not plugins and are not covered by any of this.

## Patchers

Off by default. Set `ValidatePatchers` to `true` and clients are held to a list of allowed BepInEx **patchers**, the same way they are held to a list of allowed mods.

Patchers are not plugins. They are the DLLs in `BepInEx/patchers`, and BepInEx loads them *before any plugin exists*, handing each one the game's assemblies to rewrite on the way in. That is a strictly more powerful position than any plugin has, this one included. Until now nothing here looked at that folder, so dropping a cheat there bypassed mod validation completely.

They are keyed by file path relative to `BepInEx/patchers`, not by GUID, because a patcher carries no BepInEx metadata at all - no plugin id, no version. The file hash is the only thing there is to hold one to.

| List | Who fills it in | Client has it | Client does not |
| --- | --- | --- | --- |
| `activePatchers` | Generated, every start | - | - |
| `allowedPatchers` | Auto-populated from the server's own, then yours | allowed | allowed |

**It is an allowlist, not a required list.** A client carrying no patchers always passes, which matters because almost no player has any while a server may well run several. Only a patcher a client *has* and the server has *not* allowed is refused. A patcher allowed by name is still checked against its recorded hash, since "any file under this name" would let a hostile DLL inherit an allowlisted one.

Patchers are enumerated and logged whether or not `ValidatePatchers` is on, so leave it off for a while first and read the log to see what your players actually carry.

| Setting | Default | Effect |
| --- | --- | --- |
| `ValidatePatchers` | `false` | Enforce the allowlist. Enumeration and logging happen either way |
| `AutoAddPatchersToAllowed` | `true` | Adds the server's own patchers, with hashes, at startup. Hashes are only ever added, never replaced |


## Attestation

Off by default. `AttestationPolicy` makes each client prove its mod report was generated *for this connection*.

Without it, a client reports the same plugin list and the same file hashes every session, forever - so a client patched to skip the work can capture one valid payload and replay it indefinitely. The server now hands each connection a random value during the handshake, and the client returns a digest over that value and the exact mod and patcher list it is sending. The server recomputes it and compares.

**Be clear on what a pass means.** It proves the report was produced now, by code that actually ran. It does **not** prove the report is true: a client that keeps the original DLLs on disk and hashes those still passes. What it costs an attacker is the difference between returning a constant and keeping a working hashing path alive per connection. That is a real increase, and it is not a wall.

| Value | Effect |
| --- | --- |
| `Off` | Nothing is issued and nothing is checked |
| `Report` | A missing or wrong attestation is logged; the player connects |
| `Require` | A missing or wrong attestation is a rejection |

**Before setting `Require`:** a player on an older ValheimEnforcer sends no attestation at all, and is rejected too. Roll the pack out first. If the server's own record of a connection's nonce goes missing, that connection is *allowed* rather than refused - our bookkeeping failing is not the player's fault.


## Admin Bypass

Off by default. Set `ModValidationExemptAdmins` to `true` and anyone on the server's admin list may connect with any mods at all.

That means all of it: missing required mods, mods the server does not allow, mismatched versions, modified mod files, unlisted BepInEx patchers, and a missing or wrong [attestation](#attestation) even under `Require`. It also covers an admin running **no ValheimEnforcer at all**, who is otherwise refused for never sending a mod list — which is usually the case you actually want it for.

The point is to try a mod against the live server before committing it to `Mods.yaml`, rather than editing the list, restarting, and editing it back.

**The check still runs.** Everything a normal client's list is put through is still computed and still written to the server log, so you can read exactly what your admin was carrying. Only the rejection is skipped. The Discord mod-mismatch notification is not sent for an exempt admin, because nobody was turned away and a channel that pings every time an admin joins on a test build is a channel people stop reading.

**Know what it costs.** With this on, a line in `adminlist.txt` is the only thing between an account and every mod check in this mod. Every id in that file needs to be somebody you would trust with an arbitrary client. Admin status is read by the server from its own admin list, against the connection the request arrived on, so no client can claim it — but nothing else stands behind it either.

| Setting | Default | Effect |
| --- | --- | --- |
| `ModValidationExemptAdmins` | `false` | Admins are not rejected for anything the mod gate finds. The gate still runs and still logs |

### Things worth knowing about admin bypass

- An admin whose list failed gets **no client-contradiction declaration on file** ([Client Contradictions](network-integrity.md#client-contradictions)). That feature reasons from "this peer only runs mods the server approved", which is the premise this setting sets aside, so a later guard trip from them is reported as having no declaration rather than being measured against one that was never enforced.
- The oversize-payload guard is not part of the exemption. A mod list too large to parse is refused whoever sent it; that is a resource bound, not a mod policy.
- This covers mod validation only. The cheat-tool scan, the server-authoritative guards and the character rules have their own admin exemptions, listed with each feature.
- `enforcer-whoami` says when it is on, so an admin who joined with an unapproved mod set can tell they were let in on purpose.


---

[← All documentation](README.md)
