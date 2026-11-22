using DotCharter.UI.Layout;

namespace DotCharter.Tests.UI;

/// <summary>
///     #603 row 4, the pure half: the width clamps the start-up restore applies to the AI chat
///     and Inspector columns. The shell wiring (reset, restore, maximize) is driven on the real
///     <c>MainWindow</c> in <c>Perf/LayoutResetRestoreUiPerfSmokeTests</c>.
/// </summary>
public sealed class LayoutResetRestoreTests
{
    /// <summary>A reset changes the two reset panes without losing the other pre-run choices.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void AResetSnapshot_ShowsInspectorAndPreview_AndPreservesOtherChoices(bool inspector, bool preview)
    {
        var before = new GameplayPanelSnapshot(inspector, preview, true, false, false, true);

        var after = before.Reset();

        Assert.True(after.InspectorVisible);
        Assert.True(after.PreviewVisible);
        Assert.True(after.StemMixerVisible);
        Assert.False(after.SheetMusicVisible);
        Assert.False(after.PhraseCatalogVisible);
        Assert.True(after.AiChatVisible);
        Assert.Equal(inspector, before.InspectorVisible);
        Assert.Equal(preview, before.PreviewVisible);
    }

    [Theory]
    [InlineData(900.0, 1280.0, 1280.0 - AiChatColumnPolicy.SplitterPx - AiChatColumnPolicy.MinEditorWidthPx)]
    [InlineData(500.0, 2560.0, 500.0)]
    [InlineData(900.0, 600.0, AiChatColumnPolicy.MinWidthPx)]
    [InlineData(900.0, 0.0, 900.0)]
    [InlineData(900.0, double.NaN, 900.0)]
    public void AChatWidth_IsClampedToTheSplit_LeavingTheEditorItsFloor(double width, double split, double expected) =>
        Assert.Equal(expected, AiChatColumnPolicy.ClampToSplit(width, split));

    [Theory]
    [InlineData(1400.0, 1000.0, 1000.0 - 6 - MainWindowLayoutController.MinPreviewColumnPx)]
    [InlineData(500.0, 1900.0, 500.0)]
    [InlineData(1400.0, 300.0, MainWindowLayoutController.MinInspectorWidthPx)]
    [InlineData(1400.0, 0.0, 1400.0)]
    public void AnInspectorWidth_IsClampedToItsGrid_LeavingThePreviewRoom(double width, double grid, double expected) =>
        Assert.Equal(expected, MainWindowLayoutController.ClampedInspectorWidth(width, grid));

    /// <summary>
    ///     A temporary shrink narrows the column only: growing the split back gives the restored
    ///     560 px chat and 600 px Inspector back, and those are what a close saves.
    /// </summary>
    [Fact]
    public void AShrinkThenGrow_GivesTheRestoredWidthsBack()
    {
        var chat = new SideColumnWidth();
        var inspector = new SideColumnWidth();
        chat.Prefer(560);
        inspector.Prefer(600);

        var narrowChat = chat.Shown(px => AiChatColumnPolicy.ClampToSplit(px, 900));
        var narrowInspector = inspector.Shown(px => MainWindowLayoutController.ClampedInspectorWidth(px, 700));
        Assert.Equal(900 - AiChatColumnPolicy.SplitterPx - AiChatColumnPolicy.MinEditorWidthPx, narrowChat);
        Assert.Equal(700 - 6 - MainWindowLayoutController.MinPreviewColumnPx, narrowInspector);

        // The resize that follows sees the narrowed widths it applied itself, not a drag.
        chat.Observe(narrowChat!.Value);
        inspector.Observe(narrowInspector!.Value);

        Assert.Equal(560, chat.Shown(px => AiChatColumnPolicy.ClampToSplit(px, 2400)));
        Assert.Equal(600, inspector.Shown(px => MainWindowLayoutController.ClampedInspectorWidth(px, 1900)));
        Assert.Equal(560, chat.PreferredPx);
        Assert.Equal(600, inspector.PreferredPx);
    }

    /// <summary>Cancellation ignores transient widths and late completions while preserving fitting.</summary>
    [Fact]
    public void ACancelledDrag_KeepsItsPreferenceAndRefits()
    {
        var width = new SideColumnWidth();
        width.Prefer(560);
        width.BeginDrag(width.Shown(_ => 400));
        width.Observe(720);

        width.CancelDrag(720);
        width.Observe(720);
        Assert.False(width.IsDragging);
        Assert.Equal(560, width.PreferredPx);
        Assert.Equal(350, width.Shown(_ => 350));
        width.EndDrag(730);
        Assert.Equal(560, width.Shown(px => px));

        width.BeginDrag(560);
        width.EndDrag(500);
        Assert.Equal(500, width.PreferredPx);
    }

    /// <summary>An unrelated cancellation leaves ordinary observation of splitter widths enabled.</summary>
    [Fact]
    public void CancellationWithoutADrag_DoesNotMaskAChosenWidth()
    {
        var width = new SideColumnWidth();
        width.Prefer(560);
        width.CancelDrag(720);
        width.Observe(500);
        Assert.Equal(500, width.PreferredPx);
    }

    /// <summary>A width the splitter writes while the window is narrow is the user's new width.</summary>
    [Fact]
    public void ADragDuringANarrowWindow_IsTheNewPreferredWidth()
    {
        var chat = new SideColumnWidth();
        chat.Prefer(560);
        _ = chat.Shown(px => AiChatColumnPolicy.ClampToSplit(px, 900));

        chat.Observe(350);

        Assert.Equal(350, chat.PreferredPx);
        Assert.Equal(350, chat.Shown(px => AiChatColumnPolicy.ClampToSplit(px, 2400)));
    }

    /// <summary>
    ///     During a drag the widths a layout pass sees are transient: only where the drag ends,
    ///     against where it started, decides the preferred width.
    /// </summary>
    [Fact]
    public void ADrag_IsDecidedByItsEndAlone()
    {
        var chat = new SideColumnWidth();
        chat.Prefer(560);
        var narrow = chat.Shown(px => AiChatColumnPolicy.ClampToSplit(px, 900))!.Value;

        chat.BeginDrag(narrow);
        chat.Observe(narrow - 20);
        Assert.True(chat.IsDragging);
        chat.EndDrag(narrow);
        Assert.False(chat.IsDragging);
        Assert.Equal(560, chat.PreferredPx);

        chat.BeginDrag(narrow);
        chat.EndDrag(narrow - 30);
        Assert.Equal(narrow - 30, chat.PreferredPx);
    }
}
