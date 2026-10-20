using DotCharter.Localization;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Library;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Session;
using DotCharter.Presentation.Shared.Library;

namespace DotCharter.Tests.Perf;

/// <summary>
///     LOCAL PROFILER for the owner's "search + filter in the song library feels
///     laggy" report. Not a gate: it prints wall-clock numbers and asserts
///     nothing timed, so it lives outside the default filter by name
///     (<c>*UiPerfSmokeTests</c>) exactly like the other profilers here.
///     <para>
///         The library it builds is the owner's measured scale: 2,160 songs,
///         ~5% byte-copy duplicates, real-shaped tuning/arrangement strings.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class SongLibrarySearchProfileUiPerfSmokeTests
{
    private const int LibrarySize = 2160;
    private readonly ITestOutputHelper _out;

    public SongLibrarySearchProfileUiPerfSmokeTests(ITestOutputHelper output) => _out = output;

    /// <summary>The number the OWNER feels: a keystroke with the real
    /// <c>LibraryView</c> attached, so the list's Reset-driven
    /// container rebuild is inside the measurement.
    /// <para>
    ///     Run TWICE against the same probe, so the A/B is one measurement rather
    ///     than two builds: the "before" leg injects a scheduler that runs the pass
    ///     straight away, which is exactly the shape the view model had before
    ///     <c>CoalescedFilterPass</c> (one full pass per keystroke, inside the
    ///     setter); the "after" leg is production.
    /// </para></summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Profile_SongBrowser_perKeystroke_withTheRealViewAttached(bool coalesced)
    {
        var library = BuildLibrary();
        var lines = new List<string> { coalesced ? "AFTER (coalesced)" : "BEFORE (a pass per keystroke)" };

        await HeadlessSessionFixture.Dispatch(async () =>
        {
            // The BEFORE leg's scheduler runs the callback inline, which reproduces
            // the pre-fix "ApplyFilter() straight out of OnSearchTextChanged".
            var h = new Harness(library, coalesced ? null : action => action());
            h.Vm.PsarcLocation = @"C:\lib";
            await h.Vm.ScanCommand.ExecuteAsync(null);

            var view = new DotCharter.UI.Views.Library.LibraryView { DataContext = new LibraryScreen(h.Vm) };
            var window = new Window { Width = 1600, Height = 1000, Content = view };
            window.Show();
            try
            {
                view.Measure(new Size(1600, 1000));
                view.Arrange(new Rect(0, 0, 1600, 1000));
                view.UpdateLayout();

                h.Vm.SearchText = "zzz"; // JIT warm
                view.UpdateLayout();
                h.Vm.SearchText = "";
                view.UpdateLayout();

                const string query = "metallica";
                for (var round = 1; round <= 3; round++)
                {
                    h.Vm.SearchText = "";
                    Dispatcher.UIThread.RunJobs();
                    view.UpdateLayout();

                    var before = h.Vm.FilterPass.PassCount;
                    var total = 0.0;

                    // The BURST: keystrokes arrive faster than the UI goes idle, so
                    // only input-priority work drains between them — which is
                    // exactly the window a Background-priority pass must not run in.
                    for (var i = 1; i <= query.Length; i++)
                    {
                        var sw = Stopwatch.StartNew();
                        h.Vm.SearchText = query[..i];
                        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
                        view.UpdateLayout();
                        sw.Stop();
                        total += sw.Elapsed.TotalMilliseconds;
                        lines.Add($"  r{round} '{query[..i]}' -> {sw.Elapsed.TotalMilliseconds:F1} ms, " +
                                  $"{h.Vm.Songs.Count} rows");
                    }

                    // …and then the user stops typing and the UI goes idle: the ONE
                    // coalesced pass runs, and the list re-realizes once.
                    var idle = Stopwatch.StartNew();
                    Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                    var passMs = idle.Elapsed.TotalMilliseconds;
                    view.UpdateLayout();
                    idle.Stop();

                    lines.Add($"SongBrowser + view r{round}: burst {total:F1} ms over {query.Length} keystrokes, " +
                              $"then {idle.Elapsed.TotalMilliseconds:F1} ms at idle " +
                              $"(pass {passMs:F1} / layout {idle.Elapsed.TotalMilliseconds - passMs:F1}) = " +
                              $"{total + idle.Elapsed.TotalMilliseconds:F1} ms total, " +
                              $"{h.Vm.FilterPass.PassCount - before} filter passes, {h.Vm.Songs.Count} rows, " +
                              $"{view.GetVisualDescendants().OfType<ListBoxItem>().Count()} containers");
                }
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

        foreach (var line in lines) _out.WriteLine(line);
    }

    /// <summary>Isolates the LIST from the view model: the same Reset shapes, no
    /// filter, no detail pane, no selection.</summary>
    [Fact]
    public async Task Profile_SongList_resetShapes()
    {
        var all = BuildLibrary().Select(e => new PsarcSongListItem(e, LocalizationService.Instance)).ToList();
        var lines = new List<string>();

        await HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new DotCharter.UI.Views.Library.LibraryView();
            var window = new Window { Width = 1600, Height = 1000, Content = view };
            window.Show();
            try
            {
                var list = view.GetVisualDescendants().OfType<ListBox>()
                    .First(l => l.Name == "LibraryRows");
                var rows = all.Select(s => (DotCharter.Presentation.Contracts.Library.ILibraryRow)new LibraryRow(s, () => true)).ToList();
                var bulk = new BulkObservableCollection<DotCharter.Presentation.Contracts.Library.ILibraryRow>();
                list.ItemsSource = bulk;
                bulk.ResetWith(rows);
                view.Measure(new Size(1600, 1000));
                view.Arrange(new Rect(0, 0, 1600, 1000));
                view.UpdateLayout();

                foreach (var count in new[] { 2160, 180, 90, 90, 2160, 90, 500, 40 })
                {
                    var sw = Stopwatch.StartNew();
                    bulk.ResetWith(rows.Take(count).ToList());
                    view.UpdateLayout();
                    sw.Stop();
                    lines.Add($"  real template ResetWith({count}) -> {sw.Elapsed.TotalMilliseconds:F1} ms, " +
                              $"{view.GetVisualDescendants().OfType<ListBoxItem>().Count()} containers");
                }

                // The real row is the container's own template since #180 (LibraryRowContainer):
                // the ladder below swaps ITEM templates, so it runs on the stock container.
                var theme = list.ItemContainerTheme!;
                list.ItemContainerTheme = null;

                // A LADDER of hand-built row templates: each rung adds one feature
                // of the real row, so the expensive one is named, not guessed at.
                foreach (var (name, factory) in RowTemplateLadder())
                {
                    list.ItemTemplate = new FuncDataTemplate<PsarcSongListItem>((_, _) => factory(), true);
                    bulk.ResetWith(rows);
                    view.UpdateLayout();

                    var worst = 0.0;
                    foreach (var count in new[] { 180, 90, 500 })
                    {
                        var sw = Stopwatch.StartNew();
                        bulk.ResetWith(rows.Take(count).ToList());
                        view.UpdateLayout();
                        sw.Stop();
                        worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds);
                    }

                    lines.Add($"  ladder {name,-24} -> worst {worst:F1} ms");
                }

                list.ItemTemplate = null;
                list.ItemContainerTheme = theme;
                bulk.ResetWith(rows);
                view.UpdateLayout();

                // Does the reset cost scale with the number of REALIZED rows?
                foreach (var height in new double[] { 300, 1000, 2000 })
                {
                    view.Measure(new Size(1600, height));
                    view.Arrange(new Rect(0, 0, 1600, height));
                    view.UpdateLayout();
                    bulk.ResetWith(rows.Take(1000).ToList());
                    view.UpdateLayout();

                    var sw = Stopwatch.StartNew();
                    bulk.ResetWith(rows.Take(900).ToList());
                    view.UpdateLayout();
                    sw.Stop();
                    lines.Add($"  viewport {height}px -> {sw.Elapsed.TotalMilliseconds:F1} ms, " +
                              $"{view.GetVisualDescendants().OfType<ListBoxItem>().Count()} containers");
                }

                view.Measure(new Size(1600, 1000));
                view.Arrange(new Rect(0, 0, 1600, 1000));
                view.UpdateLayout();

                // Play Mode republishes with Clear + N Add instead of one Reset.
                // Same real row template, same panel: what does that shape cost?
                var plain = new ObservableCollection<DotCharter.Presentation.Contracts.Library.ILibraryRow>();
                list.ItemsSource = plain;
                view.UpdateLayout();
                foreach (var count in new[] { 2160, 180, 90 })
                {
                    var sw = Stopwatch.StartNew();
                    plain.Clear();
                    foreach (var s in rows.Take(count)) plain.Add(s);
                    view.UpdateLayout();
                    sw.Stop();
                    lines.Add($"  Clear + {count} Add -> {sw.Elapsed.TotalMilliseconds:F1} ms");
                }

                list.ItemsSource = bulk;
                bulk.ResetWith(rows);
                view.UpdateLayout();

                // How much of that is BUILDING one row (a container wearing the row template)?
                new ListBoxItem { Theme = theme, DataContext = rows[0] }.ApplyTemplate();
                var build = Stopwatch.StartNew();
                for (var i = 0; i < 100; i++) new ListBoxItem { Theme = theme, DataContext = rows[i] }.ApplyTemplate();
                build.Stop();
                lines.Add($"  row container build x100 -> {build.Elapsed.TotalMilliseconds:F1} ms " +
                          $"({build.Elapsed.TotalMilliseconds / 100:F2} ms per row)");

                // And how much is reading the per-item projections the row binds?
                var props = Stopwatch.StartNew();
                for (var i = 0; i < 100; i++)
                {
                    _ = all[i].ArrangementChips;
                    _ = all[i].TuningCellLabel;
                    _ = all[i].TuningNeedsAttention;
                    _ = all[i].StatusLabel;
                    _ = all[i].Subtitle;
                    _ = all[i].SongLengthFormatted;
                    _ = all[i].CanLoad;
                }

                props.Stop();
                lines.Add($"  row projections x100 -> {props.Elapsed.TotalMilliseconds:F1} ms " +
                          $"({props.Elapsed.TotalMilliseconds / 100:F2} ms per row)");

                // Same panel, same shapes, a TRIVIAL row template: separates the
                // row template's cost from the panel/scroll machinery's.
                list.ItemContainerTheme = null;
                list.ItemTemplate = new FuncDataTemplate<PsarcSongListItem>((_, _) =>
                    new TextBlock { Height = 46 }, true);
                bulk.ResetWith(rows);
                view.UpdateLayout();
                foreach (var count in new[] { 2160, 180, 90, 90, 2160, 90, 500, 40 })
                {
                    var sw = Stopwatch.StartNew();
                    bulk.ResetWith(rows.Take(count).ToList());
                    view.UpdateLayout();
                    sw.Stop();
                    lines.Add($"  trivial template ResetWith({count}) -> {sw.Elapsed.TotalMilliseconds:F1} ms, " +
                              $"{view.GetVisualDescendants().OfType<ListBoxItem>().Count()} containers");
                }
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

        foreach (var line in lines) _out.WriteLine(line);
    }

    /// <summary>The same pass broken into its four stages, so the dominant one is
    /// named rather than guessed at.</summary>
    [Fact]
    public void Profile_SongBrowser_stageBreakdown()
    {
        var items = BuildLibrary().Select(e => new PsarcSongListItem(e, LocalizationService.Instance)).ToList();
        const int reps = 10;

        var dedupe = Time(reps, () => LibraryDedupe.CollapseByIdentity(items, static i => i.Song));
        var collapsed = LibraryDedupe.CollapseByIdentity(items, static i => i.Song);

        var culturePredicate = Time(reps, () =>
        {
            var n = 0;
            foreach (var s in collapsed)
            {
                if (s.Title.Contains("met", StringComparison.CurrentCultureIgnoreCase) ||
                    s.Artist.Contains("met", StringComparison.CurrentCultureIgnoreCase) ||
                    s.Album.Contains("met", StringComparison.CurrentCultureIgnoreCase))
                {
                    n++;
                }
            }

            return n;
        });

        var ordinalPredicate = Time(reps, () =>
        {
            var n = 0;
            foreach (var s in collapsed)
            {
                if (s.Title.Contains("met", StringComparison.OrdinalIgnoreCase) ||
                    s.Artist.Contains("met", StringComparison.OrdinalIgnoreCase) ||
                    s.Album.Contains("met", StringComparison.OrdinalIgnoreCase))
                {
                    n++;
                }
            }

            return n;
        });

        var arrangementRead = Time(reps, () =>
        {
            var n = 0;
            foreach (var s in collapsed)
                if (s.ArrangementSummary.Contains("Lead", StringComparison.CurrentCultureIgnoreCase)) n++;
            return n;
        });

        var cultureSort = Time(reps, () => collapsed
            .OrderBy(s => s.Artist, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase).ToList());

        var reset = Time(reps, () =>
        {
            var c = new BulkObservableCollection<PsarcSongListItem>();
            c.ResetWith(collapsed);
            return c.Count;
        });

        _out.WriteLine($"n={items.Count}, reps={reps}, all figures are ms per pass");
        _out.WriteLine($"  CollapseByIdentity          {dedupe:F2}");
        _out.WriteLine($"  predicate (CurrentCulture)  {culturePredicate:F2}");
        _out.WriteLine($"  predicate (Ordinal)         {ordinalPredicate:F2}");
        _out.WriteLine($"  path-chip predicate       {arrangementRead:F2}");
        _out.WriteLine($"  sort (CurrentCulture)       {cultureSort:F2}");
        _out.WriteLine($"  BulkObservable ResetWith    {reset:F2}");
    }

    /// <summary>Play Mode's Song Library: a SEPARATE filter (the shared half is
    /// the row FACTS, not the predicate) over a <see cref="BulkObservableCollection{T}" />.
    /// The republish legs measure the SHIPPED shapes — this leg measured a
    /// Clear + N Add long after that shape was retired, which is exactly the
    /// Perf-rot the tests guide warns about.</summary>
    [Fact]
    public void Profile_PlayModeSongLibrary_stageBreakdown()
    {
        var items = BuildLibrary().Select(e => PlaySongItem.FromPsarc(new PsarcSongListItem(e, LocalizationService.Instance))).ToList();
        const int reps = 10;
        var criteria = new LibraryFilter("metallica");

        var predicate = Time(reps, () =>
        {
            var n = 0;
            foreach (var s in items)
                if (LibraryQuery.Matches(s, criteria)) n++;
            return n;
        });

        var sort = Time(reps, () => items
            .OrderBy(s => s.Artist, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase).ToList());

        // The three republish shapes ApplyFilter can land in: a full Reset (a
        // big or reordering change), a removal-only differential (a refined
        // query), and the identical-sequence early-out.
        var trimmed = items.Where((_, i) => i % 24 != 0).ToList(); // ~4% removed
        var reset = Time(reps, () =>
        {
            var c = new BulkObservableCollection<PlaySongItem>();
            c.ResetWith(items);
            c.ResetWith(trimmed);
            return c.Count;
        });
        var differential = Time(reps, () =>
        {
            var c = new BulkObservableCollection<PlaySongItem>();
            c.ResetWith(items);
            c.SyncWith(trimmed);
            return c.Count;
        });
        var identical = Time(reps, () =>
        {
            var c = new BulkObservableCollection<PlaySongItem>();
            c.ResetWith(items);
            c.SyncWith(items);
            return c.Count;
        });

        // The artist-grouping half of RefreshSongListChrome, which every pass
        // runs over the WHOLE ordered result. Stamp() already walks the runs
        // internally, so publishing SongGroups from a second Build() is a second
        // walk of the same 2,160 rows for the same answer.
        var stampOnly = Time(reps, () =>
        {
            SongArtistGrouping.Stamp(items, true, static (_, n) => $"{n} songs", LocalizationService.Instance);
            return items.Count;
        });
        var stampThenBuild = Time(reps, () =>
        {
            SongArtistGrouping.Stamp(items, true, static (_, n) => $"{n} songs", LocalizationService.Instance);
            return SongArtistGrouping.Build(items, LocalizationService.Instance).Count;
        });
        var groups = SongArtistGrouping.Build(items, LocalizationService.Instance);
        var groupsClearAdd = Time(reps, () =>
        {
            var c = new ObservableCollection<SongArtistGroup>();
            foreach (var g in groups) c.Add(g);
            c.Clear();
            return c.Count;
        });
        var groupsSync = Time(reps, () =>
        {
            var c = new BulkObservableCollection<SongArtistGroup>();
            c.ResetWith(groups);
            c.SyncWith(groups); // the republish an unchanged grouping costs
            return c.Count;
        });

        _out.WriteLine($"n={items.Count}, reps={reps}, all figures are ms per pass");
        _out.WriteLine($"  LibraryQuery.Matches      {predicate:F2}");
        _out.WriteLine($"  sort (CurrentCulture)       {sort:F2}");
        _out.WriteLine($"  ResetWith (reset+trim)      {reset:F2}");
        _out.WriteLine($"  SyncWith  (reset+diff)      {differential:F2}");
        _out.WriteLine($"  SyncWith  (identical)       {identical:F2}");
        _out.WriteLine($"  Stamp alone                 {stampOnly:F2}");
        _out.WriteLine($"  Stamp + a second Build      {stampThenBuild:F2}  (groups={groups.Count})");
        _out.WriteLine($"  SongGroups Clear + N Add    {groupsClearAdd:F2}");
        _out.WriteLine($"  SongGroups SyncWith         {groupsSync:F2}");
    }

    /// <summary>Rungs of the row-template ladder: each one is the previous plus one
    /// feature of the real song row.</summary>
    private static IEnumerable<(string Name, Func<Control> Factory)> RowTemplateLadder()
    {
        yield return ("bare TextBlock", () => new TextBlock { Height = 46 });

        yield return ("3 bound TextBlocks", () =>
        {
            var panel = new StackPanel { Height = 46 };
            foreach (var path in new[] { "Title", "Subtitle", "TuningCellLabel" })
                panel.Children.Add(new TextBlock { [!TextBlock.TextProperty] = new Binding(path) });
            return panel;
        }
        );

        yield return ("+ TextTrimming", () =>
        {
            var panel = new StackPanel { Height = 46 };
            foreach (var path in new[] { "Title", "Subtitle", "TuningCellLabel" })
            {
                panel.Children.Add(new TextBlock
                {
                    [!TextBlock.TextProperty] = new Binding(path),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }

            return panel;
        }
        );

        yield return ("+ ToolTip.Tip", () =>
        {
            var panel = new StackPanel { Height = 46 };
            foreach (var path in new[] { "Title", "Subtitle", "TuningCellLabel" })
            {
                var text = new TextBlock { [!TextBlock.TextProperty] = new Binding(path) };
                text[!ToolTip.TipProperty] = new Binding("TuningSummary");
                panel.Children.Add(text);
            }

            return panel;
        }
        );

        yield return ("+ chips ItemsControl", () =>
        {
            var panel = new StackPanel { Height = 46 };
            panel.Children.Add(new ItemsControl { [!ItemsControl.ItemsSourceProperty] = new Binding("ArrangementChips") });
            return panel;
        }
        );

        yield return ("+ class bindings", () =>
        {
            var panel = new StackPanel { Height = 46 };
            var text = new TextBlock { [!TextBlock.TextProperty] = new Binding("Title") };
            panel.Children.Add(text);
            var border = new Border { Classes = { "FireListRow" } };
            border.Bind(StyledElement.DataContextProperty, new Binding("."));
            panel.Children.Add(border);
            return panel;
        }
        );

        yield return ("+ DynamicResource brush", () =>
        {
            var panel = new StackPanel { Height = 46 };
            var border = new Border { Width = 32, Height = 32 };
            border[!Border.BackgroundProperty] = new DynamicResourceExtension("FireSurfaceRaised");
            border.Child = new PathIcon
            {
                Width = 14, Height = 14,
                [!PathIcon.DataProperty] = new DynamicResourceExtension("IconMusicNote")
            };
            panel.Children.Add(border);
            return panel;
        }
        );
    }

    private static double Time<T>(int reps, Func<T> body)
    {
        body(); // JIT
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < reps; i++) body();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / reps;
    }

    // ---- fixture -----------------------------------------------------------------

    private static string[] Artists =>
    [
        "Metallica", "Iron Maiden", "Pink Floyd", "Rush", "Tool", "Opeth", "Dream Theater",
        "Led Zeppelin", "Black Sabbath", "Megadeth", "Slayer", "Anthrax", "Pantera",
        "Alice In Chains", "Soundgarden", "Nirvana", "Radiohead", "Muse", "Queens Of The Stone Age",
        "Foo Fighters", "The Beatles", "The Rolling Stones", "AC/DC", "Guns N' Roses"
    ];

    private static string[] Tunings =>
    [
        "E A D G B E", "Eb Ab Db Gb Bb Eb", "D A D G B E", "C G C F A D", "D A D G A D",
        "B F# B E G# C#", "E A D G", "D G C F"
    ];

    private static List<LibrarySongEntry> BuildLibrary()
    {
        var songs = new List<LibrarySongEntry>(LibrarySize);
        for (var i = 0; i < LibrarySize; i++)
        {
            var artist = Artists[i % Artists.Length];
            var arrangements = new List<LibraryArrangementEntry>
            {
                new() { ArrangementName = "Lead", SngPath = $"songs/bin/generic/s{i}_lead.sng" }
            };
            if (i % 2 == 0)
            {
                arrangements.Add(new LibraryArrangementEntry
                { ArrangementName = "Rhythm", SngPath = $"songs/bin/generic/s{i}_rhythm.sng" });
            }

            if (i % 3 == 0)
            {
                arrangements.Add(new LibraryArrangementEntry
                { ArrangementName = "Bass", SngPath = $"songs/bin/generic/s{i}_bass.sng" });
            }

            songs.Add(new LibrarySongEntry
            {
                SongKey = $"song{i:D5}",
                Charter = i % 7 == 0 ? "CustomsForge" : "",
                Version = "1.0",
                Title = $"Track Number {i} Of The Collection",
                Artist = artist,
                Album = $"Album {i % 300}",
                Year = 1970 + i % 55,
                SongLength = 120 + i % 300,
                TuningSummary = Tunings[i % Tunings.Length],
                CapoSummary = i % 11 == 0 ? 2 : 0,
                PackagePath = $@"E:\Rocksmith\cdlc\Library\{artist}\song{i:D5}_p.psarc",
                Arrangements = arrangements
            });
        }

        // ~5% byte-copy duplicates under a second file name, as a real library has.
        var dupes = songs.Where((_, i) => i % 20 == 0)
            .Select(s => new LibrarySongEntry
            {
                SongKey = s.SongKey, Charter = s.Charter, Version = s.Version,
                Title = s.Title, Artist = s.Artist, Album = s.Album, Year = s.Year,
                SongLength = s.SongLength, TuningSummary = s.TuningSummary,
                CapoSummary = s.CapoSummary, Arrangements = s.Arrangements,
                PackagePath = s.PackagePath.Replace("_p.psarc", "_copy_p.psarc", StringComparison.Ordinal)
            })
            .ToList();
        songs.AddRange(dupes);
        return songs;
    }

    private sealed class StubLibraryService(IReadOnlyList<LibrarySongEntry> result) : ILibraryService
    {
        public Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(string rootPath) => Task.FromResult(result);

        public Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(
            string rootPath, IProgress<LibraryScanProgress>? progress, CancellationToken cancellationToken)
            => Task.FromResult(result);

        public Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(
            string rootPath, IProgress<LibraryScanProgress>? progress,
            IProgress<IReadOnlyList<LibrarySongEntry>>? songFound, CancellationToken cancellationToken)
            => Task.FromResult(result);

        public Task<LibraryLoadedSong> LoadSongAsync(LibrarySongEntry song) => throw new NotSupportedException();
        public Task<string> LoadCoverImageAsync(LibrarySongEntry song) => Task.FromResult("");
    }

    private sealed class StubProjectSession : IProjectSessionService
    {
        public AppConfig AppConfig { get; } = new();
        public void Initialize() { }
        public void LoadAppConfig() { }
        public void SaveAppConfig() { }
        public void FreeAudio() { }
        public Task LoadFile(string filePath) => Task.CompletedTask;

        public Task LoadAudioFile(string filePath, bool generateBeats = true, bool preferMemoryPlayback = false)
            => Task.CompletedTask;

        public Task<bool> AttachAudioAsync(string sourcePath) => Task.FromResult(true);
        public Task LoadSilentTransportAsync(double projectLengthMs) => Task.CompletedTask;
        public Task OpenProject() => Task.CompletedTask;
        public Task<bool> SaveProjectAsync() => Task.FromResult(true);
        public Task<bool> SaveProjectAsAsync() => Task.FromResult(true);
        public IReadOnlyList<RecentProjectEntry> GetRecentProjects() => [];
        public event Action? RecentProjectsChanged { add { } remove { } }
        public bool IsProjectDirty => false;
        public Func<Task<bool>>? ConfirmDiscardAsync { get; set; }
        public Task<bool> MayReplaceOpenProjectAsync() => Task.FromResult(true);
        public void MarkProjectClean() { }
        public void MarkProjectDirty() { }
        public event Action? ProjectDirtyChanged { add { } remove { } }
        public void Dispose() { }
    }

    private sealed class Harness
    {
        public Harness(IReadOnlyList<LibrarySongEntry> library, Action<Action>? filterPassScheduler = null)
        {
            var logger = new FakeAppLogger();
            var state = new ProjectState();
            var config = new FakeConfigurationService();
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), state, config, FakeLocalization.KeysOnly());
            var selection = new SelectionService();
            var editor = new ChartEditorViewModel(
                state, transport, new HistoryService(logger), selection,
                new TempoService(logger), new TrackContextRegistry(state, selection), FakeLocalization.KeysOnly());

            Vm = new SongBrowserViewModel(
                new FakeFileStorageService(), config, new StubLibraryService(library), logger,
                new StubProjectSession(), state, editor, transport,
                new DotCharter.Presentation.Dialogs.DialogsSlot(), DotCharter.Localization.LocalizationService.Instance,
                filterPassScheduler);
        }

        public SongBrowserViewModel Vm { get; }
    }
}
