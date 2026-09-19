using System.Collections.Generic;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.bannetwork {

    /// <summary>
    /// What the policy decided about one account, and why it decided it.
    ///
    /// The explanation is not decoration. A shared ban list that refuses a player for a reason nobody can
    /// reconstruct is the thing that makes owners distrust the whole feature, so every verdict carries the
    /// layer that produced it and enforcer-ban-check prints it back verbatim.
    /// </summary>
    internal sealed class BanVerdict {

        internal bool Reject;
        /// <summary>Shown to the refused player and written to the log. Never null when Reject is true.</summary>
        internal string Reason;
        /// <summary>local | auto | builtin | legacy | network | override, or null when nothing matched.</summary>
        internal string Source;
        internal List<BanCategory> Categories = new List<BanCategory>();
        /// <summary>Matched a network entry, but under a category this server does not auto-enforce.</summary>
        internal bool Advisory = false;
        /// <summary>Which layer decided, in words, for enforcer-ban-check and the log.</summary>
        internal string Explanation;

        internal static BanVerdict Allow(string explanation) {
            return new BanVerdict { Reject = false, Explanation = explanation };
        }
    }

    /// <summary>
    /// The single place that answers "may this account join?".
    ///
    /// Everything that used to be scattered - the known-cheater list, and from Phase 4 the pulled network list
    /// - resolves here, in one order, so the join gate, the mid-session re-check and enforcer-ban-check can
    /// never disagree about what this server thinks of someone. That they used to be able to disagree is the
    /// reason this type exists.
    /// </summary>
    internal static class BanPolicy {

        /// <summary>
        /// Evaluates an account. <paramref name="subjectHash"/> is the ban network's hash of the same account
        /// when it is already known, and null when it is not yet computed - the hash is deliberately worked
        /// out off the main thread, so the join gate often asks before it is ready. Local layers do not need
        /// it; only the network layer does, and it is re-asked once the hash lands.
        /// </summary>
        internal static BanVerdict Evaluate(string hostId, string subjectHash = null) {
            if (string.IsNullOrEmpty(hostId)) {
                // Fail open on an unknown id rather than refusing everyone if identity resolution ever breaks.
                // A ban is enforced on an account; with no account there is nothing to enforce against, and the
                // handshake's other gates still run.
                return BanVerdict.Allow("No account id on this connection; the ban policy had nothing to match.");
            }

            // 1 and 2. The owner's own decision, which outranks every list including the shipped seed.
            OverrideRecord over = BanOverrides.Find(hostId, subjectHash);

            BanRecord local = BanStore.Find(hostId);

            if (over != null && !over.Allow && over.Covers(local != null ? local.Categories : null)) {
                return new BanVerdict {
                    Reject = true,
                    Reason = string.IsNullOrEmpty(over.Reason) ? "Banned by this server." : over.Reason,
                    Source = "override",
                    Explanation = $"Refused by an explicit deny override in {BanOverrides.FileName}.",
                };
            }

            if (over != null && over.Allow) {
                string note = local != null
                    ? $"Allowed by an override in {BanOverrides.FileName}, despite a {local.Source} ban ({local.Describe()})."
                    : $"Allowed by an override in {BanOverrides.FileName}.";
                return BanVerdict.Allow(note);
            }

            // 3. Local bans. These always enforce regardless of category - the owner issued them, so there is
            // nothing for a category filter to protect them from.
            if (local != null) {
                return new BanVerdict {
                    Reject = true,
                    Reason = RejectionMessage(local),
                    Source = local.Source,
                    Categories = local.Categories,
                    Explanation = $"Refused by a {local.Source} ban in {BanStore.FileName}: {local.Describe()}"
                                + (local.AddedUtc.HasValue ? $" (added {BanTime.Stamp(local.AddedUtc.Value)})." : "."),
                };
            }

            // 4. What the ban network says, filtered by this server's own policy.
            if (ValConfig.EnableBanNetwork == null || !ValConfig.EnableBanNetwork.Value) {
                return BanVerdict.Allow("No ban matched this account.");
            }
            if (string.IsNullOrEmpty(subjectHash)) {
                return BanVerdict.Allow("No local ban matched, and this account's network subject was not available.");
            }

            NetworkBanRecord network = BanNetworkStore.Find(subjectHash);
            if (network == null) {
                return BanVerdict.Allow("No ban matched this account.");
            }

            List<BanCategory> enforced = BanNetworkScheduler.EnforcedCategories();
            int minReporters = ValConfig.BanNetworkMinReporters?.Value ?? 1;

            if (!network.Enforceable(enforced, minReporters)) {
                // Listed, but not by this server's rules. Allowed, and said so - an owner who never hears
                // about these cannot tell that widening their categories would have caught somebody.
                string why = network.Reporters < minReporters
                    ? $"only {network.Reporters} server(s) have reported them and BanNetworkMinReporters is {minReporters}"
                    : $"this server enforces {Describe(enforced)}, and they are listed for {BanCategories.Canonical(network.Categories)}";
                return new BanVerdict {
                    Reject = false,
                    Advisory = true,
                    Source = BanSources.Network,
                    Categories = network.Categories,
                    Explanation = $"On the ban network - {network.Describe()} - but not acted on here: {why}.",
                };
            }

            return new BanVerdict {
                Reject = true,
                Reason = NetworkRejectionMessage(network),
                Source = BanSources.Network,
                Categories = network.Categories,
                Explanation = $"Refused by the ban network: {network.Describe()}"
                            + (network.FirstSeenUtc.HasValue ? $", first reported {BanTime.Stamp(network.FirstSeenUtc.Value)}." : "."),
            };
        }

        /// <summary>
        /// What the refused player actually reads. Vanilla can only send one of thirteen canned status codes,
        /// so this travels over the mod's rejection-reason channel - see CharacterLimitPatches - and is the
        /// difference between a player who knows why they were refused and a player who opens a support ticket.
        /// </summary>
        /// <summary>
        /// What a player refused by the network reads.
        ///
        /// It names the network rather than this server, and says how many servers reported them, because
        /// the two obvious questions are "banned for what?" and "by whom?" - and a player who was refused
        /// somewhere they have never played deserves an answer to the second.
        /// </summary>
        private static string NetworkRejectionMessage(NetworkBanRecord record) {
            string categories = BanCategories.Canonical(record.Categories);
            string what = string.IsNullOrEmpty(categories)
                ? "You are banned on the ValheimEnforcer ban network."
                : $"You are banned on the ValheimEnforcer ban network for {categories.Replace(",", ", ")}.";
            string reason = string.IsNullOrEmpty(record.Reason) ? "No reason was recorded." : record.Reason;
            string reporters = record.Reporters > 1
                ? $" Reported by {record.Reporters} servers."
                : "";
            return $"{what} {reason}{reporters} This server's owner can override it if they choose.";
        }

        private static string Describe(List<BanCategory> categories) {
            string canonical = BanCategories.Canonical(categories);
            return string.IsNullOrEmpty(canonical) ? "no categories" : canonical;
        }

        private static string RejectionMessage(BanRecord record) {
            string reason = string.IsNullOrEmpty(record.Reason) ? "No reason was recorded." : record.Reason;
            string categories = BanCategories.Canonical(record.Categories);
            string what = string.IsNullOrEmpty(categories)
                ? "You are banned from this server."
                : $"You are banned from this server for {categories.Replace(",", ", ")}.";
            return $"{what} {reason}";
        }
    }
}
