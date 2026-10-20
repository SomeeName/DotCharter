using DotCharter.UI.Layout;

namespace DotCharter.Tests.UI;

/// <summary>
///     Which editor row splitter shows (<see cref="EditorRowSplitterPolicy" />). The run with
///     nothing above the highway is the case that regressed: a splitter over an empty band.
///     The real shell is pinned by <c>Perf/GameplaySplitterUiPerfSmokeTests</c>.
/// </summary>
public sealed class EditorRowSplitterPolicyTests
{
    [Fact]
    public void ARun_WithNothingAbove_HasNoSplitter()
    {
        Assert.False(EditorRowSplitterPolicy.DockSplitterVisible(bottomDockVisible: true, gameplayPresentation: true, sheetVisible: false));
        Assert.False(EditorRowSplitterPolicy.SheetSplitterVisible(sheetVisible: false, gameplayPresentation: true));
    }

    [Fact]
    public void ARun_WithTheNotationBand_HasOneSplitter_TheDockOne()
    {
        Assert.True(EditorRowSplitterPolicy.DockSplitterVisible(bottomDockVisible: true, gameplayPresentation: true, sheetVisible: true));
        Assert.False(EditorRowSplitterPolicy.SheetSplitterVisible(sheetVisible: true, gameplayPresentation: true));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void TheEditor_ShowsTheDockSplitterWithTheDock(bool dock, bool sheet, bool expected) =>
        Assert.Equal(expected, EditorRowSplitterPolicy.DockSplitterVisible(dock, gameplayPresentation: false, sheet));

    [Fact]
    public void TheEditor_ShowsTheSheetSplitterWithTheSheet()
    {
        Assert.True(EditorRowSplitterPolicy.SheetSplitterVisible(sheetVisible: true, gameplayPresentation: false));
        Assert.False(EditorRowSplitterPolicy.SheetSplitterVisible(sheetVisible: false, gameplayPresentation: false));
    }
}
