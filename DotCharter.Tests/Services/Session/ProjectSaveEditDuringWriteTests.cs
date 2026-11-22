using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Export;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Infrastructure.Recovery;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Session;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tests.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Tone.Audio.Abstractions;

namespace DotCharter.Tests.Services.Session;

/// <summary>
///     An edit made while a save's background write is in flight is NOT in the
///     file (the file holds the snapshot taken before the write), so the save must
///     leave the project dirty and must not delete the crash-recovery snapshot that
///     holds the edit (#354). The write is held on a gate in the fake document
///     service, so the "edit during the save" window is deterministic.
/// </summary>
public sealed partial class ProjectSaveEditDuringWriteTests : IDisposable
{
    private readonly string _tempDir =
        Directory.CreateTempSubdirectory("dotcharter-saveedit-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    [Fact]
    public async Task EditDuringTheWrite_KeepsTheProjectDirty_AndKeepsTheRecoverySnapshot()
    {
        var h = new Harness(_tempDir);

        var save = h.Session.Saver.SaveProjectCoreAsync(h.ProjectPath);
        await h.Documents.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        h.History.Do(new NoOpAction());
        h.Documents.ReleaseWrite.SetResult();
        Assert.True(await save.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));

        Assert.True(h.Session.IsProjectDirty, "the edit made during the write is not in the file");
        Assert.Empty(h.Recovery.SavedNotifications);
        Assert.Equal(1, h.Recovery.ChangedNotifications);
    }

    [Fact]
    public async Task EditDuringTheWrite_UndoneBackToTheSavedState_ReadsClean()
    {
        var h = new Harness(_tempDir);

        var save = h.Session.Saver.SaveProjectCoreAsync(h.ProjectPath);
        await h.Documents.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        h.History.Do(new NoOpAction());
        h.Documents.ReleaseWrite.SetResult();
        Assert.True(await save.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));

        h.History.Undo();

        Assert.False(h.Session.IsProjectDirty);
    }

    [Fact]
    public async Task SaveWithNoEditDuringTheWrite_StillMarksClean_AndClearsTheSnapshot()
    {
        var h = new Harness(_tempDir);
        h.Documents.ReleaseWrite.SetResult();
        h.History.Do(new NoOpAction());
        Assert.True(h.Session.IsProjectDirty);

        Assert.True(await h.Session.Saver.SaveProjectCoreAsync(h.ProjectPath));

        Assert.False(h.Session.IsProjectDirty);
        Assert.Equal([h.ProjectPath], h.Recovery.SavedNotifications);
    }

    /// <summary>A second Ctrl+S during the write used to be dropped (returned false,
    /// so the close prompt's Save failed silently); it now queues ONE follow-up that
    /// writes the edit made meanwhile.</summary>
    [Fact]
    public async Task SaveRequestedDuringTheWrite_RunsAFollowUpSave_AndEndsClean()
    {
        var h = new Harness(_tempDir);

        var first = h.Session.Saver.SaveWithGuardAsync(false, SaveInitiator.User);
        await h.Documents.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        h.History.Do(new NoOpAction());
        var second = h.Session.Saver.SaveWithGuardAsync(false, SaveInitiator.User);
        var secondToo = h.Session.Saver.SaveWithGuardAsync(false, SaveInitiator.User);
        h.Documents.ReleaseWrite.SetResult();

        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
        Assert.True(await secondToo.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));

        Assert.Equal(2, h.Documents.SaveCount);
        Assert.False(h.Session.IsProjectDirty);
        Assert.Single(h.Recovery.SavedNotifications);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditDuringAudioLoad_StaysDirty_AndKeepsRecoveryArmed(bool failAudio)
    {
        var audio = new GatedAudio();
        var h = new Harness(_tempDir, audio);
        var next = new ChartProject { AudioFileName = "song.ogg" };
        h.Documents.ToLoad = next;
        var path = Path.Combine(_tempDir, "next.dcproj");
        File.WriteAllText(path, "chart");
        File.WriteAllText(Path.Combine(_tempDir, "song.ogg"), "audio");

        var load = h.Session.LoadFile(path);
        await audio.Started.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Same(next, h.State.ActiveProject);
        h.History.Do(new NoOpAction());
        var switched = h.Recovery.SwitchedNotifications;
        if (failAudio) audio.Release.SetException(new InvalidDataException("decode failed"));
        else audio.Release.SetResult();
        await load.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.True(h.Session.IsProjectDirty);
        Assert.Equal(switched, h.Recovery.SwitchedNotifications);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveAsCompletedAfterProjectReplacement_LeavesTheNewSessionAlone(bool editReplacement)
    {
        var h = new Harness(_tempDir);
        var picked = Path.Combine(_tempDir, "saved-a.dcproj");
        var save = h.Session.Saver.SaveToPickedPathAsync(picked);
        await h.Documents.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        h.Session.ConfirmDiscardAsync = () => Task.FromResult(true);
        h.Documents.ToLoad = new ChartProject();
        var nextPath = Path.Combine(_tempDir, "b.dcproj");
        File.WriteAllText(nextPath, "chart");
        await h.Session.LoadFile(nextPath);
        if (editReplacement) h.History.Do(new NoOpAction());
        var switched = h.Recovery.SwitchedNotifications;
        var changed = h.Recovery.ChangedNotifications;
        h.Documents.ReleaseWrite.SetResult();
        await save.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(picked));
        Assert.Equal(nextPath, h.State.CurrentProjectPath);
        Assert.Equal(nextPath, h.Config.AppConfig.LastProjectPath);
        Assert.DoesNotContain(h.Config.AppConfig.RecentProjects, p => p.Path == picked);
        Assert.Equal(editReplacement, h.Session.IsProjectDirty);
        Assert.Equal(switched, h.Recovery.SwitchedNotifications);
        Assert.Equal(changed, h.Recovery.ChangedNotifications);
        Assert.Empty(h.Recovery.SavedNotifications);
    }

    [Fact]
    public async Task CloseSave_WithANewerEdit_RefusesToDiscardIt()
    {
        var h = new Harness(_tempDir);
        h.History.Do(new NoOpAction());
        var save = h.Session.SaveProjectAsync(SaveInitiator.User);
        await h.Documents.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        h.History.Do(new NoOpAction());
        h.Documents.ReleaseWrite.SetResult();

        Assert.False(await save.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
        Assert.True(h.Session.IsProjectDirty);
        Assert.Empty(h.Recovery.SavedNotifications);
    }

    /// <summary>
    ///     Ctrl+S leaves the lyrics inspector's text box focused, so no lost focus commits its
    ///     draft: the save applies it first, as one history entry, and the file holds it.
    /// </summary>
    [Fact]
    public async Task Save_AppliesALyricDraftStillInItsTextBox()
    {
        var h = new Harness(_tempDir);
        h.Documents.ReleaseWrite.SetResult();
        var (lyrics, syllable) = h.TypeLyricDraft("lo");
        var node = h.History.CurrentNodeId;

        Assert.True(await h.Session.SaveProjectAsync(SaveInitiator.User));

        Assert.Equal(["lo"], h.Documents.SavedLyrics);
        Assert.Equal("lo", syllable.Text);
        Assert.Single(h.History.GetUndoSteps());
        Assert.NotEqual(node, h.History.CurrentNodeId);
        Assert.False(h.Session.IsProjectDirty);
        Assert.Equal("lo", lyrics.SelectedLyricText);
    }

    /// <summary>Unapplied Song details (#751 P7b) are a pending draft like the inspector's staged pages: Ctrl+S
    ///     leaves them staged (and still unsaved work), the close or replace prompt's Save applies them as one undo
    ///     node and the file holds them.</summary>
    [Fact]
    public async Task CtrlS_KeepsTheSongDraftStaged_ThePromptSaveAppliesIt()
    {
        var h = new Harness(_tempDir);
        h.Documents.ReleaseWrite.SetResult();
        var song = new SongOverviewViewModel(h.State, h.History, new FakeAppLogger(), FakeLocalization.English(), h.Editor);
        var oldTitle = h.State.ActiveProject!.Chart.SongName;
        song.Details.Title = "Typed title";

        Assert.True(await h.Session.SaveProjectAsync(SaveInitiator.User));

        Assert.Equal(oldTitle, h.State.ActiveProject!.Chart.SongName);
        Assert.True(song.Details.IsDirty);
        Assert.Empty(h.History.GetUndoSteps());
        Assert.True(h.Session.HasUnsavedWork());

        Assert.True(await h.Session.SaveProjectAsync(SaveInitiator.Prompt));

        Assert.Equal("Typed title", h.State.ActiveProject!.Chart.SongName);
        Assert.Single(h.History.GetUndoSteps());
        Assert.False(song.Details.IsDirty);
        Assert.False(h.Session.IsProjectDirty);
        Assert.False(h.Session.HasUnsavedWork());
    }

    /// <summary>Replacing the project with only unapplied Song details asks the ONE unsaved-changes question (no
    ///     separate song-details dialog); its Cancel abandons the replace and keeps the draft, unapplied.</summary>
    [Fact]
    public async Task ReplacingTheProject_CountsUnappliedSongDetails_InTheOnePrompt_AndCancelKeepsThem()
    {
        var h = new Harness(_tempDir);
        var song = new SongOverviewViewModel(h.State, h.History, new FakeAppLogger(), FakeLocalization.English(), h.Editor);
        var asked = 0;
        h.Session.ConfirmDiscardAsync = () =>
        {
            asked++;
            return Task.FromResult(false);
        };
        Assert.True(await h.Session.MayReplaceOpenProjectAsync());
        Assert.Equal(0, asked);

        song.Details.Title = "Typed title";

        Assert.False(await h.Session.MayReplaceOpenProjectAsync());
        Assert.Equal(1, asked);
        Assert.True(song.Details.IsDirty);
        Assert.Empty(h.History.GetUndoSteps());
    }

    /// <summary>
    ///     A save nobody asked for at that moment (stems finished separating) writes the stored text
    ///     and leaves the draft in its text box: the user may be half-way through typing it.
    /// </summary>
    [Fact]
    public async Task UnattendedSave_LeavesALyricDraftPending_AndWritesTheStoredText()
    {
        var h = new Harness(_tempDir);
        h.Documents.ReleaseWrite.SetResult();
        var (lyrics, syllable) = h.TypeLyricDraft("lo");
        var node = h.History.CurrentNodeId;

        await h.Session.SaveProjectAsync(SaveInitiator.Unattended);

        Assert.Equal(["la"], h.Documents.SavedLyrics);
        Assert.Equal("la", syllable.Text);
        Assert.Empty(h.History.GetUndoSteps());
        Assert.Equal(node, h.History.CurrentNodeId);
        Assert.Equal("lo", lyrics.SelectedLyricText);
    }

    /// <summary>Closing or replacing the project asks about unsaved work AFTER the draft is applied,
    /// so a session whose only edit is a typed draft is not taken for clean; an unchanged draft adds nothing.</summary>
    [Theory]
    [InlineData("lo", 1)]
    [InlineData("la", 0)]
    public async Task ReplacingTheProject_CountsALyricDraftAsUnsavedWork(string draft, int entries)
    {
        var h = new Harness(_tempDir);
        var asked = 0;
        h.Session.ConfirmDiscardAsync = () =>
        {
            asked++;
            return Task.FromResult(false);
        };
        var (_, syllable) = h.TypeLyricDraft(draft);

        var proceed = await h.Session.MayReplaceOpenProjectAsync();

        Assert.Equal(entries == 0, proceed);
        Assert.Equal(entries, asked);
        Assert.Equal(draft, syllable.Text);
        Assert.Equal(entries, h.History.GetUndoSteps().Count);
    }

    /// <summary>Alt+F4 also leaves the box focused: the window-close gate counts the draft.</summary>
    [Fact]
    public void Close_CountsALyricDraftAsUnsavedWork()
    {
        var h = new Harness(_tempDir);
        var (_, syllable) = h.TypeLyricDraft("lo");

        Assert.True(h.Session.HasUnsavedWork());
        Assert.Equal("lo", syllable.Text);
    }

    /// <summary>#751 P4 review: a staged inspector draft only its own Apply writes is unsaved work too, so closing
    /// or replacing the project asks rather than dropping it.</summary>
    [Fact]
    public void Close_CountsAStagedDraftAsUnsavedWork()
    {
        var h = new Harness(_tempDir);
        var held = false;
        h.Editor.AddPendingDraft(() => held, () => { });
        Assert.False(h.Session.HasUnsavedWork());

        held = true;

        Assert.True(h.Session.HasUnsavedWork());
    }

    /// <summary>#751 P4 review: the close or replace prompt counted a staged draft as unsaved work, so its Save applies
    /// the draft (one undo step) before it writes; a plain Ctrl+S leaves the draft staged.</summary>
    [Fact]
    public async Task PromptSave_AppliesAStagedDraft_ThenSaves_PlainSaveLeavesIt()
    {
        var h = new Harness(_tempDir);
        h.Documents.ReleaseWrite.SetResult();
        var held = true;
        var applied = 0;
        h.Editor.AddPendingDraft(() => held, () =>
        {
            applied++;
            held = false;
            h.History.Do(new NoOpAction());
        });

        Assert.True(await h.Session.SaveProjectAsync(SaveInitiator.User));
        Assert.Equal(0, applied);
        Assert.True(h.Session.HasUnsavedWork());

        Assert.True(await h.Session.SaveProjectAsync(SaveInitiator.Prompt));

        Assert.Equal(1, applied);
        Assert.Single(h.History.GetUndoSteps());
        Assert.False(h.Session.IsProjectDirty);
        Assert.False(h.Session.HasUnsavedWork());
    }

    private sealed class GatedAudio : FakeAudioPlaybackService, IAudioPlaybackService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task IPlaybackTransport.LoadAudioAsync(string filePath)
        {
            Started.TrySetResult();
            return Release.Task;
        }
    }

    private sealed class NoOpAction : IChartAction
    {
        public bool HasEffect => true;
        public void Execute() { }
        public void Undo() { }
    }

    private sealed class NoOpSaveCompanionService : ISaveCompanionService
    {
        public string? WriteEof(string projectPath, ChartProject snapshot) => null;

        public string? WriteDlcBuilderProject(
            string projectPath, ChartProject snapshot, double? audioLengthMs) => null;

        public Task<string?> BuildPsarcAsync(string projectPath, ChartProject snapshot,
            double? audioLengthMs, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class RecordingCrashRecovery : ICrashRecoveryService
    {
        public List<string> SavedNotifications { get; } = [];
        public int ChangedNotifications { get; private set; }
        public int SwitchedNotifications { get; private set; }
        public string SnapshotRootDirectory => string.Empty;

        public void Start() { }
        public IReadOnlyList<RecoverableSession> FindRecoverableSessions() => [];
        public void DiscardSession(RecoverableSession session) { }
        public void NotifyProjectSaved(string projectPath) => SavedNotifications.Add(projectPath);
        public void NotifyProjectSwitched() => SwitchedNotifications++;
        public void NotifyCleanShutdown() { }
        public Func<bool>? HasUnsavedChanges { get; set; }
        public void NotifyProjectChanged() => ChangedNotifications++;
        public void Dispose() { }
    }

    /// <summary>Blocks the first write on <see cref="ReleaseWrite" />; later writes pass.</summary>
    private sealed class GatedDocumentService : IProjectDocumentService
    {
        private int _saves;
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SaveCount => _saves;
        public List<string> SavedLyrics { get; } = [];

        public ChartProject ToLoad { get; set; } = new();

        public ChartProject LoadChart(string filePath) => ToLoad;

        public void SaveChart(string filePath, ChartProject project, double? audioLengthMs = null)
        {
            SavedLyrics.AddRange(project.VocalTracks.SelectMany(t => t.Phrases).SelectMany(p => p.Lyrics).Select(l => l.Text));
            if (Interlocked.Increment(ref _saves) == 1)
            {
                WriteStarted.TrySetResult();
                Assert.True(ReleaseWrite.Task.Wait(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken), "The test did not release the write.");
            }

            File.WriteAllText(filePath, "<rscp />");
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

    private sealed class Harness
    {
        public Harness(string tempDir, IAudioPlaybackService? audio = null)
        {
            var projectDir = Path.Combine(tempDir, "Band - Song");
            Directory.CreateDirectory(projectDir);
            ProjectPath = Path.Combine(projectDir, "project.dcproj");

            var project = new ChartProject { AudioFileName = "song.ogg" };
            project.Arrangements.Add(new Arrangement { Name = "Lead Guitar" });
            project.VocalTracks.Add(new VocalTrack
            {
                Phrases = [new LyricPhrase { Lyrics = [new LyricEvent { Text = "la", IsPhraseEnd = true }] }]
            });

            var logger = new FakeAppLogger();
            var state = State;
            var config = Config;
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), state, config, FakeLocalization.KeysOnly());
            var selection = new SelectionService();
            History = new HistoryService(logger);
            var editor = Editor = new ChartEditorViewModel(
                state, transport, History, selection,
                new TempoService(logger), new TrackContextRegistry(state, selection), FakeLocalization.KeysOnly());

            editor.ApplyProject(project, resetHistory: true);
            state.CurrentProjectPath = ProjectPath;
            state.CurrentAudioFileName = project.AudioFileName;

            Session = new ProjectSessionService(
                audio ?? new FakeAudioPlaybackService(), config, logger, new FakeFileStorageService(),
                Documents, state, transport, editor, new TempoService(logger),
                Recovery, History, new NoOpSaveCompanionService(),
                uiDispatcher: new TestUiDispatcher(),
                localization: FakeLocalization.English(),
                pathRekey: new FakeProjectPathRekey());
        }

        public ProjectState State { get; } = new();
        public ChartEditorViewModel Editor { get; }
        public FakeConfigurationService Config { get; } = new();
        public string ProjectPath { get; }
        public HistoryService History { get; }
        public GatedDocumentService Documents { get; } = new();
        public RecordingCrashRecovery Recovery { get; } = new();
        public ProjectSessionService Session { get; }

        /// <summary>Selects the one syllable in the lyrics inspector and types a draft, uncommitted.</summary>
        public (LyricsInspectorViewModel Lyrics, LyricEvent Syllable) TypeLyricDraft(string draft)
        {
            var lyrics = LyricDraftRig.NewInspector(Editor, History, State);
            var syllable = Editor.VocalTracks[0].Phrases[0].Lyrics[0];
            Editor.Selection.SelectedLyric = syllable;
            lyrics.SelectedLyricText = draft;
            Assert.Equal("la", syllable.Text);
            return (lyrics, syllable);
        }
    }

}
