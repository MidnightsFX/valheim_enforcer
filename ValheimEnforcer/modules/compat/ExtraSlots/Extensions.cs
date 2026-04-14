using HarmonyLib;
using System;

#nullable disable
namespace ValheimEnforcer.modules.compat.ExtraSlots {
    internal static class Extensions {
        internal static ExtraSlot ToExtraSlot(this object slot) {
            return new ExtraSlot() {
                _id = (Func<string>)(() => (string)AccessTools.Property(API._typeSlot, "ID").GetValue(slot)),
                _name = (Func<string>)(() => (string)AccessTools.Property(API._typeSlot, "Name").GetValue(slot)),
                _gridPosition = (Func<Vector2i>)(() => (Vector2i)AccessTools.Property(API._typeSlot, "GridPosition").GetValue(slot)),
                _item = (Func<ItemDrop.ItemData>)(() => (ItemDrop.ItemData)AccessTools.Property(API._typeSlot, "Item").GetValue(slot)),
                _itemFits = (Func<ItemDrop.ItemData, bool>)(item => (bool)AccessTools.Method(API._typeSlot, "ItemFits", (Type[])null, (Type[])null).Invoke(slot, new object[1]
                {
          (object) item
                })),
                _isActive = (Func<bool>)(() => (bool)AccessTools.Property(API._typeSlot, "IsActive").GetValue(slot)),
                _isFree = (Func<bool>)(() => (bool)AccessTools.Property(API._typeSlot, "IsFree").GetValue(slot)),
                _isHotkeySlot = (Func<bool>)(() => (bool)AccessTools.Property(API._typeSlot, "IsHotkeySlot").GetValue(slot)),
                _isEquipmentSlot = (Func<bool>)(() => (bool)AccessTools.Property(API._typeSlot, "IsEquipmentSlot").GetValue(slot)),
                _isQuickSlot = (Func<bool>)(() => (bool)AccessTools.Property(API._typeSlot, "IsQuickSlot").GetValue(slot)),
                _isMiscSlot = (Func<bool>)(() => (bool)AccessTools.Property(API._typeSlot, "IsMiscSlot").GetValue(slot)),
                _isAmmoSlot = (Func<bool>)(() => (bool)AccessTools.Property(API._typeSlot, "IsAmmoSlot").GetValue(slot)),
                _isFoodSlot = (Func<bool>)(() => (bool)AccessTools.Property(API._typeSlot, "IsFoodSlot").GetValue(slot)),
                _isCustomSlot = (Func<bool>)(() => (bool)AccessTools.Property(API._typeSlot, "IsCustomSlot").GetValue(slot)),
                _isEmptySlot = (Func<bool>)(() => (bool)AccessTools.Property(API._typeSlot, "IsEmptySlot").GetValue(slot))
            };
        }
    }
}
