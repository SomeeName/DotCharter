using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Presentation.Fakes.Library;
using DotCharter.Tone.UI.Controls;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #723: Play Mode Song Select's facet popover (<c>Views/Panels/SongFacetTemplates.axaml</c>,
///     the <c>FacetPopoverTemplate</c> each single-facet caption funnel opens) trims a label too
///     wide for the popover with an ellipsis — and the trimmed label must carry the FULL label as
///     its tooltip, or a long tuning name ("Drop C# (C# G# C# F# A# D#)") cannot be read at all.
///     The Library's equivalent is <see cref="LibraryTuningPopoverUiPerfSmokeTests" />.
///     <para>
///         Asserted on what is drawn: the template, from the shipped resource dictionary, opened
///         in a real <see cref="Flyout" /> over the tuning options of the Library's stress fixture
///         (<see cref="FakeLibraryScreen.ManyTunings(Func{string, string})" />, in the shipped
///         strings, so the release's de-DE / ja-JP re-run, <c>$HeadlessLayoutFilter</c>, measures
///         its own language). Every label sits on ONE line; a label whose natural width exceeds
///         its arranged width is trimmed with an ellipsis and its tooltip is the full label; and
///         at least one label IS trimmed, so the fixture cannot pass by never reaching the trim.
///         <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> writes a PNG per case, with the first trimmed label's
///         tooltip open.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class SongSelectFacetPopoverUiPerfSmokeTests
{
    private const double Tolerance = 0.5;

    private static readonly Uri Templates = new("avares://DotCharter/Views/Panels/SongFacetTemplates.axaml");

    public static TheoryData<int, int> Windows() => new() { { 1280, 720 }, { 1920, 1080 } };

    [Theory]
    [MemberData(nameof(Windows))]
    public async Task ATrimmedFacetLabel_CarriesTheFullLabelAsItsTooltip(int width, int height)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var loc = LocalizationService.Instance;
            var labels = FakeLibraryScreen.ManyTunings(key => loc[key]).TuningColumn.FilterOptions.Select(o => o.Label).ToList();
            var facet = new BrowserFacet("tuning", loc["SongBrowser_Col_Tuning"]);
            facet.ResetOptions(labels.Select((label, i) => ($"t{i}", label)));
            facet.ApplyCounts(labels.Select((_, i) => ($"t{i}", 3 + i * 7)).ToDictionary(p => p.Item1, p => p.Item2));

            var dictionary = (ResourceDictionary)AvaloniaXamlLoader.Load(Templates);
            var template = Assert.IsAssignableFrom<IDataTemplate>(dictionary["FacetPopoverTemplate"]);
            var funnel = new Button { Classes = { "FacetFunnel" }, Content = "Tuning", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
            var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft, Content = template.Build(facet) };
            ((Control)flyout.Content!).DataContext = facet;
            funnel.Flyout = flyout;

            var window = NarrowWidthLayout.NewWindow(width, height, funnel);
            window.Show();
            try
            {
                NarrowWidthLayout.Settle(window);
                flyout.ShowAt(funnel);
                NarrowWidthLayout.Settle(window);
                var what = $"Song Select tuning facet {width}x{height}";
                var popover = Assert.IsType<FirePopover>(flyout.Content);
                Assert.True(popover.IsEffectivelyVisible && popover.Bounds is { Width: > 1, Height: > 1 }, $"{what}: the popover laid out as {popover.Bounds}");

                var trimmed = AssertEveryTrimmedLabelHasItsTooltip(popover, facet, what);
                Capture(window, popover, trimmed[0], $"song-select-tuning-facet-{width}x{height}");
            }
            finally
            {
                flyout.Hide();
                window.Close();
            }
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>Returns the labels that are trimmed.</summary>
    private static List<TextBlock> AssertEveryTrimmedLabelHasItsTooltip(FirePopover popover, BrowserFacet facet, string what)
    {
        var boxes = popover.GetVisualDescendants().OfType<CheckBox>().Where(c => c.DataContext is BrowserFacetOption).ToList();
        Assert.Equal(facet.Options.Count, boxes.Count);
        var trimmed = new List<TextBlock>();
        foreach (var box in boxes)
        {
            var option = (BrowserFacetOption)box.DataContext!;
            var label = box.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == option.Label)
                        ?? throw new Xunit.Sdk.XunitException($"{what}: no text in the '{option.Label}' option prints its label");
            Assert.True(label.TextLayout.TextLines.Count == 1, $"{what}: '{option.Label}' wraps onto {label.TextLayout.TextLines.Count} lines");

            var natural = NaturalWidth(label);
            if (natural <= label.Bounds.Width + Tolerance) continue;
            trimmed.Add(label);
            Assert.True(label.TextLayout.TextLines.Any(l => l.HasCollapsed),
                $"{what}: '{option.Label}' needs {natural:0.#} px and has {label.Bounds.Width:0.#} - it is CUT, not trimmed with an ellipsis");
            var tip = label.GetSelfAndVisualAncestors().TakeWhile(v => !ReferenceEquals(v, popover)).OfType<Control>()
                .Select(ToolTip.GetTip).FirstOrDefault(t => t != null);
            Assert.True(Equals(tip, option.Label), $"{what}: '{option.Label}' is trimmed and its tooltip is '{tip}', not the full label");
        }

        NarrowWidthLayout.Report($"{what}: {boxes.Count} options, {trimmed.Count} trimmed, popover {popover.Bounds.Width:0.#} px wide");
        // The fixture carries labels wider than the popover may grow: if none trims, the popover
        // has lost its width limit (or the fixture its stress rows) and the tooltip path is untested.
        Assert.True(trimmed.Count > 0, $"{what}: no label is trimmed - the popover grew to {popover.Bounds.Width:0.#} px to fit them all");
        return trimmed;
    }

    /// <summary>Scrolls the first trimmed label into view and opens its tooltip for the picture.</summary>
    private static void Capture(Window window, FirePopover popover, TextBlock label, string name)
    {
        if (!UiSweepRunner.CaptureEnabled) return;
        var scroller = label.FindAncestorOfType<ScrollViewer>();
        if (scroller != null && popover.IsVisualAncestorOf(scroller))
        {
            scroller.Offset = new Vector(0, Math.Max(0, NarrowWidthLayout.TopIn(label, scroller.Content as Visual ?? scroller) - 40));
            NarrowWidthLayout.Settle(window);
        }

        NarrowWidthLayout.Capture(window, name);
        ToolTip.SetIsOpen(label, true);
        try
        {
            NarrowWidthLayout.Settle(window);
            NarrowWidthLayout.Capture(window, name + "-tooltip");
        }
        finally
        {
            ToolTip.SetIsOpen(label, false);
        }
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
