using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Presentation.Screens.Editor;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tests.Services.Editing.ChordShapes;

namespace DotCharter.Tests.Presentation.Editor;

/// <summary>
///     The Clean up shapes dialog over a chart shaped like an old import: each group says what it will do before
///     anything is written, every tick and answer recounts Apply, and Apply is ONE undo step that closes with true.
/// </summary>
public sealed class ShapeCleanupViewModelTests
{
    private readonly ChartEditHarness _h = new();
    private readonly MessyShapesFixture _f = new();

    public ShapeCleanupViewModelTests()
    {
        _h.Project.Arrangements.Add(_f.Arrangement);
        _h.Editor.ActiveArrangement = _f.Arrangement;
        _h.State.ActiveLevel = _f.Level0;
    }

    private ShapeCleanupViewModel Open() => new(_h.Services);

    [Fact]
    public void ItNamesWhatItFound_InThreeGroups()
    {
        var vm = Open();

        Assert.False(vm.IsTidy);
        Assert.Equal("Duplicates (2)", vm.DuplicatesHeader);
        Assert.Equal("Unused (3)", vm.UnusedHeader);
        Assert.Equal("Review (1)", vm.NearDuplicatesHeader);
        Assert.Equal("2 duplicate groups · 3 unused · 1 to review", vm.Summary);
        Assert.True(vm.HasDuplicates && vm.HasUnused && vm.HasNearDuplicates);
    }

    [Fact]
    public void ADuplicateGroup_SaysWhoKeepsTheNameAndWhatMoves()
    {
        var vm = Open();

        Assert.Equal("x x 9 8 11 x x ×3", vm.Duplicates[0].Title);
        Assert.Equal("3 unnamed copies become one and take over 2 chords · 2 hand shapes", vm.Duplicates[0].Detail);
        Assert.Equal("A5 ×2", vm.Duplicates[1].Title);
        Assert.Equal("Keeps “A5” and takes over 1 chord · 1 hand shape", vm.Duplicates[1].Detail);
        Assert.Equal(7, vm.Duplicates[0].StringCount);
        Assert.All(vm.Duplicates, d => Assert.True(d.IsIncluded));
        Assert.All(vm.Unused, u => Assert.True(u.IsIncluded));
    }

    [Fact]
    public void TheUnusedShapes_AreListedByNameOrFrets()
    {
        var vm = Open();

        Assert.Equal(["x x x 5 7 x x", "Dm", "1 x x x x x x"], vm.Unused.Select(u => u.Name));
    }

    [Fact]
    public void AReviewPair_ShowsBothSidesWithTheirUses_AndStartsOnKeepBoth()
    {
        var pair = Assert.Single(Open().NearDuplicates);

        Assert.StartsWith("Same frets, another finger on the ", pair.DifferenceText, StringComparison.Ordinal);
        Assert.Equal("x x 9 8 11 x x · 7 uses", pair.FirstLabel);
        Assert.Equal("x x 9 8 11 x x · 3 uses", pair.SecondLabel);
        Assert.Equal(["Keep both", "Use the left one", "Use the right one"], pair.Choices.Select(c => c.Label));
        Assert.Equal([true, false, false], pair.Choices.Select(c => c.IsSelected));
    }

    [Fact]
    public void ApplyCountsOnlyWhatIsTickedOrDecided_AndTellsTheDialog()
    {
        var vm = Open();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.Equal("Apply 5 changes", vm.ApplyLabel);

        vm.Unused[0].IsIncluded = false;
        Assert.Equal("Apply 4 changes", vm.ApplyLabel);
        vm.NearDuplicates[0].Choices[1].SelectCommand.Execute(null);
        Assert.Equal("Apply 5 changes", vm.ApplyLabel);
        Assert.Equal([false, true, false], vm.NearDuplicates[0].Choices.Select(c => c.IsSelected));
        vm.NearDuplicates[0].Choices[0].SelectCommand.Execute(null);
        vm.Duplicates[0].IsIncluded = false;
        vm.Duplicates[1].IsIncluded = false;
        foreach (var unused in vm.Unused) unused.IsIncluded = false;

        Assert.Equal("Apply 0 changes", vm.ApplyLabel);
        Assert.False(vm.ApplyCommand.CanExecute(null));
        Assert.Contains(nameof(IShapeCleanupScreen.ApplyLabel), changed);
    }

    [Fact]
    public void Apply_IsOneUndoStep_ClosesWithTrue_AndSaysWhatWasRemoved()
    {
        var vm = Open();
        var before = _f.UseIds();

        vm.ApplyCommand.Execute(null);

        Assert.True(ChartEditHarness.Answer(vm.Result));
        Assert.Equal(4, _f.Arrangement.ChordTemplates.Count);
        Assert.Equal("Cleaned up the shapes: 6 shapes removed.", _h.State.MainDisplayMessage);
        Assert.Single(_h.History.GetTree(), n => n.ParentId is not null);

        _h.History.Undo();

        Assert.Equal(10, _f.Arrangement.ChordTemplates.Count);
        Assert.Equal(before, _f.UseIds());
        Assert.False(_h.History.CanUndo);
    }

    [Fact]
    public void ADecidedPair_IsAppliedWithTheRest_InTheSameStep()
    {
        var vm = Open();
        vm.NearDuplicates[0].Choices[2].SelectCommand.Execute(null);

        vm.ApplyCommand.Execute(null);

        Assert.Equal(3, _f.Arrangement.ChordTemplates.Count);
        Assert.Single(_h.History.GetTree(), n => n.ParentId is not null);
    }

    [Fact]
    public void Cancel_ChangesNothing_AndClosesWithFalse()
    {
        var vm = Open();

        vm.CancelCommand.Execute(null);

        Assert.False(ChartEditHarness.Answer(vm.Result));
        Assert.Equal(10, _f.Arrangement.ChordTemplates.Count);
        Assert.False(_h.History.CanUndo);
    }

    [Fact]
    public void ATidyArrangement_SaysSo_AndHasNothingToApply()
    {
        var tidy = new ChordShapesFixture();
        foreach (var level in tidy.Arrangement.Levels)
        {
            foreach (var chord in level.Chords.Where(c => c.TemplateId == tidy.AmAlt.Id).ToList()) level.Chords.Remove(chord);
            foreach (var shape in level.HandShapes.Where(h => h.TemplateId == tidy.AmAlt.Id).ToList()) level.HandShapes.Remove(shape);
        }

        tidy.Arrangement.ChordTemplates.Remove(tidy.G);
        tidy.Arrangement.ChordTemplates.Remove(tidy.AmAlt);
        _h.Editor.ActiveArrangement = tidy.Arrangement;

        var vm = Open();

        Assert.True(vm.IsTidy);
        Assert.Equal("This arrangement's shapes are tidy.", vm.Summary);
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public void WithoutAnArrangement_ItIsTidyAndInert()
    {
        _h.Editor.ActiveArrangement = null;

        var vm = Open();

        Assert.True(vm.IsTidy);
        vm.ApplyCommand.Execute(null);
        Assert.False(_h.History.CanUndo);
    }
}
