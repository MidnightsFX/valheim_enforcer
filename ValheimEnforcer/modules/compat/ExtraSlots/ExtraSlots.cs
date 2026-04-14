using System;

#nullable disable
namespace ValheimEnforcer.modules.compat.ExtraSlots {
    public class ExtraSlot {
        internal Func<string> _id;
        internal Func<string> _name;
        internal Func<Vector2i> _gridPosition;
        internal Func<ItemDrop.ItemData> _item;
        internal Func<ItemDrop.ItemData, bool> _itemFits;
        internal Func<bool> _isActive;
        internal Func<bool> _isFree;
        internal Func<bool> _isHotkeySlot;
        internal Func<bool> _isEquipmentSlot;
        internal Func<bool> _isQuickSlot;
        internal Func<bool> _isMiscSlot;
        internal Func<bool> _isAmmoSlot;
        internal Func<bool> _isFoodSlot;
        internal Func<bool> _isCustomSlot;
        internal Func<bool> _isEmptySlot;
        public static readonly Vector2i emptyPosition = new Vector2i(-1, -1);

        public string ID => this._id != null ? this._id() : "";

        public string Name => this._name != null ? this._name() : "";

        public Vector2i GridPosition {
            get => this._gridPosition != null ? this._gridPosition() : ExtraSlot.emptyPosition;
        }

        public ItemDrop.ItemData Item => this._item != null ? this._item() : (ItemDrop.ItemData)null;

        public bool ItemFits(ItemDrop.ItemData item) => this._itemFits != null && this._itemFits(item);

        public bool IsActive => this._isActive != null && this._isActive();

        public bool IsFree => this._isFree != null && this._isFree();

        public bool IsHotkeySlot => this._isHotkeySlot != null && this._isHotkeySlot();

        public bool IsEquipmentSlot => this._isEquipmentSlot != null && this._isEquipmentSlot();

        public bool IsQuickSlot => this._isQuickSlot != null && this._isQuickSlot();

        public bool IsMiscSlot => this._isMiscSlot != null && this._isMiscSlot();

        public bool IsAmmoSlot => this._isAmmoSlot != null && this._isAmmoSlot();

        public bool IsFoodSlot => this._isFoodSlot != null && this._isFoodSlot();

        public bool IsCustomSlot => this._isCustomSlot != null && this._isCustomSlot();

        public bool IsEmptySlot => this._isEmptySlot != null && this._isEmptySlot();
    }
}
