using System.Collections;
using System.Reflection;

namespace DotCharter.Tests.Core.Utilities;

/// <summary>
///     <b>Field coverage for the snapshot layer, by REFLECTION rather than by
///     hand.</b> <see cref="ChartProjectCloner" /> is the single source of truth
///     for snapshotting (undo/redo, torn-save protection, crash recovery,
///     checkpoints) and delegates to <see cref="ChartProjectRestorer" />, so a
///     model field the restorer forgets is silently dropped from undo AND wiped
///     from disk on the next save — the same failure mode as an `.rscp` field
///     with a reader but no writer.
///     <para>
///         <see cref="ChartProjectClonerTests" /> pins the ~15 fields the 2026-07
///         audit found missing, one assertion each. That guards the fields
///         somebody already thought of; this guards the ones nobody has. It walks
///         the whole <see cref="ChartProject" /> graph, gives EVERY public
///         settable property a value distinguishable from its default (recursing
///         into nested objects and seeding every collection with two elements),
///         clones, and reports every property PATH whose value did not survive.
///         A new field on any domain type is covered the day it compiles.
///     </para>
///     <para>
///         Two shapes are checked, because the restorer has two: <c>Clone</c>
///         (restore into a brand-new empty object — the tail path) and
///         <c>Restore</c> onto an already-populated live graph (the index-wise
///         prefix path, which is what keeps undo history attached).
///     </para>
/// </summary>
public sealed class ChartProjectRestorerCoverageTests
{
    [Fact]
    public void Clone_CarriesEveryPublicSettableProperty_InTheWholeGraph()
    {
        var source = new ChartProject();
        DomainGraph.Populate(source, seed: 1);

        var clone = ChartProjectCloner.Clone(source);

        AssertNoDrops(source, clone);
    }

    [Fact]
    public void RestoreOntoAPopulatedGraph_CarriesEveryPublicSettableProperty()
    {
        var snapshot = new ChartProject();
        DomainGraph.Populate(snapshot, seed: 1);

        // A DIFFERENT population, so every field has to be actively overwritten
        // rather than happening to agree. Same shape, so the reconcile takes its
        // index-wise prefix path and not the mint-the-tail one.
        var live = new ChartProject();
        DomainGraph.Populate(live, seed: 2);

        ChartProjectRestorer.Restore(live, snapshot);

        AssertNoDrops(snapshot, live);
    }

    [Fact]
    public void CloneIsDeep_NoReferenceIsSharedWithTheSource()
    {
        var source = new ChartProject();
        DomainGraph.Populate(source, seed: 1);

        var clone = ChartProjectCloner.Clone(source);

        var shared = new List<string>();
        DomainGraph.CompareIdentity(source, clone, "ChartProject", shared);
        Assert.True(shared.Count == 0,
            "A snapshot shares a mutable reference with the live project, so editing one edits the " +
            "other — undo would restore values that had already changed:\n  " + string.Join("\n  ", shared));
    }

    /// <summary>
    ///     The quality report restores the live chart into a spare copy and reads that copy off the
    ///     UI thread while the live chart is edited (#711, <c>QualityReportCopies</c>), so a restore
    ///     must be as deep as a clone: a reference it shared with the snapshot would be read and
    ///     written on two threads at once.
    /// </summary>
    [Fact]
    public void RestoreOntoAPopulatedGraph_SharesNoReferenceWithTheSnapshot()
    {
        var snapshot = new ChartProject();
        DomainGraph.Populate(snapshot, seed: 1);
        var spare = new ChartProject();
        DomainGraph.Populate(spare, seed: 2);

        ChartProjectRestorer.Restore(spare, snapshot);

        var shared = new List<string>();
        DomainGraph.CompareIdentity(snapshot, spare, "ChartProject", shared);
        Assert.True(shared.Count == 0,
            "A restore shares a mutable reference with the project it copied, so the copy changes when the " +
            "project does:\n  " + string.Join("\n  ", shared));
    }

    /// <summary>
    ///     Vacuity keeper. The walk is only a guard while it actually reaches the
    ///     graph; if a rename or a signature change made <c>Populate</c> a no-op,
    ///     every comparison would trivially agree. So assert the walk visits the
    ///     properties we know exist, INCLUDING the deep ones.
    /// </summary>
    [Fact]
    public void TheWalkReachesTheWholeGraph()
    {
        var project = new ChartProject();
        var visited = DomainGraph.Populate(project, seed: 1);

        Assert.True(visited.Count > 200, $"only {visited.Count} properties populated — the walk stopped short.");
        foreach (var path in new[]
                 {
                     "ChartProject.IsBeatMapLocked",
                     "ChartProject.Chart.KeyAccidentals",
                     "ChartProject.ToneDefinitions[0].Json",
                     "ChartProject.Arrangements[0].ScrollSpeed",
                     "ChartProject.Arrangements[0].Properties.DropDPower",
                     "ChartProject.Arrangements[0].PhraseIterations[0].PhraseId",
                     "ChartProject.Arrangements[0].ChordTemplates[0].ForceArpeggioInRs",
                     "ChartProject.Arrangements[0].NotationVoices[0].Chords[0].NotationDuration",
                     "ChartProject.Arrangements[0].Levels[0].Notes[0].BendValues[0].Step",
                     "ChartProject.Arrangements[0].Levels[0].Chords[0].ChordNotes[0].GraceFret",
                     "ChartProject.Arrangements[0].Levels[0].FretHandMutes[0].Position",
                     "ChartProject.VocalTracks[0].Phrases[0].Lyrics[0].IsPhraseEnd"
                 })
        {
            Assert.Contains(path, visited);
        }
    }

    private static void AssertNoDrops(ChartProject expected, ChartProject actual)
    {
        var drops = new List<string>();
        DomainGraph.CompareValues(expected, actual, "ChartProject", drops);
        Assert.True(drops.Count == 0,
            "ChartProjectRestorer dropped model state. Every field it forgets vanishes from undo history " +
            "AND is wiped from disk on the next save (the save pipeline serializes the CLONE). Add it to " +
            "ChartProjectRestorer — never to ChartProjectCloner, which is a thin wrapper over it:\n  " +
            string.Join("\n  ", drops));
    }

    /// <summary>
    ///     A reflective populate/compare over the domain graph. It knows nothing
    ///     about which fields exist — that is the entire point.
    /// </summary>
    private static class DomainGraph
    {
        /// <summary>Populates every public settable property, recursing into
        /// nested domain objects and seeding every collection with two elements.
        /// Returns the paths it wrote, for the vacuity keeper.</summary>
        public static HashSet<string> Populate(object instance, int seed)
        {
            var written = new HashSet<string>(StringComparer.Ordinal);
            PopulateInto(instance, instance.GetType().Name, seed, written);
            if (instance is ChartProject project) PointPlacementsIntoTheProject(project);
            return written;
        }

        /// <summary>
        ///     A link placement names its arrangement by REFERENCE, and only an arrangement of the same
        ///     project is a link (the restorer maps it to the restored project's arrangement at the same
        ///     index; any other reference is a dead placement and is dropped). A blind fill hands it a
        ///     stray arrangement, so the fixture points it into the project, as the editor would.
        /// </summary>
        private static void PointPlacementsIntoTheProject(ChartProject project)
        {
            var i = 0;
            foreach (var placement in project.LinkGroups.SelectMany(g => g.Placements))
                placement.Arrangement = project.Arrangements[i++ % project.Arrangements.Count];
        }

        private static void PopulateInto(object instance, string path, int seed, HashSet<string> written)
        {
            foreach (var property in SettableProperties(instance.GetType()))
            {
                var childPath = $"{path}.{property.Name}";
                var value = property.GetValue(instance);

                // An ARRAY is fixed-size, so it is replaced wholesale like a value.
                // A growable collection instance is created by the type's own
                // initializer; replacing it would hide a restorer that copies the
                // reference instead of reconciling into it.
                if (value is IList list && !property.PropertyType.IsArray)
                {
                    SeedList(list, ElementTypeOf(property.PropertyType), childPath, seed, written);
                    written.Add(childPath);
                    continue;
                }

                if (value is IDictionary dictionary)
                {
                    SeedDictionary(dictionary, property.PropertyType, childPath, seed, written);
                    written.Add(childPath);
                    continue;
                }

                var made = MakeValue(property.PropertyType, childPath, seed, written);
                if (made == null) continue;

                property.SetValue(instance, made);
                written.Add(childPath);
            }
        }

        private static object? MakeValue(Type type, string path, int seed, HashSet<string> written)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;

            if (underlying == typeof(string)) return $"{path}#{seed}";
            if (underlying == typeof(bool)) return seed % 2 == 1;
            if (underlying == typeof(int)) return 40 + seed;
            if (underlying == typeof(long)) return 40L + seed;
            if (underlying == typeof(double)) return 40.5 + seed;
            if (underlying == typeof(float)) return 40.5f + seed;
            if (underlying == typeof(byte)) return (byte)(40 + seed);
            if (underlying == typeof(string[])) return new[] { $"a{seed}", $"b{seed}", $"c{seed}" };
            if (underlying == typeof(Fraction)) return new Fraction(1 + seed, 16);
            if (underlying == typeof(FractionalPosition))
                return new FractionalPosition(3 + seed, new Fraction(1 + seed, 16));
            if (underlying.IsEnum)
            {
                // Anything but the default, so a restorer that never assigns is caught.
                var values = Enum.GetValues(underlying).Cast<object>().ToList();
                return values.LastOrDefault(v => Convert.ToInt64(v) != 0) ?? values[0];
            }

            if (underlying.IsClass && underlying.GetConstructor(Type.EmptyTypes) != null)
            {
                var child = Activator.CreateInstance(underlying)!;
                PopulateInto(child, path, seed, written);
                return child;
            }

            throw new InvalidOperationException(
                $"{path}: the coverage walk has no value for {underlying.Name}. Teach MakeValue about it — " +
                "leaving it unpopulated would make this guard silently stop covering that field.");
        }

        private static void SeedList(IList list, Type elementType, string path, int seed, HashSet<string> written)
        {
            list.Clear();
            for (var i = 0; i < 2; i++)
            {
                var element = MakeValue(elementType, $"{path}[{i}]", seed + i, written);
                list.Add(element);
            }
        }

        private static void SeedDictionary(IDictionary dictionary, Type type, string path, int seed,
            HashSet<string> written)
        {
            var args = type.GetGenericArguments();
            dictionary.Clear();
            for (var i = 0; i < 2; i++)
            {
                var key = args[0] == typeof(string) ? $"k{seed}{i}" : (object)(10 + seed + i);
                dictionary[key] = MakeValue(args[1], $"{path}[{key}]", seed + i, written);
            }
        }

        /// <summary>Every value in the graph agrees, by path.</summary>
        public static void CompareValues(object expected, object actual, string path, List<string> drops)
        {
            foreach (var property in SettableProperties(expected.GetType()))
            {
                var childPath = $"{path}.{property.Name}";
                var left = property.GetValue(expected);
                var right = property.GetValue(actual);

                if (left == null || right == null)
                {
                    if (!Equals(left, right)) drops.Add($"{childPath}: {Describe(left)} != {Describe(right)}");
                    continue;
                }

                switch (left)
                {
                    case string:
                        if (!Equals(left, right)) drops.Add($"{childPath}: {Describe(left)} != {Describe(right)}");
                        continue;
                    case IList leftList when right is IList rightList:
                        CompareLists(leftList, rightList, childPath, drops);
                        continue;
                    case IDictionary leftMap when right is IDictionary rightMap:
                        CompareDictionaries(leftMap, rightMap, childPath, drops);
                        continue;
                }

                if (IsLeaf(property.PropertyType))
                {
                    if (!Equals(left, right)) drops.Add($"{childPath}: {Describe(left)} != {Describe(right)}");
                    continue;
                }

                CompareValues(left, right, childPath, drops);
            }
        }

        private static void CompareLists(IList expected, IList actual, string path, List<string> drops)
        {
            if (expected.Count != actual.Count)
            {
                drops.Add($"{path}: {expected.Count} element(s) became {actual.Count}");
                return;
            }

            for (var i = 0; i < expected.Count; i++)
            {
                var left = expected[i];
                var right = actual[i];
                if (left == null || right == null || IsLeaf(left.GetType()))
                {
                    if (!Equals(left, right)) drops.Add($"{path}[{i}]: {Describe(left)} != {Describe(right)}");
                    continue;
                }

                CompareValues(left, right, $"{path}[{i}]", drops);
            }
        }

        private static void CompareDictionaries(IDictionary expected, IDictionary actual, string path,
            List<string> drops)
        {
            if (expected.Count != actual.Count)
            {
                drops.Add($"{path}: {expected.Count} entr(ies) became {actual.Count}");
                return;
            }

            foreach (DictionaryEntry entry in expected)
            {
                if (!actual.Contains(entry.Key))
                {
                    drops.Add($"{path}[{entry.Key}]: missing");
                    continue;
                }

                var left = entry.Value;
                var right = actual[entry.Key];
                if (left == null || right == null || IsLeaf(left.GetType()))
                {
                    if (!Equals(left, right)) drops.Add($"{path}[{entry.Key}]: {Describe(left)} != {Describe(right)}");
                    continue;
                }

                CompareValues(left, right, $"{path}[{entry.Key}]", drops);
            }
        }

        /// <summary>Every mutable reference in the graph is a distinct instance.</summary>
        public static void CompareIdentity(object expected, object actual, string path, List<string> shared)
        {
            foreach (var property in SettableProperties(expected.GetType()))
            {
                var childPath = $"{path}.{property.Name}";
                var left = property.GetValue(expected);
                var right = property.GetValue(actual);
                if (left is null or string || right is null) continue;
                if (left.GetType().IsValueType) continue;

                if (ReferenceEquals(left, right))
                {
                    shared.Add(childPath);
                    continue;
                }

                switch (left)
                {
                    case IList leftList when right is IList rightList:
                        for (var i = 0; i < Math.Min(leftList.Count, rightList.Count); i++)
                        {
                            var element = leftList[i];
                            if (element is null or string || element.GetType().IsValueType) continue;
                            if (ReferenceEquals(element, rightList[i])) shared.Add($"{childPath}[{i}]");
                            else CompareIdentity(element, rightList[i]!, $"{childPath}[{i}]", shared);
                        }

                        continue;
                    case IDictionary leftMap when right is IDictionary rightMap:
                        foreach (DictionaryEntry entry in leftMap)
                        {
                            if (entry.Value is null or string || entry.Value.GetType().IsValueType) continue;
                            if (!rightMap.Contains(entry.Key)) continue;
                            if (ReferenceEquals(entry.Value, rightMap[entry.Key]))
                            {
                                shared.Add($"{childPath}[{entry.Key}]");
                            }
                            else
                            {
                                CompareIdentity(entry.Value, rightMap[entry.Key]!, $"{childPath}[{entry.Key}]",
                                shared);
                            }
                        }

                        continue;
                    default:
                        CompareIdentity(left, right, childPath, shared);
                        continue;
                }
            }
        }

        private static IEnumerable<PropertyInfo> SettableProperties(Type type)
        {
            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
                .OrderBy(p => p.Name, StringComparer.Ordinal);
        }

        private static Type ElementTypeOf(Type listType)
        {
            return listType.IsArray
                ? listType.GetElementType()!
                : listType.GetGenericArguments()[0];
        }

        private static bool IsLeaf(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            return underlying.IsPrimitive || underlying.IsEnum || underlying == typeof(string) ||
                   underlying == typeof(decimal) || underlying == typeof(Fraction) ||
                   underlying == typeof(FractionalPosition);
        }

        private static string Describe(object? value)
        {
            return value switch
            {
                null => "<null>",
                string s => $"\"{s}\"",
                _ => value.ToString() ?? "<?>"
            };
        }
    }
}
