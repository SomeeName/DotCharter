using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using DotCharter.Presentation.Fakes.Dialogs;
using DotCharter.UI.Views.Dialogs;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #638: the Chart quality report's Editor on Fire round trip, in a real window over the fake
///     screen, inside the <see cref="DialogContainerView" /> that carries the dialog grammar. Every
///     kind and its places lay out with a size, Show all lists the rest, and a report opened from a
///     lossy export's Show scrolls the round trip into view. #685: the section is an Expander, closed
///     for a charter who never exported to EOF; closed, only its header lays out, and its busy row
///     lays out while the export runs.
/// </summary>
[Collection("HeadlessUI")]
public sealed class QualityReportEofRoundTripUiPerfSmokeTests
{
    [Theory]
    [InlineData("Default")]
    [InlineData("LongText")]
    [InlineData("Full")]
    public Task EveryKindAndItsPlaces_LayOut_AndShowAllListsTheRest(string variant) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = Variant(variant);
            screen.EofRoundTrip.IsExpanded = true;
            var view = new ChartQualityReportDialogView { DataContext = screen };
            var window = Hosted(view, 1400, 1000);
            window.Show();
            try
            {
                NarrowWidthLayout.Settle(window);
                var section = view.FindControl<Border>("EofRoundTripSection")!;
                view.ShowEofRoundTrip();
                NarrowWidthLayout.Settle(window);
                NarrowWidthLayout.Capture(window, $"quality-eof-{variant}");

                Assert.True(section.IsEffectivelyVisible && section.Bounds is { Width: > 1, Height: > 1 },
                    $"the round trip laid out as {section.Bounds}");
                var groups = screen.EofRoundTrip.Groups;
                Assert.NotEmpty(groups);
                var list = view.FindControl<ItemsControl>("EofGroupList")!;
                var texts = list.GetVisualDescendants().OfType<TextBlock>().ToList();
                foreach (var group in groups)
                {
                    var title = Assert.Single(texts, t => t.Text == group.Title);
                    Assert.True(title.Bounds is { Width: > 1, Height: > 1 }, $"'{group.Title}' laid out as {title.Bounds}");
                    var subject = texts.First(t => t.Text == group.Items[0].Subject);
                    Assert.True(subject.Bounds is { Width: > 1, Height: > 1 }, $"'{group.Items[0].Subject}' laid out as {subject.Bounds}");
                    if (group.Items[0].HasTime)
                    {
                        var where = texts.First(t => t.Text == group.Items[0].Where);
                        Assert.True(where.Bounds is { Width: > 1, Height: > 1 }, $"'{group.Items[0].Where}' laid out as {where.Bounds}");
                        // A long arrangement name trims; the bar.beat and time never do.
                        Assert.False(where.TextLayout.TextLines.Any(l => l.HasCollapsed), $"'{group.Items[0].Where}' was trimmed");
                    }
                }

                var goTo = list.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
                Assert.NotEmpty(goTo);
                Assert.All(goTo, b => Assert.True(b.Bounds is { Width: > 1, Height: > 1 }, $"a Go to laid out as {b.Bounds}"));
                var open = view.FindControl<Button>("EofOpenListButton")!;
                Assert.True(open.IsEffectivelyVisible && open.Bounds is { Width: > 1, Height: > 1 }, $"Open as text laid out as {open.Bounds}");

                var showAll = view.FindControl<CheckBox>("EofShowAllBox")!;
                Assert.True(screen.EofRoundTrip.CanShowAll, $"the {variant} fixture lists every place — Show all is untested");
                Assert.True(showAll.IsEffectivelyVisible && showAll.Bounds is { Width: > 1, Height: > 1 }, $"Show all laid out as {showAll.Bounds}");
                var rows = Rows(list);
                showAll.IsChecked = true;
                NarrowWidthLayout.Settle(window);
                Assert.True(Rows(list) > rows, $"Show all left {Rows(list)} rows of {rows}");
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);

    /// <summary>Opened from the rail's Show, the report lands on the round trip; opened from the menu, at the top.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task OpenedFromShow_TheRoundTripIsScrolledIntoView(bool fromShow) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = FakeChartQualityReportScreen.Full();
            screen.FocusEofRoundTrip = fromShow;
            var view = new ChartQualityReportDialogView { DataContext = screen };
            var window = Hosted(view, 1280, 900);
            window.Show();
            try
            {
                NarrowWidthLayout.Settle(window);
                var body = view.FindControl<ScrollViewer>("QualityBody")!;
                var section = view.FindControl<Border>("EofRoundTripSection")!;
                var top = NarrowWidthLayout.TopIn(section, body);
                NarrowWidthLayout.Report($"[eof-round-trip] fromShow={fromShow}: offset {body.Offset.Y:0.#}, section top {top:0.#}");
                if (fromShow) NarrowWidthLayout.Capture(window, "quality-eof-from-show");

                if (fromShow)
                {
                    Assert.True(body.Offset.Y > 0, "the report did not scroll at all");
                    Assert.InRange(top, -1, 1);
                }
                else
                {
                    Assert.Equal(0, body.Offset.Y);
                }
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);

    /// <summary>
    ///     Closed (the Default fixture, as a charter who never exported to EOF opens it), the header
    ///     lays out and the groups do not; opened through the header, they do. The Busy fixture
    ///     shows the busy row and the "checking" line in place of the groups.
    /// </summary>
    [Fact]
    public Task Closed_OnlyTheHeaderLaysOut_AndOpeningShowsTheGroups_OrTheBusyRow() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = FakeChartQualityReportScreen.Default();
            Assert.False(screen.EofRoundTrip.IsExpanded);
            var view = new ChartQualityReportDialogView { DataContext = screen };
            var window = Hosted(view, 1280, 900);
            window.Show();
            try
            {
                NarrowWidthLayout.Settle(window);
                var expander = view.FindControl<Expander>("EofRoundTripExpander")!;
                var groups = view.FindControl<ItemsControl>("EofGroupList")!;
                var header = expander.GetVisualDescendants().OfType<ToggleButton>().First();
                Assert.True(expander.IsEffectivelyVisible && expander.Bounds is { Width: > 1, Height: > 1 }, $"the closed section laid out as {expander.Bounds}");
                Assert.True(header.Bounds is { Width: > 1, Height: > 1 }, $"its header laid out as {header.Bounds}");
                // Closed, the Expander's content is either detached or hidden; either way it is not laid out.
                Assert.False(Attached(groups) && groups.IsEffectivelyVisible,
                    $"a closed section laid its groups out ({groups.Bounds}, attached={Attached(groups)})");
                view.ShowEofRoundTrip();
                NarrowWidthLayout.Settle(window);
                NarrowWidthLayout.Capture(window, "quality-eof-closed");

                expander.IsExpanded = true;
                NarrowWidthLayout.Settle(window);
                Assert.True(screen.EofRoundTrip.IsExpanded, "the header did not open the section");
                Assert.True(groups.IsEffectivelyVisible && groups.Bounds is { Width: > 1, Height: > 1 }, $"the opened groups laid out as {groups.Bounds}");
                view.ShowEofRoundTrip();
                NarrowWidthLayout.Settle(window);
                NarrowWidthLayout.Capture(window, "quality-eof-opened");
            }
            finally
            {
                window.Close();
            }

            var busy = FakeChartQualityReportScreen.Busy();
            var busyView = new ChartQualityReportDialogView { DataContext = busy };
            var busyWindow = Hosted(busyView, 1280, 900);
            busyWindow.Show();
            try
            {
                NarrowWidthLayout.Settle(busyWindow);
                busyView.ShowEofRoundTrip();
                NarrowWidthLayout.Settle(busyWindow);
                var bar = busyView.FindControl<ProgressBar>("EofBusyBar")!;
                var summary = busyView.FindControl<TextBlock>("EofSummaryText")!;
                Assert.True(busy.EofRoundTrip.IsBusy);
                Assert.True(bar.IsEffectivelyVisible && bar.Bounds is { Width: > 1, Height: > 1 }, $"the busy row laid out as {bar.Bounds}");
                Assert.True(summary.Bounds is { Width: > 1, Height: > 1 }, $"the checking line laid out as {summary.Bounds}");
                var busyGroups = busyView.FindControl<ItemsControl>("EofGroupList")!;
                Assert.False(Attached(busyGroups) && busyGroups.IsEffectivelyVisible, "a running check listed groups");
                NarrowWidthLayout.Capture(busyWindow, "quality-eof-busy");
            }
            finally
            {
                busyWindow.Close();
            }
        }, TestContext.Current.CancellationToken);

    private static FakeChartQualityReportScreen Variant(string name) => name switch
    {
        "LongText" => FakeChartQualityReportScreen.LongText(),
        "Full" => FakeChartQualityReportScreen.Full(),
        _ => FakeChartQualityReportScreen.Default()
    };

    /// <summary>The view in the overlay container the app hosts dialogs in, where the dialog grammar is included.</summary>
    private static Window Hosted(Control view, double width, double height)
    {
        var container = new DialogContainerView();
        container.DialogPanel.Children.Clear();
        container.DialogPanel.Children.Add(view);
        return NarrowWidthLayout.NewWindow(width, height, container);
    }

    private static bool Attached(Control control) => control.GetVisualAncestors().OfType<Window>().Any();

    private static int Rows(ItemsControl list) =>
        list.GetVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible);
}
