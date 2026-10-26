using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.common {
    // Inside the namespace for the same reason as in CharacterYaml: the game has a Character class of its own.
    using Character = DataObjects.Character;

    /// <summary>
    /// A detached copy of a <see cref="Character"/>, taken on the main thread so the character can be written
    /// somewhere else.
    ///
    /// The character a client tracks is a live object. The delta tracker, a death, a respawn and the admin
    /// commands all change it on the main thread, and a save can be asked for in the middle of any of that, so
    /// the character store's worker cannot serialize the object itself. A save hands it a copy instead: every
    /// list, map and nested record new, every string shared, because strings never change. That costs the main
    /// thread an allocation per entry and no formatting at all, where serializing turned every number in the
    /// file into text on the frame that asked for the save.
    ///
    /// Written out by hand, so it carries the same risk <see cref="CharacterYaml"/> does - a property added to
    /// one of these types and not copied here would quietly go missing from every save - and it is guarded the
    /// same way. Before the copy is trusted it is checked against the same samples CharacterYaml's self-test
    /// uses, which set every property of every nested type by reflection: the copy must serialize exactly as the
    /// original does, and must not share a single list, map or record with it. If either fails,
    /// <see cref="TryCopy"/> returns null for the rest of the session and saves are written the old way, on the
    /// spot. Forgetting to teach this a new property costs frame time, never data.
    /// </summary>
    internal static class CharacterSnapshot {

        // ---- Entry points ---------------------------------------------------------------------------------

        /// <summary>
        /// A copy of <paramref name="character"/> that shares nothing mutable with it, or null when the copy has
        /// not passed its self-test (or <paramref name="character"/> is null). Main thread, or whichever thread
        /// owns the character.
        /// </summary>
        internal static Character TryCopy(Character character) {
            if (character == null || !Usable) { return null; }
            return Copy(character);
        }

        /// <summary>
        /// Runs both character self-tests - this one and CharacterYaml's - on a thread of their own. Called once,
        /// at startup.
        ///
        /// Whichever thread first asks for a self-test pays for it, and until this existed that was the main
        /// thread in the middle of the first save of a session: CharacterYaml's check builds and round trips
        /// several sample characters through the serializer with nothing compiled yet, and that is the 400-odd
        /// millisecond "Character save" hitch at join the stall warning kept reporting. A world takes far longer
        /// to load than either test takes to run, so by the first save both have long settled. A save that does
        /// arrive first waits for the test in progress rather than starting a second one.
        ///
        /// Except under Mono's native-debugger support, where compiling on two threads at once can hang the game
        /// for good (see <see cref="MonoDebugOptions"/>) and this thread was what hung it. There both tests run
        /// on the calling thread, at startup, where the time they take is spent before anybody can join - and
        /// with the serializer compiled here, the character store's worker has little left to compile later.
        /// </summary>
        internal static void WarmUpAtStartup() {
            if (MonoDebugOptions.NativeDebuggerSupport) {
                WarmUpBoth();
                return;
            }
            Thread thread = new Thread(WarmUpBoth) {
                IsBackground = true,
                Name = "VE-CharacterWarmUp",
                Priority = ThreadPriority.BelowNormal,
            };
            thread.Start();
        }

        private static void WarmUpBoth() {
            try { CharacterYaml.WarmUp(); } catch (Exception) { /* it reports its own failures */ }
            try { WarmUp(); } catch (Exception) { /* so does this */ }
        }

        // ---- Self-test ------------------------------------------------------------------------------------

        private static readonly object testLock = new object();
        private static volatile bool tested;
        private static volatile bool passed;

        /// <summary>Whether the copy has been shown to keep everything. The first call runs the self-test, on
        /// whichever thread asks; <see cref="WarmUpAtStartup"/> exists so that is not the main thread mid-save.</summary>
        internal static bool Usable {
            get {
                if (!tested) { RunSelfTest(); }
                return passed;
            }
        }

        /// <summary>Runs the self-test now if it has not run. Safe from any thread.</summary>
        internal static void WarmUp() {
            if (!tested) { RunSelfTest(); }
        }

        private static void RunSelfTest() {
            lock (testLock) {
                if (tested) { return; }
                try {
                    string problem = SelfTestProblem();
                    passed = problem == null;
                    if (passed) {
                        Logger.LogDebug("Character snapshots copy every field; character saves are written in the background.");
                    } else {
                        Logger.LogWarning("Character snapshots do not copy everything, so character saves are being written on the main thread for this session. " +
                                          $"Nothing is lost - this only costs frame time - but it should be reported: {problem}");
                    }
                } catch (Exception e) {
                    passed = false;
                    Logger.LogWarning($"Character snapshots could not be checked, so character saves are being written on the main thread for this session: {e.Message}");
                } finally {
                    tested = true;
                }
            }
        }

        /// <summary>Null when the copy of every sample keeps everything and shares nothing; otherwise what went
        /// wrong.</summary>
        internal static string SelfTestProblem() {
            foreach (Character sample in CharacterYaml.SelfTestCharacters()) {
                Character copy = Copy(sample);
                string shared = SharedState(sample, copy, "character", 0);
                if (shared != null) { return shared; }
                // Compared through the serializer, not the fast writer: what matters is that the copy is the same
                // document, whichever writer ends up producing it.
                string expected = yamlserializer.Serialize(sample);
                string actual = yamlserializer.Serialize(copy);
                if (expected != actual) { return "the copy " + FirstDifference(expected, actual); }
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
                return $"serializes line {i + 1} as [{g}] where the original has [{w}]";
            }
            return "serializes differently";
        }

        /// <summary>
        /// The first list, map or record the copy still shares with the original, by path, or null when there is
        /// none. Found by reflection, like the samples, so a property of a reference type that the copy assigns
        /// straight across instead of copying is caught whether or not anybody remembered it here. Strings and
        /// value types are skipped: they cannot be changed in place.
        /// </summary>
        private static string SharedState(object original, object copy, string path, int depth) {
            if (original == null || copy == null) {
                return (original == null) == (copy == null) ? null : $"{path} is missing from the copy";
            }
            Type type = original.GetType();
            if (type == typeof(string) || type.IsValueType) { return null; }
            if (ReferenceEquals(original, copy)) { return $"{path} is shared with the original instead of copied"; }
            if (depth > 8) { return null; }

            if (original is IDictionary map) {
                IDictionary other = (IDictionary)copy;
                foreach (DictionaryEntry entry in map) {
                    if (!other.Contains(entry.Key)) { return $"{path}[{entry.Key}] is missing from the copy"; }
                    string problem = SharedState(entry.Value, other[entry.Key], $"{path}[{entry.Key}]", depth + 1);
                    if (problem != null) { return problem; }
                }
                return null;
            }
            if (original is IList list) {
                IList other = (IList)copy;
                if (other.Count != list.Count) { return $"{path} has {other.Count} entries in the copy and {list.Count} in the original"; }
                for (int i = 0; i < list.Count; i++) {
                    string problem = SharedState(list[i], other[i], $"{path}[{i}]", depth + 1);
                    if (problem != null) { return problem; }
                }
                return null;
            }

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance)) {
                string problem = SharedState(field.GetValue(original), field.GetValue(copy), $"{path}.{field.Name}", depth + 1);
                if (problem != null) { return problem; }
            }
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
                if (!property.CanRead || property.GetIndexParameters().Length > 0) { continue; }
                string problem = SharedState(property.GetValue(original, null), property.GetValue(copy, null), $"{path}.{property.Name}", depth + 1);
                if (problem != null) { return problem; }
            }
            return null;
        }

        // ---- The copy -------------------------------------------------------------------------------------

        // Null is kept as null throughout, and so is a null entry inside a list or map: a save omits a missing
        // collection and writes a null entry, and "not tracked" and "empty" are different answers in these types.
        // The property initialisers on Character run first, so assigning null here really does leave it null.
        private static Character Copy(Character c) {
            return new Character {
                Name = c.Name,
                HostID = c.HostID,
                LastDisconnect = c.LastDisconnect,
                SkillLevels = Values(c.SkillLevels),
                GuardianPower = c.GuardianPower,
                Foods = Foods(c.Foods),
                PlayerCustomData = Values(c.PlayerCustomData),
                ActiveCharacterEffects = Effects(c.ActiveCharacterEffects),
                PlayerItems = Items(c.PlayerItems),
                ConfiscatedItems = Items(c.ConfiscatedItems),
                SkillReductions = Reductions(c.SkillReductions),
                PendingSkillRestores = Values(c.PendingSkillRestores),
                Progress = Progress(c.Progress),
                SaveSequence = c.SaveSequence,
            };
        }

        // Only for maps whose values are strings, numbers or enums - anything else needs a copy of its own, and
        // the self-test says so if one is passed through here. The comparer is carried across so a map that was
        // built case-insensitive stays that way.
        private static Dictionary<TKey, TValue> Values<TKey, TValue>(Dictionary<TKey, TValue> source) {
            return source == null ? null : new Dictionary<TKey, TValue>(source, source.Comparer);
        }

        private static List<string> Strings(List<string> source) {
            return source == null ? null : new List<string>(source);
        }

        private static List<PackedFood> Foods(List<PackedFood> source) {
            if (source == null) { return null; }
            List<PackedFood> copy = new List<PackedFood>(source.Count);
            foreach (PackedFood food in source) {
                copy.Add(food == null ? null : new PackedFood { Name = food.Name, Time = food.Time });
            }
            return copy;
        }

        private static Dictionary<string, PackedStatusEffect> Effects(Dictionary<string, PackedStatusEffect> source) {
            if (source == null) { return null; }
            Dictionary<string, PackedStatusEffect> copy = new Dictionary<string, PackedStatusEffect>(source.Count, source.Comparer);
            foreach (KeyValuePair<string, PackedStatusEffect> pair in source) {
                PackedStatusEffect se = pair.Value;
                copy[pair.Key] = se == null ? null : new PackedStatusEffect {
                    TimeRemaining = se.TimeRemaining,
                    Time = se.Time,
                    NameHash = se.NameHash,
                    DamageLeft = se.DamageLeft,
                    DamagePerHit = se.DamagePerHit,
                    FireDamageLeft = se.FireDamageLeft,
                    FireDamagePerHit = se.FireDamagePerHit,
                    SpiritDamageLeft = se.SpiritDamageLeft,
                    SpiritDamagePerHit = se.SpiritDamagePerHit,
                };
            }
            return copy;
        }

        private static List<PackedItem> Items(List<PackedItem> source) {
            if (source == null) { return null; }
            List<PackedItem> copy = new List<PackedItem>(source.Count);
            foreach (PackedItem item in source) {
                copy.Add(item == null ? null : new PackedItem {
                    prefabName = item.prefabName,
                    m_stack = item.m_stack,
                    m_durability = item.m_durability,
                    m_quality = item.m_quality,
                    m_variant = item.m_variant,
                    m_worldlevel = item.m_worldlevel,
                    m_crafterID = item.m_crafterID,
                    m_crafterName = item.m_crafterName,
                    m_customdata = Values(item.m_customdata),
                    m_equipped = item.m_equipped,
                    m_gridpos = item.m_gridpos,
                    confiscatedReason = item.confiscatedReason,
                    confiscatedTime = item.confiscatedTime,
                    confiscationId = item.confiscationId,
                });
            }
            return copy;
        }

        private static List<SkillReduction> Reductions(List<SkillReduction> source) {
            if (source == null) { return null; }
            List<SkillReduction> copy = new List<SkillReduction>(source.Count);
            foreach (SkillReduction reduction in source) {
                copy.Add(reduction == null ? null : new SkillReduction {
                    Skill = reduction.Skill,
                    From = reduction.From,
                    To = reduction.To,
                    Reason = reduction.Reason,
                    Time = reduction.Time,
                    Id = reduction.Id,
                });
            }
            return copy;
        }

        private static Progression Progress(Progression p) {
            if (p == null) { return null; }
            return new Progression {
                KnownRecipes = Strings(p.KnownRecipes),
                KnownMaterials = Strings(p.KnownMaterials),
                KnownStations = Values(p.KnownStations),
                Trophies = Strings(p.Trophies),
                KnownBiomes = Strings(p.KnownBiomes),
                KnownTexts = Values(p.KnownTexts),
                Uniques = Strings(p.Uniques),
                ShownTutorials = Strings(p.ShownTutorials),
                Stats = Buckets(p.Stats),
                Spawn = p.Spawn == null ? null : new SpawnPoints {
                    HaveCustomSpawn = p.Spawn.HaveCustomSpawn,
                    SpawnX = p.Spawn.SpawnX,
                    SpawnY = p.Spawn.SpawnY,
                    SpawnZ = p.Spawn.SpawnZ,
                    HomeX = p.Spawn.HomeX,
                    HomeY = p.Spawn.HomeY,
                    HomeZ = p.Spawn.HomeZ,
                },
                MapHash = p.MapHash,
            };
        }

        private static Dictionary<string, StatBucket> Buckets(Dictionary<string, StatBucket> source) {
            if (source == null) { return null; }
            Dictionary<string, StatBucket> copy = new Dictionary<string, StatBucket>(source.Count, source.Comparer);
            foreach (KeyValuePair<string, StatBucket> pair in source) {
                StatBucket b = pair.Value;
                copy[pair.Key] = b == null ? null : new StatBucket {
                    Stats = Values(b.Stats),
                    KnownWorlds = Values(b.KnownWorlds),
                    KnownWorldKeys = Values(b.KnownWorldKeys),
                    KnownCommands = Values(b.KnownCommands),
                    EnemyStats = EnemyStats(b.EnemyStats),
                    ItemPickup = Values(b.ItemPickup),
                    ItemCraft = Values(b.ItemCraft),
                    Pickable = Values(b.Pickable),
                    FoodEaten = Values(b.FoodEaten),
                    PiecesPlaced = Values(b.PiecesPlaced),
                };
            }
            return copy;
        }

        private static Dictionary<string, Dictionary<string, float>> EnemyStats(Dictionary<string, Dictionary<string, float>> source) {
            if (source == null) { return null; }
            Dictionary<string, Dictionary<string, float>> copy = new Dictionary<string, Dictionary<string, float>>(source.Count, source.Comparer);
            foreach (KeyValuePair<string, Dictionary<string, float>> pair in source) {
                copy[pair.Key] = Values(pair.Value);
            }
            return copy;
        }
    }
}
