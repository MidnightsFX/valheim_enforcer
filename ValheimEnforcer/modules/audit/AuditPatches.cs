using HarmonyLib;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.audit {

    /// <summary>
    /// Brackets one client's ZDOData packet so <see cref="ContainerAudit"/> knows whose changes it is
    /// reading.
    ///
    /// A second patch on this method alongside the structure validator's, rather than a call added into that
    /// one. The two features are independently switchable and the validator only resolves a peer when its own
    /// checks are on, so sharing its bracket would silently tie container auditing to structure validation
    /// being enabled. The cost of the extra patch is one boolean check per packet when the audit is off.
    /// </summary>
    [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.RPC_ZDOData))]
    internal static class ZDOMan_RPC_ZDOData_Audit {

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void Prefix(ZRpc rpc) {
            ContainerAudit.BeginPacket(rpc);
        }

        // A finalizer, not a postfix: it must run even when vanilla throws, or the peer would stay set and
        // the server's own subsequent writes would be attributed to whoever sent the packet that failed.
        [HarmonyFinalizer]
        private static void Finalizer() {
            ContainerAudit.EndPacket();
        }
    }

    /// <summary>
    /// The inspection point. The ZDO is fully populated here, prefab and all, which is what makes the
    /// container check a single hash lookup.
    ///
    /// Unlike the structure validator's checks, this one genuinely has to see every ZDO the packet carried,
    /// not only the ones it created: a chest changing hands is an update to an object that has existed for
    /// weeks. So the body cannot be moved off this path - but the patch can be left off a server that is not
    /// using it, which is what Prepare does. ZDO.Deserialize is the hottest method a Valheim server runs, and
    /// the Harmony wrapper costs the same whether the body works or returns immediately.
    ///
    /// Prepare is evaluated once, at PatchAll in the plugin's Awake, which runs after the config is bound.
    /// Turning container auditing on in a running server therefore takes a restart; ContainerAudit says so in
    /// the log the first time it sees the setting on without the hook. Turning it off needs no restart - the
    /// packet bracket stops resolving a peer and the body returns on its first line.
    /// </summary>
    [HarmonyPatch(typeof(ZDO), nameof(ZDO.Deserialize))]
    internal static class ZDO_Deserialize_Audit {

        private static bool Prepare() {
            return ContainerAudit.WantsInspectHook();
        }

        [HarmonyPostfix]
        private static void Postfix(ZDO __instance) {
            ContainerAudit.Inspect(__instance);
        }
    }

    /// <summary>Starts the audit writer once the server is up, and only on a server.</summary>
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Start))]
    internal static class ZNet_Start_Audit {

        [HarmonyPostfix]
        private static void Postfix(ZNet __instance) {
            if (__instance != null && __instance.IsServer()) { AuditLog.Initialize(); }
        }
    }

    /// <summary>
    /// Flushes and stops everything when the server does. The flush is the point: a restart that lost the
    /// last unwritten events would lose them exactly when somebody was being removed from the server.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
    internal static class ZNet_Shutdown_Audit {

        [HarmonyPostfix]
        private static void Postfix() {
            AuditLog.Shutdown();
            ContainerAudit.Reset();
            DamageAudit.Reset();
        }
    }

    /// <summary>
    /// Drops a departing player's damage window. Their recorded events stay on disk - that is the whole
    /// point - but the live rolling ring is per session and would otherwise accumulate one per account that
    /// has ever fought here.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
    internal static class ZNet_Disconnect_Audit {

        [HarmonyPrefix]
        private static void Prefix(ZNet __instance, ZNetPeer peer) {
            if (__instance == null || !__instance.IsServer()) { return; }
            DamageAudit.Forget(AuditPolicy.AccountOf(peer));
        }
    }
}
