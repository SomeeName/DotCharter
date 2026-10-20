using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Library;
using DotCharter.Presentation.Fakes.Library;
using DotCharter.Tone.UI.Controls;
using DotCharter.UI.Views.Library;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #712: the Library's Tuning filter popover over a large library. Since #651 every option
///     reads "Name (letters)" ("Drop C# (C# G# C# F# A# D#)"), one per distinct tuning, and the
///     popover shares its list with the capo options, so a real library offers ~40 rows.
///     <para>
///         Asserted on what is drawn, in the shipped strings (the column title, the unknown
///         bucket and the capo options are relabelled from <see cref="LocalizationService" />,
///         so the release's de-DE / ja-JP re-run, <c>$HeadlessLayoutFilter</c>, measures its
///         own language): the open popover stays inside the window; every option's label sits
///         on ONE line and is either shown whole or trimmed with the full label as its tooltip;
///         and every option can be reached — scrolled to the end, the last one is inside the
///         list's viewport. <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> writes a PNG per case.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class LibraryTuningPopoverUiPerfSmokeTests
{
    private const double Tolerance = 0.5;

    /// <summary>The Library's own area at 1280 x 720 and 1920 x 1080: charting loses the 76 px rail
    /// and 68 px of title and status bar, playing is full-bleed under the 44 px back bar.</summary>
    public static TheoryData<int, int> Areas() => new() { { 1204, 652 }, { 1280, 676 }, { 1844, 1012 } };

    [Theory]
    [MemberData(nameof(Areas))]
    public async Task TheTuningPopover_StaysInTheWindow_ShowsEveryLabel_AndReachesEveryOption(int width, int height)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var loc = LocalizationService.Instance;
            var screen = FakeLibraryScreen.ManyTunings(key => loc[key]);
            ((FakeLibraryColumn)screen.TuningColumn).Title = loc["SongBrowser_Col_Tuning"];
            var options = screen.TuningColumn.FilterOptions;
            Assert.True(options.Count >= 30, $"only {options.Count} tuning options - the fixture is too thin to test a long popover");

            var view = new LibraryView { DataContext = screen };
            var window = NarrowWidthLayout.NewWindow(width, height, view);
            window.Show();
            Flyout? flyout = null;
            try
            {
                NarrowWidthLayout.Settle(window);
                var funnel = Funnel(view, screen.TuningColumn);
                flyout = Assert.IsType<Flyout>(funnel.Flyout);
                flyout.ShowAt(funnel);
                NarrowWidthLayout.Settle(window);
                var capture = $"library-tuning-popover-{width}x{height}";
                NarrowWidthLayout.Capture(window, capture);

                var what = $"Tuning popover {width}x{height}";
                var popover = Assert.IsType<FirePopover>(flyout.Content);
                AssertInsideWindow(popover, window, what);
                var trimmed = AssertEveryLabelReadable(popover, options, what);
                AssertEveryOptionReachable(popover, window, options, trimmed, capture, what);
            }
            finally
            {
                flyout?.Hide();
                window.Close();
            }
        }, TestContext.Current.CancellationToken);
    }

    private static void AssertInsideWindow(FirePopover popover, Window window, string what)
    {
        Assert.True(popover.IsEffectivelyVisible && popover.Bounds is { Width: > 1, Height: > 1 }, $"{what}: the popover laid out as {popover.Bounds}");
        var topLeft = popover.TranslatePoint(new Point(0, 0), window)!.Value;
        var box = new Rect(topLeft, popover.Bounds.Size);
        NarrowWidthLayout.Report($"{what}: popover {box} in a {window.Bounds.Width}x{window.Bounds.Height} window");
        Assert.True(box.Left >= -Tolerance && box.Top >= -Tolerance, $"{what}: the popover starts at {box.TopLeft}, outside the window");
        Assert.True(box.Right <= window.Bounds.Width + Tolerance,
            $"{what}: the popover runs to x={box.Right:0.#}, past the window's right edge at {window.Bounds.Width:0.#}");
        Assert.True(box.Bottom <= window.Bounds.Height + Tolerance,
            $"{what}: the popover runs to y={box.Bottom:0.#}, past the window's bottom at {window.Bounds.Height:0.#} - its last rows and its Clear button are cut off");
    }

    /// <summary>Returns the options whose label is trimmed.</summary>
    private static List<CheckBox> AssertEveryLabelReadable(FirePopover popover, IReadOnlyList<ILibraryFilterOption> options, string what)
    {
        var boxes = OptionBoxes(popover);
        Assert.Equal(options.Count, boxes.Count);
        var trimmed = new List<CheckBox>();
        foreach (var box in boxes)
        {
            var option = (ILibraryFilterOption)box.DataContext!;
            var label = box.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == option.Label)
                        ?? throw new Xunit.Sdk.XunitException($"{what}: no text in the '{option.Label}' option prints its label");
            Assert.True(label.TextLayout.TextLines.Count == 1, $"{what}: '{option.Label}' wraps onto {label.TextLayout.TextLines.Count} lines");

            var (left, right) = NarrowWidthLayout.Span(label, popover);
            Assert.True(left >= -Tolerance && right <= popover.Bounds.Width + Tolerance,
                $"{what}: '{option.Label}' spans x={left:0.#}..{right:0.#}, outside the popover's 0..{popover.Bounds.Width:0.#}");

            var natural = NaturalWidth(label);
            if (natural <= label.Bounds.Width + Tolerance) continue;
            trimmed.Add(box);
            Assert.True(label.TextLayout.TextLines.Any(l => l.HasCollapsed),
                $"{what}: '{option.Label}' needs {natural:0.#} px and has {label.Bounds.Width:0.#} - it is CUT, not trimmed with an ellipsis");
            var tip = label.GetSelfAndVisualAncestors().TakeWhile(v => !ReferenceEquals(v, popover)).OfType<Control>()
                .Select(ToolTip.GetTip).FirstOrDefault(t => t != null);
            Assert.True(Equals(tip, option.Label), $"{what}: '{option.Label}' is trimmed and its tooltip is '{tip}', not the full label");
        }

        NarrowWidthLayout.Report($"{what}: {boxes.Count} options, {trimmed.Count} trimmed, popover {popover.Bounds.Width:0.#} px wide");
        // The fixture carries a label wider than the popover may grow: if none trims, the
        // popover has lost its width limit (or the fixture its stress row) and the trim path is untested.
        Assert.True(trimmed.Count > 0, $"{what}: no label is trimmed - the popover grew to {popover.Bounds.Width:0.#} px to fit them all");
        return trimmed;
    }

    private static void AssertEveryOptionReachable(FirePopover popover, Window window, IReadOnlyList<ILibraryFilterOption> options,
        IReadOnlyList<CheckBox> trimmed, string capture, string what)
    {
        var list = popover.GetVisualDescendants().OfType<ItemsControl>().Single(i => ReferenceEquals(i.ItemsSource, options));
        var scroller = list.FindAncestorOfType<ScrollViewer>();
        if (scroller != null && popover.IsVisualAncestorOf(scroller))
        {
            // A picture of the trimmed label, then of the list's end.
            scroller.Offset = new Vector(0, NarrowWidthLayout.TopIn(trimmed[0], list) - trimmed[0].Bounds.Height);
            NarrowWidthLayout.Settle(window);
            NarrowWidthLayout.Capture(window, capture + "-trimmed");
            scroller.Offset = new Vector(0, scroller.Extent.Height);
            NarrowWidthLayout.Settle(window);
            NarrowWidthLayout.Capture(window, capture + "-end");
        }

        var last = OptionBoxes(popover)[^1];
        var viewport = scroller != null && popover.IsVisualAncestorOf(scroller) ? (Visual)scroller : popover;
        var bottom = NarrowWidthLayout.TopIn(last, viewport) + last.Bounds.Height;
        Assert.True(bottom <= viewport.Bounds.Height + Tolerance,
            $"{what}: the last option ('{((ILibraryFilterOption)last.DataContext!).Label}') ends at y={bottom:0.#}, below its viewport's {viewport.Bounds.Height:0.#} - it cannot be reached");
        var windowBottom = NarrowWidthLayout.TopIn(last, window) + last.Bounds.Height;
        Assert.True(windowBottom <= window.Bounds.Height + Tolerance,
            $"{what}: the last option ends at y={windowBottom:0.#}, below the window's {window.Bounds.Height:0.#}");
    }

    private static List<CheckBox> OptionBoxes(FirePopover popover) =>
        [.. popover.GetVisualDescendants().OfType<CheckBox>().Where(c => c.DataContext is ILibraryFilterOption)];

    /// <summary>The funnel in the column's caption (the header's content control that shows the column).</summary>
    private static Button Funnel(Control view, ILibraryColumn column)
    {
        var header = NarrowWidthLayout.Named<Border>(view, "LibraryColumnHeader");
        var caption = header.GetVisualDescendants().OfType<ContentControl>().First(c => ReferenceEquals(c.Content, column) && c.IsEffectivelyVisible);
        return caption.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("FacetFunnel"));
    }

    /// <summary>The width the label would take on one line with nothing to stop it.</summary>
    private static double NaturalWidth(TextBlock label)
    {
        var probe = new TextBlock
        {
            Text = label.Text, FontFamily = label.FontFamily, FontSize = label.FontSize, FontWeight = label.FontWeight,
            FontStyle = label.FontStyle, FontStretch = label.FontStretch, LetterSpacing = label.LetterSpacing, Padding = label.Padding
        };
        probe.Measure(Size.Infinity);
        return probe.DesiredSize.Width;
    }
}
