using DotCharter.Services.Editing.Highway;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     #604: a chord's bend curve follows the "Apply to" ladder pinned over the Bends section, the
///     way a technique does. The inspector's bend editor commits <see cref="InspectorViewModel.ChordBendWrite" />
///     through <c>ApplyChordMutation</c>; off "selected" that is <see cref="ChordTechniqueEdit" /> over
///     the panel's <c>TechniqueTargets</c>, which is what this drives, per rung:
///     <list type="bullet">
///         <item>the curve lands on the picked STRING of exactly the rung's chords, no other string;</item>
///         <item>the chords written are exactly what the #485 preview marked over Bends (the Technique
///         family, which <see cref="SoundSectionsViewModel.ApplyFamily" /> picks for Bends);</item>
///         <item>no template changes; ONE undo node puts every level back (deep compare).</item>
///     </list>
///     The real editor's wiring through the real inspector is pinned headless
///     (<c>SoundTabRailUiPerfSmokeTests.OverTheBends_*</c>).
/// </summary>
public sealed class ChordBendApplyToTests
{
    private const int BentString = 2;

    private static readonly IReadOnlyList<BendValue> Curve =
        [new BendValue(new FractionalPosition(0), 0), new BendValue(new FractionalPosition(new Fraction(1, 4)), 1)];

    public static TheoryData<ChordApplyRung, int> Rungs() => ChordLadderFixture.Rungs();

    [Theory]
    [MemberData(nameof(Rungs))]
    public void TheCurve_LandsOnThePickedStringOfExactlyTheRung_AndWhatThePreviewMarked(ChordApplyRung rung,
        int expected)
    {
        var f = new ChordLadderFixture();
        f.Choose(rung);
        var preview = f.Panel.Preview.Resolved;
        var templates = f.TemplateSnapshot();
        var before = f.Fingerprints();

        Commit(f);

        var changed = f.ChangedSince(before);
        Assert.Equal(expected, changed.Count);
        Assert.True(changed.SetEquals(f.Panel.TechniqueTargets()!.Chords), "the bend reached other chords than the rung");
        if (rung <= ChordApplyRung.Selected) Assert.True(preview.IsEmpty);
        else Assert.True(changed.SetEquals(preview.Chords), "the bend reached other chords than the preview marked");
        foreach (var chord in changed)
        {
            foreach (var note in chord.ChordNotes)
            {
                Assert.Equal(note.StringIndex == BentString ? Curve.Count : 0, note.BendValues.Count);
            }
        }

        f.AssertNoTemplateChanged(templates);
        Assert.Single(f.History.GetUndoSteps());
    }

    [Theory]
    [InlineData(ChordApplyRung.EveryUse)]
    [InlineData(ChordApplyRung.SameGrip)]
    public void OneUndo_RestoresEveryLevel(ChordApplyRung rung)
    {
        var f = new ChordLadderFixture();
        var before = ChartProjectCloner.CloneArrangement(f.Arrangement);
        f.Choose(rung);

        Commit(f);

        Assert.Single(f.History.GetUndoSteps());
        Assert.All(f.Arrangement.Levels, l => Assert.Contains(l.Chords, c => c.ChordNotes[BentString].BendValues.Count > 0));
        f.History.Undo();
        Assert.Empty(DeepCompare.Diff("arr", before, ChartProjectCloner.CloneArrangement(f.Arrangement), new HashSet<string>()));
    }

    /// <summary>A chord that does not play the picked string is left exactly as it is.</summary>
    [Fact]
    public void AChordWithoutThePickedString_IsLeftAlone()
    {
        var chord = new Chord();
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
        var before = chord.Clone();

        InspectorViewModel.ChordBendWrite(BentString, Curve)(chord);

        Assert.Empty(chord.ChordNotes[0].BendValues);
        Assert.Equal(before.ChordNotes.Count, chord.ChordNotes.Count);
    }

    /// <summary>The inspector's bend commit: the ladder's builder off "selected", its own selection
    /// path on it — <c>InspectorViewModel.ApplyChordMutation</c>.</summary>
    private static void Commit(ChordLadderFixture f)
    {
        var targets = f.Panel.TechniqueTargets()!;
        var write = InspectorViewModel.ChordBendWrite(BentString, Curve);
        var action = targets.IsSelection
            ? new MutateChordsAction(f.Editor.PlacedChords, f.Selection.SelectedChords.ToList(),
                f.Selection.SelectedChords.Select(c => c.Clone()).ToList(),
                f.Selection.SelectedChords.Select(c =>
                {
                    var clone = c.Clone();
                    write(clone);
                    return clone;
                }).ToList(), null)
            : ChordTechniqueEdit.Build(f.Arrangement.Levels, f.Editor.PlacedChords, targets.Chords, write, null,
                _ => null);
        Assert.NotNull(action);
        f.History.Do(action);
    }
}
