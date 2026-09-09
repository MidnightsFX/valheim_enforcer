using Jotunn.Managers;
using Splatform;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ValheimEnforcer.common {
    internal static partial class TerminalManager {

        /// <summary>Named once so the refusal message in Execute cannot drift from the command it points at.</summary>
        internal const string WhoAmICommand = "enforcer-whoami";

        private static void RegisterMetaCommands() {
            _ = new EnforcerCommand("enforcer-help",
                "Format: [optional: area] Lists the ValheimEnforcer console commands, optionally just one area. eg: enforcer-help items",
                Help, CommandArea.Meta, HelpOptions, isCheat: false, allowNonAdmin: true);

            _ = new EnforcerCommand(WhoAmICommand,
                "Says whether this server treats you as an admin and, when it does not, what to change so that it does. Runs for anybody.",
                WhoAmI, CommandArea.Meta, isCheat: false, allowNonAdmin: true, alsoRunsOnServer: true);
        }

        private static List<string> HelpOptions(string[] input) {
            if (input.Length > 2) { return new List<string>(); }
            return Enum.GetNames(typeof(CommandArea)).Where(name => name != CommandArea.Meta.ToString()).ToList();
        }

        private static void Help(EnforcerCommandArgs args) {
            CommandArea? only = null;
            string filter = args.Args.GetString(0, null);
            if (filter != null) {
                if (Enum.TryParse(filter, true, out CommandArea parsed) && Enum.IsDefined(typeof(CommandArea), parsed)) {
                    only = parsed;
                } else {
                    args.Output.Warning($"Unknown area '{filter}'. Areas: {string.Join(", ", HelpOptions(new string[] { "enforcer-help", "" }))}");
                    return;
                }
            }

            // Help is for the person typing it; echoing the whole listing into the BepInEx log is noise.
            args.Output.Info("ValheimEnforcer commands:", log: false);

            int shown = 0;
            foreach (IGrouping<CommandArea, EnforcerCommand> group in Registry.Values
                .Where(command => command.HideFromHelp == false)
                .Where(command => only == null || command.Area == only.Value)
                .GroupBy(command => command.Area)
                .OrderBy(group => group.Key.ToString())) {
                args.Output.Info($"  [{group.Key}]", log: false);
                foreach (EnforcerCommand command in group.OrderBy(command => command.Command)) {
                    shown++;
                    args.Output.Detail($"    {command.Command}{Tags(command)} - {command.Description}", log: false);
                    string aliases = AliasesOf(command);
                    if (aliases.Length > 0) {
                        args.Output.Detail($"      also accepts: {aliases}", log: false);
                    }
                }
            }

            args.Output.Info(only == null
                ? $"{shown} command(s). Add an area to narrow this: {string.Join(", ", HelpOptions(new string[] { "enforcer-help", "" }))}"
                : $"{shown} command(s) in {only.Value}.", log: false);

            // The listing runs for anybody, so somebody who cannot actually use most of it should learn that
            // here rather than from a refusal on the first command they try.
            if (LocallyAdmin() == false) {
                args.Output.Warning($"Everything except enforcer-help and {WhoAmICommand} needs admin rights on this server, which you do not currently have. Run {WhoAmICommand} to see why.", log: false);
            }
        }

        private static string Tags(EnforcerCommand command) {
            List<string> tags = new List<string>();
            if (command.IsCheat) { tags.Add("cheat"); }
            if (command.RequiresAdmin) { tags.Add("admin"); }
            if (command.ServerAuthoritative) { tags.Add("server"); }
            return tags.Count == 0 ? string.Empty : $" ({string.Join(", ", tags)})";
        }

        /// <summary>
        /// The old, pre-rename names are registered as hidden aliases so existing docs and macros keep
        /// working; surface them here so the rename is discoverable rather than silent.
        /// </summary>
        private static string AliasesOf(EnforcerCommand command) {
            return string.Join(", ", Registry.Values
                .Where(other => other.HideFromHelp && other.Canonical == command.Command)
                .Select(other => other.Command)
                .OrderBy(name => name));
        }

        // -------------------------------------------------------------------------------------------------
        // enforcer-whoami
        // -------------------------------------------------------------------------------------------------

        /// <summary>
        /// Answers "does this server think I am an admin?" and, when it does not, says what to change.
        ///
        /// Every other command here refuses a non-admin, which is precisely why this one must not: somebody
        /// whose id sits in adminlist.txt in a spelling the game will not accept looks, from in-game,
        /// identical to somebody who was never added at all, and the usual answer - ask an admin - is no help
        /// when the person asking IS the operator. It runs on both sides because the two sides do not check
        /// the same thing: a client knows the platform account it is signed in as, while the server compares
        /// the host name of the socket the connection actually arrived on. Those two can disagree, and that
        /// disagreement is the thing this command exists to find.
        /// </summary>
        private static void WhoAmI(EnforcerCommandArgs args) {
            if (args.FromNetwork) { WhoAmIOnServer(args); return; }
            WhoAmIHere(args);
        }

        /// <summary>The half this machine can answer on its own, before the server is asked for the rest.</summary>
        private static void WhoAmIHere(EnforcerCommandArgs args) {
            string qualified = LocalPlatformId(out string bare);
            args.Output.Info(qualified == null
                ? "No platform account is signed in on this machine, which is normal for a dedicated server."
                : $"Signed in here as {qualified} (an admin list accepts that, or the bare {bare}).", log: false);

            if (ZNet.instance == null) {
                args.Output.Warning("You are not in a world, so there is no server to be an admin of. Join one and run this again.", log: false);
                return;
            }

            if (ZNet.instance.IsServer()) {
                args.Output.Info("This machine IS the server, so every ValheimEnforcer command runs here with full rights - there is no admin check to fail.", log: false);
                ReportAdminList(args, ZNet.instance.GetAdminList(), "adminlist.txt");
                return;
            }

            // Jotunn's flag is what Execute gates on, so name it as the thing that refuses a command rather
            // than as an opinion. The server sets it over its own RPC after login.
            args.Output.Detail($"This mod's admin gate says you are {(LocallyAdmin() ? "an admin" : "NOT an admin")}; that is the check that refuses a command on this machine.", log: false);

            List<string> list = ZNet.instance.GetAdminList() ?? new List<string>();
            args.Output.Detail($"The server has sent this client {list.Count} admin list entry(ies).", log: false);
            if (bare == null) { return; }

            // Only ever reports the caller's own line back to them: an entry that matches is by definition
            // their own account, and nothing else in the list is named.
            string exact = list.FirstOrDefault(entry => entry == qualified || entry == bare);
            if (exact != null) {
                args.Output.Detail($"Your account is in that list, written as '{exact}'.", log: false);
                return;
            }
            string near = list.FirstOrDefault(entry => PlatformIds.Matches((entry ?? string.Empty).Trim(), bare));
            args.Output.Detail(near != null
                ? $"Your account appears in that list as '{near}', which is close but not a form the game accepts as written."
                : "Your account is not in that list in any spelling, so this server has not been told you are an admin.", log: false);
        }

        /// <summary>
        /// The half only the server can answer. <c>ZNet.IsAdmin</c> against the socket's host name is what
        /// every gate in this mod and in the game itself ultimately comes down to, so this is the verdict;
        /// everything the client said is context for it.
        /// </summary>
        private static void WhoAmIOnServer(EnforcerCommandArgs args) {
            if (ZNet.instance == null || ZNet.instance.IsServer() == false) { return; }

            ZNetPeer peer = ZNet.instance.GetPeer(args.Sender);
            string host = peer != null && peer.m_socket != null ? peer.m_socket.GetHostName() : null;
            if (string.IsNullOrEmpty(host)) {
                args.Output.Error("Server: could not identify the connection this question arrived on.");
                return;
            }

            bool admin = ZNet.instance.IsAdmin(host);
            // Worth a log line on the server: an operator reading the log afterwards wants the verdict, and
            // this is the only line of the answer that is neither context nor advice.
            args.Output.Info(admin
                ? $"Server: your connection is {host}, and this server treats it as an admin."
                : $"Server: your connection is {host}, and this server does NOT treat it as an admin.");

            List<string> list = ZNet.instance.GetAdminList();
            ReportAdminList(args, list, "the server's adminlist.txt");
            if (admin) { return; }

            string near = (list ?? new List<string>())
                .FirstOrDefault(entry => PlatformIds.Matches((entry ?? string.Empty).Trim(), host));
            args.Output.Warning(near != null
                ? $"Server: the line reading '{near}' is your account, but the server did not accept it as written. Replace that line with exactly: {host}"
                : $"Server: add this to adminlist.txt on a line of its own: {host}", log: false);
            args.Output.Detail($"Server: that file is {AdminListPath()}, and it is re-read within about ten seconds of being saved - no restart needed.", log: false);
        }

        /// <summary>
        /// Reports the shape of the admin list without naming anybody in it. How many entries there are, and
        /// how many of them can never work, is what an operator needs; who else is an admin is not this
        /// command's business, least of all when the person asking is not one.
        /// </summary>
        private static void ReportAdminList(EnforcerCommandArgs args, List<string> list, string what) {
            list = list ?? new List<string>();
            args.Output.Detail($"{what} holds {list.Count} entry(ies).", log: false);

            int unusable = list.Count(Unusable);
            if (unusable > 0) {
                args.Output.Warning($"{unusable} of those line(s) carry stray whitespace or an invisible marker and can never match anybody. Re-save the file as plain UTF-8 with no byte order mark: one id per line, nothing after it.", log: false);
            }
        }

        /// <summary>
        /// A line that can never match anybody. Valheim reads adminlist.txt line by line and compares the
        /// text exactly as written - it does not trim - so one trailing space, or a byte order mark an editor
        /// left on the first line, silently costs somebody their admin rights while the file looks perfect.
        /// U+FEFF is tested separately because .NET does not count it as whitespace, so Trim leaves it behind.
        /// </summary>
        private static bool Unusable(string entry) {
            if (string.IsNullOrEmpty(entry)) { return true; }
            return entry.Trim() != entry || entry.IndexOf('\uFEFF') >= 0;
        }

        /// <summary>Whether this machine currently passes the gate every other command is held to.</summary>
        private static bool LocallyAdmin() {
            return SynchronizationManager.Instance != null && SynchronizationManager.Instance.PlayerIsAdmin;
        }

        /// <summary>
        /// The platform account signed in on this machine, in both the spellings an admin list is written
        /// with. Null when there is none, which is the normal state of a dedicated server.
        /// </summary>
        private static string LocalPlatformId(out string bare) {
            bare = null;
            try {
                PlatformUserID local = PlatformManager.DistributionPlatform?.LocalUser?.PlatformUserID ?? default;
                if (string.IsNullOrEmpty(local.m_userID)) { return null; }
                bare = local.m_userID;
                return local.ToString();
            } catch (Exception e) {
                Logger.LogDebug($"Platform layer could not supply a local user id: {e.Message}");
                return null;
            }
        }

        /// <summary>Where the server reads its admin list from, mirroring ZNet's own choice of location.</summary>
        private static string AdminListPath() {
            try {
                if (FileHelpers.LocalStorageSupport == LocalStorageSupport.Supported) {
                    return global::Utils.GetSaveDataPath(FileHelpers.FileSource.Local) + "/adminlist.txt";
                }
                if (FileHelpers.CloudStorageSupportedAndEnabled) {
                    return global::Utils.GetSaveDataPath(FileHelpers.FileSource.Cloud) + "/adminlist.txt";
                }
            } catch (Exception e) {
                Logger.LogDebug($"Could not resolve the admin list path: {e.Message}");
            }
            return "adminlist.txt in the server's save folder";
        }
    }
}
