using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ValheimEnforcer.modules.character;

namespace ValheimEnforcer.modules.worldprivacy {

    /// <summary>
    /// Keeps the world seed away from players.
    ///
    /// A seed name is all it takes to see the whole world: typed into a seed map viewer it lays out every biome,
    /// boss altar, trader and dungeon before anyone has explored a metre. Vanilla hands it out three ways:
    ///
    ///   - The join handshake. The server writes World.m_seedName into the peer info it sends each client
    ///     (ZNet.SendPeerInfo). No client needs it: terrain is generated from the numeric m_seed sent alongside,
    ///     and the only thing that writes the name on a client is that same handshake (ZNet.RPC_PeerInfo).
    ///   - The crossplay lobby. ZNet.OpenServer passes m_seedName to ZPlayFabMatchmaking.RegisterServer as its
    ///     "worldName", which becomes the lobby's WORLD attribute - readable by anyone who can join the lobby,
    ///     which happens before the password or any mod check. The Steam lobby takes the same argument and
    ///     never publishes it.
    ///   - printseeds, which prints both seeds and is not a cheat command, so any player can run it from chat
    ///     as /printseeds without enabling the console.
    ///
    /// The first two are fixed on the server and are what actually protects the seed. The third runs on the
    /// client and only keeps the numeric seed from casual players: the client has to hold that number to build
    /// terrain, so a modified client can always read it and run its own copy of the world generator. What
    /// nobody can do any more is paste the seed into a website.
    ///
    /// Anyone on the adminlist, and the host of a listen server, still gets the real seed.
    /// </summary>
    internal static class SeedPrivacy {

        /// <summary>
        /// What joining players and the crossplay lobby get instead of the seed name. Not empty, because a
        /// client joining a crossplay server reads WORLD out of the lobby and gives up on a lobby that lacks
        /// it (ZPlayFabLobbySearch treats a missing key as a broken entry), and whether PlayFab keeps an empty
        /// attribute at all is not something to depend on.
        ///
        /// Client-side mods that print the seed name - a world info command, an admin panel's "show seed"
        /// button - show this instead, which is the intent.
        /// </summary>
        internal const string Placeholder = "hidden";

        /// <summary>
        /// The seed name ZNet.SendPeerInfo writes for this connection, called from the transpiled handshake in
        /// place of reading World.m_seedName. Only the server writes the world record into peer info, so only
        /// the server gets here.
        /// </summary>
        internal static string SeedNameFor(World world, ZRpc rpc) {
            string real = world?.m_seedName;
            try {
                if (!ValConfig.HideWorldSeed.Value) { return real; }

                ZNetPeer peer = PeerIdentity.PeerFor(rpc);
                if (peer != null && PeerIdentity.IsAdmin(peer)) { return real; }
                return Placeholder;
            } catch (Exception e) {
                // Hiding is the safe direction: nothing on the client depends on the name.
                Logger.LogWarning($"Could not decide whether a joining player may see the world seed, hiding it: {e.Message}");
                return Placeholder;
            }
        }

        /// <summary>
        /// Whether this client should refuse to print the seed. Singleplayer and a listen host own the world,
        /// so there is nobody to hide it from there.
        /// </summary>
        internal static bool HiddenFromThisClient() {
            if (!ValConfig.HideWorldSeed.Value) { return false; }
            ZNet net = ZNet.instance;
            if (net == null || net.IsServer()) { return false; }
            return !net.LocalPlayerIsAdminOrHost();
        }
    }

    /// <summary>
    /// Swaps the seed name in the server's half of the join handshake for <see cref="SeedPrivacy.SeedNameFor"/>.
    ///
    /// A transpiler rather than a prefix that blanks m_seedName and a finalizer that restores it: the world
    /// save thread reads the same World object (ZNet.SaveWorldThread), and a save landing inside that window
    /// would write the placeholder into the world's .fwl and lose the real seed name for good.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), "SendPeerInfo")]
    internal static class ZNet_SendPeerInfo_HideSeed {

        private static readonly FieldInfo SeedNameField = AccessTools.Field(typeof(World), nameof(World.m_seedName));
        private static readonly MethodInfo SeedNameFor = AccessTools.Method(typeof(SeedPrivacy), nameof(SeedPrivacy.SeedNameFor));

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            int replaced = 0;

            for (int i = 0; i < code.Count; i++) {
                if (code[i].opcode != OpCodes.Ldfld || !Equals(code[i].operand, SeedNameField)) { continue; }

                // Stack [pkg, world] becomes [pkg, world, rpc], and the call leaves [pkg, name] for the Write
                // that follows. The ldfld is rewritten in place rather than replaced so a label on it stays put.
                code[i].opcode = OpCodes.Ldarg_1;
                code[i].operand = null;
                code.Insert(i + 1, new CodeInstruction(OpCodes.Call, SeedNameFor));
                replaced++;
                i++;
            }

            if (replaced == 0) {
                Logger.LogWarning("Could not find where the join handshake writes the world seed; joining players will still receive it.");
            }
            return code;
        }
    }

    /// <summary>
    /// Keeps the seed name out of the crossplay lobby. Read once, when the server opens, so a change to the
    /// setting reaches the lobby at the next restart.
    /// </summary>
    [HarmonyPatch(typeof(ZPlayFabMatchmaking), nameof(ZPlayFabMatchmaking.RegisterServer))]
    internal static class ZPlayFabMatchmaking_RegisterServer_HideSeed {

        [HarmonyPrefix]
        private static void Prefix(ref string worldName) {
            if (!ValConfig.HideWorldSeed.Value) { return; }
            worldName = SeedPrivacy.Placeholder;
        }
    }

    /// <summary>
    /// Refuses printseeds on a client that may not see the seed. On ConsoleCommand.RunAction because every
    /// route to a command ends there - the console, chat's /printseeds, a key bound with the bind command,
    /// and another mod invoking the command directly.
    /// </summary>
    [HarmonyPatch(typeof(global::Terminal.ConsoleCommand), nameof(global::Terminal.ConsoleCommand.RunAction))]
    internal static class ConsoleCommand_RunAction_HideSeed {

        [HarmonyPrefix]
        private static bool Prefix(global::Terminal.ConsoleCommand __instance, global::Terminal.ConsoleEventArgs args) {
            if (!string.Equals(__instance.Command, "printseeds", StringComparison.OrdinalIgnoreCase)) { return true; }
            if (!SeedPrivacy.HiddenFromThisClient()) { return true; }

            args?.Context?.AddString("The world seed is hidden on this server.");
            return false;
        }
    }
}
