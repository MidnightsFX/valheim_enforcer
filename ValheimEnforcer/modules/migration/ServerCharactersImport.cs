using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ValheimEnforcer.common;
using ValheimEnforcer.modules.character;

namespace ValheimEnforcer.modules.migration {

    internal sealed class ImportReport {
        internal string SourceDirectory;
        internal bool DryRun;
        internal int Scanned;
        internal int Imported;
        internal int SkippedExisting;
        internal int SkippedUnreadable;
        internal int Failed;
        internal readonly List<string> Details = new List<string>();

        internal string Summary() {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(DryRun
                ? $"ServerCharacters import (DRY RUN - nothing was written) from {SourceDirectory}"
                : $"ServerCharacters import from {SourceDirectory}");
            sb.AppendLine($"  Candidate files: {Scanned}");
            sb.AppendLine(DryRun ? $"  Would import:    {Imported}" : $"  Imported:        {Imported}");
            sb.AppendLine($"  Already present: {SkippedExisting}");
            sb.AppendLine($"  Unreadable:      {SkippedUnreadable}");
            sb.AppendLine($"  Failed to write: {Failed}");
            foreach (string line in Details) { sb.AppendLine($"  - {line}"); }
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// One-way import of ServerCharacters' server-side character files into the enforcer's own character store.
    ///
    /// ServerCharacters keeps each player's profile in the server's VANILLA local character folder
    /// (<c>&lt;savedir&gt;/characters_local/</c>) as <c>&lt;Platform&gt;_&lt;UserID&gt;_&lt;lowercased name&gt;.fch</c>,
    /// and the file is an untouched vanilla profile. A server switching to this mod otherwise starts with an
    /// empty store, so everyone's first join confiscates their whole inventory and their skills have no
    /// baseline - this closes that gap.
    ///
    /// What is produced is the enforcement BASELINE, not a full character: this mod models inventory, skills
    /// and player custom data, while a .fch also carries food, guardian power, known recipes/stations/materials,
    /// trophies, map data and spawn points. Nothing is lost to the player - ServerCharacters writes the client's
    /// own local .fch on every save, so their machine still has the complete character - the server only needs
    /// enough to stop confiscating and clamping.
    ///
    /// Read-only with respect to the source: nothing is moved, renamed or deleted.
    /// </summary>
    internal static class ServerCharactersImport {

        // <Platform>_<UserID>_<character name>.fch, e.g. Steam_76561198012345678_bjorn.fch.
        //
        // The id segment cannot contain an underscore but the NAME can, which is why this anchors on the first
        // two separators instead of splitting on every one. ServerCharacters' own parse (Utils.cs) does
        // file.Name.Split('_')[2] and therefore reads "Steam_7656..._big_bjorn.fch" as the character "big".
        private static readonly Regex ServerCharacterFile =
            new Regex(@"^(?<platform>[A-Za-z0-9]+)_(?<id>[^_]+)_(?<name>.+)\.fch$", RegexOptions.IgnoreCase);

        internal static ImportReport Run(bool dryRun, bool force) {
            ImportReport report = new ImportReport { DryRun = dryRun };

            string sourceDirectory = ResolveSourceDirectory();
            report.SourceDirectory = sourceDirectory ?? "(unresolved)";
            if (string.IsNullOrEmpty(sourceDirectory)) {
                report.Details.Add("Could not determine where ServerCharacters keeps its files. Set ServerCharactersImportPath.");
                return report;
            }
            if (!Directory.Exists(sourceDirectory)) {
                report.Details.Add($"Source directory does not exist: {sourceDirectory}");
                return report;
            }

            string[] files;
            try {
                // Top level only - characters_local/backups/ holds per-character zips of superseded saves.
                files = Directory.GetFiles(sourceDirectory, "*.fch", SearchOption.TopDirectoryOnly);
            } catch (Exception e) {
                report.Details.Add($"Could not list {sourceDirectory}: {e.Message}");
                return report;
            }

            // Internal storage mode writes through a ZDO the registry has to be linked to first;
            // WritePlayerCharacterToSave -> InternalDataStore.SaveAccountCharacter does not do this itself.
            if (!dryRun && ValConfig.InternalStorageMode.Value) {
                try {
                    InternalDataStore.InstanciateOrLinkMetadataRegistry();
                } catch (Exception e) {
                    report.Details.Add($"Could not open the in-world character registry ({e.Message}); nothing was imported.");
                    return report;
                }
            }

            foreach (string file in files) {
                string fileName = Path.GetFileName(file);

                Match match = ServerCharacterFile.Match(fileName);
                if (!match.Success) {
                    // A plain vanilla character the operator left in the folder, not a ServerCharacters file.
                    Logger.LogDebug($"Import: skipping {fileName}, not a ServerCharacters file name.");
                    continue;
                }
                if (fileName.IndexOf("_backup_", StringComparison.OrdinalIgnoreCase) >= 0) {
                    Logger.LogDebug($"Import: skipping {fileName}, it is a backup copy.");
                    continue;
                }

                report.Scanned++;
                ImportOne(file, fileName, match, dryRun, force, report);
            }

            return report;
        }

        private static void ImportOne(string path, string fileName, Match match, bool dryRun, bool force, ImportReport report) {
            if (!FchReader.TryRead(path, out FchProfile profile, out string error)) {
                report.SkippedUnreadable++;
                report.Details.Add($"{fileName}: {error}");
                Logger.LogWarning($"Import: could not read {fileName} - {error}");
                return;
            }

            // The account id in the file name is platform-prefixed (Steam_7656...), while this mod's character
            // folders are the bare id, so normalise. Prefer the name from INSIDE the profile: the file name is
            // force-lowercased by ServerCharacters, and on a case sensitive filesystem "bjorn.yaml" would not be
            // found when the client asks for "Bjorn".
            string accountId = PlatformIds.Normalize($"{match.Groups["platform"].Value}_{match.Groups["id"].Value}");
            string characterName = profile.PlayerName;

            if (string.IsNullOrEmpty(accountId) || string.IsNullOrEmpty(characterName)) {
                report.SkippedUnreadable++;
                report.Details.Add($"{fileName}: could not determine an account id and character name");
                return;
            }
            if (!string.Equals(match.Groups["name"].Value, characterName, StringComparison.OrdinalIgnoreCase)) {
                Logger.LogDebug($"Import: {fileName} is named for '{match.Groups["name"].Value}' but the profile inside is '{characterName}'; using the profile's name.");
            }

            if (!force && ValConfig.LoadCharacterFromSave(accountId, characterName) != null) {
                report.SkippedExisting++;
                report.Details.Add($"{characterName} ({accountId}): already has a character save, left alone");
                return;
            }

            DataObjects.Character character = BuildCharacter(profile, accountId, characterName);

            if (dryRun) {
                report.Imported++;
                report.Details.Add($"{characterName} ({accountId}): would import {character.PlayerItems.Count} item(s), {character.SkillLevels.Count} skill(s)");
                return;
            }

            try {
                ValConfig.WritePlayerCharacterToSave(accountId, character);
                // Mandatory after any write that goes around the async store, or it can overwrite us from cache.
                CharacterStore.Invalidate(accountId, characterName);
                report.Imported++;
                report.Details.Add($"{characterName} ({accountId}): imported {character.PlayerItems.Count} item(s), {character.SkillLevels.Count} skill(s)");
                Logger.LogInfo($"Imported ServerCharacters save for {characterName} ({accountId}).");
            } catch (Exception e) {
                report.Failed++;
                report.Details.Add($"{characterName} ({accountId}): write failed - {e.Message}");
                Logger.LogWarning($"Import: failed to write character save for {characterName} ({accountId}): {e.Message}");
            }
        }

        private static DataObjects.Character BuildCharacter(FchProfile profile, string accountId, string characterName) {
            DataObjects.Character character = new DataObjects.Character {
                Name = characterName,
                HostID = accountId,
                // A migrated character was not mid-session when it was last written, so the dirty-reconnect
                // leniency paths must not treat it as one. Clean is the enum default and so stays out of the yaml.
                LastDisconnect = DataObjects.DisconnectionState.Clean,
                SkillLevels = new Dictionary<Skills.SkillType, float>(profile.SkillLevels),
                PlayerCustomData = new Dictionary<string, string>(profile.CustomData),
                PlayerItems = new List<DataObjects.PackedItem>(),
                // Confiscation is server-owned and starts empty. It also must stay empty: MergeConfiscatedItems
                // ignores entries with a null confiscationId, so anything parked there could never be returned.
                ConfiscatedItems = new List<DataObjects.PackedItem>()
            };

            foreach (FchItem item in profile.Items) {
                character.PlayerItems.Add(new DataObjects.PackedItem {
                    prefabName = item.PrefabName,
                    m_stack = item.Stack,
                    // Taken verbatim. The clamp in Character.AddItemToPlayerItems needs item.m_shared, which is
                    // not reachable without ObjectDB, and this is the value the client itself was carrying.
                    m_durability = item.Durability,
                    // Quality 0 means "unset" in older saves and is treated as 1 everywhere else.
                    m_quality = item.Quality == 0 ? 1 : item.Quality,
                    m_variant = item.Variant,
                    m_worldlevel = item.WorldLevel,
                    m_crafterID = item.CrafterID,
                    m_crafterName = item.CrafterName ?? "",
                    // Left null rather than an empty dictionary: the field has no DefaultValue attribute, so an
                    // empty one would serialize "mCustomdata: {}" onto every item. PackedItem treats null and
                    // empty as equal.
                    m_customdata = item.CustomData,
                    m_equipped = item.Equipped,
                    m_gridpos = item.GridPos
                });
            }

            return character;
        }

        /// <summary>
        /// Where ServerCharacters keeps its files. Defaults to the game's own local character folder, which is
        /// what ServerCharacters uses (<c>PlayerProfile.GetCharacterFolderPath(FileHelpers.FileSource.Local)</c>)
        /// and which honours Valheim's -savedir argument for free.
        /// </summary>
        internal static string ResolveSourceDirectory() {
            string configured = ValConfig.ServerCharactersImportPath.Value;
            if (!string.IsNullOrWhiteSpace(configured)) { return configured.Trim(); }

            try {
                return PlayerProfile.GetCharacterFolderPath(FileHelpers.FileSource.Local);
            } catch (Exception e) {
                Logger.LogWarning($"Import: could not resolve the game's character folder ({e.Message}). Set ServerCharactersImportPath.");
                return null;
            }
        }
    }
}
