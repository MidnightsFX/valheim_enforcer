using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.worldintegrity {

    /// <summary>
    /// Works out, from the content this world actually loaded, which items a player can only ever have by
    /// crafting them.
    ///
    /// The question this exists to answer is narrower than it first looks. A crafted item carries its crafter's
    /// id; an item conjured with devcommands or a mod menu carries zero. But zero is <b>not</b> abnormal on its
    /// own - <c>ItemDrop.ItemData.m_crafterID</c> simply defaults to it, and only the crafting path ever sets
    /// it, so every loot drop, chest item, trader purchase and dungeon pickup in the game has no crafter
    /// either. Flagging "no crafter" alone would report a Dvergr circlet and a fishing rod bought from Haldor.
    ///
    /// So the useful signal is "equipment that has no crafter <i>and</i> no way of existing without one", and
    /// the world's own data answers the second half: every prefab reachable from a drop table, a creature's
    /// drop list, a pickable or a trader's stock is obtainable without crafting. Anything else with an equip
    /// slot should have a crafter.
    ///
    /// Derived rather than listed, on the same reasoning as <see cref="StructureIndex"/>: a modded boss with a
    /// custom weapon drop is covered without anyone maintaining a list, and the check stays correct across game
    /// updates that move items between loot tables.
    /// </summary>
    internal static class ItemOriginIndex {

        /// <summary>
        /// Item types that occupy an equip slot, which is what makes an item worth having a crafter.
        ///
        /// Materials, consumables, ammo, trophies and fish are deliberately out. They are the loot-heavy end of
        /// the game, they are crafted and gathered interchangeably, and none of them is what somebody spawns
        /// when they want an advantage.
        /// </summary>
        private static readonly HashSet<ItemDrop.ItemData.ItemType> EquipmentTypes = new HashSet<ItemDrop.ItemData.ItemType> {
            ItemDrop.ItemData.ItemType.OneHandedWeapon,
            ItemDrop.ItemData.ItemType.TwoHandedWeapon,
            ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft,
            ItemDrop.ItemData.ItemType.Bow,
            ItemDrop.ItemData.ItemType.Shield,
            ItemDrop.ItemData.ItemType.Helmet,
            ItemDrop.ItemData.ItemType.Chest,
            ItemDrop.ItemData.ItemType.Legs,
            ItemDrop.ItemData.ItemType.Hands,
            ItemDrop.ItemData.ItemType.Shoulder,
            ItemDrop.ItemData.ItemType.Utility,
            ItemDrop.ItemData.ItemType.Tool,
            ItemDrop.ItemData.ItemType.Torch,
            ItemDrop.ItemData.ItemType.Trinket,
        };

        private static bool built;

        /// <summary>Prefab name -> is this equipment. Case-insensitive: item names arrive from a save file.</summary>
        private static readonly Dictionary<string, bool> equipment = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Prefab names obtainable without crafting them.</summary>
        private static readonly HashSet<string> uncraftedObtainable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Per component TYPE, the fields that can name items. Resolved once per type rather than per prefab -
        // there are thousands of prefabs and a few hundred component types, and doing this the other way round
        // is the difference between a scan that costs nothing and one that stalls world load.
        private static readonly Dictionary<Type, FieldInfo[]> dropTableFields = new Dictionary<Type, FieldInfo[]>();
        private static readonly Dictionary<Type, FieldInfo[]> itemPrefabFields = new Dictionary<Type, FieldInfo[]>();

        /// <summary>
        /// Builds the index on first use. Returns false while the world is not far enough along to answer, in
        /// which case every caller treats the item as fine - a detector that cannot tell what an item is must
        /// not guess, and guessing wrong here accuses a player of cheating.
        /// </summary>
        internal static bool EnsureBuilt() {
            if (built) { return true; }
            if (ZNetScene.instance == null || ZNetScene.instance.m_prefabs == null) { return false; }
            if (ObjectDB.instance == null || ObjectDB.instance.m_items == null || ObjectDB.instance.m_items.Count == 0) { return false; }

            try {
                equipment.Clear();
                uncraftedObtainable.Clear();

                CollectItemTypes();
                if (equipment.Count == 0) {
                    Logger.LogDebug("Item origin index deferred: no items are registered yet.");
                    return false;
                }

                StallWatch timer = StallWatch.Start("Item origin index build");
                CollectUncraftedSources();
                timer.Stop();

                built = true;
                int equipCount = 0;
                foreach (bool isEquipment in equipment.Values) { if (isEquipment) { equipCount++; } }
                Logger.LogInfo($"Item origin index built: {equipment.Count} item prefab(s), {equipCount} of them equipment, {uncraftedObtainable.Count} obtainable without crafting.");
                return true;
            } catch (Exception e) {
                Logger.LogWarning($"Could not build the item origin index, uncrafted-equipment detection is inactive: {e.Message}");
                return false;
            }
        }

        /// <summary>Every item prefab in the object DB, and whether it takes an equip slot.</summary>
        private static void CollectItemTypes() {
            foreach (GameObject item in ObjectDB.instance.m_items) {
                if (item == null) { continue; }
                ItemDrop drop = item.GetComponent<ItemDrop>();
                if (drop == null || drop.m_itemData == null || drop.m_itemData.m_shared == null) { continue; }
                equipment[item.name] = EquipmentTypes.Contains(drop.m_itemData.m_shared.m_itemType);
            }
        }

        /// <summary>
        /// Everything a player can get hold of without crafting it.
        ///
        /// Found by reflection over the loaded prefabs rather than by naming the components that hold loot.
        /// There are nine different fields of type DropTable across vanilla alone - m_defaultItems,
        /// m_dropWhenDestroyed, m_extraDrops, m_items, m_dropItems - and a content mod is free to add a tenth.
        /// Asking "does this component have a DropTable in it" is both shorter and correct for content that did
        /// not exist when this was written.
        /// </summary>
        private static void CollectUncraftedSources() {
            // Reused across every prefab. The allocating GetComponentsInChildren overload returns a fresh array
            // per call, and this runs over every prefab in the game the first time a delta arrives - on the main
            // thread - so that is a few thousand arrays for a scan that happens once.
            List<Component> components = new List<Component>();

            foreach (GameObject prefab in ZNetScene.instance.m_prefabs) {
                if (prefab == null) { continue; }

                prefab.GetComponentsInChildren<Component>(true, components);
                foreach (Component component in components) {
                    // A missing script leaves a null component behind; touching its type throws.
                    if (component == null) { continue; }
                    Type type = component.GetType();

                    foreach (FieldInfo field in DropTableFieldsOf(type)) {
                        AddDropTable(field.GetValue(component) as DropTable);
                    }
                    foreach (FieldInfo field in ItemPrefabFieldsOf(type)) {
                        AddPrefab(field.GetValue(component) as GameObject);
                    }

                    // The two collections that hold items but are not DropTables.
                    CharacterDrop creatureDrops = component as CharacterDrop;
                    if (creatureDrops != null && creatureDrops.m_drops != null) {
                        foreach (CharacterDrop.Drop drop in creatureDrops.m_drops) {
                            if (drop != null) { AddPrefab(drop.m_prefab); }
                        }
                    }
                    Trader trader = component as Trader;
                    if (trader != null && trader.m_items != null) {
                        foreach (Trader.TradeItem sold in trader.m_items) {
                            if (sold != null && sold.m_prefab != null) { AddPrefab(sold.m_prefab.gameObject); }
                        }
                    }
                }
            }
        }

        private static void AddDropTable(DropTable table) {
            if (table == null || table.m_drops == null) { return; }
            foreach (DropTable.DropData drop in table.m_drops) {
                AddPrefab(drop.m_item);
            }
        }

        private static void AddPrefab(GameObject prefab) {
            if (prefab == null) { return; }
            uncraftedObtainable.Add(prefab.name);
        }

        private static FieldInfo[] DropTableFieldsOf(Type type) {
            if (dropTableFields.TryGetValue(type, out FieldInfo[] cached)) { return cached; }
            List<FieldInfo> found = new List<FieldInfo>();
            try {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance)) {
                    if (field.FieldType == typeof(DropTable)) { found.Add(field); }
                }
            } catch (Exception e) {
                Logger.LogDebug($"Could not reflect over {type.Name} for drop tables: {e.Message}");
            }
            FieldInfo[] result = found.ToArray();
            dropTableFields[type] = result;
            return result;
        }

        /// <summary>
        /// GameObject fields that name a single item directly - Pickable.m_itemPrefab and its equivalents.
        /// Matched by name because a bare GameObject field says nothing about what it holds, and adding every
        /// GameObject a prefab references would pull in half the game.
        /// </summary>
        private static FieldInfo[] ItemPrefabFieldsOf(Type type) {
            if (itemPrefabFields.TryGetValue(type, out FieldInfo[] cached)) { return cached; }
            List<FieldInfo> found = new List<FieldInfo>();
            try {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance)) {
                    if (field.FieldType != typeof(GameObject)) { continue; }
                    if (field.Name.IndexOf("itemprefab", StringComparison.OrdinalIgnoreCase) >= 0
                        || field.Name.IndexOf("spawnitem", StringComparison.OrdinalIgnoreCase) >= 0) {
                        found.Add(field);
                    }
                }
            } catch (Exception e) {
                Logger.LogDebug($"Could not reflect over {type.Name} for item prefabs: {e.Message}");
            }
            FieldInfo[] result = found.ToArray();
            itemPrefabFields[type] = result;
            return result;
        }

        /// <summary>True when this prefab takes an equip slot. False for anything the index does not know.</summary>
        internal static bool IsEquipment(string prefabName) {
            if (string.IsNullOrEmpty(prefabName)) { return false; }
            return equipment.TryGetValue(prefabName, out bool isEquipment) && isEquipment;
        }

        /// <summary>True when a player can obtain this without crafting it, so having no crafter is expected.</summary>
        internal static bool IsObtainableUncrafted(string prefabName) {
            return !string.IsNullOrEmpty(prefabName) && uncraftedObtainable.Contains(prefabName);
        }

        /// <summary>True when the index has an opinion about this prefab at all.</summary>
        internal static bool Knows(string prefabName) {
            return !string.IsNullOrEmpty(prefabName) && equipment.ContainsKey(prefabName);
        }

        internal static bool IsBuilt() { return built; }

        /// <summary>Every equipment prefab that should carry a crafter, for the terminal command.</summary>
        internal static List<string> CraftOnlyEquipment() {
            List<string> names = new List<string>();
            foreach (KeyValuePair<string, bool> entry in equipment) {
                if (entry.Value && !uncraftedObtainable.Contains(entry.Key)) { names.Add(entry.Key); }
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        /// <summary>Drops the per-world tables. Called from the ZNet.Shutdown teardown.</summary>
        internal static void Invalidate() {
            built = false;
            equipment.Clear();
            uncraftedObtainable.Clear();
        }
    }
}
