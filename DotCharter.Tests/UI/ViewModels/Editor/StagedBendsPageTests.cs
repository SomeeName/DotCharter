using DotCharter.Tests.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     #751 P5 review L4: the Bends page's "Delete point" is enabled only while a point is selected, and a curve that
///     peaks above the plot's range raises the range to hold it (never drawn flat on the top edge).
/// </summary>
public sealed class StagedBendsPageTests
{
    private const int Strand = 1;

    [Fact]
    public void DeletePoint_IsEnabledOnlyWithASelectedPoint()
    {
        var (_, pages, editor) = Build(null);
        var bends = pages.Bends;
        Assert.False(bends.HasPoints);
        Assert.False(bends.DeletePointCommand.CanExecute(null));

        bends.AddPointCommand.Execute(null);
        Assert.True(bends.HasPoints);
        Assert.True(bends.DeletePointCommand.CanExecute(null));

        editor.ClearSelection();
        Assert.False(bends.DeletePointCommand.CanExecute(null));
    }

    [Fact]
    public void ACurveAboveTheRange_RaisesTheRangeToHoldIt()
    {
        var (_, pages, _) = Build(4);
        Assert.Equal(4, pages.Bends.Range);
    }

    [Fact]
    public void ACurveWithinTheRange_KeepsTheChosenRange()
    {
        var (_, pages, _) = Build(1);
        Assert.Equal(2, pages.Bends.Range);
    }

    private static (ChordLadderFixture, InspectorStagedPages, DotCharter.UI.ViewModels.Panels.BendEditorViewModel) Build(double? peak)
    {
        var f = new ChordLadderFixture();
        var source = f.First;
        var strand = source.ChordNotes.Single(n => n.StringIndex == Strand);
        strand.Sustain = new FractionalPosition(1);
        if (peak is { } step) strand.BendValues.Add(new BendValue(source.Position + new FractionalPosition(0, new Fraction(1, 2)), step));
        var sections = new SoundSectionsViewModel { Kind = SoundSelectionKind.Chord, HasChords = true };
        sections.ChooseCommand.Execute(SoundSection.Bends);
        var pages = InspectorStagedPagesTests.Pages(f, sections);
        var editor = pages.Bends.BuildEditor(source, Strand, () => source.Position, () => source.Position + strand.Sustain);
        return (f, pages, editor);
    }
}
