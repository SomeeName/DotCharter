using System.Collections;
using System.Reflection;
using DotCharter.Services.State.Checkpoints;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.State;

/// <summary>
///     Derived coverage for the snapshot contract: <b>a project field that is not
///     snapshotted silently vanishes from undo AND from torn-save protection.</b>
///     <para>
///         <c>Core/Utilities/ChartProjectClonerTests</c> pins the fields ONE 2026-07 audit
///         found by hand. This is the same claim made structurally: the model is
///         walked by reflection, EVERY writable property and every collection is
///         filled with a distinctive value, and the snapshot is compared back
///         property by property — so a field added tomorrow joins the rule the day
///         it compiles, with no test to remember to extend.
///     </para>
///     <para>
///         Two snapshot paths, and they are different code:
///         <see cref="ChartProjectCloner" /> (which delegates to
///         <c>ChartProjectRestorer</c> — the undo/save graph copy) and the
///         checkpoint FILE, which goes through the `.rscp` serializer. A field can
///         be in one and missing from the other.
///     </para>
/// </summary>
public sealed class SnapshotFieldCoverageTests
{
    [Fact]
    public void EveryModelField_SurvivesTheUndoSnapshot()
    {
        var source = ModelGraphWalker.BuildFullyPopulated();

        var clone = ChartProjectCloner.Clone(source);

        var diffs = ModelGraphWalker.Compare(source, clone, "project");
        Assert.True(diffs.Count == 0,
            "ChartProjectRestorer does not carry these, so undo and every save that snapshots first " +
            $"drop them:\n{string.Join("\n", diffs)}");
    }

    /// <summary>
    ///     …and the deep half: the snapshot must not SHARE leaf instances with the
    ///     live graph, or an edit made after the snapshot rewrites the snapshot too.
    /// </summary>
    [Fact]
    public void TheUndoSnapshot_SharesNoInstanceWithTheLiveGraph()
    {
        var source = ModelGraphWalker.BuildFullyPopulated();
        var clone = ChartProjectCloner.Clone(source);

        var shared = ModelGraphWalker.SharedReferences(source, clone, "project");
        Assert.True(shared.Count == 0,
            $"These snapshot nodes are the SAME instance as the live graph:\n{string.Join("\n", shared)}");
    }

    /// <summary>
    ///     The checkpoint path is a FILE, written by a different serializer than
    ///     the in-memory cloner, so it needs its own coverage. Scoped to
    ///     membership — every collection of domain OBJECTS must come back with its
    ///     elements — because the `.rscp` writer deliberately normalizes plenty of
    ///     scalars (it re-stamps the format version, re-indexes difficulties, pads
    ///     a tuning to the string count). A whole collection going missing is never
    ///     normalization: it is the "model field with no writer" bug, and on this
    ///     path it costs the user a named checkpoint's worth of work.
    /// </summary>
    [Fact]
    public void EveryCollectionOfDomainObjects_SurvivesTheCheckpointFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dc_cpcov_{Guid.NewGuid():N}");
        try
        {
            var service = new CheckpointService(root);
            var source = ModelGraphWalker.BuildFullyPopulated();

            var entry = service.Create("coverage-probe", "cp", source);
            var restored = service.Restore("coverage-probe", entry);

            var missing = ModelGraphWalker
                .Compare(source, restored, "project", ModelGraphWalker.DomainCollectionsOnly)
                .Where(d => !NotMembership.Any(x => d.Contains(x, StringComparison.Ordinal)))
                .ToList();

            Assert.True(missing.Count == 0,
                "The checkpoint serializer drops these collections, so restoring a checkpoint loses " +
                $"them without a word:\n{string.Join("\n", missing)}");
        }
        finally
        {
            BestEffortDelete.Directory(root);
        }
    }

    /// <summary>
    ///     The only two shapes on this path that are NOT a membership claim, each
    ///     with its reason. Everything else is asserted, and the list is a
    ///     substring match on the reported path so it stays honest — an exclusion
    ///     that stops matching starts failing rather than silently widening.
    ///     <list type="bullet">
    ///         <item>
    ///             <c>Chart.SingleNotes</c> / <c>Chart.Chords</c> — the LEGACY flat
    ///             note store nothing writes any more (see
    ///             <c>ChartProjectPositions</c>' summary and
    ///             <c>DotCharter.Core/AGENTS.md</c>: the per-difficulty
    ///             <c>ChartLevel</c> store replaced it). The restorer still carries
    ///             it, so this is excluded on the FILE path only.
    ///         </item>
    ///         <item>
    ///             <c>Arrangement.TuningPitches</c> — a per-STRING vector, so its
    ///             length is derived from the arrangement's string count on read.
    ///             It is a fixed-width value, not a list of things the user
    ///             authored.
    ///         </item>
    ///     </list>
    /// </summary>
    private static readonly string[] NotMembership =
    [
        "project.Chart.SingleNotes", "project.Chart.Chords", ".TuningPitches",
        // Not a drop: the .rscp load check (LinkGroupVerifier) unlinks every placement whose copy
        // differs from its group, and a blind fill makes every copy differ. The link round trip
        // through the file is RscpLinkGroupsTests' and the rich fixture's (RscpDeepValueRoundTripTests).
        "project.LinkGroups"
    ];
}

/// <summary>
///     Reflection over the domain graph: fill everything, then compare everything.
///     Deterministic by construction — values are derived from a stable FNV hash of
///     the property PATH, never from <c>string.GetHashCode</c> (randomized per
///     process) and never from a clock.
/// </summary>
internal static class ModelGraphWalker
{
    private const string DomainNamespace = "DotCharter.Core.Domain";

    public static ChartProject BuildFullyPopulated()
    {
        var project = new ChartProject();
        Populate(project, "project", 0);
        MakeCrossReferencesConsistent(project);
        return project;
    }

    /// <summary>
    ///     A blind walk fills two halves of a cross-reference with two different
    ///     values, and a writer that keys one off the other then legitimately drops
    ///     it — which would read as a coverage failure. Only genuine LINKS are
    ///     repaired here; nothing is made to match by copying the snapshot back.
    /// </summary>
    private static void MakeCrossReferencesConsistent(ChartProject project)
    {
        // A link placement names an arrangement OF THIS PROJECT by reference (see LinkPlacement).
        var next = 0;
        foreach (var placement in project.LinkGroups.SelectMany(g => g.Placements))
            placement.Arrangement = project.Arrangements[next++ % project.Arrangements.Count];

        foreach (var arrangement in project.Arrangements)
        {
            // A chord points at a template by id, and a blind fill gives it one whose frets
            // do not fit its notes. The save pass repairs that by adding a fitting template
            // to the FILE (since #349 it no longer rewrites the project it is handed), which
            // would read as membership drift. Every chord fitting its template is the
            // invariant the editor keeps, so the fixture is brought to it by the same pass.
            new DotCharter.Services.Parsing.Rscp.RscpTemplateManager().RegenerateTemplates(arrangement);

            // PhraseInfos is keyed BY phrase name, so an info for a phrase that
            // does not exist is not a thing the writer is expected to keep.
            if (arrangement.Phrases.Count == 0 || arrangement.PhraseInfos.Count == 0) continue;
            var info = arrangement.PhraseInfos.Values.First();
            arrangement.PhraseInfos.Clear();
            arrangement.PhraseInfos[arrangement.Phrases[0]] = info;
        }
    }

    /// <summary>A collection whose ELEMENTS are domain objects — the membership
    /// the checkpoint file has to carry, as opposed to a tuning array or a
    /// fret→finger map, which the writer is entitled to normalize.</summary>
    public static bool DomainCollectionsOnly(PropertyInfo property)
    {
        var t = property.PropertyType;
        if (!t.IsGenericType || !typeof(IEnumerable).IsAssignableFrom(t)) return false;
        var args = t.GetGenericArguments();
        var element = args[^1];
        return element.IsClass && (element.Namespace?.StartsWith(DomainNamespace, StringComparison.Ordinal) ?? false);
    }

    private static bool IsSimple(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal)
               || t == typeof(DateTime) || t == typeof(DateTimeOffset)
               || t == typeof(FractionalPosition) || t == typeof(Fraction);
    }

    /// <summary>FNV-1a: stable across processes, unlike <c>string.GetHashCode</c>,
    /// so a failure reproduces with the same values it first appeared with.</summary>
    private static int StableHash(string value)
    {
        unchecked
        {
            var hash = 2166136261;
            foreach (var c in value) hash = (hash ^ c) * 16777619;
            return (int)(hash % 90) + 3;
        }
    }

    private static object? MakeValue(Type type, string path)
    {
        var n = StableHash(path);
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(string)) return $"v_{path}_{n}";
        if (t == typeof(bool)) return true;
        if (t.IsEnum)
        {
            var values = Enum.GetValues(t);
            return values.GetValue(values.Length > 1 ? 1 : 0);
        }

        if (t == typeof(int)) return n;
        if (t == typeof(long)) return (long)n;
        if (t == typeof(short)) return (short)n;
        if (t == typeof(byte)) return (byte)n;
        if (t == typeof(sbyte)) return (sbyte)n;
        if (t == typeof(uint)) return (uint)n;
        if (t == typeof(float)) return n + 0.25f;
        if (t == typeof(double)) return n + 0.5;
        if (t == typeof(decimal)) return (decimal)n;
        if (t == typeof(char)) return 'x';
        if (t == typeof(DateTime)) return new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(n);
        if (t == typeof(DateTimeOffset)) return new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(n);
        if (t == typeof(FractionalPosition)) return new FractionalPosition(n % 20 + 1);
        if (t == typeof(Fraction)) return new Fraction(1, 4);
        return null;
    }

    /// <summary>Builds through <paramref name="ctor" /> when every parameter is a
    /// value this walker knows how to invent; null otherwise.</summary>
    private static object? TryConstruct(ConstructorInfo ctor, string path)
    {
        var arguments = ctor.GetParameters()
            .Select(p => MakeValue(p.ParameterType, $"{path}.{p.Name}"))
            .ToArray();
        if (arguments.Any(a => a == null)) return null;
        try { return ctor.Invoke(arguments); }
        catch { return null; }
    }

    private static void Populate(object target, string path, int depth)
    {
        if (depth > 5) return;

        foreach (var property in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0) continue;
            var type = property.PropertyType;
            var childPath = $"{path}.{property.Name}";

            if (IsSimple(type))
            {
                if (!property.CanWrite) continue;
                if (MakeValue(type, childPath) is { } value) property.SetValue(target, value);
                continue;
            }

            if (type.IsArray)
            {
                if (!property.CanWrite) continue;
                var element = type.GetElementType()!;
                if (!IsSimple(element)) continue;
                var array = Array.CreateInstance(element, 6);
                for (var i = 0; i < 6; i++) array.SetValue(MakeValue(element, childPath + i), i);
                property.SetValue(target, array);
                continue;
            }

            var current = property.CanRead ? property.GetValue(target) : null;

            if (current is IDictionary dictionary)
            {
                var args = type.GetGenericArguments();
                if (args.Length != 2 || !IsSimple(args[0])) continue;
                var key = MakeValue(args[0], childPath + ".key")!;
                if (IsSimple(args[1]))
                {
                    dictionary[key] = MakeValue(args[1], childPath + ".value")!;
                    continue;
                }

                // WIDEST ctor first: an immutable value type (PhraseInfo) exposes a
                // parameterless one too, and building through THAT leaves every
                // field at its default — which a writer that only emits non-default
                // attributes then legitimately omits, and the walk reads as a drop.
                var built = args[1].GetConstructors()
                    .OrderByDescending(c => c.GetParameters().Length)
                    .Select(c => TryConstruct(c, childPath))
                    .FirstOrDefault(v => v != null);
                if (built == null) continue;
                dictionary[key] = built;
                if (!IsSimple(built.GetType())) Populate(built, $"{childPath}.value", depth + 1);
                continue;
            }

            if (current is IList list)
            {
                if (!type.IsGenericType) continue;
                var element = type.GetGenericArguments()[0];
                if (list.Count == 0)
                {
                    if (IsSimple(element))
                    {
                        if (MakeValue(element, childPath) is { } value) list.Add(value);
                        continue;
                    }

                    var ctor = element.GetConstructor(Type.EmptyTypes);
                    if (ctor == null) continue;
                    list.Add(ctor.Invoke(null));
                }

                for (var i = 0; i < list.Count; i++)
                {
                    if (list[i] is { } item && !IsSimple(item.GetType()))
                        Populate(item, $"{childPath}[{i}]", depth + 1);
                }

                continue;
            }

            if (current != null && type.IsClass && type != typeof(object)) Populate(current, childPath, depth + 1);
        }
    }

    public static List<string> Compare(
        object? live, object? snapshot, string path, Func<PropertyInfo, bool>? only = null, int depth = 0)
    {
        var diffs = new List<string>();
        if (depth > 6) return diffs;
        if (live == null || snapshot == null)
        {
            if (!ReferenceEquals(live, snapshot)) diffs.Add($"{path}: one side is null");
            return diffs;
        }

        foreach (var property in live.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || !property.CanRead) continue;
            var type = property.PropertyType;
            var childPath = $"{path}.{property.Name}";
            var inScope = only == null || only(property);

            object? a, b;
            try
            {
                a = property.GetValue(live);
                b = property.GetValue(snapshot);
            }
            catch
            {
                continue;
            }

            if (IsSimple(type))
            {
                if (inScope && !Equals(a, b)) diffs.Add($"{childPath}: '{a}' != '{b}'");
                continue;
            }

            if (a is IDictionary ad && b is IDictionary bd)
            {
                if (inScope && ad.Count != bd.Count)
                {
                    diffs.Add($"{childPath}: {ad.Count} entries != {bd.Count}");
                    continue;
                }

                foreach (DictionaryEntry pair in ad)
                {
                    if (!bd.Contains(pair.Key))
                    {
                        if (inScope) diffs.Add($"{childPath}: missing key '{pair.Key}'");
                        continue;
                    }

                    var other = bd[pair.Key];
                    if (pair.Value != null && !IsSimple(pair.Value.GetType()))
                        diffs.AddRange(Compare(pair.Value, other, $"{childPath}[{pair.Key}]", only, depth + 1));
                    else if (inScope && !Equals(pair.Value, other))
                        diffs.Add($"{childPath}[{pair.Key}]: '{pair.Value}' != '{other}'");
                }

                continue;
            }

            if (a is IEnumerable ae and not string && b is IEnumerable be and not string)
            {
                var al = ae.Cast<object?>().ToList();
                var bl = be.Cast<object?>().ToList();
                if (al.Count != bl.Count)
                {
                    if (inScope) diffs.Add($"{childPath}: {al.Count} element(s) != {bl.Count}");
                    continue;
                }

                for (var i = 0; i < al.Count; i++)
                {
                    if (al[i] is { } item && !IsSimple(item.GetType()))
                        diffs.AddRange(Compare(al[i], bl[i], $"{childPath}[{i}]", only, depth + 1));
                    else if (inScope && !Equals(al[i], bl[i]))
                        diffs.Add($"{childPath}[{i}]: '{al[i]}' != '{bl[i]}'");
                }

                continue;
            }

            if (type.IsClass && type != typeof(object))
                diffs.AddRange(Compare(a, b, childPath, only, depth + 1));
        }

        return diffs;
    }

    /// <summary>Every place the snapshot handed back the LIVE instance instead of a copy.</summary>
    public static List<string> SharedReferences(object? live, object? snapshot, string path, int depth = 0)
    {
        var shared = new List<string>();
        if (depth > 6 || live == null || snapshot == null) return shared;
        if (ReferenceEquals(live, snapshot))
        {
            shared.Add(path);
            return shared;
        }

        foreach (var property in live.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || !property.CanRead) continue;
            var type = property.PropertyType;
            if (IsSimple(type) || type == typeof(string)) continue;
            var childPath = $"{path}.{property.Name}";

            object? a, b;
            try
            {
                a = property.GetValue(live);
                b = property.GetValue(snapshot);
            }
            catch
            {
                continue;
            }

            if (a is IEnumerable ae and not string && b is IEnumerable be and not string)
            {
                if (ReferenceEquals(a, b)) shared.Add(childPath);
                var al = ae.Cast<object?>().ToList();
                var bl = be.Cast<object?>().ToList();
                for (var i = 0; i < Math.Min(al.Count, bl.Count); i++)
                {
                    if (al[i] is { } item && !IsSimple(item.GetType()))
                        shared.AddRange(SharedReferences(al[i], bl[i], $"{childPath}[{i}]", depth + 1));
                }

                continue;
            }

            if (type.IsClass) shared.AddRange(SharedReferences(a, b, childPath, depth + 1));
        }

        return shared;
    }
}
