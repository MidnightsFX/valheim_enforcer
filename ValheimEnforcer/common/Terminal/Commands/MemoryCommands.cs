using System.Collections.Generic;

namespace ValheimEnforcer.common {
    internal static partial class TerminalManager {

        private static void RegisterMemoryCommands() {
            _ = new EnforcerCommand("enforcer-memory",
                "Reports the server's memory use and what this mod is holding in it - cached character saves, audit buffers, per-player tables - alongside the world's object counts. Server admins only.",
                Memory, CommandArea.Diagnostics,
                serverAuthoritative: true, requiresAdmin: true);
        }

        private static void Memory(EnforcerCommandArgs args) {
            List<string> lines = MemoryReport.Lines();
            args.Output.Info("Server memory:", log: false);
            foreach (string line in lines) {
                args.Output.Detail($"  {line}", log: false);
            }
            args.Output.Info("The character store is what this mod controls. The world and per-peer object tables are the game's own and grow with uptime.", log: false);
        }
    }
}
