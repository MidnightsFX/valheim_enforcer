using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using ValheimEnforcer.common;
using static Mono.Security.X509.X520;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.modules.commands {
    internal static class CommandHelpers {

        public static void ClearSpecifiedPlayerConfiscatedItems(string account, string username, string prefab) {
            DataObjects.Character character = ValConfig.LoadCharacterFromSave(account, username);

            Logger.LogInfo($"Found {character.ConfiscatedItems.Count} confiscated items.");
            if (string.Compare(prefab, "all", true) == 0) {
                character.ConfiscatedItems.Clear();
                ValConfig.WritePlayerCharacterToSave(account, character);
                Logger.LogInfo($"Cleared all confiscated items.");
                return;
            }

            List<string> targetItems = prefab.Split(',').ToList();
            character.ConfiscatedItems = character.ConfiscatedItems.Where(x => targetItems.Contains(x.prefabName) == false).ToList();
            Logger.LogInfo($"Removed confiscated item with prefab {string.Join(",", targetItems)}.");
        }

        public static List<PackedItem> LoadCharacterAndFindItemsToReturn(string account, string username, string prefabfilter) {
            DataObjects.Character character = ValConfig.LoadCharacterFromSave(account, username);
            List<DataObjects.PackedItem> itemsToReturn = new List<PackedItem>();
            if (character == null) {
                Logger.LogInfo("Character was not found for the specified account.");
                return itemsToReturn;
            }
            if (character.ConfiscatedItems.Count == 0) {
                Logger.LogInfo("Player does not have any confiscated items.");
                return itemsToReturn;
            }


            if (string.Compare(prefabfilter, "all", true) == 0) {
                itemsToReturn = new List<DataObjects.PackedItem>(character.ConfiscatedItems);
                character.ConfiscatedItems.Clear();
            } else {
                List<string> targetPrefabs = prefabfilter.Split(',').Select(s => s.Trim()).ToList();
                itemsToReturn = character.ConfiscatedItems.Where(i => targetPrefabs.Contains(i.prefabName)).ToList();
                character.ConfiscatedItems.RemoveAll(i => targetPrefabs.Contains(i.prefabName));
            }

            if (itemsToReturn.Count == 0) {
                Logger.LogInfo("No matching confiscated items found for the specified filter.");
                return itemsToReturn;
            }
            return itemsToReturn;
        }
    }
}
