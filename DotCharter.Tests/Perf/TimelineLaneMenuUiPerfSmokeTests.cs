using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.UI;
using DotCharter.UI.Controls;
using DotCharter.UI.Editing.Tracks;
using DotCharter.UI.Services;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views;
using DotCharter.UI.Views.Editor;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #81 in the REAL shell over the UI sweep's seeded song: the timeline lane menu (a
///     right-click on the lane headers) is prewarmed off the first-open path, opens, binds the
///     main timeline's ribbon and measures every row; one tone auto-hides TONES with its reason
///     printed; compact rows shrink the ribbon with the gutter's captions still fitting their
///     rows; and the editor's timeline pane grows with the window instead of sitting on its
///     360 px floor.
/// </summary>
[Collection("HeadlessUI")]
public sealed class TimelineLaneMenuUiPerfSmokeTests
{
    [Fact]
    public async Task TheLaneMenu_IsPrewarmed_Opens_AndMeasuresEveryRow() =>
        await HeadlessSessionFixture.Dispatch(() => WithShell((window, state) =>
        {
            window.Width = 1920;
            window.Height = 1080;
            NarrowWidthLayout.Settle(window);

            var host = NarrowWidthLayout.Named<Border>(window, "TimelineGutterHost");
            var renderer = NarrowWidthLayout.Named<ChartRenderer>(window, "MainChartRenderer");
            var target = Assert.Single(MenuFlyoutPrewarmer.FlyoutsUnder(host));
            var menu = Assert.IsType<TimelineLaneMenuView>(target.Flyout.Content);

            // The prewarm realizes it WITHOUT opening it.
            Assert.Equal(1, MenuFlyoutPrewarmer.Realize(target, window.Bounds.Size));
            Assert.False(target.Flyout.IsOpen);
            Assert.True(menu.DesiredSize is { Width: > 0, Height: > 0 }, "the prewarm left the lane menu unmeasured");

            // One tone: TONES auto-hides, and the menu says why.
            var arrangement = state.ActiveArrangement!;
            arrangement.ToneBase = "Clean";
            arrangement.ToneChanges.Clear();
            arrangement.ToneChanges.Add(new ToneChange { ToneName = "Clean" });
            var ribbon = renderer.CurrentRibbon!;
            ribbon.ShowAllLanes();
            ribbon.AutoHideEmpty = true;
            ribbon.CompactRows = false;
            Assert.False(renderer.TopRibbon.ShowTones, "one tone did not auto-hide the TONES lane");

            target.Flyout.ShowAt(host);
            NarrowWidthLayout.Settle(window);
            try
            {
                Assert.True(target.Flyout.IsOpen);
                Assert.Same(ribbon, menu.DataContext);
                Assert.True(menu.Bounds is { Width: > 200, Height: > 0 }, $"the lane menu measured {menu.Bounds}");

                var rows = menu.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("LaneRow")).ToList();
                Assert.Equal(9, rows.Count);
                Assert.All(rows, r => Assert.True(r.Bounds.Height >= 24, $"lane row {r.Name} is {r.Bounds.Height:0.#} px tall"));

                var tones = NarrowWidthLayout.Named<ToggleButton>(menu, "LaneTones");
                Assert.True(tones.IsChecked, "the TONES toggle is on — only auto-hidden");
                Assert.Equal(LocalizationService.Instance["TimelineLanes_HiddenOneTone"], tones.Tag);
                Assert.Contains("AutoHidden", tones.Classes);

                // Compact rows from the menu: the ribbon gets shorter, the note lanes taller.
                var before = renderer.TopRibbon.TotalHeight;
                NarrowWidthLayout.Named<ToggleButton>(menu, "LaneCompact").IsChecked = true;
                NarrowWidthLayout.Settle(window);
                Assert.True(ribbon.CompactRows);
                Assert.True(renderer.TopRibbon.TotalHeight < before,
                    $"compact rows left the ribbon at {renderer.TopRibbon.TotalHeight} px (was {before})");

                // "Show all lanes" turns a switched-off lane back on.
                ribbon.ShowLyrics = false;
                NarrowWidthLayout.Named<Button>(menu, "LaneShowAll").RaiseEvent(
                    new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.True(ribbon.ShowLyrics);
            }
            finally
            {
                target.Flyout.Hide();
                ribbon.CompactRows = false;
                NarrowWidthLayout.Settle(window);
            }

            Assert.Null(menu.DataContext);
        }), CancellationToken.None);

    /// <summary>
    ///     Compact rows keep the text readable: every caption the gutter prints for a ribbon lane
    ///     fits its compact row (and the MEASURES caption its band), measured with the gutter's
    ///     own font, and the ribbon is shorter than at normal density.
    /// </summary>
    [Fact]
    public async Task CompactRows_MeasureSmaller_WithTheirCaptionsStillFitting() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var normal = new RibbonLayout(RibbonLanes.All);
            var compact = new RibbonLayout(RibbonLanes.All, RibbonDensity.Compact);
            Assert.True(compact.TotalHeight < normal.TotalHeight);

            var mono = new Typeface(new FontFamily("avares://DotCharter.Tone.UI/Assets/Fonts#JetBrains Mono"));
            foreach (var key in new[] { "Ribbon_Tones", "Ribbon_Sections", "Ribbon_Phrases", "Ribbon_Lyrics", "Timeline_Gutter_Measures" })
            {
                var text = new FormattedText(LocalizationService.Instance[key].ToUpperInvariant(), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, mono, TimelineGutterControl.CaptionSize, Brushes.White);
                var room = key == "Timeline_Gutter_Measures" ? compact.Band : compact.RowPitch;
                NarrowWidthLayout.Report($"[lanes-81] caption {key}: {text.Height:0.#} px in a {room} px compact row");
                Assert.True(text.Height > 0 && text.Height <= room,
                    $"the {key} caption is {text.Height:0.#} px tall — it does not fit a {room} px compact row");
            }
        }, CancellationToken.None);

    /// <summary>
    ///     The 360 px pin (#81). The editor split is ~968 px at 1920x1080, and the Auto UI scale
    ///     holds it there on every taller window — so with the old 2 : 3 default the timeline
    ///     area's share (385 px) sat under its 412 px floor at 1080p AND 1440p, and the dock took
    ///     every extra pixel. With the 3 : 4 split the area is a real share: every pixel the split
    ///     gains is divided 3 : 4, so the timeline grows with the window.
    /// </summary>
    [Fact]
    public async Task TheTimelinePane_GrowsWithTheWindow() =>
        await HeadlessSessionFixture.Dispatch(() => WithShell((window, _) =>
        {
            var wasAuto = UiScaleController.IsAuto;
            var scale = UiScaleController.Scale;
            UiScaleController.SetManual(1.0);
            try
            {
                var main = NarrowWidthLayout.Named<Grid>(window, "MainSplitGrid");
                var renderer = NarrowWidthLayout.Named<Control>(window, "MainChartRenderer");
                (double Split, double Top, double Timeline) At(int height)
                {
                    window.Width = 1920;
                    window.Height = height;
                    NarrowWidthLayout.Settle(window);
                    NarrowWidthLayout.Report($"[lanes-81] 1920x{height}: split {main.Bounds.Height:0.#}, timeline area " +
                                             $"{main.RowDefinitions[0].ActualHeight:0.#} (min {main.RowDefinitions[0].MinHeight}), " +
                                             $"dock {main.RowDefinitions[2].ActualHeight:0.#}, timeline {renderer.Bounds.Height:0.#}");
                    return (main.Bounds.Height, main.RowDefinitions[0].ActualHeight, renderer.Bounds.Height);
                }

                var small = At(1080);
                var tall = At(1440);
                var share = (tall.Top - small.Top) / (tall.Split - small.Split);
                Assert.True(tall.Timeline > small.Timeline + 100,
                    $"the timeline is {small.Timeline:0.#} px at 1080 and {tall.Timeline:0.#} px at 1440 — it does not grow with the window");
                Assert.True(share > 0.41,
                    $"the timeline area took {share:P0} of the {tall.Split - small.Split:0} px the split gained — " +
                    "it sat on its floor at 1080 and the dock ate the growth (the 3 : 4 split gives it 43 %)");
                Assert.True(small.Timeline >= 359.5, $"at 1920x1080 the timeline shrank to {small.Timeline:0.#} px");
            }
            finally
            {
                if (wasAuto) UiScaleController.SetAutoMode(true);
                else UiScaleController.SetManual(scale);
            }
        }), CancellationToken.None);

    private static void WithShell(Action<MainWindow, ProjectState> body)
    {
        var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
        var vm = services.GetRequiredService<MainWindowViewModel>();
        var state = services.GetRequiredService<ProjectState>();
        foreach (var dialog in vm.Dialogs.AllDialogs)
        {
            if (dialog.IsOpen)
                dialog.RequestClose();
        }

        vm.CommandPalette.Close();
        vm.IsGameplayPresentation = false;
        vm.SelectedMainTabIndex = 0;
        vm.IsInspectorVisible = true;
        vm.IsPreviewVisible = true;
        vm.IsSheetMusicVisible = false;
        var previousProject = state.ActiveProject;
        var previousArrangement = state.ActiveArrangement;
        NarrowWidthLayout.SeedSong(state);

        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            body(window, state);
        }
        finally
        {
            // No window.Close(): closing the SHELL ends the testhost (EditorShortWindowUiPerfSmokeTests).
            state.ActiveProject = previousProject;
            state.ActiveArrangement = previousArrangement;
        }
    }
}
