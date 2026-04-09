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
            InstanciateOrLinkMetadataRegistry();
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
            if (MetadataRegistry == null) {
                if (ZoneSystem.instance.GetGlobalKey($"{DataObjects.CustomDataKey}", out string val)) {
                    string[] parts = val.Split(' ');
                    if (parts.Length == 2
                        && long.TryParse(parts[0], out long userID)
                        && uint.TryParse(parts[1], out uint objID)) {
                        ZDOID zdoid = new ZDOID(userID, objID);
                        ZDO zdo = ZDOMan.instance.GetZDO(zdoid);
                        MetadataRegistry = zdo;
                    }
                }

                // Server-side only — do this once, store the ZDOID somewhere you can look it up
                long sessionID = ZDOMan.GetSessionID();
                ZDO metaZDO = ZDOMan.instance.CreateNewZDO(Vector3.zero, 0);
                metaZDO.Persistent = true;
                metaZDO.SetOwner(sessionID);
                MetadataRegistry = metaZDO;
                ZoneSystem.instance.SetGlobalKey($"{DataObjects.CustomDataKey} {MetadataRegistry.m_uid.UserID} {MetadataRegistry.m_uid.ID}");

                Logger.LogInfo($"Hooking up Metadata Registry. SessionID:{sessionID} ZDO:{metaZDO.m_uid}");
                Logger.LogInfo($"Setting globalkey: {DataObjects.CustomDataKey} {MetadataRegistry.m_uid.UserID} {MetadataRegistry.m_uid.ID}");
                //GameObject loaded = ZNetScene.instance.GetPrefab("VE_METADATA(Clone)");
                //if (loaded != null) {
                //    ZNetView zview = loaded.GetComponent<ZNetView>();
                //    Logger.LogInfo($"Found existing VE_METADATA Storage. {loaded} {zview} {zview.IsValid()}");
                //    MetadataRegistry = zview.GetZDO();
                //    return;
                //}

                //GameObject go = UnityEngine.Object.Instantiate(PrefabManager.Instance.GetPrefab("VE_METADATA"), Vector3.zero, Quaternion.identity);
                //ZNetView view = go.GetComponent<ZNetView>();

                //Logger.LogDebug($"Instantiating metadata registry... {go} {view} {view.IsValid()}");
                //MetadataRegistry = view.GetZDO();
            }
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
