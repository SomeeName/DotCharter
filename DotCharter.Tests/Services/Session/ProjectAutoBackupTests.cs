using System.IO.Compression;
using System.Text.RegularExpressions;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Session;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.Services.Session;

public sealed class ProjectAutoBackupTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dotcharter-autobackup-").FullName;
    private readonly FakeAppLogger _logger = new();
    private readonly RecordingDocumentService _documents = new();
    private readonly RecordingDispatcher _dispatcher = new();
    private readonly ProjectState _state = new();
    private readonly HistoryService _history;
    private readonly ProjectDirtyTracker _dirty;
    private readonly ProjectAutoBackup _backup;

    public ProjectAutoBackupTests()
    {
        var config = new FakeConfigurationService();
        var audio = new FakeAudioPlaybackService();
        var transport = new TransportViewModel(audio, _state, config, FakeLocalization.KeysOnly());
        var selection = new SelectionService();
        _history = new HistoryService(_logger);
        var editor = new ChartEditorViewModel(_state, transport, _history, selection,
            new TempoService(_logger), new TrackContextRegistry(_state, selection), FakeLocalization.KeysOnly());
        _dirty = new ProjectDirtyTracker(_history);
        _backup = new ProjectAutoBackup(config, _dirty, _documents, _state, transport, editor, _dispatcher, _logger,
            FakeLocalization.English());

        _state.ActiveProject = new ChartProject();
        _state.CurrentProjectPath = Path.Combine(_root, "My Song.rscp");
    }

    public void Dispose()
    {
        _backup.Dispose();
        _dirty.Dispose();
        BestEffortDelete.Directory(_root);
    }

    [Fact]
    public async Task ABackup_ZipsAClonedSnapshot_IntoTheProjectsBackupsFolder()
    {
        await _backup.CreateBackupAsync();

        var zip = Assert.Single(Directory.GetFiles(Path.Combine(_root, "backups"), "*.rscp.zip"));
        using (var archive = ZipFile.OpenRead(zip))
        {
            var entry = Assert.Single(archive.Entries);
            Assert.Matches(new Regex(@"^My Song_\d{8}_\d{6}\.rscp$"), entry.FullName);
        }

        var saved = Assert.Single(_documents.Saved);
        Assert.NotSame(_state.ActiveProject, saved.Project);
        Assert.False(File.Exists(saved.Path), "The temporary .rscp the zip was built from was left behind.");
        Assert.True(_logger.HasEntryContaining("INFO", "Auto backup saved"));
    }

    [Fact]
    public async Task AFailedWrite_IsReported_CleanedUp_AndDoesNotWedgeTheNextBackup()
    {
        _documents.FailNext = new IOException("disk full");

        await _backup.CreateBackupAsync();

        Assert.True(_logger.HasEntryContaining("ERROR", "Auto backup failed"));
        Assert.False(File.Exists(_documents.Saved.Single().Path));

        await _backup.CreateBackupAsync();

        Assert.Single(Directory.GetFiles(Path.Combine(_root, "backups"), "*.rscp.zip"));
    }

    [Fact]
    public async Task AProjectNeverSaved_IsNotBackedUp()
    {
        _state.CurrentProjectPath = "";

        await _backup.CreateBackupAsync();

        Assert.Empty(_documents.Saved);
    }

    [Fact]
    public void TwoBackupsInTheSameSecond_GetDistinctNames()
    {
        var first = ProjectAutoBackup.UniqueArchivePath(_root, "song", "20261003_120000");
        File.WriteAllText(first, "");

        var second = ProjectAutoBackup.UniqueArchivePath(_root, "song", "20261003_120000");

        Assert.EndsWith("song_20261003_120000.rscp.zip", first, StringComparison.Ordinal);
        Assert.EndsWith("song_20261003_120000_1.rscp.zip", second, StringComparison.Ordinal);
    }

    [Fact]
    public void Start_ArmsOneTick_AndDisposeStopsIt()
    {
        _backup.Start();
        _backup.Start();

        Assert.Equal(1, _dispatcher.Scheduled);
        Assert.Equal(TimeSpan.FromSeconds(30), _dispatcher.Period);

        _backup.Dispose();

        Assert.True(_dispatcher.TimerDisposed);
    }

    [Fact]
    public void TheSession_StartsAndStopsItsCollaborators()
    {
        var source = SourceText.StripComments(File.ReadAllText(Path.Combine(TestPaths.RepoRoot(),
            "DotCharter.Services", "Session", "ProjectSessionService.cs")));

        Assert.Contains("_autoBackup.Start();", Between(source, "public void Initialize()", "public void Dispose()"));
        var dispose = Between(source, "public void Dispose()", "public void LoadAppConfig()");
        Assert.Contains("_autoBackup.Dispose();", dispose);
        Assert.Contains("_silentTransport.Stop();", dispose);
        Assert.DoesNotContain("SchedulePeriodic", source);
        Assert.DoesNotContain("ZipFile", source);
    }

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from, $"Could not find '{start}' … '{end}'.");
        return source[from..to];
    }

    private sealed class RecordingDocumentService : IProjectDocumentService
    {
        public List<(string Path, ChartProject Project)> Saved { get; } = [];
        public Exception? FailNext { get; set; }

        public ChartProject LoadChart(string filePath) => new();

        public void SaveChart(string filePath, ChartProject project, double? audioLengthMs = null)
        {
            Saved.Add((filePath, project));
            if (FailNext is { } failure)
            {
                FailNext = null;
                throw failure;
            }

            File.WriteAllText(filePath, "chart");
        }

        public void RefreshArrangementFileManifest(ChartProject project)
        {
        }

        public void SaveDlcBuilderXmlBundle(string projectPath, ChartProject project, double? audioLengthMs = null)
        {
        }

        public ChartProject AssembleProject(ChartProject? baseProject, double bpm, string audioFileName,
            IEnumerable<LyricPhrase> lyrics) => baseProject ?? new ChartProject();

        public ChartProject CreateNewProject(string title, string artist, string audioFileName) => new() { AudioFileName = audioFileName };
    }

    private sealed class RecordingDispatcher : IUiDispatcher
    {
        public int Scheduled { get; private set; }
        public TimeSpan Period { get; private set; }
        public bool TimerDisposed { get; private set; }

        public bool CheckAccess() => true;

        public void Post(Action action) => action();

        public void PostNormal(Action action) => action();

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());

        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();

        public IDisposable SchedulePeriodic(TimeSpan period, Action tick)
        {
            Scheduled++;
            Period = period;
            return new Timer(this);
        }

        private sealed class Timer(RecordingDispatcher owner) : IDisposable
        {
            public void Dispose() => owner.TimerDisposed = true;
        }
    }
}
