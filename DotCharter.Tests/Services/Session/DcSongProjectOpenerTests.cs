using DotCharter.Services.Package.DcSong;
using DotCharter.Services.Session;
using DotCharter.Tone.Infrastructure.Storage;
using static DotCharter.Tests.Services.Package.DcSong.DcSongTestKit;

namespace DotCharter.Tests.Services.Session;

/// <summary>
///     The File ▸ Open half of #709: one replace prompt, the folder picker, the unpack,
///     the ordinary load of the written project, and a status line that says when the
///     package's audio is lossy. The unpacker's own rules are
///     <c>DcSongProjectUnpackerTests</c>; the session's routing is
///     <see cref="ProjectSessionDcSongOpenTests" />.
/// </summary>
public sealed class DcSongProjectOpenerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_dcsong_opener_").FullName;
    private readonly FakeFileStorageService _storage = new();
    private readonly FakeConfigurationService _config = new();
    private readonly ProjectState _state = new();
    private readonly RecordingStatusBarService _statusBar = new();
    private readonly List<string> _loaded = [];
    private int _replacePrompts;
    private bool _mayReplace = true;

    public DcSongProjectOpenerTests()
    {
        _storage.NextFolder = Directory.CreateDirectory(Path.Combine(_dir, "Projects")).FullName;
        _config.AppConfig.LastProjectFolderDir = Path.Combine(_dir, "Earlier");
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private string BaseDir => _storage.NextFolder!;

    [Fact]
    public async Task AnOpusPackage_OpensTheNewProject_AndSaysItsAudioIsLossy()
    {
        var package = BuildProjectPackage(_dir, lossless: false);

        await Opener(new FakeDecoder(available: true)).OpenAsync(package);

        var expected = Path.Combine(BaseDir, "The Artist - The Song", "project.dcproj");
        Assert.Equal(new[] { expected }, _loaded);
        Assert.Equal(1, _replacePrompts);
        Assert.Equal(StatusSeverity.Warning, _state.MainDisplaySeverity);
        Assert.Equal(FakeLocalization.English().Format("Status_DcSong_OpenedLossy", "The Song", "The Artist - The Song"),
            _state.MainDisplayMessage);
        // The picker starts where the last project folder was chosen, and remembers this one.
        Assert.Equal(Path.Combine(_dir, "Earlier"), Assert.Single(_storage.RequestedStartDirectories));
        Assert.Equal(BaseDir, _config.AppConfig.LastProjectFolderDir);
        Assert.Equal(new[] { "begin:dcsong-open", "complete:dcsong-open" }, _statusBar.Log.Select(Short));
    }

    [Fact]
    public async Task AFlacPackage_SaysNothingAboutLossyAudio()
    {
        await Opener(new FakeDecoder(available: false)).OpenAsync(BuildProjectPackage(_dir, lossless: true));

        Assert.Equal(StatusSeverity.Success, _state.MainDisplaySeverity);
        Assert.Equal(FakeLocalization.English().Format("Status_DcSong_Opened", "The Song", "The Artist - The Song"),
            _state.MainDisplayMessage);
    }

    [Fact]
    public async Task StemsLeftOutForWantOfFfmpeg_AreNamedInTheStatusLine()
    {
        await Opener(new FakeDecoder(available: false)).OpenAsync(BuildProjectPackage(_dir, lossless: true, stems: true));

        var english = FakeLocalization.English();
        Assert.Equal(StatusSeverity.Warning, _state.MainDisplaySeverity);
        Assert.Equal(english.Format("Status_DcSong_Opened", "The Song", "The Artist - The Song") + " " + english["Status_DcSong_StemsSkipped"],
            _state.MainDisplayMessage);
    }

    [Fact]
    public async Task KeepingTheUnsavedProject_StopsBeforeThePickerAndWritesNothing()
    {
        _mayReplace = false;

        await Opener(new FakeDecoder(available: true)).OpenAsync(BuildProjectPackage(_dir, lossless: false));

        Assert.Empty(_storage.RequestedTitles);
        Assert.Empty(_loaded);
        Assert.Empty(Directory.EnumerateFileSystemEntries(BaseDir));
    }

    [Fact]
    public async Task CancellingTheFolderPicker_WritesNothing()
    {
        var package = BuildProjectPackage(_dir, lossless: false);
        var baseDir = BaseDir;
        _storage.NextFolder = null;

        await Opener(new FakeDecoder(available: true)).OpenAsync(package);

        Assert.Empty(_loaded);
        Assert.Empty(Directory.EnumerateFileSystemEntries(baseDir));
        Assert.Equal(Path.Combine(_dir, "Earlier"), _config.AppConfig.LastProjectFolderDir);
    }

    [Fact]
    public async Task ARefusedPackage_SaysWhyInTheUsersLanguage_AndOpensNothing()
    {
        await Opener(new FakeDecoder(available: false)).OpenAsync(BuildProjectPackage(_dir, lossless: false));

        var english = FakeLocalization.English();
        Assert.Empty(_loaded);
        Assert.Equal(StatusSeverity.Error, _state.MainDisplaySeverity);
        Assert.Equal(english.Format("Status_DcSong_OpenFailed", english["DcSong_ReadFailure_DecoderMissing"]), _state.MainDisplayMessage);
        Assert.Contains("complete:dcsong-open", _statusBar.Log);
    }

    [Fact]
    public async Task ALoadThatFailed_KeepsItsOwnMessage()
    {
        _state.SetMessage("the load's own error", StatusSeverity.Error);

        await Opener(new FakeDecoder(available: true), adoptLoadedPath: false).OpenAsync(BuildProjectPackage(_dir, lossless: false));

        Assert.Single(_loaded);
        Assert.Equal("the load's own error", _state.MainDisplayMessage);
    }

    private DcSongProjectOpener Opener(FakeDecoder decoder, bool adoptLoadedPath = true) =>
        new(new DcSongProjectUnpacker(new FakeAppLogger(), () => decoder), _storage, _config, _state,
            FakeLocalization.English(), new FakeAppLogger(), _statusBar,
            () =>
            {
                _replacePrompts++;
                return Task.FromResult(_mayReplace);
            },
            path =>
            {
                _loaded.Add(path);
                if (adoptLoadedPath) _state.CurrentProjectPath = path;
                return Task.CompletedTask;
            });

    private static string Short(string entry) => entry.Split(':', 3) is [var verb, var id, ..] ? $"{verb}:{id}" : entry;
}
