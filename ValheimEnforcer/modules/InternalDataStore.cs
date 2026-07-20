using Jotunn.Entities;
using Jotunn.Managers;
using PlayFab.EconomyModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.VFX;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules {
    internal static class InternalDataStore {
        static ZDO MetadataRegistry = null;

        // This is for runtime registration
        internal static void SaveAccountCharacter(DataObjects.Character character) {
            UpdateAccountRegistry(character.HostID, character.Name);
            string rawAccountData = MetadataRegistry.GetString(character.HostID, null);
            if (rawAccountData != null) {
                DataObjects.CharacterSaveData accountData = DataObjects.yamldeserializer.Deserialize<DataObjects.CharacterSaveData>(rawAccountData);
                if (accountData.SavedCharacters.ContainsKey(character.Name)) {
                    accountData.SavedCharacters[character.Name] = character;
                } else {
                    accountData.SavedCharacters.Add(character.Name, character);
                }
                string updatedAccountDataRaw = DataObjects.yamlserializer.Serialize(accountData);
                MetadataRegistry.Set(character.HostID, updatedAccountDataRaw);
                return;
            }
            DataObjects.CharacterSaveData newAccountSaveData = new DataObjects.CharacterSaveData() {
                SavedCharacters = new Dictionary<string, DataObjects.Character>() {
                    { character.Name, character }
                }
            };
            string playerData = DataObjects.yamlserializer.Serialize(newAccountSaveData);
            MetadataRegistry.Set(character.HostID, playerData);
        }

        internal static DataObjects.Character GetAccountCharacter(string accountID, string characterName) {
            InstanciateOrLinkMetadataRegistry();
            string rawAccountData = MetadataRegistry.GetString(accountID, null);
            if (rawAccountData != null) {
                Logger.LogDebug($"Character data found {accountID}-{characterName}.");
                DataObjects.CharacterSaveData accountData = DataObjects.yamldeserializer.Deserialize<DataObjects.CharacterSaveData>(rawAccountData);
                if (accountData.SavedCharacters.ContainsKey(characterName)) {
                    return accountData.SavedCharacters[characterName];
                }
            }
            return null;
        }

        internal static DataObjects.CharacterSaveData GetAccountData(string accountID) {
            InstanciateOrLinkMetadataRegistry();
            string rawAccountData = MetadataRegistry.GetString(accountID, null);
            if (rawAccountData != null) {
                DataObjects.CharacterSaveData accountData = DataObjects.yamldeserializer.Deserialize<DataObjects.CharacterSaveData>(rawAccountData);
                return accountData;
            }
            return null;
        }

        internal static void RegisterMetadataHolder() {
            GameObject game_obj = ValheimEnforcer.EmbeddedResourceBundle.LoadAsset<GameObject>("VE_METADATA");
            CustomPrefab metadataPrefab = new CustomPrefab(game_obj, false);
            PrefabManager.Instance.AddPrefab(metadataPrefab);
        }

        internal static void InstanciateOrLinkMetadataRegistry() {
            // The in-world registry only exists to back internal storage mode. Don't create the ZDO or
            // write the global key when that mode is disabled — otherwise it fires eagerly on every world load.
            if (ValConfig.InternalStorageMode.Value == false) { return; }
            if (MetadataRegistry != null) { return; }

            // Server-side only — the session id owns the registry ZDO so its writes propagate.
            long sessionID = ZDOMan.GetSessionID();

            // Re-link to a registry we've already stored in this world rather than orphaning it with a new one.
            if (ZoneSystem.instance.GetGlobalKey($"{DataObjects.CustomDataKey}", out string val)) {
                string[] parts = val.Split(' ');
                if (parts.Length == 2
                    && long.TryParse(parts[0], out long userID)
                    && uint.TryParse(parts[1], out uint objID)) {
                    ZDOID zdoid = new ZDOID(userID, objID);
                    ZDO existing = ZDOMan.instance.GetZDO(zdoid);
                    if (existing != null) {
                        existing.SetOwner(sessionID);
                        MetadataRegistry = existing;
                        Logger.LogInfo($"Linked existing Metadata Registry. SessionID:{sessionID} ZDO:{existing.m_uid}");
                        return;
                    }
                    Logger.LogWarning($"Metadata Registry global key {DataObjects.CustomDataKey}={val} present but ZDO {zdoid} could not be found; creating a new registry.");
                }
            }

            // No usable existing registry — create one and record its ZDOID in a global key so it can be re-linked later.
            ZDO metaZDO = ZDOMan.instance.CreateNewZDO(Vector3.zero, 0);
            metaZDO.Persistent = true;
            metaZDO.SetOwner(sessionID);
            MetadataRegistry = metaZDO;
            ZoneSystem.instance.SetGlobalKey($"{DataObjects.CustomDataKey} {MetadataRegistry.m_uid.UserID} {MetadataRegistry.m_uid.ID}");

            Logger.LogInfo($"Hooking up Metadata Registry. SessionID:{sessionID} ZDO:{metaZDO.m_uid}");
            Logger.LogInfo($"Setting globalkey: {DataObjects.CustomDataKey} {MetadataRegistry.m_uid.UserID} {MetadataRegistry.m_uid.ID}");
        }

        internal static void UpdateAccountRegistry(string accountID, string chara = null) {
            InstanciateOrLinkMetadataRegistry();
            string currentAccounts = MetadataRegistry.GetString("VE_ACCOUNTS", null);
            if (currentAccounts != null) {
                Dictionary<string, List<string>> accounts = DataObjects.yamldeserializer.Deserialize<Dictionary<string, List<string>>>(currentAccounts);
                if (accounts.ContainsKey(accountID) == false) {
                    if (chara != null) {
                        accounts[accountID] = new List<string>() { chara };
                    } else {
                        accounts[accountID] = new List<string>();
                    }
                    string stringified = DataObjects.yamlserializer.Serialize(accounts);
                    MetadataRegistry.Set("VE_ACCOUNTS", stringified);
                }
            } else {
                List<string> accCharas = new List<string>() { };
                if (chara != null) {
                    accCharas.Add(chara);
                }
                Dictionary<string, List<string>> accountsCharacters = new Dictionary<string, List<string>>() { { accountID, accCharas } };
                string stringified = DataObjects.yamlserializer.Serialize(accountsCharacters);
                MetadataRegistry.Set("VE_ACCOUNTS", stringified);
            }
        }

        internal static Dictionary<string, List<string>> GetAccountRegistry() {
            InstanciateOrLinkMetadataRegistry();
            string currentAccounts = MetadataRegistry.GetString("VE_ACCOUNTS", null);
            if (currentAccounts != null) {
                Dictionary<string, List<string>> accounts = DataObjects.yamldeserializer.Deserialize<Dictionary<string, List<string>>>(currentAccounts);
                return accounts;
            }
            return new Dictionary<string, List<string>>();
        }

        //internal static DataObjects.Character GetCharacterFromDataHolder(string accountID, string characterName) {

        //}
    }
}
