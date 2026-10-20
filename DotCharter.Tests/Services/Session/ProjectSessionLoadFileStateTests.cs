using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Import.Audio;
using DotCharter.Services.Export;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Infrastructure.Recovery;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Session;

namespace DotCharter.Tests.Services.Session;

/// <summary>
///     What <c>ProjectSessionService.LoadFile</c> owes the session state it
///     REPLACES. Opening a project swaps the chart, and every piece of
///     project-scoped state beside it has to follow — a field the new project
///     does not set must be CLEARED, never inherited from the previous one.
///     <para>
///         The audio reference is the one that reached disk.
///         <c>CurrentAudioFileName</c> was assigned only inside
///         <c>if (!string.IsNullOrEmpty(project.AudioFileName))</c> — the lone
///         replace path that made it conditional — while
///         <c>ProjectDocumentService.AssembleProject</c> writes it into
///         <c>project.AudioFileName</c> UNCONDITIONALLY and is the entry point for
///         the manual save, the auto-backup, the crash snapshot, checkpoint
///         create, the Rocksmith export and the diagnostics bundle. So opening a
///         song with audio and then an audio-less chart (an RS1 chart-only
///         import) made the first save stamp the PREVIOUS song's
///         <c>musicFileName</c> into it, and <c>AttachAudioAsync</c> read the same
///         stale value and asked "replace the song audio?" for a project that had
///         none.
///     </para>
/// </summary>
public sealed class ProjectSessionLoadFileStateTests : IDisposable
{
    private readonly string _tempDir =
        Directory.CreateTempSubdirectory("dotcharter-loadfilestate-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    // ---- harness --------------------------------------------------------------

    /// <summary>Hands back a prepared project instead of parsing one, so the test
    /// is about what LoadFile does with it and not about the serializer.</summary>
    private sealed class StubDocumentService(ChartProject toLoad, bool throwOnLoad = false)
        : IProjectDocumentService
    {
        public ChartProject LoadChart(string filePath)
            => throwOnLoad ? throw new InvalidDataException("unreadable chart") : toLoad;

        public void SaveChart(string filePath, ChartProject project, double? audioLengthMs = null) { }

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

    /// <summary>
    ///     The PREVIOUS session: a library-style project with audio, open and
    ///     clean, exactly as a normal open leaves things. The project the next
    ///     <c>LoadFile</c> will hand back is fixed up front.
    /// </summary>
    private sealed class Harness
    {
        public Harness(ChartProject nextProject, bool throwOnLoad = false,
            string recoveryRoot = "")
        {
            var logger = new FakeAppLogger();
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), State, Config, FakeLocalization.KeysOnly());
            var selection = new SelectionService();
            var history = new HistoryService(logger);
            var editor = new ChartEditorViewModel(
                State, transport, history, selection,
                new TempoService(logger), new TrackContextRegistry(State, selection), FakeLocalization.KeysOnly());

            var previous = new ChartProject { AudioFileName = "previous-song.wav" };
            previous.Arrangements.Add(new Arrangement { Name = "Lead Guitar" });
            editor.ApplyProject(previous, resetHistory: true);
            State.CurrentProjectPath = Path.Combine(Path.GetTempPath(), "Previous", "project.rscp");
            State.CurrentAudioFileName = previous.AudioFileName;
            // What SongBrowserViewModel.LoadSongItem leaves behind after a library
            // song — the only writer of this field.
            State.CurrentSongIdentity = "lib:PreviousSongKey";

            Session = new ProjectSessionService(
                new FakeAudioPlaybackService(), Config, logger, Storage,
                new StubDocumentService(nextProject, throwOnLoad), State, transport, editor,
                new TempoService(logger),
                new NoOpCrashRecoveryService { SnapshotRootDirectory = recoveryRoot }, history,
                new NoOpSaveCompanionService(), statusBar: StatusBar,
                uiDispatcher: new TestUiDispatcher(),
                localization: FakeLocalization.English());
        }

        public ProjectState State { get; } = new();
        public FakeConfigurationService Config { get; } = new();
        public RecordingStatusBarService StatusBar { get; } = new();
        public FakeFileStorageService Storage { get; } = new();
        public ProjectSessionService Session { get; }
    }

    /// <summary>An empty chart: <c>SilentAudioTrack</c> derives no content length
    /// from it, so the audio-less arm frees the engine instead of writing a silent
    /// WAV — nothing about the audio REFERENCE depends on that difference.</summary>
    private static ChartProject ProjectWith(string audioFileName)
    {
        var project = new ChartProject { AudioFileName = audioFileName };
        project.Arrangements.Add(new Arrangement { Name = "Lead Guitar" });
        project.Chart = new ChartData { SongName = "Next Song" };
        return project;
    }

    private string NewProjectPath(string folderName)
    {
        var dir = Path.Combine(_tempDir, folderName);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "project.rscp");
        File.WriteAllText(path, "");
        return path;
    }

    // ---- the bug --------------------------------------------------------------

    /// <summary>THE regression: an audio-less project must not inherit the
    /// previous song's audio reference, because <c>AssembleProject</c> would then
    /// write it to disk on the very next save.</summary>
    [Fact]
    public async Task LoadFile_OnAnAudiolessProject_ClearsThePreviousSongsAudioReference()
    {
        var h = new Harness(ProjectWith(""));
        Assert.Equal("previous-song.wav", h.State.CurrentAudioFileName);

        await h.Session.LoadFile(NewProjectPath("Chart Only"));

        Assert.Equal("", h.State.CurrentAudioFileName);
    }

    /// <summary>The same clear must not be an over-correction: a project that DOES
    /// declare audio keeps its own name.</summary>
    [Fact]
    public async Task LoadFile_OnAProjectWithAudio_AdoptsItsOwnAudioReference()
    {
        var h = new Harness(ProjectWith("next-song.ogg"));

        await h.Session.LoadFile(NewProjectPath("With Audio"));

        Assert.Equal("next-song.ogg", h.State.CurrentAudioFileName);
    }

    /// <summary>A project whose declared audio is MISSING on disk still keeps its
    /// own declaration — the silent-transport fallback is an engine-only
    /// substitution and never the audio identity.</summary>
    [Fact]
    public async Task LoadFile_OnAProjectWhoseAudioIsMissing_KeepsItsOwnDeclaration()
    {
        var h = new Harness(ProjectWith("gone.wav"));

        await h.Session.LoadFile(NewProjectPath("Missing Audio"));

        Assert.Equal("gone.wav", h.State.CurrentAudioFileName);
    }

    // ---- the Open picker ------------------------------------------------------

    /// <summary>
    ///     File ▸ Open was the ONE picker in the app that passed no filters at
    ///     all — every other one names its types — so the dialog the user opens
    ///     most often was an unfiltered folder listing to hunt a `.rscp` in.
    ///     <para>
    ///         The FIRST filter has to span both kinds, because that is the one
    ///         the dialog opens on and this router accepts both: audio attaches to
    ///         the open project, or auditions without one. A `.rscp`-only default
    ///         would hide every song file from a command whose own title is "Open
    ///         Project or Audio".
    ///     </para>
    /// </summary>
    [Fact]
    public async Task OpenProject_OffersAFilterThatSpansBothKindsItAccepts()
    {
        var h = new Harness(ProjectWith(""));
        h.Storage.NextOpenFile = null; // cancel at the picker; this is about the offer

        await h.Session.OpenProject();

        var filters = Assert.Single(h.Storage.RequestedFilters);
        Assert.NotNull(filters);

        var first = filters[0].Patterns;
        Assert.Contains("*.rscp", first);
        Assert.All(AudioFileTypes.PickerPatterns, p => Assert.Contains(p, first));

        // …and the narrower ones stay available, All Files included, so nothing
        // the router can take is hidden behind the default.
        Assert.Contains(filters, f => f.Patterns.Contains("*.*"));
    }

    // ---- what the user is TOLD ------------------------------------------------

    /// <summary>
    ///     …and the same project has to SAY so. The silent-transport fallback
    ///     left the rail reading "Loaded Chart", so a chart whose song file had
    ///     moved opened looking perfectly normal and played silence, with the
    ///     only record of why in a log file nobody has open. The message names
    ///     the missing file AND the menu route that repairs it — File ▸ Attach
    ///     audio…, which re-points the project's reference — because an error
    ///     with no next action is where the flow ends.
    /// </summary>
    [Fact]
    public async Task LoadFile_WhenTheDeclaredAudioIsMissing_SaysSoAndNamesTheFix()
    {
        var h = new Harness(ProjectWith("gone.wav"));

        await h.Session.LoadFile(NewProjectPath("Missing Audio Message"));

        Assert.Equal(
            FakeLocalization.English().Format("Status_ProjectAudioMissingOnOpen", "gone.wav"),
            h.State.MainDisplayMessage);
        Assert.Equal(StatusSeverity.Warning, h.State.MainDisplaySeverity);
    }

    /// <summary>
    ///     The discriminator that keeps the guard above from being "always warn":
    ///     a chart that declares NO audio is a legitimate state (an RS1 chart-only
    ///     import), not a missing file, and must still report the ordinary load.
    ///     Break the branch to warn unconditionally and this fails.
    /// </summary>
    [Fact]
    public async Task LoadFile_OnAnAudiolessProject_ReportsAnOrdinaryLoad()
    {
        var h = new Harness(ProjectWith(""));

        var path = NewProjectPath("No Audio Declared");
        await h.Session.LoadFile(path);

        // It names the project, not its file: every save is called project.rscp, so
        // "Loaded chart: project.rscp" named nothing (seen live, 2026-09-24).
        Assert.Equal(
            FakeLocalization.English().Format("Status_LoadedChart",
                ProjectDisplayName.For(path, h.State.ActiveProject?.Chart)),
            h.State.MainDisplayMessage);
        Assert.DoesNotContain("project.rscp", h.State.MainDisplayMessage, StringComparison.Ordinal);
        Assert.Equal(StatusSeverity.Info, h.State.MainDisplaySeverity);
    }

    /// <summary>
    ///     A load is the one heavy project operation that had no progress pill at
    ///     all — the deserialize, the spacing enforce and the audio decode ran
    ///     under whatever message the PREVIOUS project had left on the rail. It is
    ///     begun before the work and completed in the <c>finally</c>, so it also
    ///     clears on the failure path.
    /// </summary>
    [Fact]
    public async Task LoadFile_BracketsTheLoadWithAProgressPill()
    {
        var h = new Harness(ProjectWith(""));

        await h.Session.LoadFile(NewProjectPath("Pill"));

        Assert.Equal(
            [$"begin:project-load:{FakeLocalization.English()["Status_LoadingProject"]}", "complete:project-load"],
            h.StatusBar.Log);
    }

    /// <summary>…and it is completed even when the load THROWS, or the rail keeps
    /// "Opening project…" forever over an editor showing the previous chart.</summary>
    [Fact]
    public async Task LoadFile_CompletesTheProgressPill_WhenTheLoadThrows()
    {
        var h = new Harness(ProjectWith(""), throwOnLoad: true);

        await h.Session.LoadFile(NewProjectPath("Throws"));

        Assert.Equal("complete:project-load", h.StatusBar.Log[^1]);
    }

    /// <summary>
    ///     The same rule for the LIBRARY SONG IDENTITY, which had exactly one
    ///     writer (<c>SongBrowserViewModel.LoadSongItem</c>) and no clearer.
    ///     <c>BuildWaveformCacheIdentity</c> reads it BEFORE the project path, so a
    ///     stale <c>psarc:</c> id made every own-chart project opened after a
    ///     library song key its waveform pyramid under that song — cache pollution
    ///     and a permanent miss for the project's own audio.
    /// </summary>
    [Fact]
    public async Task LoadFile_ClearsThePreviousLibrarySongIdentity()
    {
        var h = new Harness(ProjectWith("next-song.ogg"));
        Assert.Equal("lib:PreviousSongKey", h.State.CurrentSongIdentity);

        await h.Session.LoadFile(NewProjectPath("Own Chart"));

        Assert.Equal("", h.State.CurrentSongIdentity);
    }

    // ---- the crash-recovery copy is not a project the user opened ---------------

    /// <summary>
    ///     A crash snapshot lives in <c>ConfigRoot/recovery/</c> and is written by
    ///     this app, not by the user. It used to reach BOTH of the funnel's config
    ///     writes.
    ///     <para>
    ///         <c>LastProjectPath</c> is the worse half.
    ///         <c>RecoveryViewModel.RecoverAsync</c> repoints it at
    ///         <c>OriginalProjectPath</c> — but only AFTER its "did the load
    ///         succeed" check, so a snapshot that failed to load left the next
    ///         launch's startup reload opening the recovery file as the project,
    ///         and the first Ctrl+S wrote the user's work into the recovery folder.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task LoadFile_OfACrashSnapshot_DoesNotBecomeTheLastProjectPath()
    {
        var recoveryRoot = Path.Combine(_tempDir, "recovery");
        Directory.CreateDirectory(recoveryRoot);
        var h = new Harness(ProjectWith("next-song.ogg"), recoveryRoot: recoveryRoot);
        var realProject = Path.Combine(_tempDir, "Real Song", "project.rscp");
        h.Config.AppConfig.LastProjectPath = realProject;

        var snapshot = Path.Combine(recoveryRoot, "abc123.rscp");
        File.WriteAllText(snapshot, "");
        await h.Session.LoadFile(snapshot);

        // The load DID happen — this is not a test of a refused open.
        Assert.Equal(snapshot, h.State.CurrentProjectPath);
        Assert.Equal(realProject, h.Config.AppConfig.LastProjectPath);
    }

    /// <summary>
    ///     …and it may not appear in File ▸ Open Recent either. Nothing repointed
    ///     the MRU at all, so the recovery copy sat there wearing the song's real
    ///     name, one click from a session whose Save target is inside the app's own
    ///     config folder.
    /// </summary>
    [Fact]
    public async Task LoadFile_OfACrashSnapshot_DoesNotEnterOpenRecent()
    {
        var recoveryRoot = Path.Combine(_tempDir, "recovery");
        Directory.CreateDirectory(recoveryRoot);
        var h = new Harness(ProjectWith("next-song.ogg"), recoveryRoot: recoveryRoot);

        var snapshot = Path.Combine(recoveryRoot, "abc123.rscp");
        File.WriteAllText(snapshot, "");
        await h.Session.LoadFile(snapshot);

        Assert.Empty(h.Config.AppConfig.RecentProjects);
    }

    /// <summary>
    ///     The CONTROL, and it is the load-bearing half: an ordinary project opened
    ///     from anywhere else still records both. A guard that suppressed them for
    ///     every open would satisfy the two cases above while breaking Open Recent
    ///     outright.
    /// </summary>
    [Fact]
    public async Task LoadFile_OfAnOrdinaryProject_StillRecordsBoth()
    {
        var recoveryRoot = Path.Combine(_tempDir, "recovery");
        Directory.CreateDirectory(recoveryRoot);
        var h = new Harness(ProjectWith("next-song.ogg"), recoveryRoot: recoveryRoot);

        var path = NewProjectPath("Ordinary");
        await h.Session.LoadFile(path);

        Assert.Equal(path, h.Config.AppConfig.LastProjectPath);
        Assert.Equal(path, Assert.Single(h.Config.AppConfig.RecentProjects).Path);
    }
}
