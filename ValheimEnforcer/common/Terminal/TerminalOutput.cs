using System.Collections.Generic;
using UnityEngine;

namespace ValheimEnforcer.common {

    /// <summary>
    /// Where a command's output goes.
    ///
    /// Before this, every command reported through Logger, which reaches the BepInEx log and nothing else -
    /// so an admin who typed a command in-game saw an empty console and had to go and read a file to find
    /// out whether anything had happened. A Local sink writes to the terminal the admin typed into; a Remote
    /// sink batches the same lines back to the peer that asked, over an RPC. Either way the line is also
    /// written to this machine's log, so a dedicated server's log still shows everything a remote admin saw.
    ///
    /// Colour is applied only at the moment a line is handed to a Terminal. The log and the network payload
    /// carry plain text plus a severity, so no colour markup can leak into a file, and a receiving client
    /// colours using its own setting rather than the server's.
    /// </summary>
    internal class TerminalOutput {

        private const string HexInfo = "#34D399";     // green
        private const string HexDetail = "#60A5FA";   // soft blue
        private const string HexWarning = "#FBBF24";  // amber
        private const string HexError = "#F87171";    // red

        // A world scan reports a line per offender and can outlive the RPC handler by many seconds.
        // Batching is what keeps that from becoming one packet per line while still feeling live.
        private const int BatchLines = 25;
        private const float BatchSeconds = 0.5f;

        private readonly global::Terminal terminal;
        private readonly long peer;
        private readonly bool remote;
        private readonly List<KeyValuePair<OutputLevel, string>> pending;
        private float lastFlush;

        private TerminalOutput(global::Terminal context) {
            terminal = context;
            remote = false;
        }

        private TerminalOutput(long senderUid) {
            peer = senderUid;
            remote = true;
            pending = new List<KeyValuePair<OutputLevel, string>>();
            lastFlush = Time.realtimeSinceStartup;
        }

        /// <summary>
        /// The admin is typing on this machine. Context is null when the command came from somewhere with no
        /// terminal - a dedicated server has none - in which case the line still reaches the log.
        /// </summary>
        internal static TerminalOutput Local(global::Terminal context) {
            return new TerminalOutput(context);
        }

        /// <summary>The request arrived over the network; lines go back to that peer.</summary>
        internal static TerminalOutput Remote(long senderUid) {
            return new TerminalOutput(senderUid);
        }

        internal void Info(string message, bool log = true) { Write(OutputLevel.Info, message, log); }
        internal void Detail(string message, bool log = true) { Write(OutputLevel.Detail, message, log); }
        internal void Warning(string message, bool log = true) { Write(OutputLevel.Warning, message, log); }
        internal void Error(string message, bool log = true) { Write(OutputLevel.Error, message, log); }

        /// <summary>
        /// log: false is for callers that have already written the line themselves through a more specific
        /// logger, and for listings like enforcer-help that are for the person typing rather than the record.
        /// </summary>
        internal void Write(OutputLevel level, string message, bool log = true) {
            if (string.IsNullOrEmpty(message)) { return; }
            if (log) { LogLine(level, message); }

            // Several reports are built as one multi-line string. Split them so each console line is
            // coloured and batched on its own.
            foreach (string line in message.Split('\n')) {
                Deliver(level, line.TrimEnd('\r'));
            }
        }

        private void Deliver(OutputLevel level, string line) {
            if (remote == false) {
                terminal?.AddString(Colorize(level, line));
                return;
            }

            pending.Add(new KeyValuePair<OutputLevel, string>(level, line));
            if (pending.Count >= BatchLines || Time.realtimeSinceStartup - lastFlush >= BatchSeconds) {
                Flush();
            }
        }

        /// <summary>
        /// Ships whatever is buffered. Safe to call on a Local sink and safe to call repeatedly: a world scan
        /// runs as a coroutine that outlives the RPC handler, so the peer may be long gone by the time the
        /// last batch is ready.
        /// </summary>
        internal void Flush() {
            if (remote == false || pending.Count == 0) { return; }
            lastFlush = Time.realtimeSinceStartup;

            if (ZNet.instance == null || ZNet.instance.IsServer() == false || ZNet.instance.GetPeer(peer) == null) {
                pending.Clear();
                return;
            }

            ZPackage package = new ZPackage();
            package.Write(pending.Count);
            foreach (KeyValuePair<OutputLevel, string> line in pending) {
                package.Write((byte)line.Key);
                package.Write(line.Value);
            }
            pending.Clear();
            ValConfig.CommandOutputRPC.SendPackage(peer, package);
        }

        internal static void LogLine(OutputLevel level, string message) {
            switch (level) {
                case OutputLevel.Warning: Logger.LogWarning(message); break;
                case OutputLevel.Error: Logger.LogError(message); break;
                default: Logger.LogInfo(message); break;
            }
        }

        /// <summary>Used here and by the client handler that receives relayed output.</summary>
        internal static void PrintTo(global::Terminal context, OutputLevel level, string line) {
            context?.AddString(Colorize(level, line));
        }

        private static string Colorize(OutputLevel level, string line) {
            if (ValConfig.EnableTerminalColors == null || ValConfig.EnableTerminalColors.Value == false) {
                return line;
            }
            switch (level) {
                case OutputLevel.Detail: return $"<color={HexDetail}>{line}</color>";
                case OutputLevel.Warning: return $"<color={HexWarning}>{line}</color>";
                case OutputLevel.Error: return $"<color={HexError}>{line}</color>";
                default: return $"<color={HexInfo}>{line}</color>";
            }
        }
    }
}
