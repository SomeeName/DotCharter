using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.Presentation.Fakes.Dialogs;
using DotCharter.UI.Views.Dialogs;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #690: the Chart quality report opens before its checks have run. While the first check runs
///     only the busy row lays out; on a recheck the busy row sits above the previous report, whose
///     verbs are disabled; once a check lands the busy row goes. A report opened from an Editor on
///     Fire export's Show still lands on the round trip when the findings arrive after it opened.
/// </summary>
[Collection("HeadlessUI")]
public sealed class QualityReportCheckingUiPerfSmokeTests
{
    [Fact]
    public Task TheBusyRow_LaysOutAlone_ThenAboveTheDisabledReport_ThenGoes() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new ChartQualityReportDialogView { DataContext = FakeChartQualityReportScreen.Checking() };
            var window = Hosted(view, 1280, 900);
            window.Show();
            try
            {
                NarrowWidthLayout.Settle(window);
                var row = view.FindControl<StackPanel>("CheckingRow")!;
                var bar = view.FindControl<ProgressBar>("CheckingBar")!;
                var text = view.FindControl<TextBlock>("CheckingText")!;
                var body = view.FindControl<StackPanel>("ReportBody")!;
                Assert.True(row.IsEffectivelyVisible && bar.Bounds is { Width: > 1, Height: > 1 }, $"the busy bar laid out as {bar.Bounds}");
                Assert.True(text.Bounds is { Width: > 1, Height: > 1 }, $"the checking line laid out as {text.Bounds}");
                Assert.False(body.IsEffectivelyVisible, "the report showed before its first check landed");
                NarrowWidthLayout.Capture(window, "quality-checking");

                view.DataContext = FakeChartQualityReportScreen.Rechecking();
                NarrowWidthLayout.Settle(window);
                Assert.True(row.IsEffectivelyVisible && bar.Bounds is { Width: > 1, Height: > 1 }, $"the recheck's busy bar laid out as {bar.Bounds}");
                Assert.True(body.IsEffectivelyVisible && body.Bounds is { Width: > 1, Height: > 1 }, $"the previous report laid out as {body.Bounds}");
                var goTo = view.FindControl<ItemsControl>("GroupList")!.GetVisualDescendants().OfType<Button>()
                    .Where(b => b.IsEffectivelyVisible).ToList();
                Assert.NotEmpty(goTo);
                Assert.All(goTo, b => Assert.False(b.IsEffectivelyEnabled, "a previous check's verb was live during the recheck"));
                NarrowWidthLayout.Capture(window, "quality-rechecking");

                view.DataContext = FakeChartQualityReportScreen.Default();
                NarrowWidthLayout.Settle(window);
                Assert.False(row.IsEffectivelyVisible, "the busy row outlived the check");
                Assert.True(body.IsEffectivelyVisible && body.Bounds is { Width: > 1, Height: > 1 }, $"the report laid out as {body.Bounds}");
                Assert.Contains(view.FindControl<ItemsControl>("GroupList")!.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyEnabled);
                NarrowWidthLayout.Capture(window, "quality-filled");
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);

    /// <summary>Opened from the rail's Show, the report opens while it checks; when the findings land above the
    /// round trip, the round trip is brought back to the top of the body.</summary>
    [Fact]
    public Task AReportThatLandsAfterOpeningFromShow_StillShowsTheRoundTrip() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var checking = FakeChartQualityReportScreen.Checking();
            checking.FocusEofRoundTrip = true;
            var view = new ChartQualityReportDialogView { DataContext = checking };
            var window = Hosted(view, 1280, 900);
            window.Show();
            try
            {
                NarrowWidthLayout.Settle(window);
                var full = FakeChartQualityReportScreen.Full();
                full.FocusEofRoundTrip = true;
                view.DataContext = full;
                NarrowWidthLayout.Settle(window);

                var body = view.FindControl<ScrollViewer>("QualityBody")!;
                var section = view.FindControl<Border>("EofRoundTripSection")!;
                var top = NarrowWidthLayout.TopIn(section, body);
                NarrowWidthLayout.Report($"[quality-checking] landed from Show: offset {body.Offset.Y:0.#}, section top {top:0.#}");
                Assert.True(body.Offset.Y > 0, "the landed report did not scroll to the round trip");
                Assert.InRange(top, -1, 1);
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);

    /// <summary>The view in the overlay container the app hosts dialogs in, where the dialog grammar is included.</summary>
    private static Window Hosted(Control view, double width, double height)
    {
        var container = new DialogContainerView();
        container.DialogPanel.Children.Clear();
        container.DialogPanel.Children.Add(view);
        return NarrowWidthLayout.NewWindow(width, height, container);
    }
}
