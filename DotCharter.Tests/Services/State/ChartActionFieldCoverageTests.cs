using System.Collections.ObjectModel;
using System.Reflection;

namespace DotCharter.Tests.Services.State;

/// <summary>
///     The mutate actions are the second hand-maintained parallel list over the
///     domain model, and the one whose omissions are invisible: an action carries
///     a TRANSFER function (<c>CopyFrom</c>, or an inline field-by-field lambda)
///     and a COMPARER from <c>ChartActionStateComparer</c>, and
///     <c>MutateElementsAction.HasEffect</c> is the comparer. So a field the
///     comparer forgets makes an edit that touches only that field read as a
///     NO-OP: <c>HistoryService.Do</c> drops the action, the edit never happens
///     and no undo step is recorded. That has shipped at least three times — the
///     duration-only mutation, per-string <c>Accent</c>/<c>Ignore</c>, and the
///     sheet <c>GraceFret</c> hint — each recorded in a comment beside the field
///     that was added back.
///     <para>
///         The invariant this pins: <b>an action must SEE exactly what it can
///         TRANSFER.</b> A field transferred but not compared is a dropped edit; a
///         field compared but not transferred is a phantom undo node that changes
///         nothing. Both halves are asserted per property, by reflection, so a new
///         domain field is covered the day it compiles.
///     </para>
/// </summary>
public sealed class ChartActionFieldCoverageTests
{
    /// <summary>
    ///     Properties deliberately outside an action's transfer+compare set. Each
    ///     one is asserted to still EXIST, so a rename breaks the exclusion rather
    ///     than silently widening it.
    /// </summary>
    private static readonly FieldExclusion[] Exclusions =
    [
        // The whole EventPoint drag path — the gesture, the preview, the commit
        // and MutateEventPointsAction's copyFn — deals in the Position/Phrase/
        // Section triple only, and that NARROWNESS IS THE FEATURE: the new state
        // handed to the action is a freshly built marker that never carried the
        // other two fields, so a copyFn that transferred them would erase them.
        // That exact loss shipped once already through the OTHER editing path —
        // the event dialog (Services/Editing/Events/EventMarkerEdit) rebuilds the marker
        // whole, and until 0.39.2 it left `Events` off, so editing a section's
        // name dropped every crowd cue and metronome tick on it. Status/Events
        // are authored there (remove + add), never by a drag.
        new(typeof(EventPoint), nameof(EventPoint.Events),
            "MutateEventPointsAction deliberately transfers only Position/Phrase/Section: the drag gesture "
            + "builds new states that carry nothing else, so transferring Events would DELETE the marker's "
            + "crowd cues. Authored through EventMarkerEdit's remove+add path instead."),
        new(typeof(EventPoint), nameof(EventPoint.Status),
            "Same as Events — the section-tracker status is authored through the event dialog's remove+add "
            + "path, never carried by a drag, and transferring it from a drag's new state would clear it.")
    ];

    /// <summary>Every mutate action in the editor's undo system, with the element
    /// type it rewrites. Adding one here is the only manual step; forgetting a
    /// FIELD — the failure that actually happens — is caught automatically.</summary>
    public static TheoryData<string> Subjects()
    {
        var data = new TheoryData<string>();
        foreach (var name in Probes.Keys.OrderBy(k => k, StringComparer.Ordinal)) data.Add(name);
        return data;
    }

    private static readonly Dictionary<string, Func<List<string>, int>> Probes = new(StringComparer.Ordinal)
    {
        ["MutateNotesAction"] = failures => Probe<GuitarNote>(failures,
            (c, t, o, n) => new MutateNotesAction(c, t, o, n)),
        ["MutateChordsAction"] = failures => Probe<Chord>(failures,
            (c, t, o, n) => new MutateChordsAction(c, t, o, n)),
        ["MutateHandShapesAction"] = failures => Probe<HandShape>(failures,
            (c, t, o, n) => new MutateHandShapesAction(c, t, o, n)),
        ["MutateAnchorsAction"] = failures => Probe<FretAnchor>(failures,
            (c, t, o, n) => new MutateAnchorsAction(c, t, o, n)),
        ["MutateEventPointsAction"] = failures => Probe<EventPoint>(failures,
            (c, t, o, n) => new MutateEventPointsAction(c, t, o, n)),

        // Nested elements: the parent's comparer is the only thing that can see
        // them, and the parent's CopyFrom is the only thing that transfers them.
        ["MutateChordsAction/ChordNote"] = failures => ProbeNested<Chord, ChordNote>(failures,
            (c, t, o, n) => new MutateChordsAction(c, t, o, n), chord => chord.ChordNotes),
        ["MutateNotesAction/BendValue"] = failures => ProbeNested<GuitarNote, BendValue>(failures,
            (c, t, o, n) => new MutateNotesAction(c, t, o, n), note => note.BendValues),

        // MutateBeatsAction is the same shape with its own hand-written
        // CloneBeat/Apply pair instead of a CopyFrom.
        ["MutateBeatsAction"] = ProbeBeats
    };

    [Theory]
    [MemberData(nameof(Subjects))]
    public void EveryMutableProperty_IsBothTransferredAndSeenByTheComparer(string subject)
    {
        var failures = new List<string>();
        var probed = Probes[subject](failures);

        Assert.True(probed > 0, $"{subject}: the reflection scan probed no properties at all.");
        Assert.True(failures.Count == 0,
            $"{subject} is out of step with its element type:\n  " + string.Join("\n  ", failures));
    }

    /// <summary>The scan must actually reach the model — a guard that walks an
    /// empty property set passes forever. (<c>DocReferenceLintTests</c> idiom.)</summary>
    [Fact]
    public void TheScan_ReachesTheWholeModel()
    {
        var failures = new List<string>();
        var total = Probes.Values.Sum(probe => probe(failures));

        Assert.True(total >= 60,
            $"Only {total} properties were probed across {Probes.Count} actions — the scan reached almost nothing.");
        Assert.True(failures.Count == 0, string.Join("\n  ", failures));
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

    private static IEnumerable<PropertyInfo> Probed(Type type)
    {
        var excluded = Exclusions.Where(e => e.Owner == type).Select(e => e.Property).ToHashSet(StringComparer.Ordinal);
        return DomainFieldProbe.SettableProperties(type).Where(p => !excluded.Contains(p.Name));
    }

    private static int Probe<T>(List<string> failures,
        Func<ObservableCollection<T>, List<T>, List<T>, List<T>, IChartAction> make) where T : class
    {
        var probed = 0;

        foreach (var property in Probed(typeof(T)))
        {
            probed++;
            var target = DomainFieldProbe.Populated<T>();
            var oldState = DomainFieldProbe.Populated<T>();
            var newState = DomainFieldProbe.Populated<T>();
            property.SetValue(newState, DomainFieldProbe.Alternate(property.PropertyType));

            var action = make([target], [target], [oldState], [newState]);
            var label = $"{typeof(T).Name}.{property.Name}";

            if (!action.HasEffect)
            {
                failures.Add(
                    $"{label}: the comparer does not compare it, so an edit that touches only this field " +
                    "reads as a no-op and is dropped with no undo step.");
            }

            action.Execute();
            AssertTransferred(failures, label, property.GetValue(target), property.GetValue(newState));
        }

        return probed;
    }

    private static int ProbeNested<TParent, TChild>(List<string> failures,
        Func<ObservableCollection<TParent>, List<TParent>, List<TParent>, List<TParent>, IChartAction> make,
        Func<TParent, IList<TChild>> children)
        where TParent : class where TChild : class
    {
        var probed = 0;

        foreach (var property in Probed(typeof(TChild)))
        {
            probed++;
            var target = DomainFieldProbe.Populated<TParent>();
            var oldState = DomainFieldProbe.Populated<TParent>();
            var newState = DomainFieldProbe.Populated<TParent>();
            Assert.NotEmpty(children(newState));
            property.SetValue(children(newState)[0], DomainFieldProbe.Alternate(property.PropertyType));

            var action = make([target], [target], [oldState], [newState]);
            var label = $"{typeof(TParent).Name}.{typeof(TChild).Name}.{property.Name}";

            if (!action.HasEffect)
            {
                failures.Add(
                    $"{label}: the parent's comparer does not reach it, so an edit that touches only this " +
                    "field reads as a no-op and is dropped with no undo step.");
            }

            action.Execute();
            AssertTransferred(failures, label,
                property.GetValue(children(target)[0]), property.GetValue(children(newState)[0]));
        }

        return probed;
    }

    private static int ProbeBeats(List<string> failures)
    {
        var probed = 0;

        foreach (var property in Probed(typeof(BeatMarker)))
        {
            probed++;
            var target = DomainFieldProbe.Populated<BeatMarker>();
            var oldState = DomainFieldProbe.Populated<BeatMarker>();
            var newState = DomainFieldProbe.Populated<BeatMarker>();
            property.SetValue(newState, DomainFieldProbe.Alternate(property.PropertyType));

            var action = new MutateBeatsAction([target], [oldState], [newState]);
            var label = $"{nameof(BeatMarker)}.{property.Name}";

            if (!action.HasEffect)
            {
                failures.Add($"{label}: BeatsEqual does not compare it, so a tempo-map edit that touches only " +
                             "this field reads as a no-op and is dropped with no undo step.");
            }

            action.Execute();
            AssertTransferred(failures, label, property.GetValue(target), property.GetValue(newState));
        }

        return probed;
    }

    private static void AssertTransferred(List<string> failures, string label, object? actual, object? expected)
    {
        var diffs = DeepCompare.Diff(label, expected, actual, new HashSet<string>(StringComparer.Ordinal));
        foreach (var diff in diffs)
        {
            failures.Add($"{diff} — the action's transfer function does not carry this field, so Execute() " +
                         "silently leaves the old value in place (and Undo() cannot restore it).");
        }
    }
}
