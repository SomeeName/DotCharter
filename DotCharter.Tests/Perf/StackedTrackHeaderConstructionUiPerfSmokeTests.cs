using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.MultiTrack;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.Views.Shell;
using DotCharter.UI.Controls;
using DotCharter.UI.Views.Editor;

namespace DotCharter.Tests.Perf;

/// <summary>The stacked header's row chips fit inside its fixed gutter-width card.</summary>
[Collection("HeadlessUI")]
public sealed class StackedTrackHeaderConstructionUiPerfSmokeTests
{
    [Fact]
    public async Task StackedRendererExcludesLyrics_WhileTheActiveLaneMenuStillTogglesThem() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var h = new ChartEditHarness();
            var other = new Arrangement { Name = "Rhythm" };
            other.Levels.Add(new ChartLevel());
            h.Project.Arrangements.Add(other);
            var main = new ChartRenderer { Editor = h.Editor };
            var stacked = new ChartRenderer { Editor = h.Editor, OverrideArrangement = other, DrawLabels = false };
            // The fixture has no lyrics, so the row only shows while the toggle was switched ON (forced).
            var ribbon = h.Editor.TrackContexts.GetOrCreate(h.State.ActiveArrangement!).Ribbon;
            ribbon.ShowLyrics = false;
            ribbon.ShowLyrics = true;
            h.Editor.TrackContexts.GetOrCreate(other).Ribbon.ShowLyrics = true;
            var menu = new TimelineLaneMenuView { Renderer = main };
            var window = new Window { Width = 640, Height = 420, Content = menu };
            window.Show();
            try
            {
                menu.Measure(new Size(640, 420));
                menu.Arrange(new Rect(0, 0, 640, 420));
                Assert.True(menu.Bounds is { Width: > 1, Height: > 1 });
                Assert.True(main.TopRibbon.ShowLyrics);
                Assert.False(stacked.TopRibbon.ShowLyrics);
                Assert.NotNull(main.MeasureTimelineLabelWidth("7"));
                Assert.Null(stacked.MeasureTimelineLabelWidth("7"));
                var lyrics = menu.FindControl<ToggleButton>("LaneLyrics")!;
                Assert.True(lyrics.IsChecked);
                lyrics.IsChecked = false;
                Assert.False(main.TopRibbon.ShowLyrics);
                Assert.False(stacked.TopRibbon.ShowLyrics);
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, CancellationToken.None);

    [Fact]
    public async Task EveryRowChipMeasuresAndFitsInsideTheNarrowHeader() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var state = new ProjectState();
            var arrangement = new Arrangement { Name = "Rhythm" };
            arrangement.Levels.Add(new ChartLevel());
            var context = new TrackEditorContext(arrangement, new SelectionService());
            var entry = new MultiTrackEntry(arrangement, context, false, new GhostArrangementsService(),
                new StackedArrangementsService(), RecordingProxy.Create<IGlobalEditingCoordinator>().Proxy,
                state, new FakeAppLogger());
            var view = new StackedTracksView();
            view.FindControl<ItemsControl>("StackedItemsHost")!.ItemsSource = new[] { entry };
            var window = new Window { Width = 640, Height = 420, Content = view };
            window.Show();
            try
            {
                view.Measure(new Size(640, 420));
                view.Arrange(new Rect(0, 0, 640, 420));
                window.UpdateLayout();
                Assert.True(view.Bounds is { Width: > 1, Height: > 1 });
                var header = Assert.Single(view.GetVisualDescendants().OfType<Border>(),
                    b => b.Classes.Contains("TrackRowCard"));
                Assert.True(header.Bounds is { Width: > 1, Height: > 1 });
                Assert.Equal(118, header.Bounds.Width);
                var chips = header.GetVisualDescendants().OfType<ToggleButton>()
                    .Where(b => b.Classes.Contains("RibbonChip")).ToArray();
                // StackedTracksView.axaml exposes the track-specific T/S/P rows in this order.
                Assert.Equal(new[] { "T", "S", "P" }, chips.Select(b => b.Content?.ToString()));
                foreach (var chip in chips)
                {
                    Assert.True(chip.Bounds is { Width: > 1, Height: > 1 });
                    var origin = chip.TranslatePoint(default, header);
                    Assert.True(origin.HasValue);
                    var bounds = new Rect(origin.Value, chip.Bounds.Size);
                    Assert.True(bounds.Left >= 0 && bounds.Top >= 0
                        && bounds.Right <= header.Bounds.Width && bounds.Bottom <= header.Bounds.Height,
                        $"Row chip {chip.Content} at {bounds} is outside the header {header.Bounds.Size}.");
                }
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, CancellationToken.None);
}
