using System;
using System.Collections.Generic;
using System.Linq;

namespace ValheimEnforcer.common {

    /// <summary>Which family a command belongs to. Only used to group the enforcer-help listing.</summary>
    internal enum CommandArea { Meta, Player, Items, Characters, Notifications, Structures }

    /// <summary>
    /// Severity of one line of command output. Deliberately separate from the text: colour is applied where
    /// the line is displayed, so markup never reaches the BepInEx log or the wire.
    /// </summary>
    internal enum OutputLevel : byte { Info = 0, Detail = 1, Warning = 2, Error = 3 }

    /// <summary>
    /// Completions for the token currently being typed. <paramref name="input"/> is the whole console line
    /// split on spaces, so a provider can answer differently per argument position and based on what the
    /// earlier arguments already say. Vanilla's ConsoleOptionsFetcher takes no arguments and so cannot.
    /// </summary>
    internal delegate List<string> OptionProvider(string[] input);

    internal delegate void CommandAction(EnforcerCommandArgs args);

    /// <summary>
    /// One ValheimEnforcer console command.
    ///
    /// Subclasses the vanilla Terminal.ConsoleCommand rather than using Jotunn's wrapper, because Jotunn
    /// registers from a Console.Awake postfix and Console only exists on a client. Every command in this mod
    /// is an admin tool for a server, and the server is the side that has to be able to look one up when an
    /// admin's client relays it. Constructing this type only writes into the static Terminal.commands
    /// dictionary, so building the table headless - where no Terminal will ever exist - is harmless.
    /// </summary>
    internal class EnforcerCommand : global::Terminal.ConsoleCommand {

        /// <summary>
        /// The primary name, even on an alias instance, so an alias and its canonical name resolve to the
        /// same behaviour on the server side of the relay.
        /// </summary>
        internal readonly string Canonical;
        internal readonly CommandArea Area;
        internal readonly CommandAction Action;
        internal readonly OptionProvider Options;
        internal readonly bool HideFromHelp;

        /// <summary>
        /// The command reads or writes state only the server owns - character saves, the webhook URL, the
        /// world's objects - so a connected client has to ask the server to run it rather than running it
        /// locally. See TerminalManager.Execute.
        /// </summary>
        internal readonly bool ServerAuthoritative;

        /// <summary>Extra hint for enforcer-help. The real gate is SenderIsAdmin on the server side.</summary>
        internal readonly bool RequiresAdmin;

        internal EnforcerCommand(
            string command,
            string description,
            CommandAction action,
            CommandArea area,
            OptionProvider options = null,
            bool isCheat = true,
            bool serverAuthoritative = false,
            bool requiresAdmin = false,
            bool hideFromHelp = false,
            string canonical = null,
            params string[] aliases)
            : base(command, description,
                  (global::Terminal.ConsoleEvent)(args => TerminalManager.Execute(command, args)),
                  isCheat,
                  isNetwork: false,
                  onlyServer: false,
                  // isSecret also keeps the name out of tab completion, which is exactly what a back-compat
                  // alias wants: it still runs when typed in full, but never suggests itself.
                  isSecret: hideFromHelp,
                  allowInDevBuild: false,
                  optionsFetcher: null) {
            Canonical = canonical ?? command;
            Area = area;
            Action = action;
            Options = options;
            HideFromHelp = hideFromHelp;
            ServerAuthoritative = serverAuthoritative;
            RequiresAdmin = requiresAdmin;

            // Vanilla asks for the option list before our tabCycle/updateSearch prefixes get a chance to
            // replace it, so the fetcher has to exist and has to be safe when there is no Console yet. The
            // prefixes are what actually make per-argument completion work.
            m_tabOptionsFetcher = () => GetTabOptions(CurrentInput());
            // Options depend on what is already typed, so a cached list is always one keystroke stale.
            m_alwaysRefreshTabOptions = true;

            TerminalManager.Register(this);

            foreach (string alias in aliases) {
                _ = new EnforcerCommand(alias, description, action, area, options, isCheat, serverAuthoritative,
                    requiresAdmin, hideFromHelp: true, canonical: Canonical);
            }
        }

        internal List<string> GetTabOptions(string[] input) {
            if (Options == null) { return new List<string>(); }
            try {
                return Options(input) ?? new List<string>();
            } catch (Exception e) {
                Logger.LogDebug($"Tab options for {Command} failed: {e.Message}");
                return new List<string>();
            }
        }

        /// <summary>Best-effort read of the line being typed, for the vanilla fetcher path only.</summary>
        private static string[] CurrentInput() {
            global::Terminal console = global::Console.instance;
            string text = console?.m_input == null ? string.Empty : console.m_input.text;
            return (text ?? string.Empty).Split(' ');
        }
    }

    /// <summary>
    /// What a command handler receives: the arguments with the command name already stripped, and where its
    /// output goes. Handlers never write to the log directly - everything goes through Output, which is what
    /// makes the same command work identically typed on the server console and relayed from a client.
    /// </summary>
    internal class EnforcerCommandArgs {
        internal readonly string[] Args;
        internal readonly TerminalOutput Output;

        internal EnforcerCommandArgs(string[] args, TerminalOutput output) {
            Args = args ?? new string[0];
            Output = output;
        }

        internal int Length => Args.Length;

        internal bool Has(string flag) {
            return Args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        }
    }
}
