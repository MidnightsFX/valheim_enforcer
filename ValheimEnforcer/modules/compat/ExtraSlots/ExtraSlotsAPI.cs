using BepInEx.Bootstrap;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

#nullable disable
namespace ValheimEnforcer.modules.compat.ExtraSlots {
    public static class API {
        private static bool _isNotReady;
        private static readonly List<ItemDrop.ItemData> _emptyItemList = new List<ItemDrop.ItemData>();
        private static readonly List<ExtraSlot> _emptySlotList = new List<ExtraSlot>();
        internal static Type _typeAPI;
        internal static Type _typeSlot;

        public static bool IsReady() {
            if (API._isNotReady)
                return false;
            if (API._typeAPI != (Type)null && API._typeSlot != (Type)null)
                return true;
            API._isNotReady = !Chainloader.PluginInfos.ContainsKey("shudnal.ExtraSlots");
            if (API._isNotReady)
                return false;
            if (API._typeAPI == (Type)null || API._typeSlot == (Type)null) {
                Assembly assembly = Assembly.GetAssembly(Chainloader.PluginInfos["shudnal.ExtraSlots"].Instance.GetType());
                if (assembly == (Assembly)null) {
                    API._isNotReady = true;
                    return false;
                }
                API._typeAPI = assembly.GetType("ExtraSlots.API");
                API._typeSlot = assembly.GetType("ExtraSlots.Slots+Slot");
            }
            return API._typeAPI != (Type)null && API._typeSlot != (Type)null;
        }

        public static List<ExtraSlot> GetExtraSlots() {
            return !API.IsReady() ? API._emptySlotList : ((IEnumerable<object>)AccessTools.Method(API._typeAPI, nameof(GetExtraSlots), (Type[])null, (Type[])null).Invoke((object)API._typeAPI, (object[])null)).Select<object, ExtraSlot>((Func<object, ExtraSlot>)(slot => slot.ToExtraSlot())).ToList<ExtraSlot>();
        }

        public static List<ExtraSlot> GetEquipmentSlots() {
            return !API.IsReady() ? API._emptySlotList : ((IEnumerable<object>)AccessTools.Method(API._typeAPI, nameof(GetEquipmentSlots), (Type[])null, (Type[])null).Invoke((object)API._typeAPI, (object[])null)).Select<object, ExtraSlot>((Func<object, ExtraSlot>)(slot => slot.ToExtraSlot())).ToList<ExtraSlot>();
        }

        public static List<ExtraSlot> GetQuickSlots() {
            return !API.IsReady() ? API._emptySlotList : ((IEnumerable<object>)AccessTools.Method(API._typeAPI, nameof(GetQuickSlots), (Type[])null, (Type[])null).Invoke((object)API._typeAPI, (object[])null)).Select<object, ExtraSlot>((Func<object, ExtraSlot>)(slot => slot.ToExtraSlot())).ToList<ExtraSlot>();
        }

        public static List<ExtraSlot> GetFoodSlots() {
            return !API.IsReady() ? API._emptySlotList : ((IEnumerable<object>)AccessTools.Method(API._typeAPI, nameof(GetFoodSlots), (Type[])null, (Type[])null).Invoke((object)API._typeAPI, (object[])null)).Select<object, ExtraSlot>((Func<object, ExtraSlot>)(slot => slot.ToExtraSlot())).ToList<ExtraSlot>();
        }

        public static List<ExtraSlot> GetAmmoSlots() {
            return !API.IsReady() ? API._emptySlotList : ((IEnumerable<object>)AccessTools.Method(API._typeAPI, nameof(GetAmmoSlots), (Type[])null, (Type[])null).Invoke((object)API._typeAPI, (object[])null)).Select<object, ExtraSlot>((Func<object, ExtraSlot>)(slot => slot.ToExtraSlot())).ToList<ExtraSlot>();
        }

        public static List<ExtraSlot> GetMiscSlots() {
            return !API.IsReady() ? API._emptySlotList : ((IEnumerable<object>)AccessTools.Method(API._typeAPI, nameof(GetMiscSlots), (Type[])null, (Type[])null).Invoke((object)API._typeAPI, (object[])null)).Select<object, ExtraSlot>((Func<object, ExtraSlot>)(slot => slot.ToExtraSlot())).ToList<ExtraSlot>();
        }

        public static ExtraSlot FindSlot(string slotID) {
            if (!API.IsReady())
                return (ExtraSlot)null;
            return AccessTools.Method(API._typeAPI, nameof(FindSlot), (Type[])null, (Type[])null).Invoke((object)API._typeAPI, new object[1]
            {
        (object) slotID
            }).ToExtraSlot();
        }

        public static List<ItemDrop.ItemData> GetAllExtraSlotsItems() {
            return !API.IsReady() ? API._emptyItemList : (List<ItemDrop.ItemData>)AccessTools.Method(API._typeAPI, nameof(GetAllExtraSlotsItems), (Type[])null, (Type[])null).Invoke((object)API._typeAPI, (object[])null);
        }

        public static List<ItemDrop.ItemData> GetEquipmentSlotsItems() {
            return !API.IsReady() ? API._emptyItemList : (List<ItemDrop.ItemData>)AccessTools.Method(API._typeAPI, nameof(GetEquipmentSlotsItems), (Type[])null, (Type[])null).Invoke((object)API._typeAPI, (object[])null);
        }

        public static List<ItemDrop.ItemData> GetQuickSlotsItems() {
            return !API.IsReady() ? API._emptyItemList : (List<ItemDrop.ItemData>)AccessTools.Method(API._typeAPI, nameof(GetQuickSlotsItems), (Type[])null, (Type[])null).Invoke((object)API._typeAPI, (object[])null);
        }

        public static List<ItemDrop.ItemData> GetFoodSlotsItems() {
            return !API.IsReady() ? API._emptyItemList : (List<ItemDrop.ItemData>)AccessTools.Method(API._typeAPI, nameof(GetFoodSlotsItems), (Type[])null, (Type[])null).Invoke((object)API._typeAPI, (object[])null);
        }

        public static List<ItemDrop.ItemData> GetAmmoSlotsItems() {
            return !API.IsReady() ? API._emptyItemList : (List<ItemDrop.ItemData>)AccessTools.Method(API._typeAPI, nameof(GetAmmoSlotsItems), (Type[])null, (Type[])null).Invoke((object)API._typeAPI, (object[])null);
        }

        public static List<ItemDrop.ItemData> GetMiscSlotsItems() {
            return !API.IsReady() ? API._emptyItemList : (List<ItemDrop.ItemData>)AccessTools.Method(API._typeAPI, nameof(GetMiscSlotsItems), (Type[])null, (Type[])null).Invoke((object)API._typeAPI, (object[])null);
        }

        public static int GetExtraRows() {
            return !API.IsReady() ? -1 : (int)AccessTools.Method(API._typeAPI, nameof(GetExtraRows), (Type[])null, (Type[])null).Invoke((object)API._typeAPI, (object[])null);
        }

        public static int GetInventoryHeightFull() {
            return !API.IsReady() ? -1 : (int)AccessTools.Method(API._typeAPI, nameof(GetInventoryHeightFull), (Type[])null, (Type[])null).Invoke((object)API._typeAPI, (object[])null);
        }

        public static int GetInventoryHeightPlayer() {
            return !API.IsReady() ? -1 : (int)AccessTools.Method(API._typeAPI, nameof(GetInventoryHeightPlayer), (Type[])null, (Type[])null).Invoke((object)API._typeAPI, (object[])null);
        }

        public static bool IsGridPositionASlot(Vector2i gridPos) {
            if (!API.IsReady())
                return false;
            return (bool)AccessTools.Method(API._typeAPI, nameof(IsGridPositionASlot), (Type[])null, (Type[])null).Invoke((object)API._typeAPI, new object[1]
            {
        (object) gridPos
            });
        }

        public static bool IsItemInSlot(ItemDrop.ItemData item) {
            if (!API.IsReady())
                return false;
            return (bool)AccessTools.Method(API._typeAPI, nameof(IsItemInSlot), (Type[])null, (Type[])null).Invoke((object)API._typeAPI, new object[1]
            {
        (object) item
            });
        }

        public static bool IsItemInEquipmentSlot(ItemDrop.ItemData item) {
            if (!API.IsReady())
                return false;
            return (bool)AccessTools.Method(API._typeAPI, nameof(IsItemInEquipmentSlot), (Type[])null, (Type[])null).Invoke((object)API._typeAPI, new object[1]
            {
        (object) item
            });
        }

        public static bool IsAnyGlobalKeyActive(string requiredKeys) {
            if (!API.IsReady())
                return false;
            return (bool)AccessTools.Method(API._typeAPI, nameof(requiredKeys), (Type[])null, (Type[])null).Invoke((object)API._typeAPI, new object[1]
            {
        (object) requiredKeys
            });
        }

        public static bool IsItemTypeKnown(ItemDrop.ItemData.ItemType itemType) {
            if (!API.IsReady())
                return false;
            return (bool)AccessTools.Method(API._typeAPI, nameof(IsItemTypeKnown), (Type[])null, (Type[])null).Invoke((object)API._typeAPI, new object[1]
            {
        (object) itemType
            });
        }

        public static bool IsAnyMaterialDiscovered(string itemNames) {
            if (!API.IsReady())
                return false;
            return (bool)AccessTools.Method(API._typeAPI, nameof(IsAnyMaterialDiscovered), (Type[])null, (Type[])null).Invoke((object)API._typeAPI, new object[1]
            {
        (object) itemNames
            });
        }

        public static bool AddSlot(
          string slotID,
          Func<string> getName,
          Func<ItemDrop.ItemData, bool> itemIsValid,
          Func<bool> isActive) {
            if (!API.IsReady())
                return false;
            return (bool)AccessTools.Method(API._typeAPI, nameof(AddSlot), (Type[])null, (Type[])null).Invoke((object)API._typeAPI, new object[5]
            {
        (object) slotID,
        (object) -1,
        (object) getName,
        (object) itemIsValid,
        (object) isActive
            });
        }

        public static bool AddSlotWithIndex(
          string slotID,
          int slotIndex,
          Func<string> getName,
          Func<ItemDrop.ItemData, bool> itemIsValid,
          Func<bool> isActive) {
            if (!API.IsReady())
                return false;
            return (bool)AccessTools.Method(API._typeAPI, nameof(AddSlotWithIndex), (Type[])null, (Type[])null).Invoke((object)API._typeAPI, new object[5]
            {
        (object) slotID,
        (object) slotIndex,
        (object) getName,
        (object) itemIsValid,
        (object) isActive
            });
        }

        public static bool AddSlotBefore(
          string slotID,
          Func<string> getName,
          Func<ItemDrop.ItemData, bool> itemIsValid,
          Func<bool> isActive,
          params string[] slotIDs) {
            if (!API.IsReady())
                return false;
            return (bool)AccessTools.Method(API._typeAPI, nameof(AddSlotBefore), (Type[])null, (Type[])null).Invoke((object)API._typeAPI, new object[5]
            {
        (object) slotID,
        (object) getName,
        (object) itemIsValid,
        (object) isActive,
        (object) slotIDs
            });
        }

        public static bool AddSlotAfter(
          string slotID,
          Func<string> getName,
          Func<ItemDrop.ItemData, bool> itemIsValid,
          Func<bool> isActive,
          params string[] slotIDs) {
            if (!API.IsReady())
                return false;
            return (bool)AccessTools.Method(API._typeAPI, nameof(AddSlotAfter), (Type[])null, (Type[])null).Invoke((object)API._typeAPI, new object[5]
            {
        (object) slotID,
        (object) getName,
        (object) itemIsValid,
        (object) isActive,
        (object) slotIDs
            });
        }

        public static bool RemoveSlot(string slotID) {
            if (!API.IsReady())
                return false;
            return (bool)AccessTools.Method(API._typeAPI, nameof(RemoveSlot), (Type[])null, (Type[])null).Invoke((object)API._typeAPI, new object[1]
            {
        (object) slotID
            });
        }

        public static void UpdateSlots() {
            if (!API.IsReady())
                return;
            AccessTools.Method(API._typeAPI, nameof(UpdateSlots), (Type[])null, (Type[])null).Invoke((object)API._typeAPI, (object[])null);
        }
    }
}
