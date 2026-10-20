using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.VisualTree;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.Views.Editor;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The live analysis monitor (#314), built for real on the app's own <see cref="LivePlayViewModel" />
///     with realistic data — six strings at mixed confidence (one with nothing judged), and a log
///     with hits, a sure miss and an UNSURE miss — attached to a window at the monitor row's height,
///     laid out and measured. <c>Preview3DView</c>, its host, is never built headless (it owns the
///     Vulkan viewport), so this is the monitor's only layout coverage.
///     <para>
///         It asserts widths, so it is in the release's de-DE / ja-JP re-run
///         (<c>$HeadlessLayoutFilter</c>). <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> writes a PNG per width
///         (and per culture with <c>DOTCHARTER_HEADLESS_CULTURE</c>) through
///         <see cref="NarrowWidthLayout.Capture" />.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class LiveDetectionMonitorConstructionUiPerfSmokeTests
{
    /// <summary>The monitor row's MaxHeight in <c>Preview3DView.axaml</c>.</summary>
    private const double MonitorHeight = 170;

    public static TheoryData<double> Widths => [960, 520];

    [Theory]
    [MemberData(nameof(Widths))]
    public Task TheMonitor_WithMixedConfidence_LaysOutEveryStringAndTheLowConfidenceHint(double width) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var livePlay = services.GetRequiredService<LivePlayViewModel>();
            Seed(livePlay);

            var view = new LiveDetectionMonitorView { DataContext = livePlay };
            var window = NarrowWidthLayout.NewWindow(width, MonitorHeight, view);
            try
            {
                window.Show();
                NarrowWidthLayout.Settle(window);
                NarrowWidthLayout.Capture(window, $"analysis-monitor-{width:0}");

                Assert.True(view.Bounds.Width > 0 && view.Bounds.Height > 0,
                    $"the monitor measured {view.Bounds.Width} x {view.Bounds.Height}");

                var strip = NarrowWidthLayout.Named<Grid>(view, "StringConfidenceStrip");
                Assert.True(strip.Bounds.Height > 0, "the confidence strip has no height");

                var cells = view.GetVisualDescendants().OfType<StackPanel>()
                    .Where(c => c.Name == "StringConfidenceCell").ToList();
                Assert.Equal(6, cells.Count);
                var (stripLeft, stripRight) = NarrowWidthLayout.Span(strip, view);
                foreach (var cell in cells)
                {
                    Assert.True(cell.Bounds.Width > 0 && cell.Bounds.Height > 0, "a string's cell has no size");
                    var (left, right) = NarrowWidthLayout.Span(cell, view);
                    Assert.True(left >= stripLeft - 0.5 && right <= stripRight + 0.5,
                        $"a string's cell ({left:0}..{right:0}) paints outside the strip ({stripLeft:0}..{stripRight:0})");
                }

                // The two unsure strings (A, e) wear the amber fill, at their certainty's width; the
                // sure ones the green; the string with nothing judged neither.
                AssertFill(cells[0], "ConfidenceFillSure", 0.91);
                AssertFill(cells[1], "ConfidenceFillUnsure", 0.38);
                AssertFill(cells[2], "ConfidenceFillSure", 0.74);
                AssertNoFill(cells[3]);
                AssertFill(cells[4], "ConfidenceFillSure", 0.55);
                AssertFill(cells[5], "ConfidenceFillUnsure", 0.29);
                Assert.True(cells[3].Opacity < 1, "a string with nothing judged does not recede");
                Assert.False(string.IsNullOrEmpty(ToolTip.GetTip(cells[1]) as string), "a cell carries no tooltip");

                // Exactly one log line is an unsure miss, and its hint is on screen, whole.
                var badges = view.GetVisualDescendants().OfType<TextBlock>()
                    .Where(t => t.Name == "LowConfidenceBadge" && t.IsEffectivelyVisible).ToList();
                var badge = Assert.Single(badges);
                Assert.True(badge.Bounds.Width > 0 && badge.Bounds.Height > 0, "the low-confidence hint has no size");
                var (_, badgeRight) = NarrowWidthLayout.Span(badge, view);
                NarrowWidthLayout.Report($"analysis monitor at {width:0}: strip {strip.Bounds.Width:0} x " +
                                         $"{strip.Bounds.Height:0}, hint ends at {badgeRight:0} of {view.Bounds.Width:0}");
                Assert.True(badgeRight <= view.Bounds.Width + 0.5,
                    $"the low-confidence hint runs off the monitor ({badgeRight:0} > {view.Bounds.Width:0})");
            }
            finally
            {
                window.Close();
                livePlay.DetectionLog.Clear();
            }
        }, CancellationToken.None);

    private static void Seed(LivePlayViewModel livePlay)
    {
        livePlay.StringConfidence.Apply(
        [
            new LiveStringConfidenceReading(0, 6, 0.91f),
            new LiveStringConfidenceReading(1, 4, 0.38f),
            new LiveStringConfidenceReading(2, 5, 0.74f),
            new LiveStringConfidenceReading(3, 0, 0f),
            new LiveStringConfidenceReading(4, 2, 0.55f),
            new LiveStringConfidenceReading(5, 3, 0.29f)
        ], LiveStringConfidenceViewModel.LabelsFor(null));

        livePlay.DetectionLog.Clear();
        // Newest first, as the monitor inserts them.
        livePlay.DetectionLog.Add(new LiveDetectionEntry("[00:13.050] MISS  s1 f5  evid 0.04", false, true));
        livePlay.DetectionLog.Add(new LiveDetectionEntry("[00:12.800] MISS  s5 f12  evid 0.71", false, true, IsUnsure: true));
        livePlay.DetectionLog.Add(new LiveDetectionEntry("[00:12.500] HIT   s1 f7  conf 0.42", true, false));
        livePlay.DetectionLog.Add(new LiveDetectionEntry(
            "[00:12.302] note  E3+B3           164.8 Hz  cent   612  rms 0.0123  decay 0.84  mute", false, false));
        livePlay.DetectionLog.Add(new LiveDetectionEntry("[00:12.300] HIT   s0 f3  conf 0.91", true, false));
    }

    private static void AssertFill(StackPanel cell, string visibleFill, double certainty)
    {
        var fill = cell.GetVisualDescendants().OfType<Rectangle>().Single(r => r.Name == visibleFill);
        Assert.True(fill.IsEffectivelyVisible, $"{visibleFill} is hidden on a cell that should wear it");
        // Layout rounding snaps the fill to whole pixels: within one of its certainty's width.
        var expected = certainty * LiveStringConfidenceItem.MeterWidth;
        Assert.InRange(fill.Bounds.Width, expected - 1, expected + 1);
        Assert.True(fill.Bounds.Height > 0, $"{visibleFill} has no height");

        var other = visibleFill == "ConfidenceFillSure" ? "ConfidenceFillUnsure" : "ConfidenceFillSure";
        Assert.False(cell.GetVisualDescendants().OfType<Rectangle>().Single(r => r.Name == other).IsEffectivelyVisible,
            $"{other} shows on a cell that should wear {visibleFill}");
    }

    private static void AssertNoFill(StackPanel cell)
    {
        foreach (var fill in cell.GetVisualDescendants().OfType<Rectangle>()
                     .Where(r => r.Name is "ConfidenceFillSure" or "ConfidenceFillUnsure"))
        {
            Assert.False(fill.IsEffectivelyVisible, $"{fill.Name} shows on a string with nothing judged");
        }
    }
}
