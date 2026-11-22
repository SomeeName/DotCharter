using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI;
using DotCharter.UI.Layout;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #603 on the real shell: View ▸ Reset Layout and the start-up restore of the main
///     window's geometry, through <c>MainWindow</c>'s own Loaded restore and reset handler.
///     The saved layout is written into the app config before the window is built and put
///     back afterwards.
/// </summary>
[Collection("HeadlessUI")]
public sealed class LayoutResetRestoreUiPerfSmokeTests
{
    /// <summary>
    ///     Row 1: the user dragged the dock tall, then closed the Inspector and the preview (the
    ///     hide remembers the tall row). Reset Layout turns both on again, which POSTS the dock
    ///     sizing; that callback wrote the remembered tall row over the reset's default.
    /// </summary>
    [Fact]
    public Task ResetLayout_AfterTheDockWasHidden_LeavesTheDefaultDockRow() =>
        HeadlessSessionFixture.Dispatch(() => WithShell(_ => { }, (window, vm) =>
        {
            var split = NarrowWidthLayout.Named<Grid>(window, "MainSplitGrid");
            split.RowDefinitions[2].Height = new GridLength(7, GridUnitType.Star);
            NarrowWidthLayout.Settle(window);
            vm.IsInspectorVisible = false;
            vm.IsPreviewVisible = false;
            NarrowWidthLayout.Settle(window);
            Assert.Equal(0, split.RowDefinitions[2].Height.Value);

            vm.ApplyLayoutReset();
            NarrowWidthLayout.Settle(window);

            Assert.True(vm.IsBottomDockVisible);
            Assert.Equal(EditorSplitDefaults.DockRow, split.RowDefinitions[2].Height);
        }), CancellationToken.None);

    /// <summary>
    ///     Row 4: chat and Inspector widths saved on a wide monitor, restored into a 1000 px
    ///     window, are narrowed so both splitters stay on screen and the editor and the preview
    ///     keep their floors.
    /// </summary>
    [Fact]
    public Task WideSavedSideColumns_AreNarrowedToFitASmallWindow() =>
        HeadlessSessionFixture.Dispatch(() => WithShell(cfg =>
        {
            cfg.WindowWidth = 1000;
            cfg.WindowHeight = 700;
            cfg.LayoutAiChatVisible = true;
            cfg.LayoutAiChatWidth = 1400;
            cfg.LayoutInspectorWidth = 1500;
        }, (window, _) =>
        {
            var split = NarrowWidthLayout.Named<Grid>(window, "MainSplitGrid");
            var chat = split.ColumnDefinitions[2].Width;
            Assert.True(chat.IsAbsolute, $"the chat column is {chat}");
            Assert.True(chat.Value <= split.Bounds.Width - AiChatColumnPolicy.SplitterPx - AiChatColumnPolicy.MinEditorWidthPx + 0.5,
                $"a saved {1400} px chat column stayed {chat.Value:0} px in a {split.Bounds.Width:0} px split: its splitter is off-screen");
            Assert.True(split.ColumnDefinitions[0].ActualWidth >= AiChatColumnPolicy.MinEditorWidthPx - 1,
                $"the editor kept only {split.ColumnDefinitions[0].ActualWidth:0} px");

            var insp = NarrowWidthLayout.Named<Grid>(window, "InspectorSplitGrid");
            var inspector = insp.ColumnDefinitions[0].Width;
            var max = Math.Max(MainWindowLayoutController.MinInspectorWidthPx,
                insp.Bounds.Width - 6 - MainWindowLayoutController.MinPreviewColumnPx);
            Assert.True(inspector.Value <= max + 0.5,
                $"a saved 1500 px Inspector stayed {inspector.Value:0} px in a {insp.Bounds.Width:0} px grid");
        }), CancellationToken.None);

    /// <summary>
    ///     Row 4, the other direction: a width that fits is never touched by the clamp — not at
    ///     restore, not when the window grows, and a width the user drags afterwards stays.
    /// </summary>
    [Fact]
    public Task FittingSideColumns_AreNeverWidened_AndADraggedWidthStays() =>
        HeadlessSessionFixture.Dispatch(() => WithShell(cfg =>
        {
            cfg.WindowWidth = 1920;
            cfg.WindowHeight = 1080;
            cfg.LayoutAiChatVisible = true;
            cfg.LayoutAiChatWidth = 420;
            cfg.LayoutInspectorWidth = 480;
        }, (window, _) =>
        {
            var split = NarrowWidthLayout.Named<Grid>(window, "MainSplitGrid");
            var insp = NarrowWidthLayout.Named<Grid>(window, "InspectorSplitGrid");
            Assert.Equal(420, split.ColumnDefinitions[2].Width.Value);
            Assert.Equal(480, insp.ColumnDefinitions[0].Width.Value);

            // What the GridSplitter writes on a drag.
            split.ColumnDefinitions[2].Width = new GridLength(560);
            insp.ColumnDefinitions[0].Width = new GridLength(600, GridUnitType.Pixel);
            NarrowWidthLayout.Settle(window);
            window.Width = 2400;
            NarrowWidthLayout.Settle(window);

            Assert.Equal(560, split.ColumnDefinitions[2].Width.Value);
            Assert.Equal(600, insp.ColumnDefinitions[0].Width.Value);
        }), CancellationToken.None);

    /// <summary>
    ///     Row 4, a temporary shrink (half-screen snap, UI-scale change, smaller monitor): the
    ///     columns narrow, the restored 560/600 px widths come back when the window grows, and a
    ///     close saves 560/600, not the narrowed widths.
    /// </summary>
    [Fact]
    public Task SideColumns_KeepTheUsersWidthThroughATemporaryShrink() =>
        HeadlessSessionFixture.Dispatch(() => WithShell(WideRestore, (window, vm) =>
        {
            var split = NarrowWidthLayout.Named<Grid>(window, "MainSplitGrid");
            var insp = NarrowWidthLayout.Named<Grid>(window, "InspectorSplitGrid");
            Assert.Equal(560, split.ColumnDefinitions[2].Width.Value);
            Assert.Equal(600, insp.ColumnDefinitions[0].Width.Value);

            window.Width = 1000;
            NarrowWidthLayout.Settle(window);
            Assert.True(split.ColumnDefinitions[2].Width.Value < 560,
                $"the chat column stayed {split.ColumnDefinitions[2].Width.Value:0} px in a {split.Bounds.Width:0} px split");
            Assert.True(insp.ColumnDefinitions[0].Width.Value < 600,
                $"the Inspector stayed {insp.ColumnDefinitions[0].Width.Value:0} px in a {insp.Bounds.Width:0} px grid");

            window.Width = 1920;
            NarrowWidthLayout.Settle(window);
            Assert.Equal(560, split.ColumnDefinitions[2].Width.Value);
            Assert.Equal(600, insp.ColumnDefinitions[0].Width.Value);

            window.Width = 1000;
            NarrowWidthLayout.Settle(window);
            PersistOnClose(window);
            Assert.Equal(560, vm.AppConfig.LayoutAiChatWidth);
            Assert.Equal(600, vm.AppConfig.LayoutInspectorWidth);
        }), CancellationToken.None);

    /// <summary>A splitter drag while the window is narrow is the user's new width: it stays and it is saved.</summary>
    [Fact]
    public Task ADragDuringANarrowWindow_PersistsTheDraggedWidth() =>
        HeadlessSessionFixture.Dispatch(() => WithShell(WideRestore, (window, vm) =>
        {
            var split = NarrowWidthLayout.Named<Grid>(window, "MainSplitGrid");
            var insp = NarrowWidthLayout.Named<Grid>(window, "InspectorSplitGrid");
            window.Width = 1000;
            NarrowWidthLayout.Settle(window);

            // What the GridSplitter raises and writes on a drag.
            Drag(window, "AiChatSplitter", () => split.ColumnDefinitions[2].Width = new GridLength(350));
            NarrowWidthLayout.Settle(window);
            var inspectorPx = insp.ColumnDefinitions[0].Width.Value - 40;
            Drag(window, "InspectorPreviewSplitter",
                () => insp.ColumnDefinitions[0].Width = new GridLength(inspectorPx, GridUnitType.Pixel));

            window.Width = 1920;
            NarrowWidthLayout.Settle(window);
            Assert.Equal(350, split.ColumnDefinitions[2].Width.Value);
            Assert.Equal(inspectorPx, insp.ColumnDefinitions[0].Width.Value);

            PersistOnClose(window);
            Assert.Equal(350, vm.AppConfig.LayoutAiChatWidth);
            Assert.Equal(inspectorPx, vm.AppConfig.LayoutInspectorWidth);
        }), CancellationToken.None);

    /// <summary>A confirmed reset during a run replaces the geometry restored at its end.</summary>
    [Fact]
    public Task ResetLayoutDuringARun_RestoresDefaultGeometry() =>
        HeadlessSessionFixture.Dispatch(() => WithShell(WideRestore, (window, vm) =>
        {
            var main = NarrowWidthLayout.Named<Grid>(window, "MainSplitGrid");
            var sheet = NarrowWidthLayout.Named<Grid>(window, "SheetSplitGrid");
            var insp = NarrowWidthLayout.Named<Grid>(window, "InspectorSplitGrid");
            var dock = NarrowWidthLayout.Named<Dock.Avalonia.Controls.DockControl>(window, "BottomDock");
            Assert.True(main.Bounds.Width > 0 && insp.Bounds.Height > 0);
            main.RowDefinitions[0].Height = new GridLength(9, GridUnitType.Star);
            main.RowDefinitions[1].Height = new GridLength(17);
            sheet.RowDefinitions[2].Height = new GridLength(5, GridUnitType.Star);
            insp.ColumnDefinitions[1].Width = new GridLength(19);
            dock.MinWidth = 470;
            vm.IsGameplayPresentation = true;
            try
            {
                Assert.True(vm.ResetLayoutCommand.CanExecute(null));
                Assert.Null(vm.EditingAvailability.ReasonFor(vm.ResetLayoutCommand));
                vm.ConfirmLayoutResetAsync = () => Task.FromResult(true);
                vm.ResetLayoutCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                NarrowWidthLayout.Settle(window);
            }
            finally
            {
                vm.IsGameplayPresentation = false;
            }

            NarrowWidthLayout.Settle(window);
            Assert.Equal(new GridLength(1, GridUnitType.Star), insp.ColumnDefinitions[0].Width);
            Assert.Equal(new GridLength(2, GridUnitType.Star), insp.ColumnDefinitions[2].Width);
            Assert.Equal(new GridLength(6), insp.ColumnDefinitions[1].Width);
            Assert.Equal(EditorSplitDefaults.TimelineRow, main.RowDefinitions[0].Height);
            Assert.Equal(EditorSplitDefaults.DockRow, main.RowDefinitions[2].Height);
            Assert.Equal(new GridLength(6), main.RowDefinitions[1].Height);
            Assert.Equal(new GridLength(1, GridUnitType.Star), sheet.RowDefinitions[2].Height);
            Assert.Equal(320, dock.MinWidth);
            var floors = EditorRowFloors.For(main.Bounds.Height,
                TimelineAreaMinHeight.For(vm.IsSheetMusicVisible, vm.IsMinimapShown, false), true);
            Assert.Equal(floors.TopAreaMinPx, main.RowDefinitions[0].MinHeight);
            Assert.Equal(floors.TimelineRowMinPx, sheet.RowDefinitions[2].MinHeight);
            PersistOnClose(window);
            Assert.Equal(insp.ColumnDefinitions[0].ActualWidth, vm.AppConfig.LayoutInspectorWidth);
            Assert.NotEqual(600, vm.AppConfig.LayoutInspectorWidth);
        }), CancellationToken.None);

    /// <summary>A reset replaces hidden pre-run panes in both visibility and persisted dock state.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public Task ResetLayoutDuringARun_WithHiddenPanes_KeepsBothPanesVisible(bool inspector, bool preview) =>
        HeadlessSessionFixture.Dispatch(() => WithShell(WideRestore, (window, vm) =>
        {
            vm.IsInspectorVisible = inspector;
            vm.IsPreviewVisible = preview;
            vm.IsSheetMusicVisible = true;
            vm.IsAiChatVisible = true;
            vm.IsStemMixerVisible = inspector;
            vm.IsPhraseCatalogVisible = inspector;
            NarrowWidthLayout.Settle(window);
            vm.IsGameplayPresentation = true;
            try
            {
                vm.ConfirmLayoutResetAsync = () => Task.FromResult(true);
                vm.ResetLayoutCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                NarrowWidthLayout.Settle(window);
            }
            finally
            {
                vm.IsGameplayPresentation = false;
            }

            NarrowWidthLayout.Settle(window);
            Assert.True(vm.IsInspectorVisible, "run end hid the reset Inspector");
            Assert.True(vm.IsPreviewVisible, "run end hid the reset Preview");
            Assert.True(vm.IsSheetMusicVisible);
            Assert.True(vm.IsAiChatVisible);
            Assert.Equal(inspector, vm.IsStemMixerVisible);
            Assert.Equal(inspector, vm.IsPhraseCatalogVisible);
            var split = NarrowWidthLayout.Named<Grid>(window, "InspectorSplitGrid");
            var dock = NarrowWidthLayout.Named<Dock.Avalonia.Controls.DockControl>(window, "BottomDock");
            var highway = NarrowWidthLayout.Named<ContentControl>(window, "Preview3DSlot");
            Assert.True(dock.Bounds.Width > 0 && dock.Bounds.Height > 0);
            Assert.True(highway.IsVisible && highway.Bounds.Width > 0 && highway.Bounds.Height > 0);
            Assert.Equal(new GridLength(1, GridUnitType.Star), split.ColumnDefinitions[0].Width);
            Assert.Equal(new GridLength(2, GridUnitType.Star), split.ColumnDefinitions[2].Width);
            Assert.Equal(new GridLength(6), split.ColumnDefinitions[1].Width);
            var persist = typeof(MainWindow).GetMethod("PersistDockLayout", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("MainWindow.PersistDockLayout moved.");
            persist.Invoke(window, null);
            PersistOnClose(window);
            Assert.Equal("Docked", vm.AppConfig.DockLayout!.Panes["Inspector"].Mode);
            Assert.Equal("Docked", vm.AppConfig.DockLayout.Panes["SheetMusic"].Mode);
            Assert.Equal("Docked", vm.AppConfig.DockLayout.Panes["AiChat"].Mode);
            Assert.Equal(split.ColumnDefinitions[0].ActualWidth, vm.AppConfig.LayoutInspectorWidth);
        }), CancellationToken.None);

    /// <summary>Each lifecycle interruption cancels an unfinished drag without saving its transient width.</summary>
    [Theory]
    [InlineData("detach", false)]
    [InlineData("detach", true)]
    [InlineData("hide", false)]
    [InlineData("hide", true)]
    [InlineData("ancestor", false)]
    [InlineData("ancestor", true)]
    [InlineData("reset", false)]
    [InlineData("reset", true)]
    [InlineData("run", false)]
    [InlineData("run", true)]
    [InlineData("deactivate", false)]
    [InlineData("deactivate", true)]
    [InlineData("chat-close", true)]
    public Task AnAbandonedDrag_RefitsWithoutAdoptingItsWidth(string abandon, bool chat) =>
        HeadlessSessionFixture.Dispatch(() => WithShell(WideRestore, (window, vm) =>
        {
            var grid = NarrowWidthLayout.Named<Grid>(window, chat ? "MainSplitGrid" : "InspectorSplitGrid");
            var column = grid.ColumnDefinitions[chat ? 2 : 0];
            var splitter = NarrowWidthLayout.Named<GridSplitter>(window, chat ? "AiChatSplitter" : "InspectorPreviewSplitter");
            splitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragStartedEvent });
            column.Width = new GridLength(720);
            NarrowWidthLayout.Settle(window);
            switch (abandon)
            {
                case "detach":
                    grid.Children.Remove(splitter);
                    grid.Children.Add(splitter);
                    break;
                case "hide":
                    splitter.IsVisible = false;
                    splitter.IsVisible = true;
                    break;
                case "ancestor":
                    grid.IsVisible = false;
                    grid.IsVisible = true;
                    break;
                case "reset":
                    vm.ApplyLayoutReset();
                    break;
                case "run":
                    vm.IsGameplayPresentation = true;
                    vm.IsGameplayPresentation = false;
                    break;
                case "deactivate":
                    var method = typeof(WindowBase).GetMethod("HandleDeactivated", BindingFlags.NonPublic | BindingFlags.Instance)
                        ?? throw new InvalidOperationException("WindowBase.HandleDeactivated moved.");
                    method.Invoke(window, null);
                    break;
                case "chat-close":
                    vm.IsAiChatVisible = false;
                    vm.IsAiChatVisible = true;
                    break;
            }

            window.Width = 1000;
            NarrowWidthLayout.Settle(window);
            Assert.True(column.ActualWidth is > 0 and < 720);
            var layout = typeof(MainWindow).GetField("_layout", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            var width = (SideColumnWidth)typeof(MainWindowLayoutController)
                .GetField(chat ? "_chatWidth" : "_inspectorWidth", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(layout)!;
            Assert.False(width.IsDragging);
            Assert.Equal(abandon == "reset" && chat ? AiChatColumnPolicy.DefaultWidthPx : chat ? 560 : 600, width.PreferredPx);
            // A late completion must not turn cancellation into a committed drag.
            splitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent });
            window.Width = 1920;
            NarrowWidthLayout.Settle(window);
            if (abandon == "reset" && !chat)
                Assert.Equal(new GridLength(1, GridUnitType.Star), column.Width);
            else
                Assert.Equal(abandon == "reset" ? AiChatColumnPolicy.DefaultWidthPx : chat ? 560 : 600, column.Width.Value);
            PersistOnClose(window);
            if (abandon != "reset") Assert.Equal(chat ? 560 : 600, chat ? vm.AppConfig.LayoutAiChatWidth : vm.AppConfig.LayoutInspectorWidth);
        }), CancellationToken.None);

    private static void WideRestore(AppConfig cfg)
    {
        cfg.WindowWidth = 1920;
        cfg.WindowHeight = 1080;
        cfg.LayoutAiChatVisible = true;
        cfg.LayoutAiChatWidth = 560;
        cfg.LayoutInspectorWidth = 600;
    }

    /// <summary>
    ///     A press and release on a splitter without a move still raises DragStarted and
    ///     DragCompleted; in a narrow window that adopted the narrowed widths as the user's.
    /// </summary>
    [Fact]
    public Task ASplitterClickWithoutAMove_KeepsThePreferredWidth() =>
        HeadlessSessionFixture.Dispatch(() => WithShell(WideRestore, (window, _) =>
        {
            var split = NarrowWidthLayout.Named<Grid>(window, "MainSplitGrid");
            var insp = NarrowWidthLayout.Named<Grid>(window, "InspectorSplitGrid");
            window.Width = 1000;
            NarrowWidthLayout.Settle(window);
            Assert.True(insp.ColumnDefinitions[0].Width.Value < 600, "the window did not narrow the Inspector");

            Drag(window, "AiChatSplitter", () => { });
            Drag(window, "InspectorPreviewSplitter", () => { });
            window.Width = 1920;
            NarrowWidthLayout.Settle(window);

            Assert.Equal(560, split.ColumnDefinitions[2].Width.Value);
            Assert.Equal(600, insp.ColumnDefinitions[0].Width.Value);
        }), CancellationToken.None);

    /// <summary>
    ///     A drag in a narrow window that moves away and back to where it started is no choice:
    ///     mid-drag layout passes adopted the transient width as the preferred one.
    /// </summary>
    [Fact]
    public Task ADragAwayAndBack_KeepsThePreferredWidth() =>
        HeadlessSessionFixture.Dispatch(() => WithShell(WideRestore, (window, _) =>
        {
            var split = NarrowWidthLayout.Named<Grid>(window, "MainSplitGrid");
            var insp = NarrowWidthLayout.Named<Grid>(window, "InspectorSplitGrid");
            window.Width = 1000;
            NarrowWidthLayout.Settle(window);
            var chatPx = split.ColumnDefinitions[2].Width.Value;
            var inspectorPx = insp.ColumnDefinitions[0].Width.Value;
            Assert.True(chatPx < 560 && inspectorPx < 600, $"the window did not narrow the columns ({chatPx:0}, {inspectorPx:0})");

            Drag(window, "AiChatSplitter",
                () => split.ColumnDefinitions[2].Width = new GridLength(chatPx - 20),
                () => split.ColumnDefinitions[2].Width = new GridLength(chatPx));
            Drag(window, "InspectorPreviewSplitter",
                () => insp.ColumnDefinitions[0].Width = new GridLength(inspectorPx - 20, GridUnitType.Pixel),
                () => insp.ColumnDefinitions[0].Width = new GridLength(inspectorPx, GridUnitType.Pixel));
            window.Width = 1920;
            NarrowWidthLayout.Settle(window);

            Assert.Equal(560, split.ColumnDefinitions[2].Width.Value);
            Assert.Equal(600, insp.ColumnDefinitions[0].Width.Value);
        }), CancellationToken.None);

    /// <summary>
    ///     The end of a run puts back the Inspector width the run started with; a window that grew
    ///     during the run left it narrowed, one that shrank left it overflowing, until the grid
    ///     happened to resize again. The end re-fits it itself.
    /// </summary>
    [Theory]
    [InlineData(1000, 1920)]
    [InlineData(1920, 1000)]
    public Task ARunsEnd_RefitsTheInspectorToTheWindow(double startWidth, double endWidth) =>
        HeadlessSessionFixture.Dispatch(() => WithShell(WideRestore, (window, vm) =>
        {
            var insp = NarrowWidthLayout.Named<Grid>(window, "InspectorSplitGrid");
            window.Width = startWidth;
            NarrowWidthLayout.Settle(window);
            try
            {
                vm.IsGameplayPresentation = true;
                NarrowWidthLayout.Settle(window);
                window.Width = endWidth;
                NarrowWidthLayout.Settle(window);
            }
            finally
            {
                vm.IsGameplayPresentation = false;
            }

            // The end itself re-fits, before any layout pass: before this the only re-fit was the
            // grid's next resize.
            Assert.Equal(Fitted(), insp.ColumnDefinitions[0].Width.Value, 0.5);
            NarrowWidthLayout.Settle(window);
            Assert.Equal(Fitted(), insp.ColumnDefinitions[0].Width.Value, 0.5);

            window.Width = 1920;
            NarrowWidthLayout.Settle(window);
            Assert.Equal(600, insp.ColumnDefinitions[0].Width.Value);

            // The preferred 600 px as far as the grid, as it is laid out now, lets it show.
            double Fitted() => Math.Min(600, Math.Max(MainWindowLayoutController.MinInspectorWidthPx,
                insp.Bounds.Width - 6 - MainWindowLayoutController.MinPreviewColumnPx));
        }), CancellationToken.None);

    /// <summary>
    ///     A run collapses the Inspector column to 0 px so the highway owns the width; a window
    ///     resize during the run clamped it back to the preferred width, over the highway. After
    ///     the run the preferred width returns.
    /// </summary>
    [Fact]
    public Task ARunCollapsedInspector_StaysCollapsedThroughAResize() =>
        HeadlessSessionFixture.Dispatch(() => WithShell(WideRestore, (window, vm) =>
        {
            var insp = NarrowWidthLayout.Named<Grid>(window, "InspectorSplitGrid");
            Assert.Equal(600, insp.ColumnDefinitions[0].Width.Value);
            try
            {
                vm.IsGameplayPresentation = true;
                NarrowWidthLayout.Settle(window);
                Assert.Equal(0, insp.ColumnDefinitions[0].Width.Value);

                window.Width = 1000;
                NarrowWidthLayout.Settle(window);
                Assert.Equal(0, insp.ColumnDefinitions[0].Width.Value);
                window.Width = 1920;
                NarrowWidthLayout.Settle(window);
                Assert.Equal(0, insp.ColumnDefinitions[0].Width.Value);
            }
            finally
            {
                vm.IsGameplayPresentation = false;
            }

            NarrowWidthLayout.Settle(window);
            Assert.Equal(600, insp.ColumnDefinitions[0].Width.Value);
        }), CancellationToken.None);

    /// <summary>
    ///     Reset Layout, then a temporary shrink: the reset widths (the default chat width, the
    ///     Inspector's star share) come back when the window grows, not the restored ones.
    /// </summary>
    [Fact]
    public Task ResetLayout_ThenANarrowWindow_GivesTheResetWidthsBack() =>
        HeadlessSessionFixture.Dispatch(() => WithShell(WideRestore, (window, vm) =>
        {
            var split = NarrowWidthLayout.Named<Grid>(window, "MainSplitGrid");
            var insp = NarrowWidthLayout.Named<Grid>(window, "InspectorSplitGrid");
            vm.ApplyLayoutReset();
            NarrowWidthLayout.Settle(window);
            Assert.Equal(AiChatColumnPolicy.DefaultWidthPx, split.ColumnDefinitions[2].Width.Value);

            window.Width = 1000;
            NarrowWidthLayout.Settle(window);
            window.Width = 1920;
            NarrowWidthLayout.Settle(window);

            Assert.Equal(AiChatColumnPolicy.DefaultWidthPx, split.ColumnDefinitions[2].Width.Value);
            Assert.Equal(new GridLength(1, GridUnitType.Star), insp.ColumnDefinitions[0].Width);
            Assert.Equal(new GridLength(2, GridUnitType.Star), insp.ColumnDefinitions[2].Width);
        }), CancellationToken.None);

    /// <summary>
    ///     A splitter drag as the GridSplitter raises it: started, each width it writes followed
    ///     by a real layout pass (every delta resizes the grids, which runs the clamp), completed.
    /// </summary>
    private static void Drag(MainWindow window, string splitterName, params Action[] moves)
    {
        var splitter = NarrowWidthLayout.Named<GridSplitter>(window, splitterName);
        splitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragStartedEvent, Vector = default });
        foreach (var move in moves)
        {
            move();
            NarrowWidthLayout.Settle(window);
        }

        splitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent, Vector = default });
    }

    /// <summary>The window's own close-path persist (the shell itself is never closed here).</summary>
    private static void PersistOnClose(MainWindow window)
    {
        var field = typeof(MainWindow).GetField("_layout", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("MainWindow._layout has moved: repoint this test.");
        ((MainWindowLayoutController)field.GetValue(window)!).PersistOnClose();
    }

    /// <summary>
    ///     Row 5: a maximized start still takes the saved NORMAL size, so restoring down lands
    ///     on it instead of the window's declared default.
    /// </summary>
    [Fact]
    public Task AMaximizedStart_KeepsTheSavedNormalSizeToRestoreDownTo() =>
        HeadlessSessionFixture.Dispatch(() => WithShell(cfg =>
        {
            cfg.WindowMaximized = true;
            cfg.WindowWidth = 1111;
            cfg.WindowHeight = 777;
        }, (window, _) =>
        {
            Assert.Equal(WindowState.Maximized, window.WindowState);
            Assert.Equal(1111, window.Width);
            Assert.Equal(777, window.Height);
        }), CancellationToken.None);

    private static void WithShell(Action<AppConfig> configure, Action<MainWindow, MainWindowViewModel> body)
    {
        var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
        var vm = services.GetRequiredService<MainWindowViewModel>();
        var state = services.GetRequiredService<ProjectState>();
        services.GetRequiredService<ISelectionService>().ClearSelections();
        foreach (var dialog in vm.Dialogs.AllDialogs)
        {
            if (dialog.IsOpen) dialog.RequestClose();
        }

        var cfg = vm.AppConfig;
        var saved = (cfg.WindowMaximized, cfg.WindowWidth, cfg.WindowHeight, cfg.LayoutAiChatVisible, cfg.LayoutAiChatWidth,
            cfg.LayoutInspectorWidth);
        vm.IsGameplayPresentation = false;
        vm.SelectedMainTabIndex = 0;
        vm.IsInspectorVisible = true;
        vm.IsPreviewVisible = true;
        vm.IsSheetMusicVisible = false;
        vm.IsAiChatVisible = false;
        var previous = (state.ActiveProject, state.ActiveArrangement);
        NarrowWidthLayout.SeedSong(state);
        configure(cfg);

        // No window.Close(): closing the SHELL runs the app's shutdown path (see EditorShortWindowUiPerfSmokeTests).
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            NarrowWidthLayout.Settle(window);
            body(window, vm);
        }
        finally
        {
            (cfg.WindowMaximized, cfg.WindowWidth, cfg.WindowHeight, cfg.LayoutAiChatVisible, cfg.LayoutAiChatWidth,
                cfg.LayoutInspectorWidth) = saved;
            vm.IsAiChatVisible = false;
            state.ActiveProject = previous.ActiveProject;
            state.ActiveArrangement = previous.ActiveArrangement;
        }
    }
}
