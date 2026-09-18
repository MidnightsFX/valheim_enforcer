using BepInEx;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.character {

    /// <summary>
    /// Reads what characters the server has saves for, from whichever store is in use.
    ///
    /// The commands and their tab completion both need this, and both used to open-code it. The flat-file
    /// half of that open-coding was wrong in two ways: it listed the character folder with GetFiles, which
    /// returns nothing because accounts are directories, and it cut path components on '/', which is not the
    /// separator on the platform most servers run on. The result was a player list that printed nothing at
    /// all and gave no hint why.
    /// </summary>
    internal static class CharacterSaves {

        /// <summary>Account id to the character names saved under it. Empty when nothing has been saved yet.</summary>
        internal static Dictionary<string, List<string>> All() {
            if (ValConfig.InternalStorageMode.Value) {
                return InternalDataStore.GetAccountRegistry() ?? new Dictionary<string, List<string>>();
            }

            Dictionary<string, List<string>> map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            string root = Path.Combine(Paths.ConfigPath, ValConfig.ValheimEnforcer, ValConfig.CharacterFolder);
            if (!Directory.Exists(root)) { return map; }

            foreach (string accountDir in Directory.GetDirectories(root)) {
                string account = Path.GetFileName(accountDir);
                List<string> characters = Directory.GetFiles(accountDir, "*.yaml")
                    .Select(Path.GetFileNameWithoutExtension)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                map[account] = characters;
            }
            return map;
        }

        internal static List<string> Accounts() {
            return All().Keys.OrderBy(id => id, StringComparer.Ordinal).ToList();
        }

        internal static List<string> CharactersFor(string account) {
            if (string.IsNullOrEmpty(account)) { return new List<string>(); }
            Dictionary<string, List<string>> map = All();
            List<string> characters;
            // Account ids are typed by hand off a log line, so match them the forgiving way rather than
            // making a wrong-case id look like an account that does not exist.
            foreach (KeyValuePair<string, List<string>> entry in map) {
                if (string.Equals(entry.Key, account, StringComparison.OrdinalIgnoreCase)) {
                    return entry.Value ?? new List<string>();
                }
            }
            return map.TryGetValue(account, out characters) && characters != null ? characters : new List<string>();
        }

        /// <summary>
        /// Whether the server has a save for this exact account and character, used to tell "no confiscated
        /// items" apart from "that is not a character I have ever seen", which are very different answers to
        /// an admin trying to give somebody their cloak back.
        /// </summary>
        internal static bool Exists(string account, string name) {
            return CharactersFor(account).Any(character => string.Equals(character, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Whether a save file for this character exists on disk, regardless of which storage mode is active.
        ///
        /// <see cref="All"/> reports the registry and nothing else in InternalStorageMode, but
        /// <see cref="ValConfig.WritePlayerCharacterToSave"/> deliberately double-writes to both stores so that
        /// switching modes does not lose anybody's character. A character can therefore be absent from the
        /// registry and still be sitting on disk, and anything deciding "this player is brand new" has to look
        /// at both before it concludes anything.
        ///
        /// Tolerant of case and of a platform prefix on the account id, for the same reason
        /// <see cref="TryResolveSave"/> is: the point of this call is not to miss a save that is there, and on
        /// a case-sensitive filesystem an exact File.Exists on a caller-supplied spelling regularly would.
        /// </summary>
        internal static bool ExistsOnDisk(string account, string name) {
            if (string.IsNullOrEmpty(account) || string.IsNullOrEmpty(name)) { return false; }
            try {
                return TryResolveOnDisk(account, name, out _, out _);
            } catch (Exception e) {
                // Unreadable is not the same as absent; say "present" so a disk problem cannot make a
                // returning player look new.
                Logger.LogWarning($"Could not check for an on-disk save for {account}/{name}: {e.Message}");
                return true;
            }
        }

        // ---- Account folder listing -----------------------------------------------------------------------

        /// <summary>
        /// The names of the account folders, as last read, with the last-write time the character folder had
        /// when they were. Immutable once published, and swapped as a whole, so a reader on any thread sees one
        /// consistent listing.
        ///
        /// Every lookup below used to list the character folder afresh. That folder holds one directory per
        /// account that has EVER joined, so the cost grew with the age of the server rather than with who was
        /// online - a path string and a name string per account, thousands of them on a server a few months
        /// old, on the main thread, for every join, every death and every map upload. The listing only changes
        /// when an account joins for the first time.
        /// </summary>
        private sealed class FolderListing {
            internal string[] Names;
            internal DateTime RootWritten;
        }

        private static volatile FolderListing folderListing;

        /// <summary>Forgets the listing, so the next lookup reads the folder again. Called by anything in the
        /// mod that creates an account folder; a folder created by hand is caught by the time stamp instead.</summary>
        internal static void InvalidateFolderListing() {
            folderListing = null;
        }

        /// <param name="fresh">Read the folder now whatever is remembered.</param>
        private static string[] AccountFolders(string root, bool fresh) {
            // Creating or removing an entry in a directory updates that directory's last-write time, on NTFS
            // and on every Linux filesystem alike, so one stat answers "has an account been added".
            DateTime written = Directory.GetLastWriteTimeUtc(root);
            FolderListing held = folderListing;
            if (!fresh && held != null && held.RootWritten == written) { return held.Names; }

            string[] paths = Directory.GetDirectories(root);
            string[] names = new string[paths.Length];
            for (int i = 0; i < paths.Length; i++) { names[i] = Path.GetFileName(paths[i]); }
            folderListing = new FolderListing { Names = names, RootWritten = written };
            return names;
        }

        // Shared disk resolution. Walks the account folders rather than composing a path, so a folder written
        // under a different platform-prefix spelling - or a character file whose case does not match what the
        // caller was handed - is still found. Returns the spellings that actually exist.
        //
        // "Found" may be answered from the remembered listing, because it is checked against the disk anyway:
        // the character file is looked for inside the folder. "Not found" never is. That answer is what gets a
        // player treated as new here, and a new character's inventory is stripped - so before it is given the
        // folder is read again, which is exactly what every call used to cost. A listing that has gone stale
        // can therefore make a lookup slower, and cannot make it wrong.
        private static bool TryResolveOnDisk(string accountId, string characterName,
                                             out string resolvedAccountId, out string resolvedName) {
            resolvedAccountId = accountId;
            resolvedName = characterName;
            string root = ValConfig.CharacterFilePath;
            if (!Directory.Exists(root)) { return false; }

            FolderListing before = folderListing;
            if (TryResolveIn(AccountFolders(root, fresh: false), root, accountId, characterName, out resolvedAccountId, out resolvedName)) {
                return true;
            }
            // Already read from the disk on this call, so there is nothing a second read could add.
            if (before == null || !ReferenceEquals(before, folderListing)) { return false; }
            return TryResolveIn(AccountFolders(root, fresh: true), root, accountId, characterName, out resolvedAccountId, out resolvedName);
        }

        private static void CollectNames(string[] folders, string root, string accountId, List<string> into) {
            foreach (string folder in folders) {
                if (!PlatformIds.Matches(folder, accountId)) { continue; }
                string accountFolder = Path.Combine(root, folder);
                if (!Directory.Exists(accountFolder)) { continue; } // removed since the listing was read
                foreach (string characterFile in Directory.GetFiles(accountFolder, "*.yaml")) {
                    into.Add(Path.GetFileNameWithoutExtension(characterFile));
                }
            }
        }

        private static bool TryResolveIn(string[] folders, string root, string accountId, string characterName,
                                         out string resolvedAccountId, out string resolvedName) {
            resolvedAccountId = accountId;
            resolvedName = characterName;
            // Resolved as a pair on purpose. An account can own more than one folder, so picking a folder and
            // a name independently could hand back a name that lives in a different folder than the one
            // returned - which composes into a path that does not exist.
            foreach (string folder in folders) {
                if (!PlatformIds.Matches(folder, accountId)) { continue; }
                string accountFolder = Path.Combine(root, folder);
                if (!Directory.Exists(accountFolder)) { continue; } // removed since the listing was read
                foreach (string characterFile in Directory.GetFiles(accountFolder, "*.yaml")) {
                    string onDisk = Path.GetFileNameWithoutExtension(characterFile);
                    if (!string.Equals(onDisk, characterName, StringComparison.OrdinalIgnoreCase)) { continue; }
                    resolvedAccountId = folder;
                    resolvedName = onDisk;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Every character name stored under an account, tolerating a platform-prefix difference in the id
        /// (see <see cref="PlatformIds"/>). Empty when the account has none; <c>null</c> when the store could
        /// not be read at all. Those are different answers and callers must not conflate them: treating an
        /// unreadable store as "this account has nothing" is what turns a disk hiccup into a wiped character.
        ///
        /// One account can legitimately own more than one folder - the id a save was written under has come
        /// from a couple of different sources over the mod's life - so this unions across every matching
        /// folder rather than taking the first hit.
        /// </summary>
        internal static List<string> NamesForAccount(string accountId) {
            if (string.IsNullOrEmpty(accountId)) { return new List<string>(); }
            try {
                List<string> found = new List<string>();
                if (ValConfig.InternalStorageMode.Value) {
                    // An empty registry deserializes to null, and iterating that would throw straight into the
                    // catch below - which reports "could not read", not "nothing stored". On a fresh server
                    // that is every connect, and it would quietly disable first-save enforcement entirely.
                    Dictionary<string, List<string>> registry = InternalDataStore.GetAccountRegistry() ?? new Dictionary<string, List<string>>();
                    foreach (KeyValuePair<string, List<string>> account in registry) {
                        if (!PlatformIds.Matches(account.Key, accountId)) { continue; }
                        if (account.Value != null) { found.AddRange(account.Value); }
                    }
                    return Dedupe(found);
                }

                string root = ValConfig.CharacterFilePath;
                if (!Directory.Exists(root)) { return new List<string>(); }
                // Same rule as TryResolveOnDisk: names may come from the remembered listing, "this account has
                // nothing" may not - that answer is what lets a character through the account limit.
                FolderListing before = folderListing;
                CollectNames(AccountFolders(root, fresh: false), root, accountId, found);
                if (found.Count == 0 && before != null && ReferenceEquals(before, folderListing)) {
                    CollectNames(AccountFolders(root, fresh: true), root, accountId, found);
                }
                return Dedupe(found);
            } catch (Exception e) {
                Logger.LogWarning($"Could not read stored characters for {accountId}: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Resolves the exact account-id and character-name spelling a save is filed under, and reports
        /// whether one exists at all.
        ///
        /// Both halves have to come back as the spelling actually on disk, not the spelling that was asked
        /// about. Most servers run Linux, where the filesystem is case sensitive: an account folder found by a
        /// case-insensitive match, or a character matched as "testragnar" when the file says "Testragnar.yaml",
        /// will fail every subsequent File.Exists that reuses the caller's spelling. The account id has the
        /// same problem in a different form - the same account has been written as both "7656..." and
        /// "Steam_7656..." over the mod's life (see <see cref="PlatformIds"/>).
        ///
        /// <paramref name="lookupFailed"/> is the output that matters most. A false return with it set means
        /// "I could not tell", never "there is nothing here". Callers that would otherwise conclude the
        /// character is brand new - and therefore strip it - must fail open on it.
        /// </summary>
        internal static bool TryResolveSave(string accountId, string characterName,
                                            out string resolvedAccountId, out string resolvedName,
                                            out bool lookupFailed) {
            resolvedAccountId = accountId;
            resolvedName = characterName;
            lookupFailed = false;
            if (string.IsNullOrEmpty(accountId) || string.IsNullOrEmpty(characterName)) {
                // Not "this account has no such character" - we were not given enough to look anything up.
                // The caller turns a plain false into "brand new here" and strips the character, so an
                // unanswerable question has to be reported as unanswerable.
                lookupFailed = true;
                return false;
            }

            try {
                if (ValConfig.InternalStorageMode.Value) {
                    // No folders here; the registry is the index and the id stays as given.
                    Dictionary<string, List<string>> registry = InternalDataStore.GetAccountRegistry() ?? new Dictionary<string, List<string>>();
                    foreach (KeyValuePair<string, List<string>> account in registry) {
                        if (!PlatformIds.Matches(account.Key, accountId) || account.Value == null) { continue; }
                        string registered = account.Value.FirstOrDefault(n => string.Equals(n, characterName, StringComparison.OrdinalIgnoreCase));
                        if (registered == null) { continue; }
                        resolvedAccountId = account.Key;
                        resolvedName = registered;
                        return true;
                    }
                    return false;
                }

                return TryResolveOnDisk(accountId, characterName, out resolvedAccountId, out resolvedName);
            } catch (Exception e) {
                Logger.LogWarning($"Could not resolve a stored character for {accountId}/{characterName}: {e.Message}");
                lookupFailed = true;
                return false;
            }
        }

        private static List<string> Dedupe(List<string> names) {
            return names.Where(name => !string.IsNullOrEmpty(name))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                        .ToList();
        }
    }
}
