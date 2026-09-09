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
                WhoAmI, CommandArea.Meta, isCheat: false, serverAuthoritative: true, allowNonAdmin: true);
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
        /// when the person asking IS the operator.
        ///
        /// Only the server answers. A client holds two opinions about its own standing - Jotunn's synced flag
        /// and the admin list Valheim pushes once on connect - and both are snapshots that can be stale, so
        /// printing them next to the real answer only gives somebody two things to believe and a reason to
        /// trust the wrong one. <c>ZNet.IsAdmin</c> against the host name of the socket the request arrived on
        /// is what every gate in this mod, and in the game itself, ultimately comes down to; that is the whole
        /// of the answer and nothing else is worth saying.
        /// </summary>
        private static void WhoAmI(EnforcerCommandArgs args) {
            // Execute only dispatches a server-authoritative command locally when this machine is the server,
            // so this is unreachable today; answer rather than print nothing if that ever changes.
            if (ZNet.instance == null || ZNet.instance.IsServer() == false) {
                args.Output.Error("Only the server can answer this, and this machine is not it.");
                return;
            }

            // Typed on the server's own console, or on a listen host: there is no connection to look up, and
            // the server never checks itself.
            if (args.FromNetwork == false) {
                args.Output.Info("This machine IS the server, so every ValheimEnforcer command runs here with full rights - there is no admin check to fail.", log: false);
                ReportAdminList(args, ZNet.instance.GetAdminList(), "adminlist.txt");
                return;
            }

            ZNetPeer peer = ZNet.instance.GetPeer(args.Sender);
            string host = peer != null && peer.m_socket != null ? peer.m_socket.GetHostName() : null;
            if (string.IsNullOrEmpty(host)) {
                args.Output.Error("The server could not identify the connection this question arrived on.");
                return;
            }

            bool admin = ZNet.instance.IsAdmin(host);
            // Worth a log line on the server: an operator reading the log afterwards wants the verdict, and
            // this is the only line of the answer that is neither context nor advice.
            args.Output.Info(admin
                ? $"The server sees your connection as {host} and treats it as an admin."
                : $"The server sees your connection as {host} and does NOT treat it as an admin.");

            List<string> list = ZNet.instance.GetAdminList();
            ReportAdminList(args, list, "adminlist.txt");
            if (admin) { return; }

            string canonical = AdminIds.Canonical(host);
            if (canonical == null) {
                args.Output.Warning($"The admin list spelling for {host} could not be worked out, so there is no line to suggest.", log: false);
                return;
            }

            // The two cases look identical from in-game and have completely different fixes, which is most of
            // the reason this command exists.
            string stale = (list ?? new List<string>()).FirstOrDefault(entry => AdminIds.SameAccount(entry, host));
            args.Output.Warning(stale != null
                ? $"The line reading '{stale.Trim()}' is your account written the way the game wanted before the update, and is no longer honoured. Replace that line with exactly: {canonical}"
                : $"Add this to adminlist.txt on a line of its own: {canonical}", log: false);
            args.Output.Detail($"Admin ids now carry a one-letter platform prefix - {PlatformPrefixes}.", log: false);
            args.Output.Detail($"That file is {AdminListPath()}, and it is re-read within about ten seconds of being saved - no restart needed.", log: false);
        }

        /// <summary>
        /// The prefixes the game's own filtering produces. Spelled out rather than derived because the table
        /// behind it is private, and an operator staring at a file of bare SteamID64s needs to be told what
        /// changed, not just handed one corrected line.
        /// </summary>
        private const string PlatformPrefixes = "V_ Steam, N_ Nintendo, X_ Xbox, S_ PlayStation, A_ GameCenter";

        /// <summary>
        /// Reports the shape of the admin list without naming anybody in it. How many entries there are, and
        /// how many of them can never work, is what an operator needs; who else is an admin is not this
        /// command's business, least of all when the person asking is not one.
        /// </summary>
        private static void ReportAdminList(EnforcerCommandArgs args, List<string> list, string what) {
            list = list ?? new List<string>();
            args.Output.Detail($"{what} holds {list.Count} entry(ies).", log: false);

            // The one worth leading on after the update that changed the format: a server whose file predates
            // it has no admins at all and nothing anywhere says so.
            int stale = list.Count(entry => AdminIds.IsPreUpdateSpelling(entry, out _));
            if (stale > 0) {
                args.Output.Warning($"{stale} of those line(s) use the pre-update spelling - a bare id, or one prefixed with the platform's full name - and no longer grant admin to anybody. Admin ids now need a one-letter prefix: {PlatformPrefixes}.", log: false);
            }

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

        /// <summary>
        /// This machine's best guess at its own standing, and deliberately a union of every source it has:
        /// being the server, Jotunn's synced flag, and the server's own admin list matched by the game's own
        /// rule. Any one of them saying yes is enough.
        ///
        /// A union because the failure that matters here is the false negative. Jotunn's flag is pushed once
        /// after login and the pushed admin list is sent once on connect, so either can be stale or absent
        /// while the server would happily accept the command - and that is precisely the state somebody
        /// reports as "the mod says I am not an admin but kick works". Nothing is lost by being generous:
        /// every command this gates is checked again by the server, which is the only side that decides.
        /// </summary>
        private static bool LocallyAdmin() {
            if (ZNet.instance != null && ZNet.instance.IsServer()) { return true; }
            if (SynchronizationManager.Instance != null && SynchronizationManager.Instance.PlayerIsAdmin) { return true; }

            string local = LocalPlatformId();
            return local != null && ZNet.instance != null && AdminIds.Accepts(ZNet.instance.GetAdminList(), local);
        }

        /// <summary>
        /// The platform account signed in on this machine, as the platform layer spells it - which is the
        /// form the game identifies a connection by, not the form the admin list wants. Run it through
        /// <see cref="AdminIds.Canonical"/> for that. Null when there is none, which is the normal state of a
        /// dedicated server.
        /// </summary>
        private static string LocalPlatformId() {
            try {
                PlatformUserID local = PlatformManager.DistributionPlatform?.LocalUser?.PlatformUserID ?? default;
                return string.IsNullOrEmpty(local.m_userID) ? null : local.ToString();
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
