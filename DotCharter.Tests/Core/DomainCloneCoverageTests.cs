using System.Reflection;

namespace DotCharter.Tests.Core;

/// <summary>
///     Every domain type carries a hand-written <c>Clone()</c> / <c>CopyFrom(other)</c>
///     pair (the rule is in <c>DotCharter.Core/AGENTS.md</c>), and each one is a
///     PARALLEL LIST of field assignments that nothing keeps in step with the type
///     it copies. Adding a property and forgetting the assignment compiles clean,
///     passes every existing test, and surfaces much later as a value that
///     vanishes across an undo snapshot, a clipboard paste or a project clone —
///     <c>ChartProjectCloner</c> is exactly this pair applied to the whole graph.
///     <para>
///         So the guard is REFLECTION, not a list: it discovers every Core type
///         with such a pair, builds an instance whose every public settable
///         property differs from its default, round-trips it, and compares
///         property-by-property. A field missing from a copy path fails
///         automatically, forever, with nothing to maintain.
///     </para>
///     <para>
///         A property that genuinely must not be copied goes in
///         <see cref="Exclusions" /> with a REASON, and the test asserts the
///         property still exists — a rename breaks the exclusion instead of
///         silently widening it.
///     </para>
///     <para>
///         <b>Relationship to <c>ChartProjectRestorerCoverageTests</c>:</b> that
///         one walks the whole <c>ChartProject</c> graph through
///         <c>ChartProjectCloner</c>/<c>ChartProjectRestorer</c>, so it reaches
///         most of these pairs transitively — but only the ones the restorer
///         happens to route through. It never calls <c>NotationVoice.CopyFrom</c>
///         (deliberately: <c>RestoreNotationVoice</c> reconciles the two
///         collections in place instead), never <c>ChordNote.Clone</c>,
///         <c>BendValue.Clone</c> or <c>ChordTemplate.Clone</c> on their own, and
///         reports a failure as a 60-character graph PATH rather than as a type.
///         This is the per-type contract, stated once for each pair the model
///         declares.
///     </para>
/// </summary>
public sealed class DomainCloneCoverageTests
{
    /// <summary>
    ///     Empty on purpose, and that is the finding: every <c>Clone</c>/<c>CopyFrom</c>
    ///     in <c>DotCharter.Core</c> is currently COMPLETE. The list exists so the
    ///     next deliberate omission has to be written down and argued for rather
    ///     than left to look identical to a forgotten one.
    /// </summary>
    private static readonly FieldExclusion[] Exclusions = [];

    /// <summary>Discovered, not listed — a new domain type with a copy pair is
    /// covered the day it compiles.</summary>
    private static IEnumerable<Type> CopyableDomainTypes()
    {
        return typeof(GuitarNote).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true })
            .Where(t => t.GetConstructor(Type.EmptyTypes) is not null)
            .Where(t => CloneMethod(t) is not null || CopyFromMethod(t) is not null)
            .OrderBy(t => t.FullName, StringComparer.Ordinal);
    }

    private static MethodInfo? CloneMethod(Type type)
    {
        var method = type.GetMethod("Clone", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
        return method is not null && method.ReturnType == type ? method : null;
    }

    private static MethodInfo? CopyFromMethod(Type type)
    {
        var method = type.GetMethod("CopyFrom", BindingFlags.Public | BindingFlags.Instance, [type]);
        return method is not null && method.ReturnType == typeof(void) ? method : null;
    }

    private static IReadOnlySet<string> IgnoredPaths(Type type)
    {
        return Exclusions
            .Where(e => e.Owner == type)
            .Select(e => $"{type.Name}.{e.Property}")
            .ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void EveryClone_CarriesEverySettableProperty()
    {
        var failures = new List<string>();
        var checkedTypes = 0;
        var checkedProperties = 0;

        foreach (var type in CopyableDomainTypes())
        {
            var clone = CloneMethod(type);
            if (clone is null) continue;

            checkedTypes++;
            checkedProperties += DomainFieldProbe.SettableProperties(type).Count;

            var source = DomainFieldProbe.Populated(type);
            var copy = clone.Invoke(source, null);

            foreach (var diff in DeepCompare.Diff(type.Name, source, copy, IgnoredPaths(type)))
                failures.Add($"{type.Name}.Clone() lost {diff}");
        }

        Assert.True(checkedTypes >= 8,
            $"Only {checkedTypes} cloneable domain types were found — the reflection scan reached nothing.");
        Assert.True(checkedProperties >= 60,
            $"Only {checkedProperties} settable properties were probed — the value generator reached nothing.");
        Assert.True(failures.Count == 0,
            "A Clone() dropped a property. Add the assignment, or add a justified FieldExclusion:\n  " +
            string.Join("\n  ", failures));
    }

    [Fact]
    public void EveryCopyFrom_CarriesEverySettableProperty()
    {
        var failures = new List<string>();
        var checkedTypes = 0;

        foreach (var type in CopyableDomainTypes())
        {
            var copyFrom = CopyFromMethod(type);
            if (copyFrom is null) continue;

            checkedTypes++;

            var source = DomainFieldProbe.Populated(type);
            var destination = Activator.CreateInstance(type)!;
            copyFrom.Invoke(destination, [source]);

            foreach (var diff in DeepCompare.Diff(type.Name, source, destination, IgnoredPaths(type)))
                failures.Add($"{type.Name}.CopyFrom() lost {diff}");
        }

        Assert.True(checkedTypes >= 7,
            $"Only {checkedTypes} types with CopyFrom were found — the reflection scan reached nothing.");
        Assert.True(failures.Count == 0,
            "A CopyFrom() dropped a property. Add the assignment, or add a justified FieldExclusion:\n  " +
            string.Join("\n  ", failures));
    }

    /// <summary>
    ///     <c>CopyFrom</c> must be idempotent over collections: it CLEARS then
    ///     refills, so copying onto an already-populated destination may not
    ///     append. (<c>MutateElementsAction.Execute</c> copies onto the LIVE
    ///     element, which is never empty.)
    /// </summary>
    [Fact]
    public void CopyFrom_OntoAPopulatedDestination_ReplacesRatherThanAppends()
    {
        var failures = new List<string>();

        foreach (var type in CopyableDomainTypes())
        {
            var copyFrom = CopyFromMethod(type);
            if (copyFrom is null) continue;

            var source = DomainFieldProbe.Populated(type);
            var destination = DomainFieldProbe.Populated(type);
            copyFrom.Invoke(destination, [source]);

            foreach (var diff in DeepCompare.Diff(type.Name, source, destination, IgnoredPaths(type)))
                failures.Add($"{type.Name}.CopyFrom() onto a populated instance: {diff}");
        }

        Assert.True(failures.Count == 0, string.Join("\n  ", failures));
    }

    /// <summary>An exclusion that names a property nobody declares any more is a
    /// hole, not a rule.</summary>
    [Fact]
    public void EveryExclusion_StillNamesALiveProperty()
    {
        foreach (var exclusion in Exclusions)
        {
            Assert.True(exclusion.Owner.GetProperty(exclusion.Property) is not null,
                $"{exclusion.Owner.Name}.{exclusion.Property} no longer exists — delete or repoint the exclusion.");
            Assert.False(string.IsNullOrWhiteSpace(exclusion.Reason));
        }
    }
}
