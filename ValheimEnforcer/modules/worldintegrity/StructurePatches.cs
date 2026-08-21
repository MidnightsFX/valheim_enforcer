using HarmonyLib;
using System;
using UnityEngine;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.worldintegrity {

    /// <summary>
    /// Brackets one client's ZDOData packet, so everything below knows which peer it is looking at.
    ///
    /// The alternative was a transpiler on RPC_ZDOData, which is where the "this ZDO is new and came from this
    /// peer" flag actually lives. Bracketing the call and hooking the two methods it drives gets the same
    /// information without this mod depending on that method's IL.
    /// </summary>
    [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.RPC_ZDOData))]
    internal static class ZDOMan_RPC_ZDOData_StructureValidation {

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void Prefix(ZRpc rpc) {
            StructureValidator.BeginPacket(rpc);
        }

        // A finalizer rather than a postfix: it runs even when vanilla throws, and leaving the watch flag set
        // would have us inspecting ZDOs the server itself wrote.
        [HarmonyFinalizer]
        private static void Finalizer() {
            StructureValidator.EndPacket();
        }
    }

    /// <summary>
    /// Notes the id of a ZDO the packet just created. RPC_ZDOData calls this immediately before deserializing
    /// that same ZDO, so one id is enough to answer "was this new" without tracking a set.
    /// </summary>
    [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.CreateNewZDO), new Type[] { typeof(ZDOID), typeof(Vector3), typeof(int) })]
    internal static class ZDOMan_CreateNewZDO_StructureValidation {

        [HarmonyPostfix]
        private static void Postfix(ZDOID uid) {
            StructureValidator.NoteCreated(uid);
        }
    }

    /// <summary>
    /// The inspection point: the ZDO is fully populated here, prefab and all. The prefix keeps the health the
    /// ZDO held beforehand so an over-limit value that was already there is not blamed on the peer that merely
    /// owns it now.
    /// </summary>
    [HarmonyPatch(typeof(ZDO), nameof(ZDO.Deserialize))]
    internal static class ZDO_Deserialize_StructureValidation {

        [HarmonyPrefix]
        private static void Prefix(ZDO __instance, ref float __state) {
            __state = StructureValidator.CaptureHealth(__instance);
        }

        [HarmonyPostfix]
        private static void Postfix(ZDO __instance, float __state) {
            StructureValidator.Inspect(__instance, __state);
        }
    }

    /// <summary>
    /// The other way in. ZNetScene.SpawnObject has no callers anywhere in the game assembly - it is a routed
    /// RPC that makes every receiver, the server included, Instantiate any prefab by hash. The ZDO that
    /// results is created by the server, so the peer-attributed path never sees it.
    /// </summary>
    [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.RPC_SpawnObject))]
    internal static class ZNetScene_RPC_SpawnObject_StructureValidation {

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(long spawner, Vector3 pos, int prefabHash) {
            return StructureValidator.AllowSpawnObject(spawner, pos, prefabHash);
        }
    }

    /// <summary>
    /// Drops the per-world prefab tables and detector state. The index is keyed to whatever content this world
    /// loaded, so carrying it into the next one would classify against the wrong game.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
    internal static class ZNet_Shutdown_StructureValidation {

        [HarmonyPostfix]
        private static void Postfix() {
            StructureSweep.Abort();
            StructureValidator.Reset();
            StructureIndex.Invalidate();
        }
    }
}
