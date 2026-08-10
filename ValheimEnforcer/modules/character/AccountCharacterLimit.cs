using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.character {

    /// <summary>
    /// Server-side policy for the one-character-per-account rule: decides whether an account may join with
    /// a given character. The Harmony gate that applies the decision lives in CharacterLimitPatches.
    ///
    /// The set of characters an account "has" is read from the character saves the mod already keeps
    /// (Characters/&lt;accountId&gt;/&lt;Name&gt;.yaml on disk, or the InternalDataStore account registry in
    /// InternalStorageMode) rather than a separate binding registry. Two things follow from that, both
    /// deliberate: an account that already had several characters when the rule was switched on keeps all
    /// of them and is only stopped from adding another, so enabling the setting locks nobody out; and
    /// freeing a slot is just deleting that character's save, which is what a character reset already means.
    ///
    /// Every failure path here allows the connection. A server that cannot read its own character folder
    /// should let players in and log about it, not refuse the entire playerbase.
    /// </summary>
    internal static class AccountCharacterLimit {

        internal static bool Enabled {
            get { return ValConfig.EnforceCharacterLimit != null && ValConfig.EnforceCharacterLimit.Value; }
        }

        /// <summary>
        /// Null when the join is allowed. Otherwise the player-facing reason it was refused, which is sent
        /// to the client so the connection-failed panel can name the character they should be using.
        /// </summary>
        internal static string EvaluateJoin(string hostId, string playerName) {
            if (!Enabled) { return null; }
            if (string.IsNullOrEmpty(hostId) || string.IsNullOrEmpty(playerName)) { return null; }

            if (IsExempt(hostId)) {
                Logger.LogDebug($"Character limit: {hostId} is exempt, allowing '{playerName}'.");
                return null;
            }

            List<string> known = GetKnownCharacterNames(hostId);
            if (known == null) { return null; } // lookup failed; already logged, allow the join

            if (known.Any(name => string.Equals(name, playerName, StringComparison.OrdinalIgnoreCase))) {
                Logger.LogDebug($"Character limit: '{playerName}' is a known character for {hostId}.");
                return null;
            }

            int limit = ValConfig.MaxCharactersPerAccount.Value;
            if (known.Count < limit) {
                Logger.LogDebug($"Character limit: {hostId} has {known.Count}/{limit} character(s), allowing new character '{playerName}'.");
                return null;
            }

            return BuildRejectionMessage(known, limit);
        }

        /// <summary>
        /// Accounts allowed any number of characters. The id list is independent of admin status - an
        /// exempt account need not be an admin, and an admin is only exempt when CharacterLimitExemptAdmins
        /// is enabled.
        /// </summary>
        internal static bool IsExempt(string hostId) {
            foreach (string exempt in ExemptIds()) {
                if (PlatformIds.Matches(exempt, hostId)) { return true; }
            }
            return ValConfig.CharacterLimitExemptAdmins.Value
                && ZNet.instance != null
                && ZNet.instance.IsAdmin(hostId);
        }

        /// <summary>
        /// Every character name this account already has a save for. Empty when it has none (a brand new
        /// player), null when the stored characters could not be read at all - the caller treats those
        /// differently, since only the first is a real answer.
        /// </summary>
        internal static List<string> GetKnownCharacterNames(string accountId) {
            try {
                if (ValConfig.InternalStorageMode.Value) {
                    List<string> registered = new List<string>();
                    foreach (KeyValuePair<string, List<string>> account in InternalDataStore.GetAccountRegistry()) {
                        if (!PlatformIds.Matches(account.Key, accountId)) { continue; }
                        if (account.Value != null) { registered.AddRange(account.Value); }
                    }
                    return Dedupe(registered);
                }

                string root = ValConfig.CharacterFilePath;
                if (!Directory.Exists(root)) { return new List<string>(); }

                // Union across every matching folder rather than taking the first: the id a save was written
                // under has come from a couple of different sources over time, so one account can legitimately
                // own both a "7656..." and a "Steam_7656..." folder.
                List<string> found = new List<string>();
                foreach (string accountFolder in Directory.GetDirectories(root)) {
                    if (!PlatformIds.Matches(Path.GetFileName(accountFolder), accountId)) { continue; }
                    foreach (string characterFile in Directory.GetFiles(accountFolder, "*.yaml")) {
                        found.Add(Path.GetFileNameWithoutExtension(characterFile));
                    }
                }
                return Dedupe(found);
            } catch (Exception e) {
                Logger.LogWarning($"Character limit: could not read stored characters for {accountId} ({e.Message}). Allowing the connection.");
                return null;
            }
        }

        private static string BuildRejectionMessage(List<string> known, int limit) {
            if (limit == 1 && known.Count == 1) {
                return $"This server allows one character per account.\n"
                     + $"You are already playing here as '{known[0]}' - rejoin with that character.\n"
                     + $"Ask a server admin if you need your character reset.";
            }
            return $"This server allows {limit} character(s) per account.\n"
                 + $"You already have: {string.Join(", ", known.ToArray())}.\n"
                 + $"Rejoin with one of those, or ask a server admin if you need one reset.";
        }

        // EvaluateJoin runs once per connect, so the parsed list is cached and only rebuilt when the
        // setting actually changes - the same shape as CheatToolCatalog.IgnoreList().
        private static string exemptRaw;
        private static List<string> exemptParsed = new List<string>();

        private static List<string> ExemptIds() {
            string raw = ValConfig.CharacterLimitExemptAccounts.Value ?? "";
            if (raw != exemptRaw) {
                exemptParsed = raw.Split(',')
                    .Select(entry => entry.Trim())
                    .Where(entry => entry.Length > 0)
                    .ToList();
                exemptRaw = raw;
            }
            return exemptParsed;
        }

        private static List<string> Dedupe(List<string> names) {
            return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
