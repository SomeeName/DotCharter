using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Export;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Infrastructure.Recovery;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Session;

namespace DotCharter.Tests.Services.Session;

/// <summary>
///     The second half of the "restore a PSARC song nobody edited" defect (see
///     <c>CrashRecoverySnapshotGateTests</c>): only ONE of the replace
///     paths ever told the crash-recovery service that the session had been
///     replaced.
///     <para>
///         <c>ProjectSessionService.LoadFile</c> calls
///         <c>NotifyProjectSwitched()</c> in its finally, which resets the
///         snapshot scheduler and retires the outgoing project's snapshot.
///         <c>SongBrowserViewModel.LoadSongItem</c> — the path Play Mode's song
///         select drives — called only <c>MarkProjectClean()</c>, so after a
///         library song load the scheduler stayed armed by the
///         <c>HistoryChanged</c> that <c>ApplyProject(resetHistory: true)</c>
///         raises, and the previous project's snapshot stayed on disk.
///     </para>
///     <para>
///         The fix wires the DECLARATION rather than the call site:
///         <c>ProjectDirtyTracker.CleanMarked</c> (raised by every
///         <c>MarkClean</c>) drives <c>NotifyProjectSwitched</c>, so every replace
///         path — the open funnel, the library load, the importers, the default
///         project — is covered at once. This test pins that wiring; a new
///         replace path gets it for free, and removing it fails here.
///     </para>
/// </summary>
public sealed class ProjectSessionRecoveryWiringTests
{
    /// <summary>
    ///     A library/PSARC load ends in <c>MarkProjectClean()</c> — and that alone
    ///     must retire the outgoing snapshot and disarm the scheduler.
    /// </summary>
    [Fact]
    public void MarkProjectClean_RetiresTheSnapshot()
    {
        var h = new Harness();

        h.Session.MarkProjectClean();

        Assert.Equal(1, h.Recovery.SwitchedCount);
    }

    /// <summary>
    ///     …on EVERY declaration, not only when the flag flips. A session that was
    ///     already clean (browsing one library song after another) is exactly the
    ///     owner's repro, and the edge-triggered <c>IsDirtyChanged</c> is silent
    ///     there — which is why this rides <c>CleanMarked</c>.
    /// </summary>
    [Fact]
    public void MarkProjectClean_OnAnAlreadyCleanSession_StillRetiresTheSnapshot()
    {
        var h = new Harness();
        h.Session.MarkProjectClean();

        h.Session.MarkProjectClean();

        Assert.Equal(2, h.Recovery.SwitchedCount);
    }

    /// <summary>The v0.48 half, unchanged: a project-level change still arms the
    /// snapshot. Kept beside its mirror so neither can be removed unnoticed.</summary>
    [Fact]
    public void MarkProjectDirty_ArmsTheSnapshot()
    {
        var h = new Harness();

        h.Session.MarkProjectDirty();

        Assert.Equal(1, h.Recovery.ChangedCount);
        Assert.Equal(0, h.Recovery.SwitchedCount);
    }

    /// <summary>
    ///     The did-anything-change gate has to be HANDED to the recovery service —
    ///     the tracker lives inside the session, so nothing else can wire it — and
    ///     it has to report the session's real dirty state.
    /// </summary>
    [Fact]
    public void HasUnsavedChanges_IsWiredToTheSessionsDirtyFlag()
    {
        var h = new Harness();

        var gate = h.Recovery.HasUnsavedChanges;
        Assert.NotNull(gate);

        h.Session.MarkProjectClean();
        Assert.False(gate!());
        Assert.False(h.Session.IsProjectDirty);

        h.Session.MarkProjectDirty();
        Assert.True(gate());
        Assert.True(h.Session.IsProjectDirty);
    }

    // ---- harness --------------------------------------------------------------

    private sealed class RecordingCrashRecoveryService : ICrashRecoveryService
    {
        /// <summary>No store behind this fake, so nothing counts as an internal
        /// snapshot. Settable for the test that needs a real recovery root.</summary>
        public string SnapshotRootDirectory { get; set; } = string.Empty;

        public int SwitchedCount { get; private set; }
        public int ChangedCount { get; private set; }

        public void Start() { }
        public IReadOnlyList<RecoverableSession> FindRecoverableSessions() => [];
        public void DiscardSession(RecoverableSession session) { }
        public void NotifyProjectSaved(string projectPath) { }
        public void NotifyProjectSwitched() => SwitchedCount++;
        public void NotifyCleanShutdown() { }
        public void NotifyProjectChanged() => ChangedCount++;
        public Func<bool>? HasUnsavedChanges { get; set; }
        public void Dispose() { }
    }

    private sealed class StubDocumentService : IProjectDocumentService
    {
        public ChartProject LoadChart(string filePath) => throw new NotSupportedException();
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
        public Harness()
        {
            var logger = new FakeAppLogger();
            var config = new FakeConfigurationService();
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), State, config, FakeLocalization.KeysOnly());
            var selection = new SelectionService();
            var history = new HistoryService(logger);
            var editor = new ChartEditorViewModel(
                State, transport, history, selection,
                new TempoService(logger), new TrackContextRegistry(State, selection), FakeLocalization.KeysOnly());

            Session = new ProjectSessionService(
                new FakeAudioPlaybackService(), config, logger, new FakeFileStorageService(),
                new StubDocumentService(), State, transport, editor, new TempoService(logger),
                Recovery, history, new NoOpSaveCompanionService(),
                uiDispatcher: new TestUiDispatcher(),
                localization: FakeLocalization.English(),
                pathRekey: new FakeProjectPathRekey());
        }

        public ProjectState State { get; } = new();
        public RecordingCrashRecoveryService Recovery { get; } = new();
        public ProjectSessionService Session { get; }
    }
}
