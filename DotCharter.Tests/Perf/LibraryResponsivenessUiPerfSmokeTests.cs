using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.Views.Library;
using Harness = DotCharter.Tests.UI.ViewModels.Panels.SongBrowserFilterChipsTests.Harness;
using SyntheticSongLibrary = DotCharter.Tests.UI.ViewModels.Panels.SyntheticSongLibrary;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #180: the Library over a library of the owner's scale (~1,800 songs,
///     <see cref="SyntheticSongLibrary" />), the real <see cref="LibraryView" /> attached to a
///     window — first show, a re-show, and search typed one key at a time, each keystroke
///     followed by the idle frame its coalesced pass runs in. The PROFILE logs where the time
///     goes and how many row containers and layouts each step costs; the facts beside it assert
///     that STRUCTURE, never a millisecond.
/// </summary>
[Collection("HeadlessUI")]
public sealed class LibraryResponsivenessUiPerfSmokeTests(ITestOutputHelper output)
{
    /// <summary>Typed one key at a time, each settling: narrow, widen back, a second query, clear.</summary>
    private static readonly string[] Steps =
    [
        "m", "me", "met", "meta", "metal", "metall", "metalli", "metallic", "metallica",
        "metallic", "metalli", "metall", "metal", "meta", "met", "me", "m", "",
        "the", "the b", "the bl", "the bla", "the black", ""
    ];

    /// <summary>
    ///     The A/B in ONE process, interleaved run for run so JIT and box load fall on both legs:
    ///     the product (the selection scrolled into view once per change) against Avalonia's own
    ///     <c>AutoScrollToSelectedItem</c>, which follows the selected INDEX through every row a
    ///     pass inserts or removes above it. Medians over three runs per leg.
    /// </summary>
    [Fact]
    public async Task Profile_FirstShow_ReShow_AndTyping()
    {
        var lines = new List<string>();
        await HeadlessSessionFixture.Dispatch(async () =>
        {
            // A fresh browser per run: each run starts on the full list with the first song
            // selected, and no earlier run's adapter is still subscribed to it.
            Run(await Scanned(), coalesced: true); // JIT warm, discarded
            var runs = new Dictionary<bool, List<RunResult>> { [true] = [], [false] = [] };
            for (var round = 0; round < 3; round++)
            {
                runs[false].Add(Run(await Scanned(), coalesced: false));
                runs[true].Add(Run(await Scanned(), coalesced: true));
            }

            var h = await Scanned();

            foreach (var (coalesced, results) in runs.OrderBy(r => r.Key))
            {
                lines.Add(coalesced ? "PRODUCT (coalesced selection scroll)" : "AVALONIA AutoScrollToSelectedItem");
                lines.Add($"  first show {Median(results.Select(r => r.FirstShowMs)):F1} ms, re-show {Median(results.Select(r => r.ReShowMs)):F1} ms, " +
                          $"{results[0].Containers} containers on screen");
                for (var i = 0; i < Steps.Length; i++)
                {
                    var step = results.Select(r => r.Steps[i]).ToList();
                    lines.Add($"  '{Steps[i]}': {Median(step.Select(s => s.Ms)):F1} ms, {step[0].RowsBefore} -> {step[0].RowsAfter} rows, " +
                              $"{step[0].Events}, {step.Max(s => s.Prepared)} containers prepared, {step.Max(s => s.Layouts)} layouts, " +
                              $"{step.Max(s => s.Selections)} selection changes, {step.Max(s => s.RowVisualsBuilt)} row visuals built");
                }

                var totals = results.Select(r => r.Steps.Sum(s => s.Ms)).ToList();
                lines.Add($"  typing: {Steps.Length} keystrokes, median total {Median(totals):F0} ms = {Median(totals) / Steps.Length:F1} ms per keystroke, " +
                          $"worst keystroke {results.Max(r => r.Steps.Max(s => s.Ms)):F0} ms, " +
                          $"{results.Max(r => r.Steps.Sum(s => s.Prepared))} containers prepared");
            }

            // Where the pass's own time goes, with no list attached: the same keystrokes on the
            // browser alone (filter, sort, artist stamp, republish, chips, selection).
            var vmOnly = Enumerable.Range(0, 3).Select(_ => Steps.Sum(q =>
            {
                h.Vm.SearchText = q;
                var sw = Stopwatch.StartNew();
                h.Drain();
                return sw.Elapsed.TotalMilliseconds;
            })).ToList();
            lines.Add($"browser alone (no list): median {Median(vmOnly):F0} ms over {Steps.Length} keystrokes = {Median(vmOnly) / Steps.Length:F1} ms per keystroke");
        }, CancellationToken.None);

        foreach (var line in lines) output.WriteLine(line);
    }

    /// <summary>
    ///     Relaxing a query re-inserts rows above the selected song. Avalonia's own auto-scroll
    ///     follows the selected INDEX through each insert with a synchronous layout: 59 inserts
    ///     were 56 layouts and 456 containers prepared (5.6–9 s, Debug). With the selection
    ///     scrolled into view once per CHANGE, the step prepares no more containers than end up
    ///     on screen.
    /// </summary>
    [Fact]
    public async Task RelaxingAQuery_PreparesOnlyTheRowsItBringsOnScreen()
    {
        await HeadlessSessionFixture.Dispatch(async () =>
        {
            var h = await Scanned();
            WithLibrary(h, (screen, view, list) =>
            {
                foreach (var q in new[] { "m", "me", "met" }) Settle(h, screen, view, q);
                // The LAST row selected: relaxing the query inserts rows above it, each one moving its index.
                h.Vm.SelectedSong = h.Vm.Songs[^1];
                Settle(h, screen, view, "met");
                var inserts = 0;
                h.Vm.Songs.CollectionChanged += (_, e) =>
                {
                    if (e.Action == NotifyCollectionChangedAction.Add) inserts++;
                };
                var prepared = 0;
                list.ContainerPrepared += (_, _) => prepared++;

                Settle(h, screen, view, "me");

                Assert.True(inserts > 10, $"relaxing the query inserted {inserts} rows — the fixture no longer exercises the insert path");
                var onScreen = list.GetRealizedContainers().Count();
                Assert.True(prepared <= onScreen,
                    $"relaxing the query prepared {prepared} row containers for {onScreen} on screen — the list is following " +
                    "the selected index through every insert again (SelectionScroll.Coalesced off?)");
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     A filter pass REBINDS the rows it shows; it never builds them. A recycled container is
    ///     detached and re-attached, and a ContentPresenter forgets its recycled child on attach,
    ///     so a row in an ItemTemplate was built again for every container a pass brought on
    ///     screen. The row is the container's template now (<c>LibraryRowContainer</c>).
    /// </summary>
    [Fact]
    public async Task AFilterPass_RebindsItsRows_AndBuildsNone()
    {
        await HeadlessSessionFixture.Dispatch(async () =>
        {
            var h = await Scanned();
            WithLibrary(h, (screen, view, list) =>
            {
                var before = RowTitles(list);
                var resets = 0;
                h.Vm.Songs.CollectionChanged += (_, e) =>
                {
                    if (e.Action == NotifyCollectionChangedAction.Reset) resets++;
                };

                Settle(h, screen, view, "the");

                Assert.Equal(1, resets);
                var after = RowTitles(list);
                Assert.True(after.Count > 3, $"only {after.Count} rows on screen after the pass");
                var built = after.Count(t => !before.Contains(t));
                Assert.True(built == 0, $"the pass built {built} new rows for {after.Count} on screen — rows are rebuilt, not rebound");
            });
        }, CancellationToken.None);
    }

    /// <summary>The row lives in the container's template now (<c>LibraryRowContainer</c>); the
    /// FireList selected look must still reach it. A row that lost it would still lay out.</summary>
    [Fact]
    public async Task TheSelectedRow_WearsTheSelectedLook()
    {
        await HeadlessSessionFixture.Dispatch(async () =>
        {
            var h = await Scanned();
            WithLibrary(h, (_, view, list) =>
            {
                list.SelectedIndex = 1;
                view.UpdateLayout();
                var selectedFill = view.FindResource("FireRowSelectedFill");
                Assert.NotNull(selectedFill);

                var rows = list.GetRealizedContainers().OfType<ListBoxItem>()
                    .Select(item => (item, row: item.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("FireListRow"))))
                    .ToList();
                var selected = rows.Single(r => r.item.IsSelected);
                Assert.Same(selectedFill, selected.row.Background);
                Assert.All(rows.Where(r => !r.item.IsSelected), r => Assert.NotSame(selectedFill, r.row.Background));
                Assert.True(selected.row.Bounds.Height > 30, $"the selected row laid out {selected.row.Bounds.Height:0.#} px tall");
            });
        }, CancellationToken.None);
    }

    /// <summary>A search keystroke reaches the app log as ONE line per burst, after its pass
    /// settled, through the same sink as a tab switch (#180).</summary>
    [Fact]
    public async Task ASearchKeystroke_IsTimedOnce_AfterItsPass()
    {
        await HeadlessSessionFixture.Dispatch(async () =>
        {
            var h = await Scanned();
            var before = DotCharter.UI.Views.Shell.SurfaceShowTiming.Sink;
            var seen = new List<string>();
            DotCharter.UI.Views.Shell.SurfaceShowTiming.Sink = (name, _) => seen.Add(name);
            try
            {
                WithLibrary(h, (_, view, list) =>
                {
                    var box = view.GetLogicalDescendants().OfType<TextBox>().Single(t => t.Name == "LibrarySearchBox");
                    box.Text = "metal";
                    h.Drain(); // the harness's pass runs where the dispatcher's Background pass would
                    Dispatcher.UIThread.RunJobs();

                    var lines = seen.Where(n => n.StartsWith("Library search", StringComparison.Ordinal)).ToList();
                    Assert.True(lines.Count == 1, $"recorded: {string.Join(" | ", seen)}");
                    Assert.Equal(DotCharter.UI.Views.Shell.SearchSettleTiming.Name("Library", 1, list.ItemCount), lines[0]);
                    Assert.True(list.ItemCount < h.Vm.Songs.Count + 1 && list.ItemCount > 0);
                });
            }
            finally
            {
                DotCharter.UI.Views.Shell.SurfaceShowTiming.Sink = before;
            }
        }, CancellationToken.None);
    }

    private static void WithLibrary(Harness h, Action<LibraryScreen, LibraryView, ListBox> act)
    {
        var screen = new LibraryScreen(h.Vm);
        var view = new LibraryView { DataContext = screen };
        var window = new Window { Width = 1600, Height = 1000, Content = view };
        window.Show();
        try
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            act(screen, view, Rows(view));
        }
        finally
        {
            window.Close();
        }
    }

    private static void Settle(Harness h, LibraryScreen screen, LibraryView view, string query)
    {
        screen.SearchText = query;
        h.Drain();
        Dispatcher.UIThread.RunJobs();
        view.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private sealed record StepResult(double Ms, int RowsBefore, int RowsAfter, string Events, int Prepared, int Layouts, int Selections, int RowVisualsBuilt);

    private sealed record RunResult(double FirstShowMs, double ReShowMs, int Containers, List<StepResult> Steps);

    private static RunResult Run(Harness h, bool coalesced)
    {
        var open = Stopwatch.StartNew();
        var screen = new LibraryScreen(h.Vm);
        var view = new LibraryView { DataContext = screen };
        var host = new Grid { Children = { view } };
        var window = new Window { Width = 1600, Height = 1000, Content = host };
        var list = Rows(view);
        SelectionScroll.SetCoalesced(list, coalesced);
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        open.Stop();
        try
        {
            host.IsVisible = false;
            window.UpdateLayout();
            var show = Stopwatch.StartNew();
            host.IsVisible = true;
            window.UpdateLayout();
            show.Stop();
            return new RunResult(open.Elapsed.TotalMilliseconds, show.Elapsed.TotalMilliseconds,
                list.GetRealizedContainers().Count(), Type(h, screen, view, list));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Each step: set the text the way the binding does, then the idle frame.</summary>
    private static List<StepResult> Type(Harness h, LibraryScreen screen, LibraryView view, ListBox list)
    {
        var prepared = 0;
        list.ContainerPrepared += (_, _) => prepared++;
        var layouts = 0;
        view.LayoutUpdated += (_, _) => layouts++;
        var selections = 0;
        h.Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SongBrowserViewModel.SelectedSong)) selections++;
        };
        var events = new Dictionary<NotifyCollectionChangedAction, int>();
        h.Vm.Songs.CollectionChanged += (_, e) => events[e.Action] = events.GetValueOrDefault(e.Action) + 1;

        var results = new List<StepResult>();
        foreach (var step in Steps)
        {
            (prepared, layouts, selections) = (0, 0, 0);
            events.Clear();
            var before = h.Vm.Songs.Count;
            var titlesBefore = RowTitles(list);
            screen.SearchText = step;
            var sw = Stopwatch.StartNew();
            h.Drain();
            Dispatcher.UIThread.RunJobs();
            view.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            sw.Stop();
            results.Add(new StepResult(sw.Elapsed.TotalMilliseconds, before, h.Vm.Songs.Count,
                string.Join(" ", events.Select(e => $"{e.Key}x{e.Value}")), prepared, layouts, selections,
                RowTitles(list).Count(t => !titlesBefore.Contains(t))));
        }

        return results;
    }

    /// <summary>The row template's title blocks now in the list: a NEW one is a row whose visuals were built, not rebound.</summary>
    internal static HashSet<TextBlock> RowTitles(ListBox list) =>
        [.. list.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "RowTitle")];

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    internal static async Task<Harness> Scanned(int size = SyntheticSongLibrary.OwnerScale)
    {
        var h = await Harness.Scanned();
        h.Library.Result = SyntheticSongLibrary.Build(size);
        await h.Vm.ScanCommand.ExecuteAsync(null);
        h.Drain();
        return h;
    }

    internal static ListBox Rows(LibraryView view) =>
        view.GetLogicalDescendants().OfType<ListBox>().First(l => l.Name == "LibraryRows");
}
