using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ValheimEnforcer.common {

    /// <summary>
    /// Keeps a copy of a hand-edited config file before this mod rewrites it.
    ///
    /// The bug this exists for: Mods.yaml is regenerated on startup from an in-memory object, and every way of
    /// arriving at that object with less in it than the file had ended with the shortfall being written to disk
    /// on top of the admin's work. A YAML syntax error was the loudest - it degraded to empty settings, and the
    /// rewrite then published them - but a duplicate key, a truncated tail from a process killed mid-write, and
    /// a file-existence check that answered "no" for a file that was plainly there all did the same thing more
    /// quietly. Only requiredMods was ever rebuilt afterwards, from the loaded plugins, so what an admin saw was
    /// their optionalMods / adminOnlyMods / serverOnlyMods emptied by a restart they had no reason to distrust.
    ///
    /// The individual causes are fixed where they live. This is the backstop for the ones nobody has thought of
    /// yet: if a rewrite is about to happen, the previous file is recoverable afterwards.
    ///
    /// Keyed by full path rather than being a field on any one module, because ConfigFileWatcher already keys
    /// everything that way and because Notifications.yaml and KnownCheaters.yaml are rewritten by the same
    /// pattern and deserve the same net.
    /// </summary>
    internal static class ConfigFileBackup {

        /// <summary>
        /// Appended to the file name for the routine pre-rewrite copy.
        ///
        /// Deliberately not ".yaml.old" or anything else ending in .yaml. Nothing globs this directory today,
        /// but the character folder next door is enumerated for "*.yaml" and that is not a distinction worth
        /// betting on. It must also never collide with AtomicFile.TempSuffix, which lands in the same folder.
        /// </summary>
        internal const string BackupSuffix = ".bak";

        /// <summary>Sortable and filename-safe, so a directory listing puts an admin's bad edits in order.</summary>
        private const string UnreadableStamp = "yyyyMMdd-HHmmss";

        private sealed class Entry {
            internal bool BackupTaken;
        }

        private static readonly Dictionary<string, Entry> Files = new Dictionary<string, Entry>();

        private static Entry For(string path) {
            if (!Files.TryGetValue(path, out Entry entry)) {
                entry = new Entry();
                Files[path] = entry;
            }
            return entry;
        }

        /// <summary>
        /// Copies <paramref name="path"/> to "&lt;name&gt;.bak", at most once per session.
        ///
        /// Once per session is load-bearing rather than an optimisation. ModManager.SetModsActive is subscribed
        /// to both PrefabManager.OnPrefabsRegistered and OnVanillaPrefabsAvailable, so a listen host runs the
        /// startup rewrite twice, and ThunderstoreResolver is a third caller. Without the flag the second write
        /// would copy our own freshly generated output over the backup and destroy the only remaining copy of
        /// what the admin had.
        ///
        /// Never throws, and a failure does not stop the caller writing. Refusing to maintain the mod list
        /// because a disk was full would trade a loud failure for a silent one.
        /// </summary>
        internal static void TryBackupOnce(string path) {
            Entry entry = For(path);
            if (entry.BackupTaken) { return; }
            entry.BackupTaken = true; // set first: one attempt per session, successful or not

            try {
                if (!File.Exists(path)) { return; } // nothing to preserve; this run is creating it
                File.Copy(path, path + BackupSuffix, true);
                Logger.LogDebug($"Copied {Path.GetFileName(path)} to {Path.GetFileName(path)}{BackupSuffix} before rewriting it.");
            } catch (Exception e) {
                Logger.LogWarning($"Could not back up {Path.GetFileName(path)} before rewriting it: {e.Message}. Continuing with the write.");
            }
        }

        /// <summary>
        /// Copies a file that could not be parsed to a timestamped name of its own, and returns that name for
        /// the message telling the admin where their data went.
        ///
        /// A name of its own, rather than reusing the routine .bak, because the two have very different
        /// lifetimes. Say an admin makes a bad edit: this session copies the good file aside and writes a fresh
        /// one in its place. Next session the file on disk parses cleanly - it is the one we generated - so the
        /// routine backup runs and overwrites .bak with that regenerated copy. The admin's lists would be gone
        /// from both the file and its backup, which is precisely the failure being fixed, arrived at by a
        /// longer route. Timestamped so two bad edits in different sessions, which are genuinely different
        /// data, do not overwrite one another either.
        /// </summary>
        /// <returns>The backup's file name, or null when nothing was copied.</returns>
        internal static string BackupUnreadable(string path) {
            try {
                if (!File.Exists(path)) { return null; }
                string stamp = DateTime.Now.ToString(UnreadableStamp, CultureInfo.InvariantCulture);
                string target = $"{path}.unreadable-{stamp}{BackupSuffix}";
                File.Copy(path, target, true);
                // The routine copy must not run afterwards and publish our regenerated output as "the backup":
                // this file IS the backup for this session, and it is the one worth keeping.
                For(path).BackupTaken = true;
                return Path.GetFileName(target);
            } catch (Exception e) {
                Logger.LogWarning($"Could not save a copy of the unreadable {Path.GetFileName(path)}: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// A parse failure rendered for somebody who has to go and fix the file.
        ///
        /// YamlDotNet carries the position on the exception and the bare Message does not mention it, so the old
        /// log line described the error without saying where it was - on a six hundred line file, the single
        /// most useful thing it could have said.
        /// </summary>
        internal static string DescribeParseFailure(Exception e) {
            if (e is YamlDotNet.Core.YamlException yaml && yaml.Start.Line > 0) {
                return $"line {yaml.Start.Line}, column {yaml.Start.Column}: {yaml.Message}";
            }
            return e.Message;
        }
    }
}
