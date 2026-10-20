using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Import.Eof;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Parsing.Eof;
using DotCharter.Services.Session;
using DotCharter.Tests.Services.Parsing.Eof;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.Services.Import;

/// <summary>
///     The half of an Editor on Fire import that runs after its dialog closed.
///     Everything here used to run on the UI thread with no busy state, and the
///     completion line said "created project" even when the audio half had
///     failed — a project that could not play a note read as a finished import.
/// </summary>
public sealed class EofProjectCreatorTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dotcharter-eofcreator-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    /// <summary>A REAL .eof, exported from the representative chart the EOF round-trip parity suite uses.</summary>
    internal static string WriteFixtureEofFile(string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "fixture.eof");
        File.WriteAllBytes(path, EofExporter.Export(EofExportTestCharts.BuildRepresentativeProject()).Bytes);
        return path;
    }

    internal static void WriteCompanionAudio(string folder) =>
        File.WriteAllBytes(Path.Combine(folder, "song.ogg"), [0x4F, 0x67, 0x67, 0x53, 0, 0, 0, 0]);

    private static EofImportSelection Parse(string eofPath)
    {
        var importer = new EofImporter();
        var ids = importer.GetAvailableTracks(eofPath).Select(t => t.Id).ToList();
        var result = importer.ImportTracksWithMetadata(eofPath, ids);
        var audio = EofProjectCreator.FindCompanionAudio(new ExportTargetWriter(), eofPath, result.Metadata);
        return new EofImportSelection(eofPath, result, audio);
    }

    [Fact]
    public async Task When_the_audio_fails_to_decode_it_does_not_claim_a_plain_success()
    {
        var h = new CreatorHarness();
        var songDir = Path.Combine(_tempDir, "brokenaudio");
        var path = WriteFixtureEofFile(songDir);
        WriteCompanionAudio(songDir);
        h.Storage.NextFolder = Directory.CreateDirectory(Path.Combine(_tempDir, "out")).FullName;
        h.Session.AudioLoadFailure = new InvalidDataException("not a decodable stream");

        await h.Creator.CreateAsync(Parse(path));

        Assert.NotEmpty(h.Documents.SavedPaths);
        var message = h.State.MainDisplayMessage;
        Assert.Contains("not a decodable stream", message);
        Assert.NotEqual(FakeLocalization.English().Format("Status_CreatedProject",
            h.State.ActiveProject?.Chart.SongName ?? "", h.State.ActiveProject?.Chart.Artist ?? ""), message);
    }

    /// <summary>New Project, Songsterr and this importer ask the ONE folder question, and start where the last project went.</summary>
    [Fact]
    public async Task The_project_folder_picker_starts_where_the_last_project_went()
    {
        var h = new CreatorHarness();
        var remembered = Directory.CreateDirectory(Path.Combine(_tempDir, "charts")).FullName;
        h.Config.Update(c => c.LastProjectFolderDir = remembered);
        var songDir = Path.Combine(_tempDir, "startdir");
        var path = WriteFixtureEofFile(songDir);
        WriteCompanionAudio(songDir);
        h.Storage.NextFolder = Directory.CreateDirectory(Path.Combine(_tempDir, "out-startdir")).FullName;

        await h.Creator.CreateAsync(Parse(path));

        Assert.Contains(remembered, h.Storage.RequestedStartDirectories);
    }

    [Fact]
    public async Task The_folder_the_user_picked_becomes_the_next_start_directory()
    {
        var h = new CreatorHarness();
        var songDir = Path.Combine(_tempDir, "remember");
        var path = WriteFixtureEofFile(songDir);
        WriteCompanionAudio(songDir);
        var chosen = Directory.CreateDirectory(Path.Combine(_tempDir, "out-remember")).FullName;
        h.Storage.NextFolder = chosen;

        await h.Creator.CreateAsync(Parse(path));

        Assert.Equal(chosen, h.Config.LoadAppConfig().LastProjectFolderDir);
    }

    /// <summary>The happy path still reports the plain success line, and the saved project is the one adopted.</summary>
    [Fact]
    public async Task When_the_audio_loads_it_reports_the_plain_success_line()
    {
        var h = new CreatorHarness();
        var songDir = Path.Combine(_tempDir, "goodaudio");
        var path = WriteFixtureEofFile(songDir);
        WriteCompanionAudio(songDir);
        h.Storage.NextFolder = Directory.CreateDirectory(Path.Combine(_tempDir, "out-good")).FullName;

        await h.Creator.CreateAsync(Parse(path));

        Assert.Single(h.Session.LoadedAudioFiles);
        Assert.StartsWith("Created project", h.State.MainDisplayMessage);
        Assert.NotNull(h.State.ActiveArrangement);
        Assert.EndsWith("project.rscp", h.State.CurrentProjectPath);
    }

    /// <summary>The import replaces the open project, so a kept unsaved project stops it before anything is written.</summary>
    [Fact]
    public async Task Keeping_the_unsaved_project_writes_nothing()
    {
        var h = new CreatorHarness();
        h.Session.MayReplace = false;
        var songDir = Path.Combine(_tempDir, "kept");
        var path = WriteFixtureEofFile(songDir);
        WriteCompanionAudio(songDir);
        h.Storage.NextFolder = Directory.CreateDirectory(Path.Combine(_tempDir, "out-kept")).FullName;

        await h.Creator.CreateAsync(Parse(path));

        Assert.Empty(h.Documents.SavedPaths);
        Assert.Empty(h.Storage.RequestedTitles);
    }
}

/// <summary>A real chart editor and transport over fakes for everything that touches disk or audio.</summary>
internal sealed class CreatorHarness
{
    public CreatorHarness()
    {
        var logger = new FakeAppLogger();
        Transport = new TransportViewModel(new FakeAudioPlaybackService(), State, Config, FakeLocalization.KeysOnly());
        var selection = new SelectionService();
        Editor = new ChartEditorViewModel(State, Transport, new HistoryService(logger), selection,
            new TempoService(logger), new TrackContextRegistry(State, selection), FakeLocalization.KeysOnly());
        Creator = new EofProjectCreator(Editor, Transport, State, Session, Storage, Config, Documents,
            new ExportTargetWriter(), logger,
            FakeLocalization.English());
    }

    public ProjectState State { get; } = new();
    public FakeConfigurationService Config { get; } = new();
    public FakeFileStorageService Storage { get; } = new();
    public RecordingDocuments Documents { get; } = new();
    public FakeSession Session { get; } = new();
    public TransportViewModel Transport { get; }
    public ChartEditorViewModel Editor { get; }
    public EofProjectCreator Creator { get; }

    internal sealed class RecordingDocuments : IProjectDocumentService
    {
        public List<string> SavedPaths { get; } = [];

        public ChartProject LoadChart(string filePath) => throw new NotSupportedException();

        public void SaveChart(string filePath, ChartProject project, double? audioLengthMs = null) =>
            SavedPaths.Add(filePath);

        public void RefreshArrangementFileManifest(ChartProject project) { }

        public void SaveDlcBuilderXmlBundle(string projectPath, ChartProject project, double? audioLengthMs = null) { }

        public ChartProject AssembleProject(ChartProject? baseProject, double bpm, string audioFileName,
            IEnumerable<LyricPhrase> lyrics) => baseProject ?? new ChartProject();

        public ChartProject CreateNewProject(string title, string artist, string audioFileName) =>
            new() { Chart = new ChartData { SongName = title, Artist = artist } };
    }

    internal sealed class FakeSession : IProjectSessionService
    {
        /// <summary>Makes <see cref="LoadAudioFile" /> throw — the "the chart landed but the song is silent" half.</summary>
        public Exception? AudioLoadFailure { get; set; }

        public bool MayReplace { get; set; } = true;

        public List<string> LoadedAudioFiles { get; } = [];

        public AppConfig AppConfig { get; } = new();
        public void Initialize() { }
        public void LoadAppConfig() { }
        public void SaveAppConfig() { }
        public void FreeAudio() { }
        public Task LoadFile(string filePath) => Task.CompletedTask;

        public Task LoadAudioFile(string filePath, bool generateBeats = true, bool preferMemoryPlayback = false)
        {
            if (AudioLoadFailure != null) throw AudioLoadFailure;
            LoadedAudioFiles.Add(filePath);
            return Task.CompletedTask;
        }

        public Task<bool> AttachAudioAsync(string sourcePath) => Task.FromResult(true);
        public Task LoadSilentTransportAsync(double projectLengthMs) => Task.CompletedTask;
        public Task OpenProject() => Task.CompletedTask;
        public Task<bool> SaveProjectAsync() => Task.FromResult(true);
        public Task<bool> SaveProjectAsAsync() => Task.FromResult(true);
        public IReadOnlyList<RecentProjectEntry> GetRecentProjects() => [];
        public event Action? RecentProjectsChanged { add { } remove { } }
        public bool IsProjectDirty => false;
        public Func<Task<bool>>? ConfirmDiscardAsync { get; set; }
        public Task<bool> MayReplaceOpenProjectAsync() => Task.FromResult(MayReplace);
        public void MarkProjectClean() { }
        public void MarkProjectDirty() { }
        public event Action? ProjectDirtyChanged { add { } remove { } }
        public void Dispose() { }
    }
}
