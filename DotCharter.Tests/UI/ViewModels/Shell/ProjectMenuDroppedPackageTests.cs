using DotCharter.Services.Editing.Maintenance;
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
///     #965: a <c>.psarc</c> dropped or File-opened from OUTSIDE the Library folder
///     opens as a project through the Library row's own load path
///     (<see cref="SongBrowserViewModel.LoadSongItem" />), instead of only a status
///     line saying packages open from the Library. Drives the real funnel
///     (<see cref="ProjectMenuViewModel.LoadFile" />), the real browser and the real
///     <see cref="LibraryService" />; the package is the repo's <c>.dcsong</c> fixture
///     read by its real source, registered for the <c>.psarc</c> extension so the
///     drop takes the package route without an official archive.
/// </summary>
public sealed class ProjectMenuDroppedPackageTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-965-").FullName;

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
    public async Task DroppedPackageOutsideTheLibrary_OpensAsAProject()
    {
        var h = new Harness(_root);
        var built = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "build")).FullName, "song_p.psarc");
        DcSongLibraryKit.Write(built, DcSongLibraryKit.Song("Dropped Song", "Band"), "dropped", lossless: true);
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "downloads")).FullName, "song_p.psarc");
        File.Move(built, path);
        var opened = 0;
        h.Menu.ProjectOpened += () => opened++;

        await h.Menu.LoadFile(path);

        Assert.Equal("Dropped Song", h.State.ActiveProject?.Chart.SongName);
        Assert.Equal(1, opened);
        Assert.Equal(0, h.LoadFileCalls);
        Assert.Equal(FakeLocalization.English().Format("Status_PsarcLoadedSong", "Dropped Song"), h.State.MainDisplayMessage);
        // Opened in place: nothing copied beside it.
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task UnreadablePackage_ShowsTheReasonOnTheStatusRail()
    {
        var h = new Harness(_root);
        var path = Path.Combine(_root, "broken.psarc");
        await File.WriteAllBytesAsync(path, "not a package at all"u8.ToArray(), TestContext.Current.CancellationToken);
        var opened = 0;
        h.Menu.ProjectOpened += () => opened++;

        await h.Menu.LoadFile(path);

        Assert.Null(h.State.ActiveProject);
        Assert.Equal(0, opened);
        var failedPrefix = FakeLocalization.English().Format("Status_PsarcLoadFailed", "");
        Assert.StartsWith(failedPrefix, h.State.MainDisplayMessage, StringComparison.Ordinal);
        Assert.True(h.State.MainDisplayMessage.Length > failedPrefix.Length, "the failure must carry its reason");
    }

    /// <summary>The menu, browser and library the app composes, over a temp cache.</summary>
    private sealed class Harness
    {
        public Harness(string root)
        {
            Session = new CheckpointsViewModelTests.FakeSession
            {
                LoadFileHandler = _ =>
                {
                    LoadFileCalls++;
                    return Task.CompletedTask;
                }
            };
            var logger = new FakeAppLogger();
            var source = new PsarcExtension(DcSongLibraryKit.Source(Path.Combine(root, "cache"),
                new DcSongTestKit.FakeDecoder(available: true), logger));
            var library = new LibraryService(logger, [source]);
            var localization = FakeLocalization.English();
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), State, new FakeConfigurationService(), localization);
            var selection = new SelectionService();
            var editor = new ChartEditorViewModel(
                State, transport, new HistoryService(logger), selection,
                new TempoService(logger), new TrackContextRegistry(State, selection), localization);
            var browser = new SongBrowserViewModel(
                new FakeFileStorageService(), new FakeConfigurationService(), library, logger, Session, State, editor, transport,
                new DotCharter.Presentation.Dialogs.DialogsSlot(), localization, library, playability: library);
            var (maintenance, _) = RecordingProxy.Create<IChartMaintenanceService>();
            var (dialogs, _) = RecordingProxy.Create<DotCharter.Presentation.Dialogs.IDialogs>();
            Menu = new ProjectMenuViewModel(Session, State,
                new ImportMenuViewModel(logger, ImportMenuViewModelTests.Offer(State, logger), ImportMenuViewModelTests.CleanupOffer()),
                maintenance, dialogs, new TestUiDispatcher(), logger, localization,
                DotCharter.Services.Plugins.PackageFeatures.All, new FakeFileStorageService(), packageOpener: browser);
        }

        public ProjectState State { get; } = new();

        /// <summary>Counts the project loader's calls: a package must never reach it.</summary>
        public int LoadFileCalls { get; private set; }

        public CheckpointsViewModelTests.FakeSession Session { get; }
        public ProjectMenuViewModel Menu { get; }
    }

    /// <summary>The fixture source, answering for <c>.psarc</c> as the game-package source does.</summary>
    private sealed class PsarcExtension(ILibraryPackageSource inner) : ILibraryPackageSource
    {
        public IReadOnlyList<string> FileExtensions { get; } = [".psarc"];
        public void BeginScan() => inner.BeginScan();
        public IReadOnlyList<LibrarySongEntry> ReadForIndex(string path, CancellationToken cancellationToken) =>
            inner.ReadForIndex(path, cancellationToken);
        public Task<LibraryLoadedSong> LoadSongAsync(LibrarySongEntry song) => inner.LoadSongAsync(song);
        public LibraryAudioPrepareOutcome PrepareAudio(LibrarySongEntry song, CancellationToken cancellationToken) =>
            inner.PrepareAudio(song, cancellationToken);
        public string LoadCover(LibrarySongEntry song) => inner.LoadCover(song);
        public LibrarySongBlock BlockFor(LibrarySongEntry song) => inner.BlockFor(song);
        public PackageProbeResult Probe(string path) => inner.Probe(path);
    }
}
