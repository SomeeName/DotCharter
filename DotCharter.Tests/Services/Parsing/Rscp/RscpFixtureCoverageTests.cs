using System.Collections;
using System.Reflection;

namespace DotCharter.Tests.Services.Parsing.Rscp;

/// <summary>
///     <c>RscpDeepValueRoundTripTests</c> already walks the whole
///     <c>ChartProject</c> graph by reflection after a save/load — but it can only
///     see a loss in a field the FIXTURE actually populated. A property left at
///     its default on both sides compares equal, so a newly added domain field
///     that the writer never learned about passes that suite VACUOUSLY on the day
///     it is added, which is exactly the day it needed to fail.
///     <para>
///         This is the other half: the fixture itself is linted, by reflection,
///         for "every settable property of every domain type in the graph carries
///         a non-default value on at least one instance". The union is the right
///         statement — the fixture deliberately spreads coverage across two notes
///         and two chord notes so it can also exercise the DEFAULT branch of each
///         writer.
///     </para>
/// </summary>
public sealed class RscpFixtureCoverageTests
{
    /// <summary>
    ///     Properties the rich fixture deliberately leaves at their default. Each
    ///     is asserted to still exist, so a rename breaks the exclusion rather
    ///     than silently widening it.
    /// </summary>
    private static readonly FieldExclusion[] Exclusions =
    [
        new(typeof(ChartProject), nameof(ChartProject.Provenance),
            "An official rich fixture would be play-only, and DcSongChartTests / DcSongRoundTripTests EXPORT it "
            + "(#822). OfficialContentProvenanceTests.The_flags_round_trip_through_the_rscp owns the round trip."),

        new(typeof(Arrangement), nameof(Arrangement.Provenance),
            "Same as ChartProject.Provenance: OfficialContentProvenanceTests round-trips it per arrangement (#822)."),

        new(typeof(GuitarNote), nameof(GuitarNote.GraceSlide),
            "Import-transient: GpPostProcessor.MaterializeGraceSlides consumes it and clears it before the "
            + "chart leaves the importer, and no format persists it. A fixture that set it would be asserting "
            + "the rscp carries a flag it is designed never to carry."),

        new(typeof(ArrangementProperties), nameof(ArrangementProperties.StandardTuning),
            "The 31 technique/path flags have their own exhaustive round trip — "
            + "RscpLossyFieldRoundTripTests.ArrangementProperties_RoundTripEveryFlag flips every one of them "
            + "against its default and reads them all back. Duplicating that in the rich fixture would only "
            + "make the whole-graph walk harder to read."),

        new(typeof(ChartData), nameof(ChartData.ChartFormatVersion),
            "Stamped, not carried: RscpWriter writes RscpMigrations.CurrentVersion literally and "
            + "RscpMigrations.Migrate sets the loaded project to it, so this field can only ever come back "
            + "as the current version whatever the fixture puts in. RscpMigrationTests owns the versioning."),

        new(typeof(ChartData), nameof(ChartData.SingleNotes),
            "The LEGACY flat note store. The .rscp persists the per-difficulty ChartLevel collections only — "
            + "nothing in Parsing/Rscp so much as names SingleNotes — so a fixture value here would assert a "
            + "round trip that is deliberately not implemented."),

        new(typeof(ChartData), nameof(ChartData.Chords),
            "The legacy flat chord store, same as ChartData.SingleNotes: not persisted by the .rscp at all."),

        new(typeof(Arrangement), nameof(Arrangement.StringCount),
            "Structurally coupled to Tuning/TuningPitches — the rich arrangement is a six-string on purpose, "
            + "and a StringCount that disagrees with its six offsets would be measuring the fixture rather "
            + "than the writer. The non-six case round-trips in "
            + "RscpTuningRoundTripTests.SevenStringTuningsSurviveToo.")
    ];

    /// <summary>The technique flags are excluded as a GROUP — one named entry
    /// above stands for the set, and this is the set.</summary>
    private static bool IsTechniqueFlag(PropertyInfo property)
    {
        return property.DeclaringType == typeof(ArrangementProperties) &&
               property.PropertyType == typeof(int);
    }

    [Fact]
    public void TheRichProjectFixture_LeavesNoSettablePropertyAtItsDefault()
    {
        var project = RichProjectFactory.Build();
        var instances = CollectDomainInstances(project);

        Assert.True(instances.Count >= 10,
            $"Only {instances.Count} domain types were reached in the fixture graph — the walk found nothing.");

        var excluded = Exclusions.Select(e => (e.Owner, e.Property)).ToHashSet();
        var unset = new List<string>();
        var probed = 0;

        foreach (var (type, values) in instances.OrderBy(kv => kv.Key.Name, StringComparer.Ordinal))
        {
            if (type.GetConstructor(Type.EmptyTypes) is null) continue;
            var fresh = Activator.CreateInstance(type)!;

            foreach (var property in DomainFieldProbe.SettableProperties(type))
            {
                if (excluded.Contains((type, property.Name))) continue;
                if (IsTechniqueFlag(property)) continue;

                probed++;
                if (values.Any(v => CarriesAValue(property, v, fresh))) continue;
                unset.Add($"{type.Name}.{property.Name} (over {values.Count} instance(s))");
            }
        }

        Assert.True(probed >= 120,
            $"Only {probed} properties were probed — the fixture walk reached almost nothing.");
        Assert.True(unset.Count == 0,
            "RichProjectFactory leaves these properties at their default, so RscpDeepValueRoundTripTests "
            + "compares default against default and would not notice the writer dropping them. Give each a "
            + "distinctive value in the fixture, or add a justified FieldExclusion:\n  "
            + string.Join("\n  ", unset));
    }

    [Fact]
    public void EveryExclusion_StillNamesALivePropertyAndCarriesAReason()
    {
        Assert.NotEmpty(Exclusions);
        foreach (var exclusion in Exclusions)
        {
            Assert.True(exclusion.Owner.GetProperty(exclusion.Property) is not null,
                $"{exclusion.Owner.Name}.{exclusion.Property} no longer exists — delete or repoint the exclusion.");
            Assert.True(exclusion.Reason.Length > 40,
                $"{exclusion.Owner.Name}.{exclusion.Property}: an exclusion needs a real reason, not a label.");
        }
    }

    private static bool CarriesAValue(PropertyInfo property, object instance, object fresh)
    {
        var value = property.GetValue(instance);
        var baseline = property.GetValue(fresh);
        return DeepCompare.Diff(property.Name, baseline, value, new HashSet<string>(StringComparer.Ordinal)).Count > 0;
    }

    /// <summary>Every DotCharter.Core class instance reachable from the fixture,
    /// grouped by type.</summary>
    private static Dictionary<Type, List<object>> CollectDomainInstances(object root)
    {
        var domainAssembly = typeof(GuitarNote).Assembly;
        var found = new Dictionary<Type, List<object>>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current)) continue;

            var type = current.GetType();
            if (type.Assembly == domainAssembly && type is { IsClass: true, IsAbstract: false })
            {
                if (!found.TryGetValue(type, out var list)) found[type] = list = [];
                list.Add(current);
            }

            foreach (var child in Children(current)) queue.Enqueue(child);
        }

        return found;
    }

    private static IEnumerable<object> Children(object instance)
    {
        if (instance is string) yield break;

        if (instance is IDictionary dictionary)
        {
            foreach (var value in dictionary.Values)
            {
                if (value is not null and not string && !value.GetType().IsPrimitive)
                    yield return value;
            }

            yield break;
        }

        if (instance is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item is not null and not string && !item.GetType().IsPrimitive)
                    yield return item;
            }

            yield break;
        }

        foreach (var property in DomainFieldProbe.SettableProperties(instance.GetType()))
        {
            object? value;
            try
            {
                value = property.GetValue(instance);
            }
            catch (TargetInvocationException)
            {
                continue;
            }

            if (value is null or string) continue;
            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || type.IsValueType) continue;
            yield return value;
        }
    }
}
