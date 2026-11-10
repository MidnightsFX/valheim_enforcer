using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Jotunn.Managers;
using Jotunn.Utils;
using System;
using UnityEngine.SceneManagement;
using ValheimEnforcer.API;

namespace ValheimEnforcerModSync {

    /// <summary>
    /// Fixes a player's mods for a ValheimEnforcer server that turned them away, with the player's say-so.
    ///
    /// ValheimEnforcer works out what the server needs - which Thunderstore packages at which exact versions,
    /// which of the player's plugins to turn off - and this does the installing: it downloads those packages from
    /// thunderstore.io, checks them against the file hashes the server recorded, stages them, and has a small
    /// helper program apply them once the game has closed, then start it again.
    ///
    /// Talks to ValheimEnforcer only through its public <see cref="ModSyncApi"/>, and stays off when that API is
    /// not the version this was written against.
    /// </summary>
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(ModSyncApi.EnforcerGuid, MinimumEnforcerVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    // A player's own tool: servers neither need it nor ask for it, so Jotunn's version check leaves it out.
    [NetworkCompatibility(CompatibilityLevel.NotEnforced, VersionStrictness.None)]
    internal class ValheimEnforcerModSync : BaseUnityPlugin {

        public const string PluginGUID = ModSyncApi.AddonGuid;
        public const string PluginName = "ValheimEnforcerModSync";
        public const string PluginVersion = "1.0.0";

        // The first ValheimEnforcer with ModSyncApi.
        private const string MinimumEnforcerVersion = "0.37.0";

        // The ModSyncApi version this was written against.
        private const int ApiVersion = 1;

        internal const string ConfigFileName = PluginGUID + ".cfg";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> AllowServerModInstalls;

        public void Awake() {
            Log = Logger;

            // Local, and it has to be: this is the player's consent. Nothing a server sends can change it.
            AllowServerModInstalls = Config.Bind("Client config", "AllowServerModInstalls", true,
                "Lets a ValheimEnforcer server that turns you away for your mods fix them for you. When a server that offers this refuses your connection, the connection error window gets a 'Fix mods' button. It shows you exactly what would change - which Thunderstore packages would be installed or updated, and which of your mods would be turned off - and nothing happens until you agree. Every package comes from thunderstore.io at the exact version the server pinned, and its files are checked against the hashes the server recorded before anything is installed; mods are turned off by renaming them to .old, never deleted, and anything replaced is moved to BepInEx/cache/ValheimEnforcer/modsync/replaced. Applying the changes closes Valheim, which then starts again and rejoins the server; a small helper program inside this mod makes the changes once the game has closed. Windows, Linux (including Proton) and macOS. Turn it off to keep the button showing you the list without installing anything.");

            if (!ApiMatches()) { return; }

            // A fix from the last session was applied after the game had exited; say what happened.
            if (AllowServerModInstalls.Value) { ModSync.ReportLastRun(); }
            GUIManager.OnCustomGUIAvailable += AddWindow;
        }

        /// <summary>
        /// The ModSyncApi version check. Read inside a try, because against a ValheimEnforcer without the API
        /// even reading the property fails - BepInEx's minimum version on the dependency should prevent that,
        /// but a hand-edited install is not bound by it.
        /// </summary>
        private static bool ApiMatches() {
            try {
                int found = ReadApiVersion();
                if (found == ApiVersion) { return true; }
                Log.LogWarning($"This ValheimEnforcer offers mod sync API version {found}; this add-on needs version {ApiVersion}. Mod fixing is off until the two match - update this add-on from the ValheimEnforcer GitHub releases.");
            } catch (Exception e) when (e is MissingMemberException || e is TypeLoadException) {
                Log.LogWarning($"This ValheimEnforcer has no mod sync API this add-on can use ({e.GetType().Name}). Mod fixing is off - update ValheimEnforcer.");
            }
            return false;
        }

        // Its own method so a missing API fails when this is compiled, inside ApiMatches' try, not when Awake is.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int ReadApiVersion() {
            return ModSyncApi.Version;
        }

        private static void AddWindow() {
            // Only the main menu shows the connection error window this adds to.
            if (!SceneManager.GetActiveScene().name.Equals("start")) { return; }
            // Idle unless a server that refused us offered mod downloads.
            GUIManager.CustomGUIFront.AddComponent<ModSyncWindow>();
        }
    }
}
