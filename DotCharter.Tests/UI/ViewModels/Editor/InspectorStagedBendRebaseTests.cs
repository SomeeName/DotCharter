using DotCharter.Tests.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     #751 P5 review H2 / H3 / H6: a staged bend stays relative to its sound (a move or a sustain change carries it
///     along, so Apply writes the shape that was drawn), a keyboard edit of the bend itself drops the draft and names
///     it on the status rail (Apply never writes over the keyboard), a chord keeps one draft per string (switching the
///     plot's string keeps the other; Apply writes both), and every Apply reports how far it went on the status rail.
/// </summary>
public sealed class InspectorStagedBendRebaseTests
{
    private static readonly FractionalPosition Half = new(0, new Fraction(1, 2));

    private static IReadOnlyList<BendValue> Curve(FractionalPosition onset) =>
        [new BendValue(onset, 0), new BendValue(onset + Half, 2), new BendValue(onset + new FractionalPosition(1), 2)];

    private static ChordLadderFixture Fixture()
    {
        var f = new ChordLadderFixture();
        f.Selection.SelectedChords.Remove(f.Second);
        foreach (var strand in f.First.ChordNotes) strand.Sustain = new FractionalPosition(1);
        return f;
    }

    private static void Rewrite(ChordLadderFixture f, Action<Chord> edit)
    {
        var clone = f.First.Clone();
        edit(clone);
        f.History.Do(new MutateChordsAction(f.Arrangement.Levels[0].Chords, [f.First], [f.First.Clone()], [clone]));
    }

    [Fact]
    public void AMovedChord_CarriesItsStagedCurve_AndApplyWritesTheShapeThatWasDrawn()
    {
        var f = Fixture();
        using var staged = InspectorStagedEditsTests.Engine(f);
        f.Choose(ChordApplyRung.Selected);
        staged.StageBend(2, Curve(f.First.Position));

        var moved = new FractionalPosition(5, new Fraction(1, 4));
        Rewrite(f, c => c.Position = moved);

        Assert.Equal(Curve(moved).Select(v => v.Position), staged.BendOn(2)!.Values.Select(v => v.Position));
        Assert.True(staged.Apply(StagedPage.Bends));
        Assert.Equal(Curve(moved).Select(v => (v.Position, v.Step)), f.First.ChordNotes[2].BendValues.Select(v => (v.Position, v.Step)));
    }

    [Fact]
    public void AKeyboardEditOfTheBend_DropsTheDraft_AndNamesItOnTheStatusRail()
    {
        var f = Fixture();
        using var staged = InspectorStagedEditsTests.Engine(f);
        staged.StageBend(2, Curve(f.First.Position));

        Rewrite(f, c => c.ChordNotes[2].BendValues.Add(new BendValue(f.First.Position, 1)));

        Assert.Null(staged.BendOn(2));
        Assert.True(staged.CanLeave(StagedPage.Bends));
        Assert.False(staged.Apply(StagedPage.Bends));
        Assert.Equal(1, f.First.ChordNotes[2].BendValues.Single().Step);
        Assert.Contains("0:00.000", f.State.MainDisplayMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AStringSwitch_KeepsTheOtherStringsDraft_AndApplyWritesBoth()
    {
        var f = Fixture();
        using var staged = InspectorStagedEditsTests.Engine(f);
        f.Choose(ChordApplyRung.Selected);
        staged.StageBend(2, Curve(f.First.Position));
        staged.StageBend(1, Curve(f.First.Position));

        Assert.NotNull(staged.BendOn(2));
        Assert.NotNull(staged.BendOn(1));
        var steps = f.History.GetUndoSteps().Count;
        Assert.True(staged.Apply(StagedPage.Bends));

        Assert.Equal(steps + 1, f.History.GetUndoSteps().Count);
        Assert.Equal(3, f.First.ChordNotes[2].BendValues.Count);
        Assert.Equal(3, f.First.ChordNotes[1].BendValues.Count);
        Assert.Empty(f.First.ChordNotes[0].BendValues);
    }

    [Fact]
    public void AnAuthoredBendOnTheSourceItself_IsNamedBeforeApplyReplacesIt()
    {
        var f = Fixture();
        f.First.ChordNotes[2].BendValues.Add(new BendValue(f.First.Position, 1));
        using var staged = InspectorStagedEditsTests.Engine(f);
        f.Choose(ChordApplyRung.Selected);
        staged.StageBend(2, Curve(f.First.Position));

        Assert.Contains(f.First, staged.Plan(StagedPage.Bends).Replacements);
        Assert.True(staged.Review(StagedPage.Bends).NeedsConfirm);
    }

    [Fact]
    public void EveryApply_SaysOnTheStatusRailHowFarItWent()
    {
        var f = new ChordLadderFixture();
        using var staged = InspectorStagedEditsTests.Engine(f);
        f.Choose(ChordApplyRung.EveryUse);
        var reached = f.Panel.TechniqueTargets()!.Chords;

        staged.StageTechnique(new StagedKey(TechniqueField.Vibrato, 1), 1);
        Assert.True(staged.Apply(StagedPage.Technique));
        Assert.StartsWith($"Technique applied to {reached.Count} chords on ", f.State.MainDisplayMessage, StringComparison.Ordinal);

        staged.StageBend(2, Curve(f.First.Position));
        Assert.True(staged.Apply(StagedPage.Bends));
        Assert.StartsWith($"Bend applied to {reached.Count} chords", f.State.MainDisplayMessage, StringComparison.Ordinal);
    }
}
