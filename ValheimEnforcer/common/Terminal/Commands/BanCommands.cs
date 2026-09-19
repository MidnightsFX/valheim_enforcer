using System;
using System.Collections.Generic;
using System.Linq;
using ValheimEnforcer.modules.bannetwork;
using ValheimEnforcer.modules.character;

namespace ValheimEnforcer.common {

    internal static partial class TerminalManager {

        private const string BanUsage = "Format: <accountId | characterName> <category[,category]> <reason> [--for <duration>] [--share|--no-share]. "
                                      + "Categories: cheating, rulebreaking, griefing, toxic. Duration like 30m, 12h, 7d. "
                                      + "eg: enforcer-ban 76561198012345678 griefing,toxic Burned the communal base --for 7d";

        private static void RegisterBanCommands() {
            _ = new EnforcerCommand("enforcer-ban",
                "Bans a player by account id or by the character name they are currently playing, recording what it was for. " + BanUsage,
                BanPlayer, CommandArea.Bans, BanOptions,
                serverAuthoritative: true, requiresAdmin: true,
                aliases: "Enforcer-Ban-Add");

            _ = new EnforcerCommand("enforcer-unban",
                "Lifts a ban this server issued, removing it from both this mod's list and Valheim's. Format: <accountId | characterName>. eg: enforcer-unban 76561198012345678",
                UnbanPlayer, CommandArea.Bans, BannedAccounts,
                serverAuthoritative: true, requiresAdmin: true);

            _ = new EnforcerCommand("enforcer-ban-list",
                "Lists bans. Format: [local|network|all] [category] - either argument, either order. 'local' is this server's own bans, 'network' is what the ban network has sent, 'all' is both (the default). eg: enforcer-ban-list network cheating",
                BanList, CommandArea.Bans, BanListOptions,
                serverAuthoritative: true, requiresAdmin: true);

            _ = new EnforcerCommand("enforcer-ban-check",
                "Explains what this server makes of one account: which rule would refuse them, or why nothing does. The command to run when a ban is not behaving as expected. Format: <accountId | characterName>. eg: enforcer-ban-check 76561198012345678",
                BanCheck, CommandArea.Bans, BannedAccounts,
                serverAuthoritative: true, requiresAdmin: true);

            _ = new EnforcerCommand("enforcer-ban-allow",
                "Lets a player in despite a ban, and records why. Format: <accountId | subjectHash> <reason>. eg: enforcer-ban-allow 76561198012345678 Appeal accepted",
                BanAllow, CommandArea.Bans, BannedAccounts,
                serverAuthoritative: true, requiresAdmin: true);

            _ = new EnforcerCommand("enforcer-ban-deny",
                "Refuses a player regardless of what any list says. Format: <accountId | subjectHash> <reason>. eg: enforcer-ban-deny 76561198012345678 Not welcome here",
                BanDeny, CommandArea.Bans, BannedAccounts,
                serverAuthoritative: true, requiresAdmin: true);

            _ = new EnforcerCommand("enforcer-ban-override-clear",
                "Drops an allow or deny override, putting the player back under the normal rules. Format: <accountId | subjectHash>. eg: enforcer-ban-override-clear 76561198012345678",
                BanOverrideClear, CommandArea.Bans, OverriddenSubjects,
                serverAuthoritative: true, requiresAdmin: true);
        }

        // ---- Handlers -------------------------------------------------------------------------------------

        private static void BanPlayer(EnforcerCommandArgs args) {
            if (!ResolveTarget(args, 0, BanUsage, out string hostId, out string playerName, out string how)) { return; }

            string rawCategories = args.Args.GetString(1, null);
            if (string.IsNullOrEmpty(rawCategories)) {
                args.Output.Error($"A category is required, so the ban says what it was for. {BanUsage}");
                return;
            }
            if (!BanCategories.TryParseList(rawCategories, out List<BanCategory> categories, out string invalid)) {
                args.Output.Error($"'{rawCategories}' contains no valid category. Valid: {string.Join(", ", BanCategories.Names().ToArray())}. {BanUsage}");
                return;
            }
            if (invalid != null) {
                args.Output.Warning($"Ignoring unknown categor(ies): {invalid}. Banning for {BanCategories.Canonical(categories)}.");
            }

            // Flags are pulled out of the tail first; whatever is left is the reason. Without this a
            // "--for 7d" typed at the end would silently become part of the reason text and the ban would
            // be permanent - a failure that looks exactly like success.
            if (!ReadBanFlags(args, 2, out DateTime? expires, out bool? shareOverride, out string reason, out string flagError)) {
                args.Output.Error($"{flagError} {BanUsage}");
                return;
            }
            if (string.IsNullOrEmpty(reason)) {
                args.Output.Error($"A reason is required. It is what the player is shown and what other admins read later. {BanUsage}");
                return;
            }

            // The amplification case a machine cannot judge. If the network already lists this player, an
            // admin banning them here may be acting on their own evidence - or may simply be echoing what
            // the network told them, which would inflate the reporter count with no new information behind
            // it. So the default flips to not publishing, and saying so, rather than guessing either way.
            bool alreadyOnNetwork = false;
            if (ValConfig.EnableBanNetwork != null && ValConfig.EnableBanNetwork.Value) {
                string subject = BanSubjectCache.Hash(hostId);
                alreadyOnNetwork = subject != null && BanNetworkStore.Find(subject) != null;
            }
            bool share = shareOverride ?? !alreadyOnNetwork;

            string issuedBy = IssuedBy(args);
            BanRecord record = new BanRecord {
                Id = hostId,
                Name = playerName,
                Categories = categories,
                Reason = reason,
                AddedBy = string.IsNullOrEmpty(issuedBy) ? "console" : issuedBy,
                Source = BanSources.Local,
                ExpiresUtc = expires,
                Share = share,
            };
            bool isNew = BanStore.Add(record);

            try {
                ZNet.instance?.Ban(hostId);
            } catch (Exception e) {
                args.Output.Warning($"The ban is recorded, but Valheim's own ban list refused it: {e.Message}");
            }

            string window = expires.HasValue ? $" until {BanTime.Stamp(expires.Value)} UTC" : "";
            args.Output.Info($"{(isNew ? "Banned" : "Updated the ban for")} {Describe(hostId, playerName)} ({how}) "
                           + $"for {BanCategories.Canonical(categories)}{window}: {reason}");

            if (KickIfOnline(hostId)) { args.Output.Detail("They were connected and have been disconnected.", log: false); }

            if (alreadyOnNetwork && shareOverride == null) {
                args.Output.Warning("The ban network already lists this player, so this ban was NOT published - otherwise this "
                                  + "server would be counted as a second, independent report when it may just be agreeing with "
                                  + "the first. Add --share if you caught them yourself.");
            }
            BanStore.OfferToNetwork(record);
        }

        /// <summary>
        /// Reads the optional flags off the end of an enforcer-ban line and returns the rest as the reason.
        /// </summary>
        private static bool ReadBanFlags(EnforcerCommandArgs args, int from,
                                         out DateTime? expires, out bool? share, out string reason, out string error) {
            expires = null;
            share = null;
            reason = null;
            error = null;

            List<string> words = new List<string>();
            for (int i = from; i < args.Length; i++) {
                string token = args.Args.GetString(i, null);
                if (string.IsNullOrEmpty(token)) { continue; }

                if (string.Equals(token, "--share", StringComparison.OrdinalIgnoreCase)) { share = true; continue; }
                if (string.Equals(token, "--no-share", StringComparison.OrdinalIgnoreCase)) { share = false; continue; }
                if (string.Equals(token, "--for", StringComparison.OrdinalIgnoreCase)) {
                    string duration = args.Args.GetString(++i, null);
                    if (!TryParseDuration(duration, out TimeSpan span)) {
                        error = $"'{duration}' is not a duration - use something like 30m, 12h or 7d.";
                        return false;
                    }
                    expires = DateTime.UtcNow.Add(span);
                    continue;
                }
                words.Add(token);
            }

            reason = string.Join(" ", words.ToArray()).Trim();
            return true;
        }

        /// <summary>Parses 30m / 12h / 7d / 4w. Deliberately small: these are the units bans come in.</summary>
        private static bool TryParseDuration(string value, out TimeSpan span) {
            span = TimeSpan.Zero;
            if (string.IsNullOrEmpty(value) || value.Length < 2) { return false; }

            char unit = char.ToLowerInvariant(value[value.Length - 1]);
            if (!int.TryParse(value.Substring(0, value.Length - 1), out int amount) || amount <= 0) { return false; }

            switch (unit) {
                case 'm': span = TimeSpan.FromMinutes(amount); return true;
                case 'h': span = TimeSpan.FromHours(amount); return true;
                case 'd': span = TimeSpan.FromDays(amount); return true;
                case 'w': span = TimeSpan.FromDays(amount * 7); return true;
                default: return false;
            }
        }

        private static void UnbanPlayer(EnforcerCommandArgs args) {
            string token = args.Args.GetString(0, null);
            if (string.IsNullOrEmpty(token)) {
                args.Output.Error("An account id or character name is required. eg: enforcer-unban 76561198012345678");
                return;
            }
            // Deliberately not ResolveTarget: an unban usually targets someone who is not connected, and the
            // id typed is the one in the list. Resolution still runs, but a miss is not fatal here.
            string hostId = ResolveQuietly(token) ?? token;

            int removed = ValConfig.UnbanHost(hostId);
            if (removed == 0) {
                args.Output.Warning($"{hostId} was not in this server's ban list. Valheim's own ban list was cleared of them anyway, in case the ban was set there directly.");
                return;
            }
            args.Output.Info($"Unbanned {hostId} ({removed} entr{(removed == 1 ? "y" : "ies")} removed).");

            OverrideRecord over = BanOverrides.Find(hostId, null);
            if (over != null && !over.Allow) {
                args.Output.Warning($"Note: a deny override still refuses them. Run enforcer-ban-override-clear {hostId} to lift it.");
            }
        }

        private static void BanList(EnforcerCommandArgs args) {
            bool wantLocal = true;
            bool wantNetwork = true;
            bool filtered = false;
            BanCategory category = BanCategory.Cheating;

            // Both arguments are optional and neither has a fixed position: an admin who types the scope
            // and the category in the other order should get their list, not a usage message.
            for (int i = 0; i < args.Length; i++) {
                string token = args.Args.GetString(i, null);
                if (string.IsNullOrEmpty(token)) { continue; }
                switch (token.ToLowerInvariant()) {
                    case "local":   wantLocal = true;  wantNetwork = false; continue;
                    case "network": wantLocal = false; wantNetwork = true;  continue;
                    case "all":     wantLocal = true;  wantNetwork = true;  continue;
                }
                if (BanCategories.TryParse(token, out category)) { filtered = true; continue; }
                args.Output.Error($"'{token}' is neither a scope (local, network, all) nor a category ({string.Join(", ", BanCategories.Names().ToArray())}).");
                return;
            }

            if (wantLocal) { ListLocal(args, filtered, category); }
            if (wantNetwork) { ListNetwork(args, filtered, category); }
            ListOverrides(args);
        }

        private static void ListLocal(EnforcerCommandArgs args, bool filtered, BanCategory category) {
            List<BanRecord> bans = BanStore.All();
            if (bans.Count == 0) {
                args.Output.Info("This server has not banned anyone itself.");
                return;
            }

            int shown = 0;
            args.Output.Info($"Local bans ({BanStore.FileName}):", log: false);
            foreach (BanRecord record in bans) {
                if (filtered && !record.Categories.Contains(category)) { continue; }
                shown++;
                string flags = "";
                if (record.Expired) { flags += " [expired]"; }
                if (!record.Reportable) { flags += " [not shared]"; }
                OverrideRecord over = BanOverrides.Find(record.Id, null);
                if (over != null && over.Allow) { flags += " [overridden: allowed in]"; }
                args.Output.Detail($"  {record.Id}{(string.IsNullOrEmpty(record.Name) ? "" : $" ({record.Name})")} - {record.Describe()} - source: {record.Source}{flags}", log: false);
            }
            args.Output.Info($"{shown} of {bans.Count} local ban(s){(filtered ? $" carrying '{BanCategories.Name(category)}'" : "")}.");
        }

        /// <summary>
        /// Network entries, with every id this server can put a name to resolved.
        ///
        /// A bare digest is close to useless to a human deciding whether to override an entry, so anything
        /// the subject index can resolve is shown as an id; the rest are shown as the hash and labelled, and
        /// that hash is what enforcer-ban-allow takes.
        /// </summary>
        private static void ListNetwork(EnforcerCommandArgs args, bool filtered, BanCategory category) {
            if (ValConfig.EnableBanNetwork == null || !ValConfig.EnableBanNetwork.Value) {
                args.Output.Detail("The ban network is off, so there are no network entries.", log: false);
                return;
            }

            List<NetworkBanRecord> entries = BanNetworkStore.All();
            if (entries.Count == 0) {
                args.Output.Info("The ban network has sent no entries yet. enforcer-ban-network-status says why if that is unexpected.");
                return;
            }

            List<BanCategory> enforced = BanNetworkScheduler.EnforcedCategories();
            int minReporters = ValConfig.BanNetworkMinReporters.Value;

            int shown = 0;
            int advisory = 0;
            args.Output.Info($"Ban network entries ({BanNetworkStore.FileName}):", log: false);
            foreach (NetworkBanRecord record in entries) {
                if (filtered && !record.Categories.Contains(category)) { continue; }
                shown++;

                string who = BanSubjectCache.Resolve(record.Subject);
                string label = who != null
                    ? $"{who}{(string.IsNullOrEmpty(record.Name) ? "" : $" ({record.Name})")}"
                    : $"{record.Subject} (not known to this server{(string.IsNullOrEmpty(record.Name) ? "" : $"; last seen as {record.Name}")})";

                string flags = "";
                if (!record.Enforceable(enforced, minReporters)) { flags += " [advisory only]"; advisory++; }
                OverrideRecord over = BanOverrides.Find(who, record.Subject);
                if (over != null) { flags += over.Allow ? " [overridden: allowed in]" : " [overridden: refused]"; }

                args.Output.Detail($"  {label} - {record.Describe()}{flags}", log: false);
            }
            args.Output.Info($"{shown} of {entries.Count} network entr(ies){(filtered ? $" carrying '{BanCategories.Name(category)}'" : "")}"
                           + (advisory > 0 ? $"; {advisory} recorded but not enforced here." : "."));
        }

        private static void ListOverrides(EnforcerCommandArgs args) {
            List<OverrideRecord> overrides = BanOverrides.All();
            if (overrides.Count == 0) { return; }
            args.Output.Info($"Overrides ({BanOverrides.FileName}):", log: false);
            foreach (OverrideRecord over in overrides) {
                string scope = over.Categories.Count == 0 ? "all categories" : BanCategories.Canonical(over.Categories);
                args.Output.Detail($"  {over.Subject} - {(over.Allow ? "allow" : "deny")} ({scope}) - {over.Reason ?? "no reason recorded"}", log: false);
            }
        }

        /// <summary>
        /// Answers "what does this server make of this account, and why?".
        ///
        /// Takes either an account id, a character name, or a subject hash copied out of a network entry -
        /// the last of which is the case that matters, because a pulled entry is all an owner has when they
        /// are deciding whether to override it. Prints the layer that decided, so a ban behaving unexpectedly
        /// is a question with an answer rather than a guess.
        /// </summary>
        private static void BanCheck(EnforcerCommandArgs args) {
            string token = args.Args.GetString(0, null);
            if (string.IsNullOrEmpty(token)) {
                args.Output.Error("An account id, character name or subject hash is required. eg: enforcer-ban-check 76561198012345678");
                return;
            }

            string hostId;
            string subjectHash;
            if (BanSubject.LooksLikeHash(token)) {
                subjectHash = token.ToLowerInvariant();
                hostId = BanSubjectCache.Resolve(subjectHash);
                if (hostId == null) {
                    args.Output.Info($"{subjectHash}: this server has no record of whose account that is.");
                    args.Output.Detail("  Only ids this server has seen - its saves, its bans, its admins, anyone connected - can be "
                                     + "resolved back from a hash. That is the point of the hashing, not a fault.", log: false);
                    // Still worth evaluating: an override can be written against a bare hash, so one may match.
                    Report(args, subjectHash, BanPolicy.Evaluate(subjectHash, subjectHash));
                    return;
                }
                args.Output.Detail($"  {subjectHash} is {hostId} on this server.", log: false);
            } else {
                hostId = ResolveQuietly(token) ?? token;
                subjectHash = ValConfig.EnableBanNetwork != null && ValConfig.EnableBanNetwork.Value
                    ? BanSubjectCache.Hash(hostId)
                    : null;
            }

            Report(args, hostId, BanPolicy.Evaluate(hostId, subjectHash));

            if (subjectHash != null) {
                args.Output.Detail($"  Ban network subject: {subjectHash}", log: false);
            } else if (ValConfig.EnableBanNetwork == null || !ValConfig.EnableBanNetwork.Value) {
                args.Output.Detail("  The ban network is off, so only this server's own lists were consulted.", log: false);
            } else if (!BanSubject.Usable) {
                args.Output.Detail("  Subject hashing is unavailable on this runtime, so the ban network layer was skipped. "
                                 + "See the startup log for the self test failure.", log: false);
            }
        }

        private static void Report(EnforcerCommandArgs args, string subject, BanVerdict verdict) {
            string outcome = verdict.Reject ? "REFUSED"
                           : verdict.Advisory ? "ALLOWED (listed by the network, but not under a category this server enforces)"
                           : "ALLOWED";
            args.Output.Info($"{subject}: {outcome}");
            args.Output.Detail($"  {verdict.Explanation}", log: false);
            if (verdict.Reject) {
                args.Output.Detail($"  They are shown: {verdict.Reason}", log: false);
            }
        }

        private static void BanAllow(EnforcerCommandArgs args) {
            SetOverride(args, allow: true);
        }

        private static void BanDeny(EnforcerCommandArgs args) {
            SetOverride(args, allow: false);
        }

        private static void SetOverride(EnforcerCommandArgs args, bool allow) {
            string verb = allow ? "enforcer-ban-allow" : "enforcer-ban-deny";
            string token = args.Args.GetString(0, null);
            if (string.IsNullOrEmpty(token)) {
                args.Output.Error($"An account id or subject hash is required. eg: {verb} 76561198012345678 Appeal accepted");
                return;
            }
            string reason = args.Args.GetStringFrom(1, null);
            if (string.IsNullOrEmpty(reason)) {
                args.Output.Error($"A reason is required - an override with no note is unreadable a month later. eg: {verb} {token} Appeal accepted");
                return;
            }

            // A subject hash is 32 hex characters; anything else is treated as a platform id. Recording it
            // under the right field is what lets the policy match a pulled entry this server has never seen.
            bool isHash = BanSubject.LooksLikeHash(token);
            string resolved = isHash ? null : (ResolveQuietly(token) ?? token);
            string issuedBy = IssuedBy(args);

            BanOverrides.Set(new OverrideRecord {
                Id = isHash ? null : resolved,
                Hash = isHash ? token.ToLowerInvariant() : null,
                Allow = allow,
                Reason = reason,
                AddedBy = string.IsNullOrEmpty(issuedBy) ? "console" : issuedBy,
            });

            args.Output.Info($"{(allow ? "Allowing" : "Refusing")} {(isHash ? token : resolved)} regardless of the ban lists: {reason}");
            if (!allow) {
                if (KickIfOnline(resolved)) { args.Output.Detail("They were connected and have been disconnected.", log: false); }
            } else if (!isHash) {
                BanRecord local = BanStore.Find(resolved);
                if (local != null) {
                    args.Output.Warning($"Note: a {local.Source} ban is still on record for them ({local.Describe()}). "
                                      + $"The override lets them in, but enforcer-unban {resolved} is what removes the ban itself.");
                }
            }
        }

        private static void BanOverrideClear(EnforcerCommandArgs args) {
            string token = args.Args.GetString(0, null);
            if (string.IsNullOrEmpty(token)) {
                args.Output.Error("An account id or subject hash is required. eg: enforcer-ban-override-clear 76561198012345678");
                return;
            }
            string subject = BanSubject.LooksLikeHash(token) ? token.ToLowerInvariant() : (ResolveQuietly(token) ?? token);
            int removed = BanOverrides.Clear(subject);
            if (removed == 0) {
                args.Output.Warning($"No override was set for {subject}.");
                return;
            }
            args.Output.Info($"Cleared the override for {subject}. They are back under the normal ban rules.");

            BanVerdict verdict = BanPolicy.Evaluate(subject);
            if (verdict.Reject) {
                args.Output.Detail($"  They are now refused: {verdict.Explanation}", log: false);
                if (KickIfOnline(subject)) { args.Output.Detail("  They were connected and have been disconnected.", log: false); }
            }
        }

        // ---- Shared ---------------------------------------------------------------------------------------

        /// <summary>
        /// Turns what the admin typed into an account id, accepting either the id itself or the name of a
        /// character. Reports how it resolved, because "I banned a name and it hit someone else" is the one
        /// mistake this command must not make quietly - names are not unique and not owned.
        /// </summary>
        private static bool ResolveTarget(EnforcerCommandArgs args, int index, string usage, out string hostId, out string playerName, out string how) {
            hostId = null;
            playerName = null;
            how = null;

            string token = args.Args.GetString(index, null);
            if (string.IsNullOrEmpty(token)) {
                args.Output.Error($"An account id or character name is required. {usage}");
                return false;
            }

            List<ZNetPeer> online = OnlinePeers();

            // An exact id match on a connected peer is unambiguous, so it wins outright.
            foreach (ZNetPeer peer in online) {
                string account = PeerIdentity.AccountFor(peer);
                if (!PlatformIds.Matches(account, token)) { continue; }
                hostId = account;
                playerName = peer.m_playerName;
                how = "connected now";
                return true;
            }

            List<ZNetPeer> byName = online
                .Where(peer => string.Equals(peer.m_playerName, token, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (byName.Count > 1) {
                args.Output.Error($"'{token}' is the name of {byName.Count} connected characters, so it does not identify one account. "
                                + "Use the account id - enforcer-ban-check on each, or enforcer-player-list, will show you which is which.");
                return false;
            }
            if (byName.Count == 1) {
                hostId = PeerIdentity.AccountFor(byName[0]);
                playerName = byName[0].m_playerName;
                if (string.IsNullOrEmpty(hostId)) {
                    args.Output.Error($"'{token}' is connected but this server could not resolve their account id, so there is nothing to ban. Try again in a moment.");
                    return false;
                }
                how = $"connected now as {playerName}";
                return true;
            }

            // Nobody online. Fall back to saves, which is how an offline griefer gets banned.
            try {
                foreach (string account in CharacterSaves.Accounts()) {
                    foreach (string character in CharacterSaves.CharactersFor(account)) {
                        if (!string.Equals(character, token, StringComparison.OrdinalIgnoreCase)) { continue; }
                        hostId = account;
                        playerName = character;
                        how = $"offline, from the save for {character}";
                        return true;
                    }
                }
            } catch (Exception e) {
                Logger.LogDebug($"Ban target lookup could not read the character saves: {e.Message}");
            }

            // Not a known name: take it as an id. Banning an id nobody has used yet is legitimate - it is how
            // you act on a report before the player has ever connected.
            hostId = token;
            how = "taken as an account id; this server has no record of them";
            return true;
        }

        /// <summary>Resolution without the reporting, for commands where a miss is not an error.</summary>
        private static string ResolveQuietly(string token) {
            if (string.IsNullOrEmpty(token)) { return null; }
            if (BanSubject.LooksLikeHash(token)) {
                string resolved = BanSubjectCache.Resolve(token);
                if (resolved != null) { return resolved; }
            }
            try {
                foreach (ZNetPeer peer in OnlinePeers()) {
                    string account = PeerIdentity.AccountFor(peer);
                    if (PlatformIds.Matches(account, token)) { return account; }
                    if (string.Equals(peer.m_playerName, token, StringComparison.OrdinalIgnoreCase)) { return account; }
                }
                foreach (BanRecord record in BanStore.All()) {
                    if (PlatformIds.Matches(record.Id, token)) { return record.Id; }
                    if (!string.IsNullOrEmpty(record.Name) && string.Equals(record.Name, token, StringComparison.OrdinalIgnoreCase)) { return record.Id; }
                }
                foreach (string account in CharacterSaves.Accounts()) {
                    if (PlatformIds.Matches(account, token)) { return account; }
                    foreach (string character in CharacterSaves.CharactersFor(account)) {
                        if (string.Equals(character, token, StringComparison.OrdinalIgnoreCase)) { return account; }
                    }
                }
            } catch (Exception e) {
                Logger.LogDebug($"Quiet ban lookup failed for '{token}': {e.Message}");
            }
            return null;
        }

        private static List<ZNetPeer> OnlinePeers() {
            List<ZNetPeer> peers = new List<ZNetPeer>();
            try {
                if (ZNet.instance == null) { return peers; }
                foreach (ZNetPeer peer in ZNet.instance.GetPeers()) {
                    if (peer != null && peer.m_socket != null) { peers.Add(peer); }
                }
            } catch (Exception e) {
                Logger.LogDebug($"Could not read the peer list: {e.Message}");
            }
            return peers;
        }

        /// <summary>
        /// Disconnects a banned player who is already in the world. A ban only bites at the handshake, so
        /// without this the person you just banned keeps playing until they choose to leave.
        /// </summary>
        private static bool KickIfOnline(string hostId) {
            if (string.IsNullOrEmpty(hostId)) { return false; }
            try {
                foreach (ZNetPeer peer in OnlinePeers()) {
                    if (!PlatformIds.Matches(PeerIdentity.AccountFor(peer), hostId)) { continue; }
                    ZNet.instance.Kick(hostId);
                    return true;
                }
            } catch (Exception e) {
                Logger.LogWarning($"Could not disconnect {hostId} after banning them: {e.Message}");
            }
            return false;
        }

        /// <summary>
        /// Who to record as the issuer. The id comes from the socket the command arrived on, never from
        /// anything the caller sent, for the same reason every other identity in this mod does.
        /// </summary>
        private static string IssuedBy(EnforcerCommandArgs args) {
            if (!args.FromNetwork) { return "console"; }
            try {
                ZNetPeer peer = ZNet.instance?.GetPeer(args.Sender);
                string host = peer != null && peer.m_socket != null ? peer.m_socket.GetHostName() : null;
                return string.IsNullOrEmpty(host) ? "console" : host;
            } catch (Exception e) {
                Logger.LogDebug($"Could not resolve the issuing admin: {e.Message}");
                return "console";
            }
        }

        private static string Describe(string hostId, string playerName) {
            return string.IsNullOrEmpty(playerName) ? hostId : $"{playerName} ({hostId})";
        }

        // ---- Tab completion -------------------------------------------------------------------------------

        private static List<string> BanOptions(string[] input) {
            if (input.Length == 2) { return BanTargets(); }
            if (input.Length == 3) { return BanCategories.Names(); }
            // Past the reason, offer the flags rather than nothing - they are easy to forget and there is
            // no other way to discover them without reading the help text.
            return new List<string> { "--for", "--share", "--no-share" };
        }

        private static List<string> BanListOptions(string[] input) {
            if (input.Length == 2) {
                List<string> options = new List<string> { "all", "local", "network" };
                options.AddRange(BanCategories.Names());
                return options;
            }
            return input.Length == 3 ? BanCategories.Names() : new List<string>();
        }

        /// <summary>Connected characters first - the usual target - then accounts with a save.</summary>
        private static List<string> BanTargets() {
            List<string> options = new List<string>();
            try {
                foreach (ZNetPeer peer in OnlinePeers()) {
                    if (!string.IsNullOrEmpty(peer.m_playerName)) { options.Add(peer.m_playerName); }
                }
                options.AddRange(CharacterSaves.Accounts());
            } catch (Exception) {
                // Completion is a convenience; failing to build it must never stop the command being typed.
            }
            return options.Distinct().ToList();
        }

        private static List<string> BannedAccounts(string[] input) {
            if (input.Length > 2) { return new List<string>(); }
            List<string> options = new List<string>();
            try {
                foreach (BanRecord record in BanStore.All()) { options.Add(record.Id); }
                options.AddRange(BanTargets());
            } catch (Exception) {
            }
            return options.Distinct().ToList();
        }

        private static List<string> OverriddenSubjects(string[] input) {
            if (input.Length > 2) { return new List<string>(); }
            List<string> options = new List<string>();
            try {
                foreach (OverrideRecord over in BanOverrides.All()) { options.Add(over.Subject); }
            } catch (Exception) {
            }
            return options;
        }
    }
}
