using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using YamlDotNet.Serialization.NamingConventions;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.common {
    // Inside the namespace on purpose. The game has a Character class of its own in the global namespace, and a
    // member of an enclosing namespace is found before anything a using directive at the top of the file brings
    // in - which is why the rest of the mod spells this DataObjects.Character every time.
    using Character = DataObjects.Character;

    /// <summary>
    /// Writes a <see cref="Character"/> as YAML by hand, instead of through YamlDotNet's serializer.
    ///
    /// The document is the same document. Same keys, same nesting, same "leave out what is still at its
    /// default" rule, and it is still read back by the ordinary YamlDotNet deserializer, so a save written this
    /// way and one written the old way are interchangeable in both directions. What changes is the cost of
    /// producing it. Measured on a real 66 KB save, the serializer allocated 3.5 MB to write it - about 54 bytes
    /// of garbage per character of output - because it walks the object through reflection and builds an event
    /// object per token. No serializer setting moves that by more than a few percent; it is what the library
    /// is. A character is rewritten on every incremental update from every player, so on a full server that
    /// one call was the single largest source of garbage in the mod, and Unity's collector stops every thread
    /// to clean up after the worker this runs on. Appending the fields to a writer directly costs roughly the
    /// size of the numbers in the file.
    ///
    /// Two things keep a hand-written writer from being a liability:
    ///
    ///  - It proves itself before it is trusted. <see cref="FastPathUsable"/> runs a self-test the first time it
    ///    is asked: a character with every property of every nested type set - found by reflection, so a
    ///    property added later is covered without anybody remembering to - and a collection of strings chosen
    ///    to break a YAML writer, written both ways and read back. Any difference at all, and this class hands
    ///    every call to the serializer for the rest of the session and says so in the log. Forgetting to teach
    ///    it a new field therefore costs speed, never data.
    ///  - Every call falls back to the serializer if the hand-written path throws.
    ///
    /// FastCharacterWriter switches it off outright.
    ///
    /// Reading is untouched. A character is parsed once per join; it is written thousands of times.
    /// </summary>
    internal static class CharacterYaml {

        // ---- Entry points ---------------------------------------------------------------------------------

        /// <summary>The character as a YAML document.</summary>
        internal static string ToYaml(Character character) {
            if (character != null && FastPathUsable) {
                try {
                    StringBuilder text = new StringBuilder(16 * 1024);
                    using (StringWriter writer = new StringWriter(text, CultureInfo.InvariantCulture)) {
                        Emit(writer, character);
                    }
                    return text.ToString();
                } catch (Exception e) {
                    Logger.LogWarning($"The fast character writer failed on {character.Name}; using the serializer for this one: {e.Message}");
                }
            }
            return yamlserializer.Serialize(character);
        }

        /// <summary>
        /// Writes the character to <paramref name="path"/> through a temporary file beside it, and returns the
        /// last-write time of the published file - the same contract as <see cref="AtomicFile.WriteYaml"/>.
        /// </summary>
        internal static DateTime WriteFile(string path, Character character) {
            if (character != null && FastPathUsable) {
                try {
                    return AtomicFile.WriteWith(path, writer => Emit(writer, character));
                } catch (Exception e) {
                    // Nothing was published: AtomicFile only renames over the destination once the writer has
                    // finished. If this was the disk rather than the writer, the attempt below fails the same
                    // way and the caller hears about it exactly as it always did.
                    Logger.LogWarning($"The fast character writer failed on {character.Name}; using the serializer for this one: {e.Message}");
                }
            }
            return AtomicFile.WriteYaml(path, character, yamlserializer);
        }

        // ---- Switch and self-test -------------------------------------------------------------------------

        // A snapshot of FastCharacterWriter, because this is read from the character store's worker thread and
        // that thread never touches a ConfigEntry - the config watcher can reload one underneath it.
        private static volatile bool enabled = true;

        /// <summary>Main thread only. Wired to the setting and to its SettingChanged.</summary>
        internal static void SetEnabled(bool value) {
            enabled = value;
        }

        private static readonly object testLock = new object();
        private static volatile bool tested;
        private static volatile bool passed;

        /// <summary>
        /// Whether the hand-written path may be used: switched on, and shown to agree with the serializer.
        /// The first call runs the self-test, on whichever thread asks; <see cref="WarmUp"/> exists so that is
        /// not the main thread in the middle of somebody's join.
        /// </summary>
        internal static bool FastPathUsable {
            get {
                if (!enabled) { return false; }
                if (!tested) { RunSelfTest(); }
                return passed;
            }
        }

        /// <summary>Runs the self-test now if it has not run. Safe from any thread; meant for a worker.</summary>
        internal static void WarmUp() {
            if (enabled && !tested) { RunSelfTest(); }
        }

        private static void RunSelfTest() {
            lock (testLock) {
                if (tested) { return; }
                try {
                    string problem = SelfTestProblem();
                    passed = problem == null;
                    if (passed) {
                        Logger.LogDebug("The fast character writer agrees with the serializer; using it for character saves.");
                    } else {
                        Logger.LogWarning("The fast character writer does not agree with the serializer, so character saves are being written by the serializer for this session. " +
                                          $"Nothing is lost - this only costs speed - but it should be reported: {problem}");
                    }
                } catch (Exception e) {
                    passed = false;
                    Logger.LogWarning($"The fast character writer could not be checked, so character saves are being written by the serializer for this session: {e.Message}");
                } finally {
                    tested = true;
                }
            }
        }

        /// <summary>
        /// Null when what the hand-written writer produces reads back as the character it was given; otherwise
        /// what differed.
        ///
        /// Never compared as text. The two writers are free to quote a string differently or spell a float with
        /// more digits; what has to match is what a reader gets out, so every comparison is of characters that
        /// have been through the deserializer, rendered by the serializer so they can be compared at all.
        ///
        /// Two yardsticks, because neither is right for everything. The structural sample is held against the
        /// SERIALIZER's own round trip, which cancels out whatever the deserializer does to a value on its way
        /// in (it is free to hand a UTC time back as local, and that is not this class's business). The string
        /// sample is held against the ORIGINAL, which is the stricter test and the one that matters for text:
        /// a name or a mod's custom data has to come back as exactly the characters that went in, whatever the
        /// serializer's own writer would have done with them.
        /// </summary>
        internal static string SelfTestProblem() {
            foreach (KeyValuePair<Character, bool> sample in SelfTestSamples()) {
                Character original = sample.Key;
                string expected = sample.Value
                    ? yamlserializer.Serialize(original)
                    : yamlserializer.Serialize(yamldeserializer.Deserialize<Character>(yamlserializer.Serialize(original)));

                StringBuilder text = new StringBuilder();
                using (StringWriter writer = new StringWriter(text, CultureInfo.InvariantCulture)) { Emit(writer, original); }
                Character back = yamldeserializer.Deserialize<Character>(text.ToString());
                string actual = back == null ? "" : yamlserializer.Serialize(back);
                if (expected != actual) { return FirstDifference(expected, actual); }
            }
            return null;
        }

        private static string FirstDifference(string expected, string actual) {
            string[] want = expected.Split('\n');
            string[] got = actual.Split('\n');
            for (int i = 0; i < Math.Max(want.Length, got.Length); i++) {
                string w = i < want.Length ? want[i].TrimEnd('\r') : "<end of document>";
                string g = i < got.Length ? got[i].TrimEnd('\r') : "<end of document>";
                if (w == g) { continue; }
                if (w.Length > 160) { w = w.Substring(0, 160) + "..."; }
                if (g.Length > 160) { g = g.Substring(0, 160) + "..."; }
                return $"line {i + 1} reads back as [{g}] where the serializer's reads back as [{w}]";
            }
            return "the documents differ";
        }

        /// <summary>
        /// Strings picked to break a YAML writer: everything with a meaning to the grammar, everything that reads
        /// as something other than a string, and everything a line-oriented reader splits on.
        /// </summary>
        private static readonly string[] AwkwardStrings = {
            "", " ", "plain", "two words", " leading space", "trailing space ", "multi\nline", "cr\rlf\r\n", "tab\there",
            "quote\"double", "quote'single", "back\\slash", "colon: space", "colon:nospace", "trailing colon:", " #comment",
            "hash#inside", "- dash", "-dash", "? question", "null", "Null", "NULL", "~", "true", "false", "yes", "no", "123",
            "1.5", "-7", "0x1F", "1e5", ".inf", ".nan", "=", "<<", "@at", "`tick", "%percent", "!bang", "&anchor", "*alias",
            "|pipe", ">folded", "{brace}", "[bracket]", ",comma", "---", "...", "a, b", "key: value", "{\"json\": [1, 2, {\"x\": null}]}",
            "unicode " + (char)0xE9 + " " + (char)0xFC + " " + (char)0x6F22 + (char)0x5B57, "emoji " + char.ConvertFromUtf32(0x1F600) + " pair",
            "nel" + (char)0x85 + "here", "ls" + (char)0x2028 + "here",
            "ps" + (char)0x2029 + "here", "bom" + (char)0xFEFF + "here", "ctrl" + (char)0x01 + "here", "nul\0here", "del" + (char)0x7F + "here",
            "c1" + (char)0x9B + "here", "nbsp" + (char)0xA0 + "here",
            "$item_wood", "randyknapp.mods.epicloot#EpicLoot.MagicItemComponent", "AgAAAAMAAADyVpAM+/==", "O'Brien", "76561197993757990",
            "Steam_76561197993757990", "path\\to\\thing", "ends with backslash\\", "\"", "'", "''", "\"\"", "  ", "\t", "\n",
        };

        /// <summary>Each sample, and whether it is held against the original (true) or the serializer's round trip.</summary>
        private static IEnumerable<KeyValuePair<Character, bool>> SelfTestSamples() {
            // Every awkward string, as a value, as a map key, as a list entry and as a plain property. No times
            // in this one, so it can be held against the original.
            Character text = new Character { Name = "two words", HostID = "Steam_76561197993757990", GuardianPower = "" };
            for (int i = 0; i < AwkwardStrings.Length; i++) {
                text.PlayerCustomData["v" + i] = AwkwardStrings[i];
                text.PlayerCustomData[AwkwardStrings[i] + "#k" + i] = "k" + i;
            }
            text.PlayerCustomData[new string('k', 1500)] = new string('v', 5000); // past YAML's 1024 character simple key limit
            text.Progress = new Progression { KnownRecipes = new List<string>(AwkwardStrings), KnownTexts = new Dictionary<string, string>() };
            foreach (string awkward in AwkwardStrings) {
                // Not the empty string for the crafter. That property is declared with "" as its default, so the
                // serializer leaves an empty one out and the deserializer hands back null - the one place the
                // serializer's own round trip is not an identity, and nothing this writer could be held to.
                string crafter = awkward.Length == 0 ? null : awkward;
                text.PlayerItems.Add(new PackedItem { prefabName = awkward, m_stack = 1, m_crafterName = crafter, confiscatedReason = awkward });
                text.Progress.KnownTexts[awkward + "#t"] = awkward;
            }
            yield return new KeyValuePair<Character, bool>(text, true);

            // Every property of every nested type, set to something that is not its default.
            int seed = 0;
            Character full = (Character)Populate(typeof(Character), ref seed, 0);
            full.PlayerCustomData["nothing"] = null;
            if (full.Progress == null) { full.Progress = new Progression(); }
            full.Progress.KnownRecipes = new List<string> { "a", null, "b" };
            full.SkillLevels = new Dictionary<Skills.SkillType, float> {
                { Skills.SkillType.Swords, 27.1f }, { Skills.SkillType.Run, 100f }, { (Skills.SkillType)291323262, 7f },
                { Skills.SkillType.Axes, 0f }, { Skills.SkillType.Bows, 1e10f }, { Skills.SkillType.Clubs, 1.5e-5f },
                { Skills.SkillType.Knives, float.MaxValue }, { Skills.SkillType.Jump, float.NaN },
                { Skills.SkillType.Sneak, float.PositiveInfinity }, { Skills.SkillType.Swim, float.NegativeInfinity },
                { Skills.SkillType.Blocking, 94.50999f }, { Skills.SkillType.Spears, -0.1f },
            };
            full.ActiveCharacterEffects["bare"] = new PackedStatusEffect();
            full.ActiveCharacterEffects["absent"] = null;
            full.PlayerItems.Add(new PackedItem());
            full.PlayerItems.Add(null);
            full.PlayerItems.Add(new PackedItem { prefabName = "Hammer", m_stack = 1, m_gridpos = new Vector2i(0, 5), m_crafterName = "" });
            full.PlayerItems.Add(new PackedItem { prefabName = "Wood", m_customdata = new Dictionary<string, string>(), m_gridpos = new Vector2i(-1, 0) });
            yield return new KeyValuePair<Character, bool>(full, false);

            // Freshly constructed: the empty-collection spellings, and every optional field absent.
            yield return new KeyValuePair<Character, bool>(new Character(), false);

            // Every collection missing altogether, which is what a save trimmed by hand reads back as.
            yield return new KeyValuePair<Character, bool>(new Character {
                Name = "x", SkillLevels = null, PlayerCustomData = null, ActiveCharacterEffects = null,
                PlayerItems = null, ConfiscatedItems = null, LastDisconnect = DisconnectionState.DirtyDisconnect,
                GuardianPower = "", Foods = new List<PackedFood>(), Progress = new Progression { Spawn = new SpawnPoints() },
            }, false);
        }

        /// <summary>
        /// An instance of <paramref name="type"/> with every public settable property holding a non-default
        /// value, recursively. This is what makes the self-test notice a property this writer has not been
        /// taught: it is found here by reflection, written by the serializer, and missing from what the
        /// hand-written document reads back as.
        /// </summary>
        private static object Populate(Type type, ref int seed, int depth) {
            seed++;
            if (type == typeof(string)) { return "s" + seed; }
            if (type == typeof(int)) { return seed; }
            if (type == typeof(long)) { return 1000000000000L + seed; }
            if (type == typeof(float)) { return seed + 0.25f; }
            if (type == typeof(double)) { return seed + 0.125d; }
            if (type == typeof(bool)) { return true; }
            if (type == typeof(DateTime)) { return new DateTime(2026, 9, 10, 3, 18, 6, DateTimeKind.Utc).AddTicks(3332883 + seed); }
            if (type.IsEnum) {
                Array values = Enum.GetValues(type);
                return values.GetValue(values.Length > 1 ? 1 + (seed % (values.Length - 1)) : 0);
            }
            if (depth > 8) { return null; }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)) {
                IList list = (IList)Activator.CreateInstance(type);
                Type item = type.GetGenericArguments()[0];
                list.Add(Populate(item, ref seed, depth + 1));
                list.Add(Populate(item, ref seed, depth + 1));
                return list;
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>)) {
                IDictionary map = (IDictionary)Activator.CreateInstance(type);
                Type[] args = type.GetGenericArguments();
                for (int i = 0; i < 2; i++) {
                    object key = Populate(args[0], ref seed, depth + 1);
                    if (key != null && !map.Contains(key)) { map[key] = Populate(args[1], ref seed, depth + 1); }
                }
                return map;
            }

            object instance = Activator.CreateInstance(type);
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance)) {
                field.SetValue(instance, Populate(field.FieldType, ref seed, depth + 1));
            }
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
                if (!property.CanWrite || !property.CanRead || property.GetIndexParameters().Length > 0) { continue; }
                property.SetValue(instance, Populate(property.PropertyType, ref seed, depth + 1), null);
            }
            return instance;
        }

        // ---- Keys -----------------------------------------------------------------------------------------

        // Spelled by the same naming convention the deserializer matches keys with, rather than typed out, so
        // the two cannot disagree about what "m_crafterID" is called in a file.
        private static string K(string property) {
            return CamelCaseNamingConvention.Instance.Apply(property);
        }

        private static readonly string kName = K(nameof(Character.Name));
        private static readonly string kHostID = K(nameof(Character.HostID));
        private static readonly string kLastDisconnect = K(nameof(Character.LastDisconnect));
        private static readonly string kSkillLevels = K(nameof(Character.SkillLevels));
        private static readonly string kGuardianPower = K(nameof(Character.GuardianPower));
        private static readonly string kFoods = K(nameof(Character.Foods));
        private static readonly string kPlayerCustomData = K(nameof(Character.PlayerCustomData));
        private static readonly string kActiveCharacterEffects = K(nameof(Character.ActiveCharacterEffects));
        private static readonly string kPlayerItems = K(nameof(Character.PlayerItems));
        private static readonly string kConfiscatedItems = K(nameof(Character.ConfiscatedItems));
        private static readonly string kSkillReductions = K(nameof(Character.SkillReductions));
        private static readonly string kPendingSkillRestores = K(nameof(Character.PendingSkillRestores));
        private static readonly string kProgress = K(nameof(Character.Progress));
        private static readonly string kSaveSequence = K(nameof(Character.SaveSequence));

        private static readonly string kFoodName = K(nameof(PackedFood.Name));
        private static readonly string kFoodTime = K(nameof(PackedFood.Time));

        private static readonly string kSeTimeRemaining = K(nameof(PackedStatusEffect.TimeRemaining));
        private static readonly string kSeTime = K(nameof(PackedStatusEffect.Time));
        private static readonly string kSeNameHash = K(nameof(PackedStatusEffect.NameHash));
        private static readonly string kSeDamageLeft = K(nameof(PackedStatusEffect.DamageLeft));
        private static readonly string kSeDamagePerHit = K(nameof(PackedStatusEffect.DamagePerHit));
        private static readonly string kSeFireDamageLeft = K(nameof(PackedStatusEffect.FireDamageLeft));
        private static readonly string kSeFireDamagePerHit = K(nameof(PackedStatusEffect.FireDamagePerHit));
        private static readonly string kSeSpiritDamageLeft = K(nameof(PackedStatusEffect.SpiritDamageLeft));
        private static readonly string kSeSpiritDamagePerHit = K(nameof(PackedStatusEffect.SpiritDamagePerHit));

        private static readonly string kItemPrefab = K(nameof(PackedItem.prefabName));
        private static readonly string kItemStack = K(nameof(PackedItem.m_stack));
        private static readonly string kItemDurability = K(nameof(PackedItem.m_durability));
        private static readonly string kItemQuality = K(nameof(PackedItem.m_quality));
        private static readonly string kItemVariant = K(nameof(PackedItem.m_variant));
        private static readonly string kItemWorldLevel = K(nameof(PackedItem.m_worldlevel));
        private static readonly string kItemCrafterID = K(nameof(PackedItem.m_crafterID));
        private static readonly string kItemCrafterName = K(nameof(PackedItem.m_crafterName));
        private static readonly string kItemCustomData = K(nameof(PackedItem.m_customdata));
        private static readonly string kItemEquipped = K(nameof(PackedItem.m_equipped));
        private static readonly string kItemGridPos = K(nameof(PackedItem.m_gridpos));
        private static readonly string kItemConfiscatedReason = K(nameof(PackedItem.confiscatedReason));
        private static readonly string kItemConfiscatedTime = K(nameof(PackedItem.confiscatedTime));
        private static readonly string kItemConfiscationId = K(nameof(PackedItem.confiscationId));
        private static readonly string kX = K(nameof(Vector2i.x));
        private static readonly string kY = K(nameof(Vector2i.y));

        private static readonly string kRedSkill = K(nameof(SkillReduction.Skill));
        private static readonly string kRedFrom = K(nameof(SkillReduction.From));
        private static readonly string kRedTo = K(nameof(SkillReduction.To));
        private static readonly string kRedReason = K(nameof(SkillReduction.Reason));
        private static readonly string kRedTime = K(nameof(SkillReduction.Time));
        private static readonly string kRedId = K(nameof(SkillReduction.Id));

        private static readonly string kKnownRecipes = K(nameof(Progression.KnownRecipes));
        private static readonly string kKnownMaterials = K(nameof(Progression.KnownMaterials));
        private static readonly string kKnownStations = K(nameof(Progression.KnownStations));
        private static readonly string kTrophies = K(nameof(Progression.Trophies));
        private static readonly string kKnownBiomes = K(nameof(Progression.KnownBiomes));
        private static readonly string kKnownTexts = K(nameof(Progression.KnownTexts));
        private static readonly string kUniques = K(nameof(Progression.Uniques));
        private static readonly string kShownTutorials = K(nameof(Progression.ShownTutorials));
        private static readonly string kStats = K(nameof(Progression.Stats));
        private static readonly string kSpawn = K(nameof(Progression.Spawn));
        private static readonly string kMapHash = K(nameof(Progression.MapHash));

        private static readonly string kBucketStats = K(nameof(StatBucket.Stats));
        private static readonly string kBucketKnownWorlds = K(nameof(StatBucket.KnownWorlds));
        private static readonly string kBucketKnownWorldKeys = K(nameof(StatBucket.KnownWorldKeys));
        private static readonly string kBucketKnownCommands = K(nameof(StatBucket.KnownCommands));
        private static readonly string kBucketEnemyStats = K(nameof(StatBucket.EnemyStats));
        private static readonly string kBucketItemPickup = K(nameof(StatBucket.ItemPickup));
        private static readonly string kBucketItemCraft = K(nameof(StatBucket.ItemCraft));
        private static readonly string kBucketPickable = K(nameof(StatBucket.Pickable));
        private static readonly string kBucketFoodEaten = K(nameof(StatBucket.FoodEaten));
        private static readonly string kBucketPiecesPlaced = K(nameof(StatBucket.PiecesPlaced));

        private static readonly string kHaveCustomSpawn = K(nameof(SpawnPoints.HaveCustomSpawn));
        private static readonly string kSpawnX = K(nameof(SpawnPoints.SpawnX));
        private static readonly string kSpawnY = K(nameof(SpawnPoints.SpawnY));
        private static readonly string kSpawnZ = K(nameof(SpawnPoints.SpawnZ));
        private static readonly string kHomeX = K(nameof(SpawnPoints.HomeX));
        private static readonly string kHomeY = K(nameof(SpawnPoints.HomeY));
        private static readonly string kHomeZ = K(nameof(SpawnPoints.HomeZ));

        // ---- The document ---------------------------------------------------------------------------------

        /// <summary>
        /// One open block mapping. A struct, used only as a local and passed by ref, so writing a save does not
        /// allocate one of these per item.
        ///
        /// It owns the line break before its first key because only it knows whether there will be one: the
        /// parent has already written "key:" and an empty mapping has to finish that line as " {}" rather than
        /// start a new one. onDashLine is the other case - the first key of a list entry shares the line its
        /// "- " is on, and so does the root of the document.
        /// </summary>
        private struct Map {
            private readonly TextWriter writer;
            private readonly int indent;
            private readonly bool onDashLine;
            private bool any;

            internal Map(TextWriter writer, int indent, bool onDashLine) {
                this.writer = writer;
                this.indent = indent;
                this.onDashLine = onDashLine;
                any = false;
            }

            internal int ChildIndent { get { return indent + 2; } }
            internal int Indent { get { return indent; } }

            /// <summary>Starts an entry: the key and its colon, with nothing after them yet.</summary>
            internal void Key(string key) {
                if (any || !onDashLine) {
                    if (!any) { writer.WriteLine(); }
                    WriteIndent(writer, indent);
                }
                any = true;
                // A key the scanner may have to find the ':' for cannot run past 1024 characters; past that it
                // has to be written in the explicit form. Far more conservative than the limit, because
                // escaping can lengthen a key.
                if (key != null && key.Length > 256) {
                    writer.Write("? ");
                    WriteString(writer, key);
                    writer.WriteLine();
                    WriteIndent(writer, indent);
                    writer.Write(':');
                    return;
                }
                WriteString(writer, key);
                writer.Write(':');
            }

            internal void End() {
                if (any) { return; }
                writer.Write(onDashLine ? "{}" : " {}");
                writer.WriteLine();
            }
        }

        private static void Emit(TextWriter w, Character c) {
            Map root = new Map(w, 0, onDashLine: true);
            Text(ref root, w, kName, c.Name);
            Text(ref root, w, kHostID, c.HostID);
            if (c.LastDisconnect != DisconnectionState.Clean) { Text(ref root, w, kLastDisconnect, c.LastDisconnect.ToString()); }
            SkillMap(ref root, w, kSkillLevels, c.SkillLevels);
            Text(ref root, w, kGuardianPower, c.GuardianPower);
            if (c.Foods != null) {
                root.Key(kFoods);
                if (BeginList(w, c.Foods.Count)) {
                    foreach (PackedFood food in c.Foods) {
                        if (NullEntry(w, root.Indent, food)) { continue; }
                        Map entry = ListEntry(w, root.Indent);
                        Text(ref entry, w, kFoodName, food.Name);
                        Number(ref entry, w, kFoodTime, food.Time);
                        entry.End();
                    }
                }
            }
            TextMap(ref root, w, kPlayerCustomData, c.PlayerCustomData);
            if (c.ActiveCharacterEffects != null) {
                root.Key(kActiveCharacterEffects);
                Map effects = new Map(w, root.ChildIndent, onDashLine: false);
                foreach (KeyValuePair<string, PackedStatusEffect> pair in c.ActiveCharacterEffects) {
                    effects.Key(pair.Key);
                    PackedStatusEffect se = pair.Value;
                    if (se == null) { w.Write(" ~"); w.WriteLine(); continue; }
                    Map effect = new Map(w, effects.ChildIndent, onDashLine: false);
                    Number(ref effect, w, kSeTimeRemaining, se.TimeRemaining);
                    Number(ref effect, w, kSeTime, se.Time);
                    Whole(ref effect, w, kSeNameHash, se.NameHash);
                    Number(ref effect, w, kSeDamageLeft, se.DamageLeft);
                    Number(ref effect, w, kSeDamagePerHit, se.DamagePerHit);
                    Number(ref effect, w, kSeFireDamageLeft, se.FireDamageLeft);
                    Number(ref effect, w, kSeFireDamagePerHit, se.FireDamagePerHit);
                    Number(ref effect, w, kSeSpiritDamageLeft, se.SpiritDamageLeft);
                    Number(ref effect, w, kSeSpiritDamagePerHit, se.SpiritDamagePerHit);
                    effect.End();
                }
                effects.End();
            }
            Items(ref root, w, kPlayerItems, c.PlayerItems);
            Items(ref root, w, kConfiscatedItems, c.ConfiscatedItems);
            if (c.SkillReductions != null) {
                root.Key(kSkillReductions);
                if (BeginList(w, c.SkillReductions.Count)) {
                    foreach (SkillReduction reduction in c.SkillReductions) {
                        if (NullEntry(w, root.Indent, reduction)) { continue; }
                        Map entry = ListEntry(w, root.Indent);
                        if (reduction.Skill != default(Skills.SkillType)) { Text(ref entry, w, kRedSkill, SkillName(reduction.Skill)); }
                        Number(ref entry, w, kRedFrom, reduction.From);
                        Number(ref entry, w, kRedTo, reduction.To);
                        Text(ref entry, w, kRedReason, reduction.Reason);
                        Time(ref entry, w, kRedTime, reduction.Time);
                        Text(ref entry, w, kRedId, reduction.Id);
                        entry.End();
                    }
                }
            }
            SkillMap(ref root, w, kPendingSkillRestores, c.PendingSkillRestores);
            if (c.Progress != null) {
                root.Key(kProgress);
                Map progress = new Map(w, root.ChildIndent, onDashLine: false);
                EmitProgress(ref progress, w, c.Progress);
                progress.End();
            }
            Whole(ref root, w, kSaveSequence, c.SaveSequence);
            root.End();
        }

        private static void EmitProgress(ref Map map, TextWriter w, Progression p) {
            TextList(ref map, w, kKnownRecipes, p.KnownRecipes);
            TextList(ref map, w, kKnownMaterials, p.KnownMaterials);
            if (p.KnownStations != null) {
                map.Key(kKnownStations);
                Map stations = new Map(w, map.ChildIndent, onDashLine: false);
                foreach (KeyValuePair<string, int> pair in p.KnownStations) {
                    stations.Key(pair.Key);
                    w.Write(' ');
                    WriteWhole(w, pair.Value);
                    w.WriteLine();
                }
                stations.End();
            }
            TextList(ref map, w, kTrophies, p.Trophies);
            TextList(ref map, w, kKnownBiomes, p.KnownBiomes);
            TextMap(ref map, w, kKnownTexts, p.KnownTexts);
            TextList(ref map, w, kUniques, p.Uniques);
            TextList(ref map, w, kShownTutorials, p.ShownTutorials);
            if (p.Stats != null) {
                map.Key(kStats);
                Map buckets = new Map(w, map.ChildIndent, onDashLine: false);
                foreach (KeyValuePair<string, StatBucket> pair in p.Stats) {
                    buckets.Key(pair.Key);
                    StatBucket b = pair.Value;
                    if (b == null) { w.Write(" ~"); w.WriteLine(); continue; }
                    Map bucket = new Map(w, buckets.ChildIndent, onDashLine: false);
                    NumberMap(ref bucket, w, kBucketStats, b.Stats);
                    NumberMap(ref bucket, w, kBucketKnownWorlds, b.KnownWorlds);
                    NumberMap(ref bucket, w, kBucketKnownWorldKeys, b.KnownWorldKeys);
                    NumberMap(ref bucket, w, kBucketKnownCommands, b.KnownCommands);
                    if (b.EnemyStats != null) {
                        bucket.Key(kBucketEnemyStats);
                        Map modifiers = new Map(w, bucket.ChildIndent, onDashLine: false);
                        foreach (KeyValuePair<string, Dictionary<string, float>> modifier in b.EnemyStats) {
                            NumberMap(ref modifiers, w, modifier.Key, modifier.Value, nullAsTilde: true);
                        }
                        modifiers.End();
                    }
                    NumberMap(ref bucket, w, kBucketItemPickup, b.ItemPickup);
                    NumberMap(ref bucket, w, kBucketItemCraft, b.ItemCraft);
                    NumberMap(ref bucket, w, kBucketPickable, b.Pickable);
                    NumberMap(ref bucket, w, kBucketFoodEaten, b.FoodEaten);
                    NumberMap(ref bucket, w, kBucketPiecesPlaced, b.PiecesPlaced);
                    bucket.End();
                }
                buckets.End();
            }
            if (p.Spawn != null) {
                map.Key(kSpawn);
                Map spawn = new Map(w, map.ChildIndent, onDashLine: false);
                if (p.Spawn.HaveCustomSpawn) { spawn.Key(kHaveCustomSpawn); w.Write(" true"); w.WriteLine(); }
                Number(ref spawn, w, kSpawnX, p.Spawn.SpawnX);
                Number(ref spawn, w, kSpawnY, p.Spawn.SpawnY);
                Number(ref spawn, w, kSpawnZ, p.Spawn.SpawnZ);
                Number(ref spawn, w, kHomeX, p.Spawn.HomeX);
                Number(ref spawn, w, kHomeY, p.Spawn.HomeY);
                Number(ref spawn, w, kHomeZ, p.Spawn.HomeZ);
                spawn.End();
            }
            Text(ref map, w, kMapHash, p.MapHash);
        }

        private static void Items(ref Map map, TextWriter w, string key, List<PackedItem> items) {
            if (items == null) { return; }
            map.Key(key);
            if (!BeginList(w, items.Count)) { return; }
            foreach (PackedItem item in items) {
                if (NullEntry(w, map.Indent, item)) { continue; }
                Map entry = ListEntry(w, map.Indent);
                Text(ref entry, w, kItemPrefab, item.prefabName);
                Whole(ref entry, w, kItemStack, item.m_stack);
                Number(ref entry, w, kItemDurability, item.m_durability);
                Whole(ref entry, w, kItemQuality, item.m_quality);
                Whole(ref entry, w, kItemVariant, item.m_variant);
                Whole(ref entry, w, kItemWorldLevel, item.m_worldlevel);
                Whole(ref entry, w, kItemCrafterID, item.m_crafterID);
                // Declared with an empty string as its default, so empty is left out as well as null.
                if (!string.IsNullOrEmpty(item.m_crafterName)) { Text(ref entry, w, kItemCrafterName, item.m_crafterName); }
                TextMap(ref entry, w, kItemCustomData, item.m_customdata);
                if (item.m_equipped) { entry.Key(kItemEquipped); w.Write(" true"); w.WriteLine(); }
                if (item.m_gridpos.x != 0 || item.m_gridpos.y != 0) {
                    entry.Key(kItemGridPos);
                    Map grid = new Map(w, entry.ChildIndent, onDashLine: false);
                    Whole(ref grid, w, kX, item.m_gridpos.x);
                    Whole(ref grid, w, kY, item.m_gridpos.y);
                    grid.End();
                }
                Text(ref entry, w, kItemConfiscatedReason, item.confiscatedReason);
                Time(ref entry, w, kItemConfiscatedTime, item.confiscatedTime);
                Text(ref entry, w, kItemConfiscationId, item.confiscationId);
                entry.End();
            }
        }

        // ---- Entries --------------------------------------------------------------------------------------
        // Each of these writes one "key: value" and leaves it out when the value is the type's default, which is
        // what the serializer's OmitDefaults does. Null and default are both "absent"; empty is a value.

        private static void Text(ref Map map, TextWriter w, string key, string value) {
            if (value == null) { return; }
            map.Key(key);
            w.Write(' ');
            WriteString(w, value);
            w.WriteLine();
        }

        private static void Whole(ref Map map, TextWriter w, string key, long value) {
            if (value == 0L) { return; }
            map.Key(key);
            w.Write(' ');
            WriteWhole(w, value);
            w.WriteLine();
        }

        private static void Number(ref Map map, TextWriter w, string key, float value) {
            if (value == 0f) { return; }
            map.Key(key);
            w.Write(' ');
            WriteNumber(w, value);
            w.WriteLine();
        }

        private static void Time(ref Map map, TextWriter w, string key, DateTime value) {
            if (value == default(DateTime)) { return; }
            map.Key(key);
            w.Write(' ');
            // The round-trip form, which is what the serializer writes and what carries the Kind.
            w.Write(value.ToString("O", CultureInfo.InvariantCulture));
            w.WriteLine();
        }

        private static void TextList(ref Map map, TextWriter w, string key, List<string> values) {
            if (values == null) { return; }
            map.Key(key);
            if (!BeginList(w, values.Count)) { return; }
            foreach (string value in values) {
                WriteIndent(w, map.Indent);
                w.Write("- ");
                WriteString(w, value);
                w.WriteLine();
            }
        }

        private static void TextMap(ref Map map, TextWriter w, string key, Dictionary<string, string> values) {
            if (values == null) { return; }
            map.Key(key);
            Map child = new Map(w, map.ChildIndent, onDashLine: false);
            foreach (KeyValuePair<string, string> pair in values) {
                child.Key(pair.Key);
                w.Write(' ');
                WriteString(w, pair.Value);
                w.WriteLine();
            }
            child.End();
        }

        private static void NumberMap(ref Map map, TextWriter w, string key, Dictionary<string, float> values, bool nullAsTilde = false) {
            if (values == null) {
                // A property that is null is left out. An ENTRY that is null - a named map holding nothing -
                // has to be written, or its key would vanish from the map it belongs to.
                if (nullAsTilde) { map.Key(key); w.Write(" ~"); w.WriteLine(); }
                return;
            }
            map.Key(key);
            Map child = new Map(w, map.ChildIndent, onDashLine: false);
            foreach (KeyValuePair<string, float> pair in values) {
                child.Key(pair.Key);
                w.Write(' ');
                WriteNumber(w, pair.Value);
                w.WriteLine();
            }
            child.End();
        }

        private static void SkillMap(ref Map map, TextWriter w, string key, Dictionary<Skills.SkillType, float> values) {
            if (values == null) { return; }
            map.Key(key);
            Map child = new Map(w, map.ChildIndent, onDashLine: false);
            foreach (KeyValuePair<Skills.SkillType, float> pair in values) {
                child.Key(SkillName(pair.Key));
                w.Write(' ');
                WriteNumber(w, pair.Value);
                w.WriteLine();
            }
            child.End();
        }

        /// <summary>Finishes the "key:" line of a list. False when the list is empty and there is nothing to follow.</summary>
        private static bool BeginList(TextWriter w, int count) {
            if (count == 0) {
                w.Write(" []");
                w.WriteLine();
                return false;
            }
            w.WriteLine();
            return true;
        }

        /// <summary>Opens a list entry that is a mapping. Entries sit at their parent key's own indent.</summary>
        private static Map ListEntry(TextWriter w, int indent) {
            WriteIndent(w, indent);
            w.Write("- ");
            return new Map(w, indent + 2, onDashLine: true);
        }

        /// <summary>Writes a null list entry. True when it did, and the caller has nothing more to write for it.</summary>
        private static bool NullEntry(TextWriter w, int indent, object entry) {
            if (entry != null) { return false; }
            WriteIndent(w, indent);
            w.Write("- ~");
            w.WriteLine();
            return true;
        }

        // ---- Scalars --------------------------------------------------------------------------------------

        private static void WriteIndent(TextWriter w, int indent) {
            for (int i = 0; i < indent; i++) { w.Write(' '); }
        }

        // Enum.ToString goes through reflection and allocates every time; a skill list is two dozen of them
        // per save. Undefined values - skills a mod added - render as their number, which is what the
        // serializer wrote and what Enum.Parse reads back.
        private static readonly ConcurrentDictionary<int, string> skillNames = new ConcurrentDictionary<int, string>();

        private static string SkillName(Skills.SkillType skill) {
            int id = (int)skill;
            if (skillNames.TryGetValue(id, out string name)) { return name; }
            name = skill.ToString();
            skillNames[id] = name;
            return name;
        }

        [ThreadStatic]
        private static char[] digits;

        /// <summary>A whole number, written without going through a culture or allocating a string.</summary>
        private static void WriteWhole(TextWriter w, long value) {
            if (value == long.MinValue) { w.Write("-9223372036854775808"); return; }
            char[] buffer = digits ?? (digits = new char[24]);
            int at = buffer.Length;
            bool negative = value < 0;
            if (negative) { value = -value; }
            do {
                buffer[--at] = (char)('0' + (int)(value % 10));
                value /= 10;
            } while (value != 0);
            if (negative) { buffer[--at] = '-'; }
            w.Write(buffer, at, buffer.Length - at);
        }

        private static void WriteNumber(TextWriter w, float value) {
            if (float.IsNaN(value)) { w.Write(".nan"); return; }
            if (float.IsPositiveInfinity(value)) { w.Write(".inf"); return; }
            if (float.IsNegativeInfinity(value)) { w.Write("-.inf"); return; }
            // "R": the shortest spelling that reads back as exactly this value.
            w.Write(value.ToString("R", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// A string, plain where that is unambiguous and double-quoted with escapes everywhere else.
        ///
        /// The plain rule is deliberately narrower than YAML's: a string is written bare only when nothing in it
        /// could mean anything to the grammar, wherever it lands - as a key, a value or a list entry. Anything
        /// in doubt is quoted, and a quoted string is always one line with every awkward character escaped, so
        /// what a player typed into a name or a mod stored in custom data cannot change the shape of the
        /// document around it. Null is only reachable for an entry inside a collection; a null property is left
        /// out before it gets here.
        /// </summary>
        private static void WriteString(TextWriter w, string value) {
            if (value == null) { w.Write('~'); return; }
            if (IsPlain(value)) { w.Write(value); return; }

            w.Write('"');
            for (int i = 0; i < value.Length; i++) {
                char c = value[i];
                switch (c) {
                    case '"': w.Write("\\\""); continue;
                    case '\\': w.Write("\\\\"); continue;
                    case '\n': w.Write("\\n"); continue;
                    case '\r': w.Write("\\r"); continue;
                    case '\t': w.Write("\\t"); continue;
                    case '\0': w.Write("\\0"); continue;
                    case (char)0x85: w.Write("\\N"); continue;   // NEL
                    case (char)0x2028: w.Write("\\L"); continue; // line separator
                    case (char)0x2029: w.Write("\\P"); continue; // paragraph separator
                }
                if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) {
                    WriteHex(w, "\\U", char.ConvertToUtf32(c, value[++i]), 8);
                    continue;
                }
                if (char.IsSurrogate(c)) {
                    w.Write((char)0xFFFD); // half a pair: not encodable as UTF-8, and YAML has no escape for one
                    continue;
                }
                if (c < ' ' || (c >= (char)0x7F && c <= (char)0x9F)) { WriteHex(w, "\\x", c, 2); continue; }
                if (c == (char)0xFEFF || c >= (char)0xFFFE) { WriteHex(w, "\\u", c, 4); continue; }
                w.Write(c);
            }
            w.Write('"');
        }

        private static void WriteHex(TextWriter w, string prefix, int value, int width) {
            w.Write(prefix);
            for (int shift = (width - 1) * 4; shift >= 0; shift -= 4) {
                int nibble = (value >> shift) & 0xF;
                w.Write((char)(nibble < 10 ? '0' + nibble : 'A' + (nibble - 10)));
            }
        }

        private static bool IsPlain(string value) {
            if (value.Length == 0) { return false; }
            char first = value[0];
            if (!char.IsLetterOrDigit(first) && first != '_' && first != '$') { return false; }
            if (value[value.Length - 1] == ' ') { return false; }
            for (int i = 1; i < value.Length; i++) {
                char c = value[i];
                if (char.IsLetterOrDigit(c)) { continue; }
                switch (c) {
                    case '_': case '-': case '.': case '$': case '+': case '/': case '=': case '(': case ')': case ' ': case '\'':
                        continue;
                    default:
                        return false;
                }
            }
            // The deserializer reads these as null whatever type it is filling in, plain or not.
            return value != "null" && value != "Null" && value != "NULL";
        }
    }
}
