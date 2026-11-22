using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Export;
using DotCharter.Services.Import.Audio;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Infrastructure.Recovery;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Session;

namespace DotCharter.Tests.Services.Session;

/// <summary>
///     Regression cover for "a project created without audio can never get any".
///     Three stacked defects made that true:
///     <list type="number">
///         <item>no menu command existed (see <c>PowerToolReachabilityTests</c>),</item>
///         <item>the drop router accepted only <c>.wav</c>/<c>.ogg</c>, so an
///               <c>.mp3</c> fell through to the PROJECT loader and surfaced as
///               "error loading project",</item>
///         <item>even the accepted path only auditioned the file — it never wrote
///               <c>ChartProject.AudioFileName</c>, so the save wrote the
///               <c>song.wav</c> fallback and reopening found nothing.</item>
///     </list>
/// </summary>
public sealed partial class ProjectSessionAttachAudioTests : IDisposable
{
    private readonly string _tempDir =
        Directory.CreateTempSubdirectory("dotcharter-attachaudio-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    // ---- harness --------------------------------------------------------------

    private sealed class RecordingDocumentService : IProjectDocumentService
    {
        public List<string> LoadChartCalls { get; } = [];

        public Func<string, ChartProject>? Load { get; set; }
        public string? SavedAudioReference { get; private set; }

        public ChartProject LoadChart(string filePath)
        {
            // Mirrors the real ResolveFormat: anything that is not a known project
            // format throws, and ProjectSessionService.LoadFile swallows it into
            // "error loading project".
            LoadChartCalls.Add(filePath);
            if (Load != null) return Load(filePath);
            throw new NotSupportedException($"Unsupported project format: {filePath}");
        }

        public void SaveChart(string filePath, ChartProject project, double? audioLengthMs = null)
        {
            SavedAudioReference = project.AudioFileName;
            new DotCharter.Services.Parsing.Rscp.RscpChartSerializer().Save(filePath, project);
        }
        public void RefreshArrangementFileManifest(ChartProject project) { }

        public void SaveDlcBuilderXmlBundle(string projectPath, ChartProject project,
            double? audioLengthMs = null)
        { }

        public ChartProject AssembleProject(ChartProject? baseProject, double bpm, string audioFileName,
            IEnumerable<LyricPhrase> lyrics) => baseProject ?? new ChartProject();

        public ChartProject CreateNewProject(string title, string artist, string audioFileName)
            => new() { AudioFileName = audioFileName };
    }

    private sealed class NoOpCrashRecoveryService : ICrashRecoveryService
    {
        /// <summary>No store behind this fake, so nothing counts as an internal
        /// snapshot. Settable for the test that needs a real recovery root.</summary>
        public string SnapshotRootDirectory { get; set; } = string.Empty;

        public void Start() { }
        public IReadOnlyList<RecoverableSession> FindRecoverableSessions() => [];
        public void DiscardSession(RecoverableSession session) { }
        public void NotifyProjectSaved(string projectPath) { }
        public void NotifyProjectSwitched() { }
        public void NotifyCleanShutdown() { }
        public Func<bool>? HasUnsavedChanges { get; set; }
        public void NotifyProjectChanged() { }
        public void Dispose() { }
    }

    private sealed class NoOpSaveCompanionService : ISaveCompanionService
    {
        public string? WriteEof(string projectPath, ChartProject snapshot) => null;

        public string? WriteDlcBuilderProject(
            string projectPath, ChartProject snapshot, double? audioLengthMs) => null;

        public Task<string?> BuildPsarcAsync(string projectPath, ChartProject snapshot,
            double? audioLengthMs, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class Harness
    {
        /// <summary>The project is applied BEFORE the session is built so the dirty
        /// tracker baselines on the loaded state — exactly the real startup order.
        /// Baselining after an ApplyProject would read dirty from the word go.</summary>
        public Harness(ChartProject project, string projectPath, DotCharter.Services.Diagnostics.IMessagePrompt? prompt = null,
            DotCharter.Tone.Diagnostics.IUiDispatcher? uiDispatcher = null, FakeAudioPlaybackService? audio = null,
            IProjectDocumentService? documents = null)
        {
            Audio = audio ?? Audio;
            var logger = Logger;
            Transport = new TransportViewModel(Audio, State, Config, FakeLocalization.KeysOnly());
            var selection = new SelectionService();
            History = new HistoryService(logger);
            Editor = new ChartEditorViewModel(
                State, Transport, History, selection,
                new TempoService(logger), new TrackContextRegistry(State, selection), FakeLocalization.KeysOnly());

            Editor.ApplyProject(project, resetHistory: true);
            State.CurrentProjectPath = projectPath;
            State.CurrentAudioFileName = project.AudioFileName;

            Session = new ProjectSessionService(
                Audio, Config, logger, Storage,
                documents ?? Documents, State, Transport, Editor, new TempoService(logger),
                new NoOpCrashRecoveryService(), History, new NoOpSaveCompanionService(),
                uiDispatcher: uiDispatcher ?? new TestUiDispatcher(),
                localization: FakeLocalization.English(),
                pathRekey: new FakeProjectPathRekey(), messageDialog: prompt,
                silentAudio: new DotCharter.Services.Audio.SilentTransport.SilentAudioTrack(logger,
                    Path.Combine(Path.GetDirectoryName(projectPath) ?? Path.GetTempPath(), "silent-cache")));
        }

        public FakeAppLogger Logger { get; } = new();
        public ProjectState State { get; } = new();
        public FakeAudioPlaybackService Audio { get; } = new()
        {
            TotalAudioLengthMs = 1000,
            WaveformResult = DotCharter.Tone.Audio.Abstractions.WaveformSamples.FromBasePeaks([0.5f], 2)
        };
        public FakeFileStorageService Storage { get; } = new();
        public FakeConfigurationService Config { get; } = new();
        public RecordingDocumentService Documents { get; } = new();
        public HistoryService History { get; }
        public TransportViewModel Transport { get; }
        public ChartEditorViewModel Editor { get; }
        public ProjectSessionService Session { get; }
    }

    /// <summary>A project already laid out on disk, exactly like the audio-less
    /// "start without audio" new-project flow leaves things.</summary>
    private (Harness Harness, string ProjectDir) CreateAudiolessProject(string folderName = "Band - Song", FakeAudioPlaybackService? audio = null)
    {
        var projectDir = Path.Combine(_tempDir, folderName);
        Directory.CreateDirectory(projectDir);

        var project = new ChartProject { AudioFileName = "" };
        project.Arrangements.Add(new Arrangement { Name = "Lead Guitar" });
        return (new Harness(project, Path.Combine(projectDir, "project.rscp"), audio: audio), projectDir);
    }

    private string CreateSourceAudio(string fileName)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllBytes(path, [0x49, 0x44, 0x33, 1, 2, 3, 4]);
        return path;
    }

    // ---- the attach itself ----------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_silently_refused_decode_never_publishes_the_replacement(bool waveformRejected)
    {
        var (h, folder) = CreateAudiolessProject();
        var previous = Path.Combine(folder, "song.ogg");
        File.WriteAllText(previous, "previous audio");
        h.State.ActiveProject!.AudioFileName = "song.ogg";
        h.State.CurrentAudioFileName = "song.ogg";
        h.Audio.TotalAudioLengthMs = 0;
        if (waveformRejected) h.Audio.WaveformResult = DotCharter.Tone.Audio.Abstractions.WaveformSamples.Empty;

        Assert.False(await h.Session.AttachAudioAsync(CreateSourceAudio("corrupt.ogg")));

        Assert.Equal("previous audio", File.ReadAllText(previous));
        Assert.Equal("song.ogg", h.State.ActiveProject.AudioFileName);
        Assert.False(h.Session.IsProjectDirty);
        Assert.Equal(waveformRejected ? 0 : 1, h.Audio.MemoryLoads.Count);
        Assert.Equal([previous], Directory.GetFiles(folder));
    }

    [Fact]
    public async Task An_abandoned_waveform_pass_never_replaces_the_new_sessions_audio()
    {
        var (h, folder) = CreateAudiolessProject();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Audio.WaveformPass = async () =>
        {
            entered.SetResult();
            await release.Task;
        };
        var attachment = h.Session.AttachAudioAsync(CreateSourceAudio("replacement.ogg"));
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var replacement = new ChartProject { AudioFileName = "B.wav" };
        h.Editor.ApplyProject(replacement, resetHistory: true);
        h.State.CurrentAudioFileName = "B.wav";
        h.Audio.WaveformPass = null;
        var otherAudio = CreateSourceAudio("B.wav");
        await h.Session.LoadAudioFile(otherAudio, false);
        var waveform = h.State.WaveformData;
        release.SetResult();

        Assert.False(await attachment);
        Assert.Same(replacement, h.State.ActiveProject);
        Assert.Equal("B.wav", h.State.CurrentAudioFileName);
        Assert.Equal([otherAudio], h.Audio.LoadedPaths);
        Assert.Empty(h.Audio.MemoryLoads);
        Assert.Same(waveform, h.State.WaveformData);
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task A_new_load_wins_even_when_the_old_attachment_is_already_inside_the_engine()
    {
        var (h, _) = CreateAudiolessProject();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Audio.MemoryLoadPass = async () =>
        {
            entered.SetResult();
            await release.Task;
        };
        var attachment = h.Session.AttachAudioAsync(CreateSourceAudio("replacement.ogg"));
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        h.Editor.ApplyProject(new ChartProject { AudioFileName = "B.wav" }, resetHistory: true);
        var otherAudio = CreateSourceAudio("B.wav");
        var nextLoad = h.Session.LoadAudioFile(otherAudio, false);
        release.SetResult();

        Assert.False(await attachment);
        await nextLoad;
        Assert.Equal(otherAudio, h.Audio.CompletedLoadPath);
    }

    [Fact]
    public async Task A_failed_replacement_decode_preserves_the_existing_audio_and_references()
    {
        var (h, projectDir) = CreateAudiolessProject();
        var previous = Path.Combine(projectDir, "song.ogg");
        File.WriteAllText(previous, "previous audio");
        h.State.CurrentAudioFileName = "song.ogg";
        h.State.ActiveProject!.AudioFileName = "song.ogg";
        h.Audio.LoadAudioFailure = new InvalidDataException("corrupt audio");
        h.Audio.MemoryLoadFailure = h.Audio.LoadAudioFailure;

        Assert.False(await h.Session.AttachAudioAsync(CreateSourceAudio("corrupt.ogg")));

        Assert.Equal("previous audio", File.ReadAllText(previous));
        Assert.Equal("song.ogg", h.State.CurrentAudioFileName);
        Assert.Equal("song.ogg", h.State.ActiveProject.AudioFileName);
        Assert.False(h.Session.IsProjectDirty);
        Assert.Equal([previous], Directory.GetFiles(projectDir));
    }

    [Fact]
    public async Task Replacement_decodes_before_the_existing_file_is_published()
    {
        var (h, projectDir) = CreateAudiolessProject();
        var previous = Path.Combine(projectDir, "song.ogg");
        File.WriteAllText(previous, "previous audio");
        var source = CreateSourceAudio("replacement.ogg");
        h.Audio.DuringMemoryLoad = () => Assert.Equal("previous audio", File.ReadAllText(previous));

        Assert.True(await h.Session.AttachAudioAsync(source));

        Assert.Single(h.Audio.MemoryLoads);
        Assert.Equal(previous, Assert.Single(h.Audio.MemoryLoadDisplayPaths));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(previous));
        Assert.Equal([previous], Directory.GetFiles(projectDir));
    }

    [Theory]
    [InlineData("A.wav")]
    [InlineData("")]
    [InlineData("missing.wav")]
    public async Task ReloadProjectAudio_replaces_playback_and_waveform_with_the_active_projects_media(string reference)
    {
        var (h, projectDir) = CreateAudiolessProject();
        h.Audio.WaveformResult = DotCharter.Tone.Audio.Abstractions.WaveformSamples.Empty;
        h.Audio.TotalAudioLengthMs = 0;
        var audio = Path.Combine(projectDir, "A.wav");
        File.WriteAllText(audio, "audio");
        h.State.ActiveProject!.AudioFileName = reference;
        h.State.CurrentAudioFileName = "B.wav";
        h.State.TotalAudioLengthMs = 1234;

        var reload = typeof(ProjectSessionService).GetMethod("ReloadProjectAudioAsync");
        Assert.NotNull(reload);
        await Assert.IsAssignableFrom<Task>(reload.Invoke(h.Session, null));

        Assert.Equal(reference, h.State.CurrentAudioFileName);
        Assert.Same(DotCharter.Tone.Audio.Abstractions.WaveformSamples.Empty, h.State.WaveformData);
        Assert.Equal(0, h.State.TotalAudioLengthMs);
        Assert.Equal(reference == "A.wav" ? new[] { audio } : [], h.Audio.LoadedPaths);
        Assert.False(h.Session.IsProjectDirty);
    }

    [Theory]
    [InlineData("source.mp3", "song.mp3")]
    [InlineData("source.flac", "song.flac")]
    [InlineData("source.ogg", "song.ogg")]
    [InlineData("source.m4a", "song.m4a")]
    public async Task AttachAudioAsync_CopiesIntoTheProjectFolder_AndWritesBothAudioReferences(
        string sourceName, string expectedWorkingName)
    {
        var (h, projectDir) = CreateAudiolessProject();
        var source = CreateSourceAudio(sourceName);

        var attached = await h.Session.AttachAudioAsync(source);

        Assert.True(attached);
        // Copied in, next to the project — not merely auditioned from wherever it sat.
        Assert.True(File.Exists(Path.Combine(projectDir, expectedWorkingName)));
        // BOTH references. The session's alone is what the old code left behind,
        // and the save path reads the project's.
        Assert.Equal(expectedWorkingName, h.State.CurrentAudioFileName);
        Assert.Equal(expectedWorkingName, h.State.ActiveProject!.AudioFileName);
    }

    [Fact]
    public async Task AttachAudioAsync_MarksTheProjectDirty_SoTheReferenceCanBeSaved()
    {
        var (h, _) = CreateAudiolessProject();
        Assert.False(h.Session.IsProjectDirty);

        await h.Session.AttachAudioAsync(CreateSourceAudio("source.mp3"));

        Assert.True(h.Session.IsProjectDirty);
    }

    /// <summary>
    ///     An attach never bakes leading silence (unlike NEW-project creation): the
    ///     chart already has beats placed against a time base, and shifting the audio
    ///     under them would desync every note.
    /// </summary>
    [Fact]
    public async Task AttachAudioAsync_DoesNotShiftTheChart()
    {
        var (h, _) = CreateAudiolessProject();

        await h.Session.AttachAudioAsync(CreateSourceAudio("source.mp3"));

        Assert.Equal(0.0, h.State.ActiveProject!.Chart.MainAudioOffsetAdded);
    }

    /// <summary>
    ///     A session that has never been saved has no folder to copy into. Rather
    ///     than forcing a Save As mid-attach, the absolute source path is stored —
    ///     both audio resolvers already accept a rooted AudioFileName.
    /// </summary>
    [Fact]
    public async Task AttachAudioAsync_WithoutAProjectFolder_StoresTheAbsolutePath()
    {
        var h = new Harness(new ChartProject { AudioFileName = "" }, projectPath: "");
        var source = CreateSourceAudio("unsaved.mp3");

        var attached = await h.Session.AttachAudioAsync(source);

        Assert.True(attached);
        Assert.Equal(Path.GetFullPath(source), h.State.CurrentAudioFileName);
        Assert.Equal(Path.GetFullPath(source), h.State.ActiveProject!.AudioFileName);
    }

    [Fact]
    public async Task AttachAudioAsync_AlreadyInTheProjectFolder_ReferencesItInPlace()
    {
        var (h, projectDir) = CreateAudiolessProject();
        var inPlace = Path.Combine(projectDir, "already-here.ogg");
        File.WriteAllBytes(inPlace, [1, 2, 3]);

        var attached = await h.Session.AttachAudioAsync(inPlace);

        Assert.True(attached);
        // Kept its own name; no "song.ogg" copy-onto-itself.
        Assert.Equal("already-here.ogg", h.State.CurrentAudioFileName);
        Assert.False(File.Exists(Path.Combine(projectDir, "song.ogg")));
    }

    [Fact]
    public async Task AttachAudioAsync_MissingFile_IsRejectedWithoutTouchingTheReference()
    {
        var (h, _) = CreateAudiolessProject();

        var attached = await h.Session.AttachAudioAsync(Path.Combine(_tempDir, "nope.mp3"));

        Assert.False(attached);
        Assert.Equal("", h.State.CurrentAudioFileName);
    }

    // ---- the drop router ------------------------------------------------------

    /// <summary>
    ///     The reported bug: dropping an .mp3 hit the PROJECT loader, whose
    ///     ResolveFormat throws NotSupportedException, and the blanket catch reported
    ///     it as "error loading project" — a corrupt-file message for a good song.
    /// </summary>
    [Theory]
    [InlineData("dropped.mp3")]
    [InlineData("dropped.flac")]
    [InlineData("dropped.m4a")]
    [InlineData("dropped.aac")]
    [InlineData("dropped.wav")]
    [InlineData("dropped.ogg")]
    public async Task LoadFile_WithAudio_Attaches_AndNeverReachesTheProjectLoader(string fileName)
    {
        var (h, _) = CreateAudiolessProject();
        var source = CreateSourceAudio(fileName);

        await h.Session.LoadFile(source);

        Assert.Empty(h.Documents.LoadChartCalls);
        Assert.Equal("song" + Path.GetExtension(fileName), h.State.CurrentAudioFileName);
    }

    [Fact]
    public async Task LoadFile_WithANonAudioFile_StillRoutesToTheProjectLoader()
    {
        var (h, _) = CreateAudiolessProject();
        var chart = Path.Combine(_tempDir, "chart.rscp");
        File.WriteAllText(chart, "<project />");

        await h.Session.LoadFile(chart);

        Assert.Equal(chart, Assert.Single(h.Documents.LoadChartCalls));
    }

    // ---- the shared extension list -------------------------------------------

    [Theory]
    [InlineData("song.wav")]
    [InlineData("song.ogg")]
    [InlineData("song.mp3")]
    [InlineData("song.flac")]
    [InlineData("song.m4a")]
    [InlineData("song.aac")]
    [InlineData("SONG.MP3")]
    [InlineData(@"C:\music\Some Band - Some Song.Flac")]
    public void AudioFileTypes_recognizes_every_format_the_engine_decodes(string path)
        => Assert.True(AudioFileTypes.IsAudioFile(path));

    [Theory]
    [InlineData("project.rscp")]
    [InlineData("chart.xml")]
    [InlineData("tab.gp5")]
    [InlineData("notes.eof")]
    [InlineData("noextension")]
    [InlineData("")]
    [InlineData(null)]
    public void AudioFileTypes_rejects_everything_else(string? path)
        => Assert.False(AudioFileTypes.IsAudioFile(path));

    [Fact]
    public void AudioFileTypes_pickerPatterns_match_the_extension_list()
    {
        Assert.Equal(
            AudioFileTypes.Extensions.Select(e => "*" + e),
            AudioFileTypes.PickerPatterns);
    }
}
