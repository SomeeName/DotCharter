using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The gameplay presentation (a Play Mode run drawn in the editor shell's 3D preview row) in
///     the REAL shell: with nothing above the highway there is no row splitter and the run fills
///     the editor's whole height; the HUD's notation toggle is the one view that can sit above it,
///     and only while it is on does a splitter show, directly between the notation band and the run.
///     The bug this pins: entering the run collapsed the timeline row and its splitter, then the
///     posted bottom-dock row sizing put the 6 px splitter row back, and an empty band sat above it.
///     With <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> each state is captured.
/// </summary>
[Collection("HeadlessUI")]
public sealed class GameplaySplitterUiPerfSmokeTests
{
    private const double Tolerance = 1;

    [Fact]
    public Task TheRun_WithNothingAbove_HasNoSplitter_AndFillsTheHeight() =>
        HeadlessSessionFixture.Dispatch(() => WithShell((window, vm) =>
        {
            vm.IsGameplayPresentation = true;
            NarrowWidthLayout.Settle(window);
            NarrowWidthLayout.Capture(window, "gameplay-nothing-above");

            AssertRunFillsTheHeight(window, "entering the run");
        }), CancellationToken.None);

    [Fact]
    public Task TheNotationToggle_BringsTheBandAndItsSplitter_AndTakesThemAway() =>
        HeadlessSessionFixture.Dispatch(() => WithShell((window, vm) =>
        {
            vm.IsGameplayPresentation = true;
            NarrowWidthLayout.Settle(window);

            vm.IsSheetMusicVisible = true;
            NarrowWidthLayout.Settle(window);
            NarrowWidthLayout.Capture(window, "gameplay-notation-on");

            var split = NarrowWidthLayout.Named<Grid>(window, "MainSplitGrid");
            var band = NarrowWidthLayout.Named<Control>(window, "SheetMusicDock");
            var run = NarrowWidthLayout.Named<Grid>(window, "InspectorSplitGrid");
            Assert.True(band.IsEffectivelyVisible && band.Bounds.Height > 40, $"the notation band laid out as {band.Bounds}");
            var splitters = VisibleRowSplitters(split);
            var splitter = Assert.Single(splitters);
            var bandBottom = Top(band, split) + band.Bounds.Height;
            var splitterTop = Top(splitter, split);
            var runTop = Top(run, split);
            NarrowWidthLayout.Report($"notation on: band 0..{bandBottom:0}, splitter {splitterTop:0}, run {runTop:0}..{runTop + run.Bounds.Height:0} of {split.Bounds.Height:0}");
            Assert.True(Math.Abs(splitterTop - bandBottom) < Tolerance, $"the splitter sits at {splitterTop:0}, the band ends at {bandBottom:0}");
            Assert.True(Math.Abs(runTop - (splitterTop + splitter.Bounds.Height)) < Tolerance,
                $"a {runTop - splitterTop - splitter.Bounds.Height:0} px gap between the splitter and the run");
            Assert.True(Math.Abs(runTop + run.Bounds.Height - split.Bounds.Height) < Tolerance, "the run does not reach the bottom");

            // The seam trades height between the band and the run: the band fills what it is given.
            var bandBefore = band.Bounds.Height;
            var from = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)!.Value;
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(from.WithY(from.Y + 40));
            window.MouseMove(from.WithY(from.Y + 80));
            window.MouseUp(from.WithY(from.Y + 80), MouseButton.Left);
            NarrowWidthLayout.Settle(window);
            var grown = band.Bounds.Height - bandBefore;
            NarrowWidthLayout.Report($"notation band {bandBefore:0} -> {band.Bounds.Height:0} px after an 80 px drag");
            Assert.True(Math.Abs(grown - 80) < 8, $"an 80 px drag grew the band by {grown:0} px");
            Assert.True(Math.Abs(Top(run, split) + run.Bounds.Height - split.Bounds.Height) < Tolerance, "the run left the bottom after the drag");
            Assert.True(Math.Abs(Top(run, split) - (Top(band, split) + band.Bounds.Height + splitter.Bounds.Height)) < Tolerance,
                "a gap opened between the band and the run after the drag");

            vm.IsSheetMusicVisible = false;
            NarrowWidthLayout.Settle(window);
            AssertRunFillsTheHeight(window, "turning the notation off");
        }), CancellationToken.None);

    private static void AssertRunFillsTheHeight(Window window, string at)
    {
        var split = NarrowWidthLayout.Named<Grid>(window, "MainSplitGrid");
        var run = NarrowWidthLayout.Named<Grid>(window, "InspectorSplitGrid");
        var runTop = Top(run, split);
        NarrowWidthLayout.Report($"{at}: run {runTop:0}..{runTop + run.Bounds.Height:0} of {split.Bounds.Height:0}, " +
                                 $"rows {string.Join(" / ", split.RowDefinitions.Select(r => $"{r.Height}={r.ActualHeight:0}"))}");
        var splitters = VisibleRowSplitters(split);
        Assert.True(splitters.Count == 0, $"{at}: nothing is above the run, yet {string.Join(", ", splitters.Select(s => s.Name ?? "a splitter"))} shows");
        Assert.True(runTop < Tolerance, $"{at}: the run starts {runTop:0} px down, under an empty band");
        Assert.True(Math.Abs(run.Bounds.Height - split.Bounds.Height) < Tolerance,
            $"{at}: the run is {run.Bounds.Height:0} of {split.Bounds.Height:0} px");
    }

    /// <summary>Every row splitter in the editor that is on screen with a height to grab.</summary>
    private static List<GridSplitter> VisibleRowSplitters(Grid split) =>
        split.GetVisualDescendants().OfType<GridSplitter>()
            .Where(s => s.ResizeDirection == GridResizeDirection.Rows && s.IsEffectivelyVisible && RowHeight(s) > 0)
            .ToList();

    /// <summary>The height of the grid row the splitter sits in — a visible splitter in a 0 px row is not there.</summary>
    private static double RowHeight(GridSplitter splitter) =>
        splitter.Parent is Grid grid && Grid.GetRow(splitter) < grid.RowDefinitions.Count
            ? grid.RowDefinitions[Grid.GetRow(splitter)].ActualHeight
            : splitter.Bounds.Height;

    private static double Top(Visual control, Visual space) => control.TranslatePoint(new Point(0, 0), space)!.Value.Y;

    private static void WithShell(Action<MainWindow, MainWindowViewModel> body)
    {
        var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
        var vm = services.GetRequiredService<MainWindowViewModel>();
        var state = services.GetRequiredService<ProjectState>();
        var selection = services.GetRequiredService<ISelectionService>();
        foreach (var dialog in vm.Dialogs.AllDialogs)
        {
            if (dialog.IsOpen) dialog.RequestClose();
        }

        vm.CommandPalette.Close();
        vm.IsGameplayPresentation = false;
        vm.SelectedMainTabIndex = 0;
        vm.IsInspectorVisible = true;
        vm.IsPreviewVisible = true;
        vm.IsSheetMusicVisible = false;
        vm.IsAiChatVisible = false;
        var previous = (state.ActiveProject, state.ActiveArrangement);
        NarrowWidthLayout.SeedSong(state);
        selection.ClearSelections();

        // No window.Close(): closing the SHELL runs the app's shutdown path (see EditorShortWindowUiPerfSmokeTests).
        var window = new MainWindow { DataContext = vm, Width = 1920, Height = 1080 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            NarrowWidthLayout.Settle(window);
            body(window, vm);
        }
        finally
        {
            vm.IsGameplayPresentation = false;
            vm.IsSheetMusicVisible = false;
            state.ActiveProject = previous.ActiveProject;
            state.ActiveArrangement = previous.ActiveArrangement;
        }
    }
}
