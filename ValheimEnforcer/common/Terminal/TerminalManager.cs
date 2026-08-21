using HarmonyLib;
using Jotunn.Managers;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ValheimEnforcer.common {

    /// <summary>
    /// Registration and dispatch for every ValheimEnforcer console command.
    ///
    /// Commands are built once from the plugin's Awake rather than from a Terminal or Console hook, because a
    /// dedicated server has neither - Console.Awake, which is where Jotunn's command manager registers, only
    /// ever runs on a client. Every command here is a server admin tool, so the server is precisely the side
    /// that needs the table: it is what turns a relayed request from an admin's client into a call. Building
    /// a Terminal.ConsoleCommand only touches a static dictionary, so doing it headless is harmless.
    /// </summary>
    internal static partial class TerminalManager {

        internal static readonly Dictionary<string, EnforcerCommand> Registry = new Dictionary<string, EnforcerCommand>();

        /// <summary>
        /// Whichever terminal the admin last used to send a server-authoritative command, so the relayed
        /// reply lands where they typed rather than always in the console.
        /// </summary>
        private static global::Terminal responseTerminal;

        private static bool initialized;

        internal static void Init() {
            if (initialized) { return; }
            initialized = true;

            RegisterMetaCommands();
            RegisterPlayerCommands();
            RegisterItemCommands();
            RegisterCharacterCommands();
            RegisterNotificationCommands();
            RegisterStructureCommands();

            Logger.LogDebug($"Registered {Registry.Count} ValheimEnforcer console commands.");
        }

        internal static void Register(EnforcerCommand command) {
            Registry[Key(command.Command)] = command;
        }

        private static string Key(string name) {
            return (name ?? string.Empty).ToLowerInvariant();
        }

        private static EnforcerCommand Lookup(string name) {
            return Registry.TryGetValue(Key(name), out EnforcerCommand command) ? command : null;
        }

        /// <summary>Every command's vanilla action funnels through here.</summary>
        internal static void Execute(string name, global::Terminal.ConsoleEventArgs consoleArgs) {
            EnforcerCommand command = Lookup(name);
            if (command == null) { return; }

            string[] args = consoleArgs.Args.Skip(1).ToArray();
            TerminalOutput output = TerminalOutput.Local(consoleArgs.Context);

            if (command.ServerAuthoritative == false) {
                Invoke(command, args, output);
                return;
            }

            if (ZNet.instance == null) {
                output.Error($"You must be in a world to use {command.Canonical}.");
                return;
            }
            // An integrated host is the server, so it just runs the thing. IsServer rather than
            // IsCurrentServerDedicated: a client attached to somebody else's listen host must still forward,
            // because the character saves and the webhook live on that host, not here.
            if (ZNet.instance.IsServer()) {
                Invoke(command, args, output);
                return;
            }
            // The client-side check is for a clear message only; the server re-checks the sender either way.
            if (SynchronizationManager.Instance.PlayerIsAdmin == false) {
                output.Error($"Only server admins can run {command.Canonical} from a client.");
                return;
            }
            ZNetPeer server = ZNet.instance.GetServerPeer();
            if (server == null) {
                output.Error($"No server connection, so {command.Canonical} cannot be sent.");
                return;
            }

            responseTerminal = consoleArgs.Context;
            output.Info($"Asked the server to run {command.Canonical}; its output follows.");
            ValConfig.ClientCommandRequestRPC.SendPackage(server.m_uid, BuildRequest(command.Canonical, args));
        }

        /// <summary>Server side of the relay. The caller has already established that the sender is an admin.</summary>
        internal static void ExecuteFromNetwork(string name, string[] args, TerminalOutput output) {
            EnforcerCommand command = Lookup(name);
            // Never dispatch a name the client picked that is not one of ours, and never let the relay reach
            // a command that was not built to run server-side.
            if (command == null || command.ServerAuthoritative == false) {
                output.Error($"'{name}' is not a server-runnable ValheimEnforcer command.");
                output.Flush();
                return;
            }
            Invoke(command, args, output);
        }

        private static void Invoke(EnforcerCommand command, string[] args, TerminalOutput output) {
            try {
                command.Action(new EnforcerCommandArgs(args, output));
            } catch (Exception e) {
                // A command that throws must not take the console or the RPC handler with it, and the admin
                // must not be left staring at a console that printed nothing.
                output.Error($"{command.Canonical} failed: {e.Message}");
                Logger.LogError($"{command.Canonical} threw: {e}");
            } finally {
                // Commands that finish synchronously are fully flushed here. One that started a coroutine
                // keeps using the same sink and flushes again as it goes.
                output.Flush();
            }
        }

        private static ZPackage BuildRequest(string name, string[] args) {
            ZPackage package = new ZPackage();
            package.Write(name);
            package.Write(args.Length);
            foreach (string arg in args) { package.Write(arg); }
            return package;
        }

        /// <summary>A relayed line arriving back on the requesting client.</summary>
        internal static void PrintResponse(OutputLevel level, string line) {
            TerminalOutput.LogLine(level, line);
            // A null check is not enough for a UnityEngine.Object: a destroyed terminal is a non-null
            // reference that only compares equal to null through Unity's own operator.
            global::Terminal target = responseTerminal != null ? responseTerminal : null;
            if (target == null && global::Console.instance != null) { target = global::Console.instance; }
            TerminalOutput.PrintTo(target, level, line);
        }

        // -------------------------------------------------------------------------------------------------
        // Tab completion
        //
        // Vanilla only ever completes the first argument: Terminal.Update hands tabCycle/updateSearch
        // strArray[1] and a single flat list from ConsoleCommand.GetTabOptions(). These two prefixes swap in
        // a list that depends on everything typed so far and point `word` at the token actually being
        // edited, so completion keeps working at the second argument and beyond - which is what makes
        // tabbing through an account id and then its characters possible.
        // -------------------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(global::Terminal), nameof(global::Terminal.tabCycle))]
        private static class Terminal_tabCycle_Patch {
            private static void Prefix(global::Terminal __instance, ref string word, ref List<string> options, bool usePrefix) {
                ApplyContextOptions(__instance, usePrefix, ref word, ref options);
            }
        }

        [HarmonyPatch(typeof(global::Terminal), nameof(global::Terminal.updateSearch))]
        private static class Terminal_updateSearch_Patch {
            private static void Prefix(global::Terminal __instance, ref string word, ref List<string> options, bool usePrefix) {
                ApplyContextOptions(__instance, usePrefix, ref word, ref options);
            }
        }

        private static void ApplyContextOptions(global::Terminal terminal, bool usePrefix, ref string word, ref List<string> options) {
            // usePrefix means the command name itself is being completed; vanilla already handles that.
            if (usePrefix || terminal == null || terminal.m_input == null) { return; }

            string[] tokens = (terminal.m_input.text ?? string.Empty).Split(' ');
            if (tokens.Length < 2) { return; }

            // Chat prefixes commands with m_tabPrefix ('/'), the console does not.
            string name = terminal.m_tabPrefix == char.MinValue
                ? tokens[0]
                : (tokens[0].Length == 0 ? string.Empty : tokens[0].Substring(1));

            EnforcerCommand command = Lookup(name);
            if (command == null) { return; }

            List<string> resolved = command.GetTabOptions(tokens);
            if (resolved == null || resolved.Count == 0) { return; }

            options = resolved;
            word = tokens[tokens.Length - 1];
        }
    }
}
