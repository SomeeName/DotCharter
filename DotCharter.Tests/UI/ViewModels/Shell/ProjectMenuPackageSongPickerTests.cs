using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Infrastructure.LibraryDoctor;
using DotCharter.Services.Library;
using DotCharter.Services.Library.Prefetch;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tests.Services.Package.DcSong;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.UI.ViewModels.Shell;

/// <summary>
///     A multi-song package opened from outside the Library asks which song to open
///     (<see cref="IPackageSongPickerScreen" />); one song opens directly, and
///     Cancel opens nothing and says so on the status rail. The package is the
///     repo's <c>.dcsong</c> fixture; its reader reports a decoy song FIRST, so
///     choosing the second row proves the pick is honoured rather than the first
///     song being opened.
/// </summary>
public sealed class ProjectMenuPackageSongPickerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-pick-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder costs disk space, never a result.
        }
    }

    [Fact]
    public async Task PickingTheSecondSong_OpensThatSong_AndListsEverySong()
    {
        var h = new Harness(_root, withDecoy: true, pick: screen =>
        {
            screen.Selected = screen.Songs[1];
            screen.OpenCommand.Execute(null);
        });

        await h.Menu.LoadFile(h.PackagePath);

        Assert.Equal("Dropped Song", h.State.ActiveProject?.Chart.SongName);
        Assert.DoesNotContain("decoy", h.Source.LoadedKeys);
        Assert.Single(h.Source.LoadedKeys);
        var screen = Assert.IsType<PackageSongPickerViewModel>(Assert.Single(h.Dialogs.Opened));
        Assert.Equal(["Decoy Song", "Dropped Song"], screen.Songs.Select(row => row.Title));
        Assert.Equal("song_p.psarc", screen.PackageName);
    }

    [Fact]
    public async Task CancellingThePicker_OpensNothing_AndSaysSo()
    {
        var h = new Harness(_root, withDecoy: true, pick: screen => screen.CancelCommand.Execute(null));
        var opened = 0;
        h.Menu.ProjectOpened += () => opened++;

        await h.Menu.LoadFile(h.PackagePath);

        Assert.Null(h.State.ActiveProject);
        Assert.Empty(h.Source.LoadedKeys);
        Assert.Equal(0, opened);
        Assert.Equal(FakeLocalization.English().Format("Status_PackageSongPickCancelled", "song_p.psarc"), h.State.MainDisplayMessage);
    }

    [Fact]
    public async Task OnePackageSong_OpensDirectly_WithoutAsking()
    {
        var h = new Harness(_root, withDecoy: false, pick: _ => throw new InvalidOperationException("must not ask"));

        await h.Menu.LoadFile(h.PackagePath);

        Assert.Equal("Dropped Song", h.State.ActiveProject?.Chart.SongName);
        Assert.Empty(h.Dialogs.Opened);
    }

    [Fact]
    public async Task Picker_AnswersTheRowIndex_AndNullOnCancel()
    {
        var picker = new PackageSongPickerViewModel();
        picker.Configure(new PackageSongPickerRequest("p.psarc", [new("A", "x"), new("B", "y")]));
        picker.Selected = picker.Songs[1];
        picker.OpenCommand.Execute(null);
        Assert.Equal(1, await picker.Result);

        var first = new PackageSongPickerViewModel();
        first.Configure(new PackageSongPickerRequest("p.psarc", [new("A", "x"), new("B", "y")]));
        first.OpenCommand.Execute(null);
        Assert.Equal(0, await first.Result);

        var cancelled = new PackageSongPickerViewModel();
        cancelled.Configure(new PackageSongPickerRequest("p.psarc", [new("A", "x"), new("B", "y")]));
        Assert.True(cancelled.TryCancel());
        Assert.Null(await cancelled.Result);
    }

    private sealed class Harness
    {
        public Harness(string root, bool withDecoy, Action<IPackageSongPickerScreen> pick)
        {
            var built = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "build")).FullName, "song_p.psarc");
            DcSongLibraryKit.Write(built, DcSongLibraryKit.Song("Dropped Song", "Band"), "dropped", lossless: true);
            PackagePath = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "downloads")).FullName, "song_p.psarc");
            File.Move(built, PackagePath);

            var session = new CheckpointsViewModelTests.FakeSession();
            var logger = new FakeAppLogger();
            var source = Source = new PsarcExtension(DcSongLibraryKit.Source(Path.Combine(root, "cache"),
                new DcSongTestKit.FakeDecoder(available: true), logger));
            var library = new LibraryService(logger, [source]);
            var localization = FakeLocalization.English();
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), State, new FakeConfigurationService(), localization);
            var selection = new SelectionService();
            var editor = new ChartEditorViewModel(
                State, transport, new HistoryService(logger), selection,
                new TempoService(logger), new TrackContextRegistry(State, selection), localization);
            Dialogs = new ScriptedDialogs(_ => new PackageSongPickerViewModel(), screen => pick((IPackageSongPickerScreen)screen));
            var browser = new SongBrowserViewModel(
                new FakeFileStorageService(), new FakeConfigurationService(), library, logger, session, State, editor, transport,
                Dialogs, localization, new DecoyReader(library, withDecoy), playability: library);
            var (maintenance, _) = RecordingProxy.Create<DotCharter.Services.Editing.Maintenance.IChartMaintenanceService>();
            var (dialogs, _) = RecordingProxy.Create<DotCharter.Presentation.Dialogs.IDialogs>();
            Menu = new ProjectMenuViewModel(session, State,
                new ImportMenuViewModel(logger, ImportMenuViewModelTests.Offer(State, logger), ImportMenuViewModelTests.CleanupOffer()),
                maintenance, dialogs, new TestUiDispatcher(), logger, localization,
                DotCharter.Services.Plugins.PackageFeatures.All, new FakeFileStorageService(), packageOpener: browser);
        }

        public string PackagePath { get; }
        public PsarcExtension Source { get; }
        public ProjectState State { get; } = new();
        public ScriptedDialogs Dialogs { get; }
        public ProjectMenuViewModel Menu { get; }
    }

    /// <summary>Reads the real package, then puts a song that cannot load in front of it.</summary>
    private sealed class DecoyReader(ILibraryPackageFileReader inner, bool withDecoy) : ILibraryPackageFileReader
    {
        public async Task<IReadOnlyList<LibrarySongEntry>> ReadPackageAsync(string path, CancellationToken cancellationToken = default)
        {
            var songs = await inner.ReadPackageAsync(path, cancellationToken);
            if (!withDecoy) return songs;
            var real = songs[0];
            var decoy = new LibrarySongEntry { Title = "Decoy Song", Artist = "Other", PackagePath = real.PackagePath, SongKey = "decoy", SongBank = real.SongBank };
            return [decoy, real];
        }
    }

    internal sealed class PsarcExtension(ILibraryPackageSource inner) : ILibraryPackageSource
    {
        public IReadOnlyList<string> FileExtensions { get; } = [".psarc"];
        public void BeginScan() => inner.BeginScan();
        public IReadOnlyList<LibrarySongEntry> ReadForIndex(string path, CancellationToken cancellationToken) =>
            inner.ReadForIndex(path, cancellationToken);
        public List<string> LoadedKeys { get; } = [];

        public Task<LibraryLoadedSong> LoadSongAsync(LibrarySongEntry song)
        {
            LoadedKeys.Add(song.SongKey);
            return inner.LoadSongAsync(song);
        }

        public LibraryAudioPrepareOutcome PrepareAudio(LibrarySongEntry song, CancellationToken cancellationToken) =>
            inner.PrepareAudio(song, cancellationToken);
        public string LoadCover(LibrarySongEntry song) => inner.LoadCover(song);
        public LibrarySongBlock BlockFor(LibrarySongEntry song) => inner.BlockFor(song);
        public PackageProbeResult Probe(string path) => inner.Probe(path);
    }
}
