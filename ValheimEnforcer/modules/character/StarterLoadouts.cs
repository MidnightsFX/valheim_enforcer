using System;
using System.Collections.Generic;
using System.IO;
using ValheimEnforcer.common;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.modules.character {

    /// <summary>
    /// Starting kits for characters this server has never seen: items, skill levels, a head start on what
    /// they know, and where they wake up.
    ///
    /// Sits on the other side of the same decision the new-character rules make. Those say what a character
    /// may KEEP of whatever it arrived with; this says what the server HANDS it once that is settled. The
    /// order is what keeps the two from fighting - the strip runs first and the grant second - so an item in a
    /// loadout never has to be listed in NewCharacterStartingItems as well, and a loadout may grant an
    /// upgraded item even though the allowlist refuses one that turns up on its own.
    ///
    /// Split the same way every other join rule is. The record is changed by a pure transformation inside
    /// <see cref="NewCharacterRules"/>, which the server runs on its own copy; the live player is changed
    /// here, on the client, which is the only side with an inventory to put anything into.
    /// </summary>
    internal static class StarterLoadouts {

        internal const string FileName = "Loadouts.yaml";

        internal static string FilePath {
            get { return Path.Combine(ValConfig.GetSecondaryConfigDirectoryPath(), FileName); }
        }

        internal static bool Enabled {
            get { return ValConfig.EnableStarterLoadouts != null && ValConfig.EnableStarterLoadouts.Value; }
        }

        // Replaced wholesale on a reload rather than mutated, so anything already holding one keeps reading a
        // consistent set - the same shape as NewCharacterRules.StartingPrefabs and AccountCharacterLimit.
        private static Loadouts loaded = new Loadouts();

        /// <summary>
        /// Reads the file once at startup.
        ///
        /// Deliberately does NOT register a watcher. Loadouts.yaml is one of the files
        /// <see cref="ValConfig.LoadYamlConfigs"/> creates and watches, and its edits arrive back here through
        /// <see cref="LoadFromText"/> - the same route KnownCheaters.yaml and Notifications.yaml take. Doing
        /// both threw on the duplicate path and, because this runs from the ValConfig constructor, took the
        /// rest of the plugin's startup with it.
        /// </summary>
        internal static void Initialize() {
            Load();
        }

        internal static void Load() {
            try {
                if (!File.Exists(FilePath)) { loaded = new Loadouts(); return; }
                LoadFromText(File.ReadAllText(FilePath));
            } catch (Exception e) {
                Report(e);
            }
        }

        /// <summary>Parses loadouts from yaml text - the file watcher's reload path.</summary>
        internal static void LoadFromText(string text) {
            try {
                Loadouts parsed = DataObjects.yamldeserializer.Deserialize<Loadouts>(text);
                // A file that parses to nothing, or whose one key is absent, is an empty set rather than a
                // null one. Every reader below would otherwise have to guard, and one that forgot would take
                // a join down.
                if (parsed == null) { parsed = new Loadouts(); }
                if (parsed.loadouts == null) { parsed.loadouts = new Dictionary<string, Loadout>(); }
                loaded = parsed;
                Logger.LogInfo($"Loaded {loaded.loadouts.Count} starter loadout(s) from {FileName}.");
            } catch (Exception e) {
                Report(e);
            }
        }

        // The previous set is kept. A file an admin has just broken should not silently remove the kit every
        // new player is getting - and unlike the mod list there is nothing here that a half-read file could
        // make LESS strict.
        private static void Report(Exception e) {
            Logger.LogError($"Could not read {FileName} ({e.Message}). Keeping the {loaded.loadouts.Count} loadout(s) already loaded; fix the file and it will be re-read.");
        }

        /// <summary>Every loadout name, for the commands and their tab completion.</summary>
        internal static List<string> Names() {
            List<string> names = new List<string>(loaded.loadouts.Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        /// <summary>One loadout by name, or null. Tolerant of case, because an admin types these.</summary>
        internal static Loadout Find(string name) {
            if (string.IsNullOrEmpty(name)) { return null; }
            foreach (KeyValuePair<string, Loadout> entry in loaded.loadouts) {
                if (string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase)) { return entry.Value; }
            }
            return null;
        }

        /// <summary>
        /// Main thread only. The loadout a brand new character should get, or null when there is none.
        /// Captured into the policy so the CharacterStore worker never reads a ConfigEntry or this file.
        /// </summary>
        internal static Loadout Current() {
            if (!Enabled) { return null; }
            string name = ValConfig.DefaultStarterLoadout != null ? ValConfig.DefaultStarterLoadout.Value : "";
            if (string.IsNullOrWhiteSpace(name)) { return null; }
            Loadout loadout = Find(name.Trim());
            if (loadout == null) {
                Logger.LogWarning($"DefaultStarterLoadout names '{name}', which is not in {FileName}. New characters get nothing until the two agree.");
            }
            return loadout;
        }

        // ---------------------------------------------------------------------------------------------
        // The record (pure - also runs server side, on the store's worker)
        // ---------------------------------------------------------------------------------------------

        /// <summary>What a grant actually did, for the log line.</summary>
        internal sealed class Result {
            internal int ItemsGranted;
            internal int SkillsRaised;
            internal int KnownAdded;
            internal bool SpawnSet;

            internal bool Changed {
                get { return ItemsGranted > 0 || SkillsRaised > 0 || KnownAdded > 0 || SpawnSet; }
            }

            internal string Describe() {
                List<string> parts = new List<string>();
                if (ItemsGranted > 0) { parts.Add($"{ItemsGranted} item(s)"); }
                if (SkillsRaised > 0) { parts.Add($"{SkillsRaised} skill(s)"); }
                if (KnownAdded > 0) { parts.Add($"{KnownAdded} known item(s)"); }
                if (SpawnSet) { parts.Add("a spawn point"); }
                return parts.Count == 0 ? "nothing" : string.Join(", ", parts.ToArray());
            }
        }

        /// <summary>
        /// Writes a loadout into a character record. Pure data - safe on the CharacterStore worker.
        ///
        /// <paramref name="progressionTracked"/> and <paramref name="spawnTracked"/> come from the progression
        /// sync settings: without them the server has nowhere to record what a character knows or where it
        /// wakes up, so those parts of a loadout are skipped rather than written into a record that would
        /// never be read back.
        /// </summary>
        internal static Result ApplyToRecord(DataObjects.Character character, Loadout loadout,
                                             bool progressionTracked, bool spawnTracked) {
            Result result = new Result();
            if (character == null || loadout == null) { return result; }

            if (loadout.items != null) {
                if (character.PlayerItems == null) { character.PlayerItems = new List<PackedItem>(); }
                foreach (LoadoutItem item in loadout.items) {
                    if (item == null || string.IsNullOrEmpty(item.prefabName)) { continue; }
                    character.PlayerItems.Add(new PackedItem {
                        prefabName = item.prefabName,
                        m_stack = Math.Max(1, item.stack),
                        m_quality = Math.Max(1, item.quality),
                        m_variant = item.variant,
                        m_durability = float.MaxValue, // "full", whatever this item's maximum turns out to be
                        m_equipped = item.equipped,
                        m_crafterName = "",
                    });
                    result.ItemsGranted++;
                }
            }

            if (loadout.skills != null) {
                if (character.SkillLevels == null) { character.SkillLevels = new Dictionary<Skills.SkillType, float>(); }
                foreach (KeyValuePair<Skills.SkillType, float> entry in loadout.skills) {
                    // Only ever upwards. A loadout is a gift, and one that could lower a level would be a
                    // second, quieter copy of NewCharacterSetSkillsToZero.
                    if (character.SkillLevels.TryGetValue(entry.Key, out float held) && held >= entry.Value) { continue; }
                    character.SkillLevels[entry.Key] = entry.Value;
                    result.SkillsRaised++;
                }
            }

            if (progressionTracked && (loadout.knownMaterials != null || loadout.knownRecipes != null)) {
                if (character.Progress == null) { character.Progress = new Progression(); }
                result.KnownAdded += AddAll(character.Progress, loadout.knownMaterials, materials: true);
                result.KnownAdded += AddAll(character.Progress, loadout.knownRecipes, materials: false);
            }

            if (spawnTracked && loadout.haveSpawnPoint) {
                if (character.Progress == null) { character.Progress = new Progression(); }
                character.Progress.Spawn = new SpawnPoints {
                    HaveCustomSpawn = true,
                    SpawnX = loadout.spawnX, SpawnY = loadout.spawnY, SpawnZ = loadout.spawnZ,
                    HomeX = loadout.spawnX, HomeY = loadout.spawnY, HomeZ = loadout.spawnZ,
                };
                result.SpawnSet = true;
            }

            return result;
        }

        private static int AddAll(Progression progress, List<string> values, bool materials) {
            if (values == null || values.Count == 0) { return 0; }
            // Assigned back through the property rather than with a ref to it - a property cannot be passed
            // by reference, and null still has to become a list somewhere.
            if (materials && progress.KnownMaterials == null) { progress.KnownMaterials = new List<string>(); }
            if (!materials && progress.KnownRecipes == null) { progress.KnownRecipes = new List<string>(); }
            List<string> target = materials ? progress.KnownMaterials : progress.KnownRecipes;
            int added = 0;
            foreach (string value in values) {
                if (string.IsNullOrEmpty(value) || target.Contains(value)) { continue; }
                target.Add(value);
                added++;
            }
            return added;
        }

        // ---------------------------------------------------------------------------------------------
        // The live player (client only)
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Puts the loadout into the hands of the player standing there.
        ///
        /// Done explicitly rather than left to AddMissingItemsFromPlayerServerSave, which would otherwise
        /// notice the record holding items the inventory does not and put them in. That would work, but it
        /// would make a loadout silently depend on a setting that has nothing to do with it - and give
        /// nothing at all to a server with it off. Running both is safe: the restore compares prefab and
        /// stack against what is held, and by then these are held.
        /// </summary>
        internal static void ApplyToPlayer(Player player, Loadout loadout, string characterName) {
            if (player == null || loadout == null) { return; }

            int granted = 0;
            if (loadout.items != null) {
                foreach (LoadoutItem item in loadout.items) {
                    if (item == null || string.IsNullOrEmpty(item.prefabName)) { continue; }
                    // Looked up first so a typo in a prefab name is reported as one. AddItem would answer a
                    // missing prefab with the same null it uses for a full inventory, and those are very
                    // different things to tell an admin.
                    UnityEngine.GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(item.prefabName) : null;
                    if (prefab == null || prefab.GetComponent<ItemDrop>() == null) {
                        Logger.LogWarning($"Starter loadout: no item prefab called '{item.prefabName}'. Check the spelling in {FileName}.");
                        continue;
                    }
                    // cheated: false - this is the server handing the item over, and marking it cheated would
                    // put the character out of the running for achievements for the rest of its life.
                    ItemDrop.ItemData added = player.GetInventory().AddItem(
                        item.prefabName, Math.Max(1, item.stack), Math.Max(1, item.quality), item.variant, 0L, "",
                        cheated: false);
                    if (added == null) {
                        Logger.LogWarning($"Starter loadout: no room in {characterName}'s inventory for {item.prefabName}.");
                        continue;
                    }
                    granted++;
                    if (item.equipped) { player.EquipItem(added, triggerEquipEffects: false); }
                }
            }

            int raised = 0;
            if (loadout.skills != null) {
                foreach (KeyValuePair<Skills.SkillType, float> entry in loadout.skills) {
                    Skills.Skill skill = player.GetSkills().GetSkill(entry.Key);
                    if (skill == null || skill.m_level >= entry.Value) { continue; }
                    skill.m_level = entry.Value;
                    skill.m_accumulator = 0f;
                    raised++;
                }
            }

            if (loadout.knownMaterials != null) {
                foreach (string material in loadout.knownMaterials) {
                    if (!string.IsNullOrEmpty(material)) { player.m_knownMaterial.Add(material); }
                }
            }
            if (loadout.knownRecipes != null) {
                foreach (string recipe in loadout.knownRecipes) {
                    if (!string.IsNullOrEmpty(recipe)) { player.m_knownRecipes.Add(recipe); }
                }
            }
            if (loadout.knownMaterials != null || loadout.knownRecipes != null) {
                player.UpdateKnownRecipesList();
                player.UpdateAvailablePiecesList();
                if (MessageHud.instance != null) { MessageHud.instance.ClearUnlockQueue(); }
            }

            if (loadout.haveSpawnPoint && Game.instance != null && ZNet.instance != null) {
                Game.instance.GetPlayerProfile().SetCustomSpawnPoint(
                    new UnityEngine.Vector3(loadout.spawnX, loadout.spawnY, loadout.spawnZ));
            }

            Logger.LogInfo($"Starter loadout given to {characterName}: {granted} item(s), {raised} skill(s) raised.");
        }
    }
}
