using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Fakes.Editor;
using DotCharter.Presentation.Screens.Editor;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.Views.Editor;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The Chord shapes tab (#486), built for real: the app's own inspector view model (so the
///     screen is proven REACHED through the composition root, not just registered) over a seeded
///     arrangement with many templates and long names, attached, laid out at the inspector's
///     default size, every row realized and measured. Plus every fake variant on its own.
///     <para>
///         And the palette (#477): a real pointer click on a row's chord diagram places that chord
///         at the playhead, and the recent strip it fills lays out inside the pane.
///     </para>
///     <para>
///         And the restructure (#567), at the inspector's DEFAULT size and the three widths the
///         owner reviews: a pinned header over a list that is the only scroll region, at least
///         <see cref="MinVisibleRows" /> whole rows in view, no text clipped (the UI sweep's rule),
///         and diagrams whose fret label, o / x markers and finger dots never collide.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class ChordShapesConstructionUiPerfSmokeTests
{
    private const double PaneWidth = 420;

    /// <summary>
    ///     The Inspector pane at the app's default split on a 1920 x 1080 window: a third of what
    ///     the 76 px activity rail and the 6 px splitter leave (<c>InspectorSplitGrid</c> is
    ///     <c>*,6,2*</c>), about 612 px, rounded down.
    /// </summary>
    private const double DefaultInspectorWidth = 600;

    /// <summary>
    ///     Its height there (#232: about 550 px of dock under the timeline), less the dock's own
    ///     tab header.
    /// </summary>
    private const double DefaultInspectorHeight = 524;

    /// <summary>
    ///     The Chord shapes tab's own height inside that pane, the inspector's tab strip taking the
    ///     rest. The real-inspector case measures it and fails if the tab is any shorter, so the
    ///     variant cases below are never measured on more room than the app gives.
    /// </summary>
    private const double DefaultTabHeight = 470;

    /// <summary>The owner's acceptance (#567): three or four shapes in view at the default size.</summary>
    private const int MinVisibleRows = 3;

    /// <summary>Narrower than the default the rows wrap taller: two whole shapes still show from 420 px.</summary>
    private const int MinVisibleRowsNarrow = 2;

    public static TheoryData<string> Variants => [.. FakeChordShapesScreen.Variants.Select(v => v.Name)];

    public static TheoryData<string, double> AcceptanceCases =>
    [
        .. from variant in new[] { "Default", "LongText", "Full", "Empty" }
        from width in new[] { 320.0, 420.0, 560.0, DefaultInspectorWidth }
        select (variant, width)
    ];

    [Fact]
    public Task TheInspectorsChordShapesTab_OverARealArrangement_LaysOutEveryRow() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var state = services.GetRequiredService<ProjectState>();
            var selection = services.GetRequiredService<ISelectionService>();
            var inspector = services.GetRequiredService<InspectorViewModel>();
            var screen = Assert.IsType<ChordShapesViewModel>(inspector.ChordShapes);
            var previous = (state.ActiveProject, state.ActiveArrangement, state.ActiveLevel, inspector.SelectedTabIndex,
                state.CurrentTimeMs);
            Window? window = null;
            try
            {
                var arrangement = Seed(state, templates: 60);
                selection.ClearSelections();
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                Assert.Equal(arrangement.ChordTemplates.Count, screen.Rows.Count);

                inspector.SelectedTabIndex = InspectorViewModel.ChordShapesTabIndex;
                var view = new InspectorView { DataContext = inspector };
                window = NarrowWidthLayout.NewWindow(DefaultInspectorWidth, DefaultInspectorHeight, view);
                window.Show();
                Settle(window);

                NarrowWidthLayout.Capture(window, "chord-shapes-tab");
                var tab = view.GetVisualDescendants().OfType<ChordShapesView>().Single();
                Assert.True(tab.IsEffectivelyVisible, "the Chord shapes tab is not the visible panel");
                NarrowWidthLayout.Report($"chord shapes tab in the default inspector: {tab.Bounds.Width:0} x {tab.Bounds.Height:0}");
                Assert.True(tab.Bounds.Height >= DefaultTabHeight - 0.5,
                    $"the tab is {tab.Bounds.Height:0.#} px tall in the default inspector; the variant cases assume {DefaultTabHeight}");
                AssertRowsLaidOut(tab, arrangement.ChordTemplates.Count, DefaultInspectorWidth);
                AssertListIsTheOnlyScrollRegion(tab, MinVisibleRows);

                // An edit on the chart reaches the hosted list without a click, and what it did
                // goes to the status rail, not into the pane (#567).
                var row = screen.AllRows.First(r => r.IsDuplicate);
                row.MergeTargets[0].MergeCommand.Execute(null);
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                Settle(window);
                Assert.True(row.IsUnused, "the merged row still counts uses after the merge");
                Assert.StartsWith(Loc("ChordShapes_Status_Merged").Split('{')[0], state.MainDisplayMessage, StringComparison.Ordinal);

                // The palette: CLICK the first row's chord diagram — it must not swallow the press.
                var level = arrangement.Levels[0];
                var at = new FractionalPosition(61);
                Assert.DoesNotContain(level.Chords, c => c.Position == at);
                state.CurrentTimeMs = at.GetPositionMs(state.Beats);
                selection.ClearSelections();
                var firstRow = (ChordShapeRowViewModel)screen.Rows[0];
                var list = tab.FindControl<ItemsControl>("ChordShapesRows")!;
                var box = list.ContainerFromIndex(0)!.GetVisualDescendants().OfType<ChordDiagramControl>().First();
                var point = box.TranslatePoint(new Point(box.Bounds.Width * 0.7, box.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                Settle(window);

                var placed = Assert.Single(level.Chords, c => c.Position == at);
                Assert.Equal(firstRow.Template.Id, placed.TemplateId);
                Assert.Equal([firstRow], screen.RecentRows);
                AssertRecentStripLaidOut(tab, 1, DefaultInspectorWidth);
                AssertListIsTheOnlyScrollRegion(tab, MinVisibleRows);
            }
            finally
            {
                window?.Close();
                inspector.SelectedTabIndex = previous.SelectedTabIndex;
                state.CurrentTimeMs = previous.CurrentTimeMs;
                state.ActiveProject = previous.ActiveProject;
                state.ActiveArrangement = previous.ActiveArrangement;
                state.ActiveLevel = previous.ActiveLevel;
            }
        }, CancellationToken.None);

    [Theory]
    [MemberData(nameof(Variants))]
    public Task EveryFakeVariant_BuildsAndLaysOut(string variant) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = FakeChordShapesScreen.Variants.Single(v => v.Name == variant).Create();
            var view = new ChordShapesView { DataContext = screen };
            var window = NarrowWidthLayout.NewWindow(PaneWidth, 900, view);
            window.Show();
            try
            {
                Settle(window);
                Assert.True(view.Bounds is { Width: > 1, Height: > 1 }, $"{variant}: the view laid out as {view.Bounds}");
                if (screen.HasRows)
                {
                    AssertRowsLaidOut(view, screen.Rows.Count, PaneWidth);
                    AssertRecentStripLaidOut(view, screen.RecentRows.Count, PaneWidth);
                    Assert.True(view.FindControl<Button>("ChordShapesRepeatPrevious")!.Bounds.Height > 1,
                        $"{variant}: the Repeat previous chord action did not lay out");
                    AssertNoClippedText(view, variant);
                    AssertDiagramsReadable(view, variant);
                }
                else
                {
                    Assert.True(view.FindControl<TextBlock>("ChordShapesEmpty")!.Bounds.Height > 1,
                        $"{variant}: the empty text did not lay out");
                }
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    /// <summary>
    ///     The owner's acceptance, per variant and width, at the default tab height. Captures
    ///     <c>chord-shapes-&lt;variant&gt;-&lt;width&gt;</c> with <c>DOTCHARTER_UI_SWEEP_DUMP=1</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(AcceptanceCases))]
    public Task AtTheDefaultSize_TheHeaderIsPinned_AndTheListShowsSeveralReadableShapes(string variant, double width) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = FakeChordShapesScreen.Variants.Single(v => v.Name == variant).Create();
            var view = new ChordShapesView { DataContext = screen };
            var window = NarrowWidthLayout.NewWindow(width, DefaultTabHeight, view);
            window.Show();
            try
            {
                Settle(window);
                NarrowWidthLayout.Capture(window, $"chord-shapes-{variant}-{width:0}");
                if (!screen.HasRows)
                {
                    Assert.True(view.FindControl<TextBlock>("ChordShapesEmpty")!.Bounds.Height > 1, $"{variant}: no empty text");
                    return;
                }

                AssertNoClippedText(view, $"{variant} @ {width:0}");
                AssertDiagramsReadable(view, $"{variant} @ {width:0}");
                // The default size carries the owner's acceptance; 320 px (the pane's MinWidth) is
                // measured and logged, and must still scroll under a pinned header.
                // LongText is the stress case (sixty-character names and every verb on a row, in
                // German too): one row fewer. 320 px is the pane's MinWidth: one whole row and a
                // scrolling list under a pinned header.
                var stress = variant == "LongText" ? 1 : 0;
                var minRows = width < 420 ? 1
                    : width >= DefaultInspectorWidth ? MinVisibleRows - stress
                    : MinVisibleRowsNarrow - stress;
                AssertListIsTheOnlyScrollRegion(view, Math.Min(minRows, screen.Rows.Count));

                // The end of the list, for the review captures: the unnamed shapes sit there.
                var scroll = view.FindControl<ScrollViewer>("ChordShapesScroll")!;
                scroll.Offset = new Vector(0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
                Settle(window);
                NarrowWidthLayout.Capture(window, $"chord-shapes-{variant}-{width:0}-end");
                AssertNoClippedText(view, $"{variant} @ {width:0}, scrolled to the end");
                AssertDiagramsReadable(view, $"{variant} @ {width:0}, scrolled to the end");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    /// <summary>
    ///     The list scrolls and the header does not; at least <paramref name="minRows" /> whole rows
    ///     are in view; the list's scrollbar is drawn (not auto-hidden) while there is more.
    /// </summary>
    private static void AssertListIsTheOnlyScrollRegion(ChordShapesView view, int minRows)
    {
        var scroll = view.FindControl<ScrollViewer>("ChordShapesScroll")!;
        var list = view.FindControl<ItemsControl>("ChordShapesRows")!;
        var header = view.FindControl<Border>("ChordShapesHeader")!;
        var summary = view.FindControl<TextBlock>("ChordShapesSummary")!;
        Assert.False(scroll.AllowAutoHide, "the list's scrollbar auto-hides: nothing says the list goes on");
        Assert.DoesNotContain(header.GetVisualAncestors(), a => a is ScrollViewer);

        var viewportTop = scroll.TranslatePoint(new Point(0, 0), view)!.Value.Y;
        var viewportBottom = viewportTop + scroll.Viewport.Height;
        var rows = list.GetRealizedContainers().ToList();
        var whole = rows.Count(r =>
        {
            var top = r.TranslatePoint(new Point(0, 0), view)!.Value.Y;
            return top >= viewportTop - 0.5 && top + r.Bounds.Height <= viewportBottom + 0.5;
        });
        var heights = string.Join(", ", rows.Take(4).Select(r => r.Bounds.Height.ToString("0")));
        NarrowWidthLayout.Report($"chord shapes {view.Bounds.Width:0}x{view.Bounds.Height:0}: header {header.Bounds.Height:0} px, " +
                                 $"viewport {scroll.Viewport.Height:0} px, {whole} whole rows in view (row heights {heights})");
        Assert.True(whole >= minRows,
            $"{whole} whole shape rows in view at {view.Bounds.Width:0} x {view.Bounds.Height:0} (header {header.Bounds.Height:0} px, " +
            $"rows {heights} px); the acceptance is {minRows}");

        if (scroll.Extent.Height <= scroll.Viewport.Height + 1) return;
        var summaryTop = summary.TranslatePoint(new Point(0, 0), view)!.Value.Y;
        var firstTop = rows[0].TranslatePoint(new Point(0, 0), view)!.Value.Y;
        scroll.Offset = new Vector(0, Math.Min(150, scroll.Extent.Height - scroll.Viewport.Height));
        view.UpdateLayout();
        Assert.Equal(summaryTop, summary.TranslatePoint(new Point(0, 0), view)!.Value.Y, 1);
        Assert.True(rows[0].TranslatePoint(new Point(0, 0), view)!.Value.Y < firstTop - 10, "the list did not scroll under the header");
        scroll.Offset = default;
        view.UpdateLayout();
    }

    /// <summary>The UI sweep's clipped-text rule: a NoWrap, untrimmed TextBlock laid out narrower than its text.</summary>
    private static void AssertNoClippedText(Control root, string what)
    {
        var clipped = new List<string>();
        foreach (var text in root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible))
        {
            if (string.IsNullOrEmpty(text.Text) || text.TextWrapping != TextWrapping.NoWrap
                                                || text.TextTrimming != TextTrimming.None || text.Bounds.Width <= 1)
            {
                continue;
            }

            var slot = text.Bounds.Width - text.Padding.Left - text.Padding.Right;
            var needed = text.TextLayout.WidthIncludingTrailingWhitespace;
            if (needed > slot + 1.0) clipped.Add($"'{text.Text}' needs {needed:0.#} px in {slot:0.#}");
        }

        Assert.True(clipped.Count == 0, $"{what}: clipped text: {string.Join("; ", clipped)}");
    }

    /// <summary>Every drawn diagram: the fret label left of the grid, o / x above it, dots apart.</summary>
    private static void AssertDiagramsReadable(Control root, string what)
    {
        var diagrams = root.GetVisualDescendants().OfType<ChordDiagramControl>().Where(d => d.IsEffectivelyVisible).ToList();
        Assert.NotEmpty(diagrams);
        foreach (var diagram in diagrams)
        {
            var layout = diagram.CurrentLayout();
            var name = $"{what}: diagram {diagram.Bounds.Width:0}x{diagram.Bounds.Height:0} from fret {layout.StartFret}";
            Assert.True(layout.DotRadius * 2 < Math.Min(layout.StringSpacing, layout.FretSpacing),
                $"{name}: dots {layout.DotRadius * 2:0.#} px wide on a {layout.StringSpacing:0.#} x {layout.FretSpacing:0.#} grid touch");
            Assert.True(layout.GridLeft - layout.DotRadius >= -0.5 && layout.GridRight + layout.DotRadius <= diagram.Bounds.Width + 0.5,
                $"{name}: the outer strings' dots run off the diagram");
            Assert.True(layout.MarkerY + layout.FontSize * 0.3 <= layout.GridTop, $"{name}: the o / x row sits on the nut");
            if (layout.StartFret <= 1) continue;
            Assert.True(layout.LabelRect.Right <= layout.GridLeft - layout.DotRadius,
                $"{name}: the fret label ends at {layout.LabelRect.Right:0.#}, into the first string's dots at {layout.GridLeft - layout.DotRadius:0.#}");
            Assert.True(layout.LabelRect.Left >= 0, $"{name}: the fret label starts outside the diagram");
        }
    }

    private static string Loc(string key) => DotCharter.Localization.LocalizationService.Instance[key];

    private static void AssertRecentStripLaidOut(ChordShapesView view, int expectedTiles, double width)
    {
        var strip = view.FindControl<ItemsControl>("ChordShapesRecent")!;
        Assert.True(view.IsEffectivelyVisible, "the Chord shapes view is no longer the visible panel");
        Assert.True(strip.IsEffectivelyVisible, "the recent strip is hidden although shapes were placed");
        var tiles = strip.GetVisualDescendants().OfType<ChordDiagramControl>().ToList();
        Assert.Equal(expectedTiles, tiles.Count);
        var scroller = view.FindControl<ScrollViewer>("ChordShapesRecentScroll")!;
        var scrollerRight = scroller.TranslatePoint(new Point(scroller.Bounds.Width, 0), view)!.Value.X;
        Assert.True(scrollerRight <= width + 0.5, $"the recent strip ends at {scrollerRight:0.#} px in a {width} px pane");
        Assert.True(scroller.Bounds.Height <= 64, $"the recent strip is {scroller.Bounds.Height:0} px tall; it is a strip, not a box");
        foreach (var tile in tiles)
            Assert.True(tile.Bounds is { Width: >= 48, Height: >= 44 }, $"a recent tile's diagram laid out as {tile.Bounds}");
    }

    private static void AssertRowsLaidOut(ChordShapesView view, int expectedRows, double width)
    {
        var summary = view.FindControl<TextBlock>("ChordShapesSummary")!;
        Assert.True(summary.Bounds.Height > 1, $"the summary laid out as {summary.Bounds}");

        var list = view.FindControl<ItemsControl>("ChordShapesRows")!;
        var rows = list.GetRealizedContainers().ToList();
        Assert.Equal(expectedRows, rows.Count);
        Assert.All(rows, r => Assert.True(r.Bounds is { Width: > 1, Height: > 40 }, $"a row laid out as {r.Bounds}"));

        var boxes = list.GetVisualDescendants().OfType<ChordDiagramControl>().ToList();
        Assert.Equal(expectedRows, boxes.Count);
        Assert.All(boxes, b => Assert.True(b.Bounds is { Width: > 60, Height: > 60 }, $"a chord diagram laid out as {b.Bounds}"));

        // Nothing in a row may run past the pane: long names trim, the verbs wrap.
        foreach (var row in rows.Take(12))
        {
            foreach (var part in row.GetVisualDescendants().OfType<Control>().Where(c => c is TextBlock or Button && c.IsEffectivelyVisible))
            {
                var right = part.TranslatePoint(new Point(part.Bounds.Width, 0), view)?.X ?? 0;
                Assert.True(right <= width + 0.5, $"{part.GetType().Name} '{(part as TextBlock)?.Text}' ends at {right:0.#} px in a {width} px pane");
            }
        }
    }

    private static Arrangement Seed(ProjectState state, int templates)
    {
        var project = new ChartProject { Chart = { SongName = "Chord shapes fixture" } };
        for (var i = 0; i < 64; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i % 4 == 0 });

        var arrangement = new Arrangement { Name = "Lead", StringCount = 6 };
        var level = new ChartLevel { DifficultyIndex = 0 };
        arrangement.Levels.Add(level);
        string[] names =
        [
            "Am", "C", "G", "Cmaj7#11 (from the import, check the voicing before export)",
            "Gesperrter Septakkord mit Barré im neunten Bund", "ドロップDのパワーコード(ブリッジミュート)", ""
        ];
        for (var i = 0; i < templates; i++)
        {
            // Every fourth template repeats the previous one's frets: a duplicate pair.
            var shift = i % 4 == 3 ? (i - 1) % 9 : i % 9;
            var template = new ChordTemplate { Id = i, Name = names[i % names.Length] };
            template.Frets[1] = 1 + shift;
            template.Frets[2] = 3 + shift;
            template.Frets[3] = 3 + shift;
            if (i % 5 != 0) ChordTemplateFingerSetter.SetSuggestedFingers(template, 6);
            if (i % 7 == 0) template.Arpeggio = true;
            arrangement.ChordTemplates.Add(template);

            if (i % 6 == 5) continue;
            var chord = new Chord { Position = new FractionalPosition(i % 60), TemplateId = i };
            foreach (var (s, f) in template.Frets) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f });
            level.Chords.Add(chord);
        }

        project.Arrangements.Add(arrangement);
        state.ActiveProject = project;
        state.ActiveArrangement = arrangement;
        state.ActiveLevel = level;
        return arrangement;
    }

    private static void Settle(Window window)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            window.UpdateLayout();
        }
    }
}
