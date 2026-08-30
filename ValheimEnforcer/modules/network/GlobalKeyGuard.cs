using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimEnforcer.common;
using ValheimEnforcer.modules.cheatmonitor;

namespace ValheimEnforcer.modules.network {

    /// <summary>
    /// Decides which global keys a client is allowed to set.
    ///
    /// Global keys are the world's progression and its rules in one flat list: which bosses are dead, and also
    /// whether building costs resources, whether recipes are unlocked, what the damage rates are. ZoneSystem
    /// registers SetGlobalKey and RemoveGlobalKey as routed RPCs and neither handler looks at the sender
    /// (ZoneSystem.cs:1958) - so any client can hand itself a finished world, or take one away from everybody
    /// else.
    ///
    /// The allowed set is not a list somebody has to maintain. Every key a client legitimately sets is written
    /// on a prefab - the key a creature sets when it dies, the one an offering bowl sets when a boss is
    /// summoned, the one a vegvisir sets when it is read, the one a trader sets on a purchase - so the set is
    /// read out of the loaded prefabs, exactly as StructureIndex reads buildable pieces out of piece tables.
    /// A modded boss with its own key is covered without anyone listing it. Two keys are set from code rather
    /// than a prefab field and are named below.
    ///
    /// Everything else - the world modifiers, the presets - is set by the host at world setup and never by a
    /// connected client, so a client asking for one is asking for something no honest client asks for.
    ///
    /// Off by default even with the other guards on, because the failure mode is asymmetric: a key wrongly
    /// refused stops progression registering and does it quietly. The guard is inert until the prefab scan has
    /// succeeded, refusals name the key so an admin can put it in AllowedClientGlobalKeys, and the default
    /// action does nothing to the player.
    /// </summary>
    internal static class GlobalKeyGuard {

        /// <summary>
        /// Keys clients legitimately set from code rather than from a prefab field, so the prefab scan cannot
        /// find them. Hardcoded rather than configurable for the same reason as StructureIndex.AlwaysAllowed:
        /// getting this wrong is a correctness bug in the guard, not a matter of server policy.
        ///
        ///   activeBosses  - BaseAI.cs:1204 and Character.cs:1840, maintained by whichever client is fighting
        ///                   the boss. Always carries a numeric value, which the key/value split handles.
        ///   ashlandsocean - Ship.cs:348, set by a client that sails into the Ashlands ocean.
        /// </summary>
        private static readonly HashSet<string> EngineSetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "activebosses",
            "ashlandsocean",
        };

        /// <summary>
        /// Trader.TradeItem.m_setsGlobalKey, or null on a game build that has no such field.
        ///
        /// Resolved reflectively because this one has come and gone across Valheim versions - it is absent
        /// from the build this was compiled against and present in others. Naming it directly would make the
        /// mod fail to compile on one and fail to load on the other, for a field that is read once per world.
        /// </summary>
        private static readonly System.Reflection.FieldInfo TradeItemSetsKey =
            typeof(Trader.TradeItem).GetField("m_setsGlobalKey",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        private static bool built;
        private static readonly HashSet<string> prefabKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Parsed form of AllowedClientGlobalKeys, rebuilt only when the admin edits the setting.
        private static string extraRaw;
        private static HashSet<string> extraParsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Collects every global key a loaded prefab can set. Returns false while the world is not far enough
        /// along to answer, in which case the guard allows everything - a check that cannot tell what is
        /// legitimate must not guess, and guessing wrong here breaks boss progression.
        /// </summary>
        internal static bool EnsureBuilt() {
            if (built) { return true; }
            if (ZNetScene.instance == null || ZNetScene.instance.m_prefabs == null) { return false; }

            try {
                prefabKeys.Clear();

                foreach (GameObject prefab in ZNetScene.instance.m_prefabs) {
                    if (prefab == null) { continue; }

                    // Every creature that sets a key on death: the five boss defeats, killed_surtling,
                    // KilledTroll, KilledBat, and whatever a content mod adds.
                    Character character = prefab.GetComponent<Character>();
                    if (character != null) { Add(character.m_defeatSetGlobalKey); }

                    // Boss summoning altars.
                    OfferingBowl bowl = prefab.GetComponent<OfferingBowl>();
                    if (bowl != null) { Add(bowl.m_setGlobalKey); }

                    // Runestones that record having been read.
                    Vegvisir vegvisir = prefab.GetComponent<Vegvisir>();
                    if (vegvisir != null) { Add(vegvisir.m_setsGlobalKey); }

                    // Haldor and Hildir: buying certain items can set a key.
                    Trader trader = prefab.GetComponent<Trader>();
                    if (trader != null && trader.m_items != null && TradeItemSetsKey != null) {
                        foreach (Trader.TradeItem item in trader.m_items) {
                            if (item != null) { Add(TradeItemSetsKey.GetValue(item) as string); }
                        }
                    }
                }

                // No prefab in the game sets no keys at all. An empty result means the scene is not populated
                // yet, and treating it as "clients may set nothing" would refuse every boss kill on the server.
                if (prefabKeys.Count == 0) {
                    Logger.LogDebug("Global key allowlist deferred: no prefab sets a global key yet.");
                    return false;
                }

                built = true;
                Logger.LogInfo($"Global key allowlist built: {prefabKeys.Count} key(s) set by loaded prefabs.");
                return true;
            } catch (Exception e) {
                Logger.LogWarning($"Could not build the global key allowlist, the global key guard is inactive: {e.Message}");
                return false;
            }
        }

        private static void Add(string key) {
            if (string.IsNullOrWhiteSpace(key)) { return; }
            prefabKeys.Add(NameOf(key));
        }

        /// <summary>
        /// The key's name, without any value attached to it. ZoneSystem stores "activeBosses 2" as the key
        /// "activebosses" with the value "2" (ZoneSystem.GetKeyValue), so the allowlist is about the part
        /// before the first space.
        /// </summary>
        internal static string NameOf(string key) {
            if (string.IsNullOrEmpty(key)) { return ""; }
            int space = key.IndexOf(' ');
            return (space > 0 ? key.Substring(0, space) : key).Trim();
        }

        private static bool IsAllowed(string keyName) {
            if (prefabKeys.Contains(keyName)) { return true; }
            if (EngineSetKeys.Contains(keyName)) { return true; }
            return Extras().Contains(keyName);
        }

        private static HashSet<string> Extras() {
            string raw = ValConfig.AllowedClientGlobalKeys.Value ?? "";
            if (raw == extraRaw) { return extraParsed; }

            HashSet<string> parsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in CheatToolCatalog.SplitList(raw)) {
                parsed.Add(NameOf(entry));
            }
            extraParsed = parsed;
            extraRaw = raw;
            return parsed;
        }

        /// <summary>
        /// The shared gate for both handlers. Returns true to let vanilla run.
        /// </summary>
        /// <param name="sender">Routed sender, already corrected by RoutedRpcGuard.</param>
        /// <param name="removing">True for RemoveGlobalKey, which is judged more harshly - see below.</param>
        internal static bool Allow(long sender, string key, bool removing) {
            try {
                if (!RpcGuardPolicy.Active() || !ValConfig.GuardGlobalKeys.Value) { return true; }

                // No peer behind this id means the server set the key itself - world setup, an admin console
                // command run on the server, a mod. Never filtered.
                ZNetPeer peer = ZNet.instance.GetPeer(sender);
                if (peer == null) { return true; }
                if (RpcGuardPolicy.IsExempt(peer)) { return true; }

                if (!EnsureBuilt()) { return true; }

                string name = NameOf(key);
                if (string.IsNullOrEmpty(name)) { return true; }

                // Removal is its own case. Nothing in vanilla gameplay removes a global key - only console
                // commands and world setup do - so there is no allowlist that makes a client's removal
                // legitimate, and checking one would only let a client erase the very progression keys it is
                // allowed to set.
                if (removing && ValConfig.BlockClientGlobalKeyRemoval.Value) {
                    RpcGuardPolicy.Refuse(peer, "global-key",
                        $"asked to REMOVE the global key '{RpcGuardPolicy.Trim(name)}', which no client legitimately does");
                    return false;
                }

                if (IsAllowed(name)) { return true; }

                RpcGuardPolicy.Refuse(peer, "global-key",
                    $"asked to {(removing ? "remove" : "set")} the global key '{RpcGuardPolicy.Trim(name)}', which no loaded prefab sets " +
                    "(add it to AllowedClientGlobalKeys if this is legitimate)");
                return false;
            } catch (Exception e) {
                // Let it through rather than blocking progression on a bug of ours.
                Logger.LogWarning($"Global key guard failed while checking a key, letting it through: {e}");
                return true;
            }
        }

        /// <summary>Drops the per-world allowlist. Called from the ZNet.Shutdown teardown.</summary>
        internal static void Invalidate() {
            built = false;
            prefabKeys.Clear();
            extraRaw = null;
            extraParsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    [HarmonyPatch(typeof(ZoneSystem), "RPC_SetGlobalKey")]
    internal static class ZoneSystem_RPC_SetGlobalKey_Guard {

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(long sender, string name) {
            return GlobalKeyGuard.Allow(sender, name, removing: false);
        }
    }

    [HarmonyPatch(typeof(ZoneSystem), "RPC_RemoveGlobalKey")]
    internal static class ZoneSystem_RPC_RemoveGlobalKey_Guard {

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(long sender, string name) {
            return GlobalKeyGuard.Allow(sender, name, removing: true);
        }
    }

    /// <summary>
    /// Drops the guards' per-world and per-session state, alongside the structure validator's own teardown.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
    internal static class ZNet_Shutdown_RpcGuards {

        [HarmonyPostfix]
        private static void Postfix() {
            GlobalKeyGuard.Invalidate();
            RpcGuardPolicy.Reset();
            PeerTrust.Reset();
        }
    }
}
