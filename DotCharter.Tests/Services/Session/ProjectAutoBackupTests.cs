using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Text.RegularExpressions;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Export.Audio;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Session;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.Services.Session;

public sealed partial class ProjectAutoBackupTests : IDisposable
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
        _state.CurrentProjectPath = Path.Combine(_root, "My Song.dcproj");
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

        var zip = Assert.Single(Directory.GetFiles(Path.Combine(_root, "backups"), "*.dcproj.zip"));
        using (var archive = ZipFile.OpenRead(zip))
        {
            var entry = Assert.Single(archive.Entries);
            Assert.Matches(new Regex(@"^My Song_\d{8}_\d{6}\.dcproj$"), entry.FullName);
        }

        var saved = Assert.Single(_documents.Saved);
        Assert.NotSame(_state.ActiveProject, saved.Project);
        Assert.False(File.Exists(saved.Path), "The temporary .dcproj the zip was built from was left behind.");
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

        Assert.Single(Directory.GetFiles(Path.Combine(_root, "backups"), "*.dcproj.zip"));
    }

    [Fact]
    public void AFailedTick_RetriesUnchangedDirtyContent_OnTheNextDueTick()
    {
        _dirty.MarkDirty();
        _documents.FailNext = new IOException("disk full");

        RunDueTick();

        Assert.True(_logger.HasEntryContaining("ERROR", "Auto backup failed"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "backups")));

        RunDueTick();

        var path = Assert.Single(Directory.GetFiles(Path.Combine(_root, "backups"), "*.dcproj.zip"));
        using var archive = ZipFile.OpenRead(path);
        Assert.Single(archive.Entries);
    }

    [Fact]
    public void ASuccessfulTick_DoesNotBackUpUnchangedDirtyContentAgain()
    {
        _dirty.MarkDirty();
        RunDueTick();
        var path = Assert.Single(Directory.GetFiles(Path.Combine(_root, "backups")));

        RunDueTick();

        Assert.Equal(path, Assert.Single(Directory.GetFiles(Path.Combine(_root, "backups"))));
    }

    [Fact]
    public void AnEditDuringTheWrite_IsStillOwedABackup()
    {
        _dirty.MarkDirty();
        RunDueTick(_dirty.MarkDirty);

        RunDueTick();

        Assert.Equal(2, Directory.GetFiles(Path.Combine(_root, "backups"), "*.dcproj.zip").Length);
    }

    [Fact]
    public void AProjectSwitchDuringTheWrite_DoesNotReuseThePreviousProjectsStamp()
    {
        _dirty.MarkDirty();
        RunDueTick(() =>
        {
            _state.ActiveProject = new ChartProject();
            _backup.ResetSchedule();
        });

        RunDueTick();

        Assert.Equal(2, Directory.GetFiles(Path.Combine(_root, "backups"), "*.dcproj.zip").Length);
    }

    // Hold the worker until Tick has returned, then run its posted continuation
    // to completion. This drives the real gate without sleeping or racing a timer.
    private void RunDueTick(Action? afterSnapshot = null)
    {
        using var release = new ManualResetEventSlim();
        using var context = new TickContext();
        _documents.WriteRelease = release;
        var previous = SynchronizationContext.Current;
        var pending = false;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            typeof(ProjectAutoBackup).GetField("_lastBackupUtc", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(_backup, DateTimeOffset.MinValue);
            _backup.Tick();
            pending = (bool)typeof(ProjectAutoBackup).GetField("_inProgress", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(_backup)!;
            afterSnapshot?.Invoke();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            release.Set();
        }

        if (pending)
        {
            context.CompleteWrite(() => !(bool)typeof(ProjectAutoBackup)
                .GetField("_inProgress", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_backup)!);
        }
        _documents.WriteRelease = null;
    }

    private sealed class TickContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _posted = [];

        public override void Post(SendOrPostCallback d, object? state) => _posted.Add((d, state));

        public void CompleteWrite(Func<bool> completed)
        {
            var deadlock = System.Diagnostics.Stopwatch.StartNew();
            var previous = Current;
            try
            {
                SetSynchronizationContext(this);
                while (!completed())
                {
                    var remaining = BoundedWait.DeadlockCap - deadlock.Elapsed;
                    Assert.True(remaining > TimeSpan.Zero, "The auto-backup dispatcher never finished (deadlock or a starved box).");
                    Assert.True(_posted.TryTake(out var work, remaining), "The auto-backup dispatcher never posted its next continuation.");
                    work.Callback(work.State);
                }
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }

        public void Dispose() => _posted.Dispose();
    }

    [Fact]
    public async Task AZipThatFailsMidWrite_LeavesNoFinalNamedArchive_AndKeepsTheGoodBackup()
    {
        var backups = Path.Combine(_root, "backups");
        Directory.CreateDirectory(backups);
        var good = Path.Combine(backups, "My Song_20200101_000000.dcproj.zip");
        using (var seed = ZipFile.Open(good, ZipArchiveMode.Create)) seed.CreateEntry("x.dcproj");
        var goodBytes = File.ReadAllBytes(good);

        _documents.VanishAfterWrite = true;
        await _backup.CreateBackupAsync();

        Assert.True(_logger.HasEntryContaining("ERROR", "Auto backup failed"));
        Assert.Equal(good, Assert.Single(Directory.GetFiles(backups)));
        Assert.Equal(goodBytes, File.ReadAllBytes(good));
    }

    [Fact]
    public void PruneBackups_IgnoresAndSweepsStagedZips_SoTheLastGoodOneSurvivesKeepOne()
    {
        File.WriteAllText(Path.Combine(_root, "song_20200101_000000.dcproj.zip"), "good");
        var staged = Path.Combine(_root, "song_20300101_000000.dcproj.zip.tmp");
        File.WriteAllText(staged, "truncated");

        var deleted = ProjectAutoBackup.PruneBackups(_root, "song", 1);

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(Path.Combine(_root, "song_20200101_000000.dcproj.zip")));
        Assert.False(File.Exists(staged));
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

        Assert.EndsWith("song_20261003_120000.dcproj.zip", first, StringComparison.Ordinal);
        Assert.EndsWith("song_20261003_120000_1.dcproj.zip", second, StringComparison.Ordinal);
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
        public List<ProjectVolumeFields> WrittenVolumeFields { get; } = [];
        public Exception? FailNext { get; set; }
        public ManualResetEventSlim? WriteRelease { get; set; }

        /// <summary>Makes the zip step fail AFTER the archive file exists: the .dcproj it zips vanishes.</summary>
        public bool VanishAfterWrite { get; set; }

        public ChartProject LoadChart(string filePath) => new();

        public void SaveChart(string filePath, ChartProject project, double? audioLengthMs = null)
        {
            if (WriteRelease is { } release) BoundedWait.For(release, "the auto-backup writer release");
            Saved.Add((filePath, project));
            WrittenVolumeFields.Add(ProjectVolumeFields.From(project.Chart));
            if (FailNext is { } failure)
            {
                FailNext = null;
                throw failure;
            }

            File.WriteAllText(filePath, "chart");
            if (VanishAfterWrite) File.Delete(filePath);
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
