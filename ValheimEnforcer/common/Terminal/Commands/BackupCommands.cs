using System;
using System.Collections.Generic;
using System.IO;
using ValheimEnforcer.modules.archive;

namespace ValheimEnforcer.common {
    internal static partial class TerminalManager {

        private static void RegisterBackupCommands() {
            _ = new EnforcerCommand("enforcer-archive-list",
                "Lists the save archives this server holds, newest first, with their sizes and the total. Reads only. eg: enforcer-archive-list",
                ArchiveList, CommandArea.Backups,
                serverAuthoritative: true, requiresAdmin: true);

            _ = new EnforcerCommand("enforcer-archive-now",
                "Asks for an archive of the next completed world save, ignoring SaveArchiveIntervalMinutes. Nothing is written until that save finishes, because an archive taken mid-save would be of a half-written world. Format: [save] - adding 'save' starts a world save straight away instead of waiting for the next one. eg: enforcer-archive-now save",
                ArchiveNow, CommandArea.Backups, ArchiveNowOptions,
                serverAuthoritative: true, requiresAdmin: true);

            _ = new EnforcerCommand("enforcer-archive-prune",
                "Deletes archives past SaveArchiveKeepCount and SaveArchiveMaxTotalMB now, instead of waiting for the next archive to do it. Format: confirm. eg: enforcer-archive-prune confirm",
                ArchivePrune, CommandArea.Backups, ArchivePruneOptions,
                serverAuthoritative: true, requiresAdmin: true);
        }

        private static List<string> ArchiveNowOptions(string[] input) {
            if (input.Length <= 2) { return new List<string>() { "save" }; }
            return new List<string>();
        }

        private static List<string> ArchivePruneOptions(string[] input) {
            if (input.Length <= 2) { return new List<string>() { "confirm" }; }
            return new List<string>();
        }

        private static bool ArchivesEnabled(EnforcerCommandArgs args) {
            if (ValConfig.EnableSaveArchives != null && ValConfig.EnableSaveArchives.Value) { return true; }
            args.Output.Error("Save archives are off. Set EnableSaveArchives to true first; nothing in this feature runs until it is on.");
            return false;
        }

        private static void ArchiveList(EnforcerCommandArgs args) {
            string directory = SaveArchiver.CurrentOutputDir();
            List<SaveArchiver.Existing> archives = SaveArchiver.List(directory);

            args.Output.Detail($"  folder: {directory}", log: false);
            long total = 0;
            foreach (SaveArchiver.Existing archive in archives) {
                total += archive.Bytes;
                args.Output.Detail($"  {Path.GetFileName(archive.Path)}  {SaveArchiver.Describe(archive.Bytes)}", log: false);
            }

            SaveArchiver.Stats stats = SaveArchiver.Snapshot();
            if (archives.Count == 0) {
                args.Output.Info(ValConfig.EnableSaveArchives != null && ValConfig.EnableSaveArchives.Value
                    ? "No archives yet. One is written after the next world save completes."
                    : "No archives, and EnableSaveArchives is off.");
            } else {
                args.Output.Info($"{archives.Count} archive(s), {SaveArchiver.Describe(total)} in total. Keeping {ValConfig.SaveArchiveKeepCount.Value}.");
            }

            // Written/failed/pruned are since this server started, not since the folder was created - say so,
            // because a count of 0 written on a server with archives on disk is otherwise alarming.
            args.Output.Detail($"  since startup: {stats.Written} written, {stats.Failed} failed, {stats.Pruned} pruned", log: false);
            if (stats.Running) { args.Output.Detail("  an archive is being written right now", log: false); }
            if (stats.QueueDepth > 0) { args.Output.Detail($"  {stats.QueueDepth} queued", log: false); }
            if (SaveArchiver.Pending) { args.Output.Detail("  one has been asked for and is waiting on the next completed save", log: false); }
        }

        private static void ArchiveNow(EnforcerCommandArgs args) {
            if (!ArchivesEnabled(args)) { return; }

            SaveArchiver.RequestNow("asked for by an admin");
            bool alsoSave = args.Has("save");
            if (!alsoSave) {
                args.Output.Info("An archive will be written after the next world save completes. Add 'save' to start one now instead of waiting.");
                return;
            }

            if (ZNet.instance == null || !ZNet.instance.IsServer()) {
                args.Output.Warning("Asked for an archive, but this is not the server, so no save could be started here.");
                return;
            }
            // The archive rides the save's completion rather than being taken here: a world save writes on
            // the game's own background thread and returns long before the files are finished.
            // Named, and kept named: Valheim adds optional parameters in the MIDDLE of existing
            // signatures, and a positional call would silently start passing the new one.
            ZNet.instance.Save(sync: false);
            args.Output.Info("Started a world save; the archive is written once it finishes. Run enforcer-archive-list in a moment to see it.");
        }

        private static void ArchivePrune(EnforcerCommandArgs args) {
            if (!ArchivesEnabled(args)) { return; }

            string directory = SaveArchiver.CurrentOutputDir();
            List<SaveArchiver.Existing> before = SaveArchiver.List(directory);
            int keep = ValConfig.SaveArchiveKeepCount.Value;
            long cap = ValConfig.SaveArchiveMaxTotalMB.Value > 0
                ? (long)ValConfig.SaveArchiveMaxTotalMB.Value * 1024L * 1024L : 0L;

            List<string> doomed = SaveArchiver.WouldPrune(before, keep, cap);
            if (doomed.Count == 0) {
                args.Output.Info($"Nothing to prune: {before.Count} archive(s), within the keep count of {keep}"
                                 + (cap > 0 ? $" and the {ValConfig.SaveArchiveMaxTotalMB.Value} MB ceiling." : "."));
                return;
            }

            if (!args.Has("confirm")) {
                foreach (string path in doomed) { args.Output.Detail($"  would delete {Path.GetFileName(path)}", log: false); }
                args.Output.Warning($"{doomed.Count} archive(s) would be deleted and cannot be recovered. Re-run with 'confirm' on the end to do it.");
                return;
            }

            int deleted = SaveArchiver.PruneNow(doomed);
            args.Output.Info($"Deleted {deleted} of {doomed.Count} archive(s); {before.Count - deleted} left in {directory}.");
        }
    }
}
