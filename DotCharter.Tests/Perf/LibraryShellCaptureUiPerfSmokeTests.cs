using Avalonia.Controls;
using Avalonia.Threading;
using DotCharter.Presentation.Fakes.PlayMode;
using DotCharter.Services.Library;
using DotCharter.Tests.UI.ViewModels.Panels;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views;
using DotCharter.UI.Views.Library;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The wired Library as a picture IN THE APP SHELL (#306): the real <c>MainWindow</c>, the
///     Library opened through the view model's own <c>OpenSongBrowserCommand</c> (the rail's
///     Library row), and the <see cref="LibraryView" /> the shell built in <c>SongBrowserHost</c>
///     over <see cref="LibraryScreen" />. Three states: the app's own library (the suite's
///     throwaway config has no folder, so first run), an owner-sized library and the long-text
///     stress case — the last two by handing the hosted view a <see cref="LibraryScreen" /> over
///     a scanned song browser, so the rows, rail and footer are the real adapter's.
///     <para>
///         A CAPTURE harness like <see cref="NarrowWidthShellCaptureUiPerfSmokeTests" />: it does
///         its work only with <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> (Skia drawing, set before the
///         session starts), so the ordinary headless run does not pay for a <c>MainWindow</c>.
///         Switching the shell's tab is not inert (a whole-window screenshot harness once
///         took the TESTHOST down that way): the Library's switch posts
///         <c>SongBrowser.Activate()</c>, which only scans a configured folder, and the throwaway
///         config has none. PNGs go to <see cref="NarrowWidthLayout.ArtifactDirectory" />
///         (<c>DOTCHARTER_RIG_CAPTURE_DIR</c> moves them).
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class LibraryShellCaptureUiPerfSmokeTests
{
    private const string LongFolder = @"D:\Musik\Rocksmith\Eigene Songs (CDLC)\Sammlung 2024 – aktualisiert und neu sortiert";

    [Fact]
    public async Task TheWiredLibrary_IsCapturedInTheAppShell()
    {
        if (!UiSweepRunner.CaptureEnabled) return;

        await HeadlessSessionFixture.Dispatch(async () =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var vm = services.GetRequiredService<MainWindowViewModel>();
            foreach (var dialog in vm.Dialogs.AllDialogs)
            {
                if (dialog.IsOpen)
                    dialog.RequestClose();
            }

            vm.CommandPalette.Close();
            vm.IsGameplayPresentation = false;
            vm.SelectedMainTabIndex = 0;

            var normal = await Seeded(@"E:\Rocksmith\dlc", SyntheticSongLibrary.Build(400));
            var longText = await Seeded(LongFolder, LongTextLibrary());

            var window = new MainWindow { DataContext = vm, Width = 1920, Height = 1080 };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                vm.OpenSongBrowserCommand.Execute(null);
                NarrowWidthLayout.Settle(window);

                var view = Assert.Single(window.FindControl<Grid>("SongBrowserHost")!.Children.OfType<LibraryView>());
                var own = Assert.IsType<LibraryScreen>(view.DataContext);
                Assert.True(view.IsEffectivelyVisible && view.Bounds is { Width: > 1, Height: > 1 }, $"the hosted Library laid out as {view.Bounds}");
                NarrowWidthLayout.Capture(window, own.ShowFirstRun ? "library-shell-empty-1920x1080" : "library-shell-own-1920x1080");

                Show(view, window, Select(new LibraryScreen(normal.Vm), r => r.TuningNeedsAttention), "library-shell-normal-1920x1080");
                Show(view, window, Select(new LibraryScreen(longText.Vm), r => r.Title.Length > 60), "library-shell-longtext-1920x1080");

                window.Width = 1280;
                window.Height = 800;
                Show(view, window, Select(new LibraryScreen(longText.Vm), r => r.Title.Length > 60), "library-shell-longtext-1280x800");
                Show(view, window, Select(new LibraryScreen(normal.Vm), r => r.TuningNeedsAttention), "library-shell-normal-1280x800");

                view.DataContext = own;
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    /// <summary>A browser that has scanned <paramref name="songs" /> as a NEW folder, so its header has no "Refreshed" line.</summary>
    private static async Task<SongBrowserFilterChipsTests.Harness> Seeded(string folder, List<LibrarySongEntry> songs)
    {
        var h = await SongBrowserFilterChipsTests.Harness.Scanned();
        h.Vm.PsarcLocation = folder;
        h.Library.Result = songs;
        await h.Vm.ScanCommand.ExecuteAsync(null);
        h.Drain();
        return h;
    }

    private static LibraryScreen Select(LibraryScreen screen, Func<LibraryRow, bool> pick)
    {
        screen.SelectedRow = screen.Rows.OfType<LibraryRow>().FirstOrDefault(pick) ?? screen.Rows.FirstOrDefault();
        return screen;
    }

    private static void Show(LibraryView view, Window window, LibraryScreen screen, string name)
    {
        view.DataContext = screen;
        NarrowWidthLayout.Settle(window);
        Assert.True(view.Bounds is { Width: > 1, Height: > 1 }, $"{name}: the Library laid out as {view.Bounds}");
        Assert.True(screen.HasSelection, $"{name}: nothing selected - the rail would be empty");
        NarrowWidthLayout.Capture(window, name);
    }

    /// <summary>German and Japanese titles, a very long artist and album, an unusual tuning with a capo, an unreadable archive.</summary>
    private static List<LibrarySongEntry> LongTextLibrary() =>
    [
        Entry("long", FakeFlowText.LongSongTitle, FakeFlowText.LongArtist, "Complete Recordings, Remastered Deluxe Anniversary Edition", "C# F# B E G# C#", 3),
        Entry("de", FakeFlowText.GermanSongTitle, "Die Ärzte", "Geräusch (Live in der Wuhlheide, erweiterte Fassung)", "D# G# C# F# A# D#"),
        Entry("ja", FakeFlowText.JapaneseSongTitle, "東京事変", "大発見", "E A D G B E"),
        Entry("ja2", "閃光少女", "東京事変", "娯楽", "E A D G B E"),
        Entry("drop", "Ein außergewöhnlich langer Titel mit Drop-Stimmung", "Rammstein", "Mutter (Remastered)", "C G C F A D"),
        Entry("plain1", "Sakasama", "Polkadot Stingray", "Wanderlust", "E A D G B E"),
        Entry("plain2", "Die Ärzte Medley – Schrei nach Liebe / Männer sind Schweine / Westerland", "Die Ärzte", "Das Beste von kurz nach früher bis jetze", "E A D G B E"),
        SongBrowserFilterChipsTests.Harness.Unknown()
    ];

    private static LibrarySongEntry Entry(string key, string title, string artist, string album, string tuning, int capo = 0) => new()
    {
        SongKey = key, Title = title, Artist = artist, Album = album, Year = 2004, SongLength = 287, TuningSummary = tuning,
        CapoSummary = capo, Difficulty = 7, PackagePath = $@"{LongFolder}\{key}_sehr_langer_dateiname_p.psarc",
        Arrangements =
        [
            new LibraryArrangementEntry { ArrangementName = "Lead", SngPath = $"s/{key}_lead.sng", NoteCount = 812 },
            new LibraryArrangementEntry { ArrangementName = "Rhythm", SngPath = $"s/{key}_rhythm.sng", NoteCount = 455 },
            new LibraryArrangementEntry { ArrangementName = "Bass", SngPath = $"s/{key}_bass.sng", NoteCount = 390 }
        ]
    };
}
