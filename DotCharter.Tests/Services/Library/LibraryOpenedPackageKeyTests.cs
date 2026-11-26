using DotCharter.Services.Export;
using DotCharter.Services.Export.DlcBuilder;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Infrastructure.LibraryDoctor;
using DotCharter.Services.Library;
using DotCharter.Services.Library.Prefetch;

namespace DotCharter.Tests.Services.Library;

/// <summary>
///     A package opened for editing must not export under the package's own DLC key: the
///     game lists one song per key, so the re-export and the original package would hide one
///     another. The project gets a key of its own on open, the same one on every open, and
///     a key the project already carries is kept unless it is the package's.
/// </summary>
public sealed class LibraryOpenedPackageKeyTests
{
    private const string Artist = "Some Artist";
    private const string Title = "A Long Song Title";
    private const string Charter = "Somebody";

    [Fact]
    public async Task A_reopened_DotCharter_package_exports_under_a_key_of_its_own()
    {
        var packageKey = RocksmithDlcKey.Create(Artist, Title);

        var project = await OpenAsync(packageKey);

        AssertNoClash(packageKey, project);
    }

    [Fact]
    public async Task A_reopened_DLC_Builder_package_exports_under_a_key_of_its_own()
    {
        var packageKey = Rs2DlcStrings.CreateDlcKey(Charter, Artist, Title);

        var project = await OpenAsync(packageKey);

        AssertNoClash(packageKey, project);
    }

    [Fact]
    public async Task Every_open_of_one_package_gives_the_same_key()
    {
        var packageKey = RocksmithDlcKey.Create(Artist, Title);

        var first = await OpenAsync(packageKey);
        var second = await OpenAsync(packageKey);

        Assert.False(string.IsNullOrWhiteSpace(first.Chart.DlcKey));
        Assert.Equal(first.Chart.DlcKey, second.Chart.DlcKey);
    }

    [Fact]
    public async Task A_key_the_project_carries_is_kept_unless_it_is_the_package_key()
    {
        var packageKey = RocksmithDlcKey.Create(Artist, Title);

        var own = await OpenAsync(packageKey, "MyOwnKey2026");
        var copied = await OpenAsync(packageKey, packageKey.ToUpperInvariant());

        Assert.Equal("MyOwnKey2026", own.Chart.DlcKey);
        AssertNoClash(packageKey, copied);
    }

    private static async Task<ChartProject> OpenAsync(string packageKey, string projectKey = "")
    {
        var source = new OneSongSource(() => new ChartProject
        {
            Chart = new ChartData { SongName = Title, Artist = Artist, Charter = Charter, DlcKey = projectKey }
        });
        var service = new LibraryService(new FakeAppLogger(), [source]);
        var song = new LibrarySongEntry
        {
            SongKey = packageKey,
            Title = Title,
            Artist = Artist,
            Charter = Charter,
            PackagePath = Path.Combine(Path.GetTempPath(), "opened_p.psarc")
        };
        return (await service.LoadSongAsync(song)).Project;
    }

    private static void AssertNoClash(string packageKey, ChartProject project)
    {
        var packageExportKey = RsPackageMetadataFactory.Create(project, new RocksmithBuildOptions { OutputPath = "out_p.psarc", AudioPath = "song.wav" }).DlcKey;
        var dlcBuilderKey = Rs2DlcProjectBuilder.Build(project,
            new Rs2DlcProjectBuilder.Rs2DlcBuildOptions("song.wav", "", null, "")).DlcKey;

        Assert.NotEqual(packageKey, packageExportKey, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(packageKey, dlcBuilderKey, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class OneSongSource(Func<ChartProject> project) : ILibraryPackageSource
    {
        public IReadOnlyList<string> FileExtensions { get; } = [".psarc"];

        public void BeginScan() { }

        public IReadOnlyList<LibrarySongEntry> ReadForIndex(string path, CancellationToken cancellationToken) => [];

        public Task<LibraryLoadedSong> LoadSongAsync(LibrarySongEntry song) =>
            Task.FromResult(new LibraryLoadedSong { Project = project() });

        public LibraryAudioPrepareOutcome PrepareAudio(LibrarySongEntry song, CancellationToken cancellationToken) =>
            LibraryAudioPrepareOutcome.NoAudio;

        public string LoadCover(LibrarySongEntry song) => "";

        public LibrarySongBlock BlockFor(LibrarySongEntry song) => LibrarySongBlock.None;

        public PackageProbeResult Probe(string path) => new(true, AudioAvailability.Unknown);
    }
}
