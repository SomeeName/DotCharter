using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Audio.ChartSynth;
using DotCharter.Tone.Audio.ChartSynth;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Services.Audio.Dsp.Tones;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tones;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Export;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Infrastructure.Recovery;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Session;
using DotCharter.Tone.Audio.Dsp.Tones;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.Services.Session;

/// <summary>
///     #196: an own chart's player tone edit and chart-synth instrument live OUTSIDE the project
///     (<see cref="SongToneEditStore" />, keyed by the project path and the tone key). Renaming the
///     tone (#181's one undoable verb) must move them to the new key — but only when a SAVE writes
///     that key: an unsaved, undone or discarded rename leaves the store exactly as it was, and a
///     library (<c>psarc:</c>) song is never written. Driven through the real save pipeline over a
///     temp store; nothing here reads the user's config root.
/// </summary>
public sealed class SongToneEditKeyFollowTests : IDisposable
{
    private const string Old = "lead_dist";
    private const string New = "lead_crunch";
    private const string Other = "clean";

    private static readonly ChartSynthToneOverride NeckOverride =
        new(new ChartSynthInstrument(ChartSynthModel.StratSss, ChartSynthPickup.Neck, false), null);

    private readonly string _tempDir = Directory.CreateTempSubdirectory("dotcharter-tonefollow-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    [Fact]
    public async Task ARenameThatIsSaved_TakesTheEditAndTheInstrument_ToTheNewKey()
    {
        var h = Harness.Create(_tempDir);
        h.SeedStore(h.ProjectPath, Old, -6f, withInstrument: true);
        h.SeedStore(h.ProjectPath, Other, -1f, withInstrument: false);

        h.Rename(Old, New);
        Assert.True(await h.Session.Saver.SaveProjectCoreAsync(h.ProjectPath));

        var edits = h.Store.Read(h.ProjectPath);
        Assert.Equal([Other, New], edits.Keys.Order(StringComparer.OrdinalIgnoreCase));   // "clean" sorts first
        Assert.Equal(-6f, edits[New].Modules[0].Params["gainDb"]);
        Assert.Equal(-1f, edits[Other].Modules[0].Params["gainDb"]);
        Assert.Equal(NeckOverride, Assert.Single(h.Store.ReadInstruments(h.ProjectPath), i => i.Key == New).Value);
        Assert.DoesNotContain(Old, h.Store.ReadInstruments(h.ProjectPath).Keys);
    }

    [Fact]
    public async Task ARenameThatWasUndone_BeforeTheSave_LeavesTheStoreAlone()
    {
        var h = Harness.Create(_tempDir);
        h.SeedStore(h.ProjectPath, Old, -6f, withInstrument: true);
        var before = File.ReadAllBytes(h.Store.PathFor(h.ProjectPath));

        h.Rename(Old, New);
        h.History.Undo();
        // …and a NEW tone then takes the name the undone rename had: that is not a rename either.
        Assert.True(h.History.Do(new UpsertToneDefinitionAction(h.State.ActiveProject!, null,
            new ToneDefinition { Key = New, Name = New, Json = "{}" })));
        Assert.True(await h.Session.Saver.SaveProjectCoreAsync(h.ProjectPath));

        Assert.Equal(before, File.ReadAllBytes(h.Store.PathFor(h.ProjectPath)));
    }

    [Fact]
    public async Task AnUnsavedRename_ThatIsDiscarded_NeverMovesTheStore()
    {
        var h = Harness.Create(_tempDir);
        Assert.True(await h.Session.Saver.SaveProjectCoreAsync(h.ProjectPath));   // the file on disk names Old
        h.SeedStore(h.ProjectPath, Old, -6f, withInstrument: true);
        var before = File.ReadAllBytes(h.Store.PathFor(h.ProjectPath));

        h.Rename(Old, New);
        Assert.Equal(before, File.ReadAllBytes(h.Store.PathFor(h.ProjectPath)));   // a rename alone moves nothing

        // Discard: the project is reopened from disk (a new instance, a fresh history), then saved.
        // It even gains a NEW tone under the discarded rename's key — still not a rename.
        var reopened = new RscpChartSerializer().Load(h.ProjectPath);
        reopened.ToneDefinitions.Add(new ToneDefinition { Key = New, Name = New, Json = "{}" });
        h.Editor.ApplyProject(reopened, resetHistory: true);
        Assert.True(await h.Session.Saver.SaveProjectCoreAsync(h.ProjectPath));

        Assert.Equal(before, File.ReadAllBytes(h.Store.PathFor(h.ProjectPath)));
    }

    [Fact]
    public async Task UndoingASavedRename_AndSavingAgain_MovesItBack()
    {
        var h = Harness.Create(_tempDir);
        h.SeedStore(h.ProjectPath, Old, -6f, withInstrument: true);

        h.Rename(Old, New);
        Assert.True(await h.Session.Saver.SaveProjectCoreAsync(h.ProjectPath));
        Assert.Equal([New], h.Store.Read(h.ProjectPath).Keys);

        h.History.Undo();
        Assert.True(await h.Session.Saver.SaveProjectCoreAsync(h.ProjectPath));
        Assert.Equal([Old], h.Store.Read(h.ProjectPath).Keys);
        Assert.Equal([Old], h.Store.ReadInstruments(h.ProjectPath).Keys);

        h.History.Redo();
        Assert.True(await h.Session.Saver.SaveProjectCoreAsync(h.ProjectPath));
        Assert.Equal([New], h.Store.Read(h.ProjectPath).Keys);
    }

    [Fact]
    public async Task TwoTonesSwappedThroughATemporaryKey_ExchangeTheirEdits()
    {
        var h = Harness.Create(_tempDir);
        h.SeedStore(h.ProjectPath, Old, -6f, withInstrument: true);
        h.SeedStore(h.ProjectPath, Other, -1f, withInstrument: false);

        h.Rename(Old, "swap_tmp");
        h.Rename(Other, Old);
        h.Rename("swap_tmp", Other);
        Assert.True(await h.Session.Saver.SaveProjectCoreAsync(h.ProjectPath));

        var edits = h.Store.Read(h.ProjectPath);
        Assert.Equal(-6f, edits[Other].Modules[0].Params["gainDb"]);
        Assert.Equal(-1f, edits[Old].Modules[0].Params["gainDb"]);
        Assert.Equal([Other], h.Store.ReadInstruments(h.ProjectPath).Keys);
    }

    [Fact]
    public async Task SaveAs_CopiesTheEditsRenamed_ToTheNewPath_AndLeavesTheOldPathAlone()
    {
        var h = Harness.Create(_tempDir);
        h.SeedStore(h.ProjectPath, Old, -6f, withInstrument: true);
        var oldFile = File.ReadAllBytes(h.Store.PathFor(h.ProjectPath));
        var copyPath = Path.Combine(_tempDir, "Band - Song (live)", "project.rscp");
        Directory.CreateDirectory(Path.GetDirectoryName(copyPath)!);

        h.Rename(Old, New);
        Assert.True(await h.Session.Saver.SaveToPickedPathAsync(copyPath));

        Assert.Equal(oldFile, File.ReadAllBytes(h.Store.PathFor(h.ProjectPath)));   // that chart still says Old
        Assert.Equal([New], h.Store.Read(copyPath).Keys);
        Assert.Equal([New], h.Store.ReadInstruments(copyPath).Keys);

        // The copy is the new baseline: a plain save of it moves nothing again.
        Assert.True(await h.Session.Saver.SaveProjectCoreAsync(copyPath));
        Assert.Equal([New], h.Store.Read(copyPath).Keys);
    }

    [Fact]
    public async Task ALibrarySong_SavedAsAProject_LeavesItsLibraryEditsUntouched()
    {
        const string library = "lib:CallMe";
        var h = Harness.Create(_tempDir);
        h.State.CurrentProjectPath = "";            // a library load: no project path…
        h.State.CurrentSongIdentity = library;      // …the package is the identity
        h.SeedStore(library, Old, -6f, withInstrument: true);
        var before = File.ReadAllBytes(h.Store.PathFor(library));

        h.Rename(Old, New);
        Assert.True(await h.Session.Saver.SaveToPickedPathAsync(h.ProjectPath));

        Assert.Equal(before, File.ReadAllBytes(h.Store.PathFor(library)));
        Assert.Empty(h.Store.Read(h.ProjectPath));
    }

    [Fact]
    public void TheStore_RefusesToMoveALibrarySongsKeys()
    {
        var store = new SongToneEditStore(Path.Combine(_tempDir, "store"));
        Assert.True(store.Save("lib:CallMe", Old, Old, Rig(-6f)).Ok);

        var result = store.MoveKeys("lib:CallMe", "lib:CallMe", new Dictionary<string, string> { [Old] = New });

        Assert.Equal(SongToneEditWriteOutcome.Failed, result.Outcome);
        Assert.Equal([Old], store.Read("lib:CallMe").Keys);
    }

    [Fact]
    public async Task AStoreThatCannotBeWritten_FailsNoSave_AndTheNextSaveMovesTheEdit()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Relies on Windows file locking.");
        var h = Harness.Create(_tempDir);
        h.SeedStore(h.ProjectPath, Old, -6f, withInstrument: true);

        h.Rename(Old, New);
        using (new FileStream(h.Store.PathFor(h.ProjectPath), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(await h.Session.Saver.SaveProjectCoreAsync(h.ProjectPath));
        }

        Assert.False(h.Session.IsProjectDirty);
        Assert.Equal([Old], h.Store.Read(h.ProjectPath).Keys);   // not moved, and not lost

        Assert.True(await h.Session.Saver.SaveProjectCoreAsync(h.ProjectPath));
        Assert.Equal([New], h.Store.Read(h.ProjectPath).Keys);
    }

    [Fact]
    public void TheJournal_NetsOutAnUndo_AndComposesAChain()
    {
        var project = new ChartProject();
        var journal = ToneKeyRenameJournal.For(project);

        journal.Record("a", "b");
        journal.Record("b", "a");
        Assert.Empty(journal.NetMoves(journal.Count));

        journal.Record("a", "b");
        journal.Record("b", "c");
        Assert.Equal(new Dictionary<string, string> { ["a"] = "c" }, journal.NetMoves(journal.Count));
        Assert.Equal(new Dictionary<string, string> { ["a"] = "b" }, journal.NetMoves(3));

        journal.Settle(journal.Count);
        Assert.Equal(0, journal.Count);
        Assert.Same(journal, ToneKeyRenameJournal.For(project));
        Assert.NotSame(journal, ToneKeyRenameJournal.For(new ChartProject()));
    }

    private static DcRigPreset Rig(float gainDb) =>
        new([new DcRigModule("gain", new Dictionary<string, float> { ["gainDb"] = gainDb })]);

    private sealed class Harness
    {
        private Harness(string tempDir)
        {
            var projectDir = Path.Combine(tempDir, "Band - Song");
            Directory.CreateDirectory(projectDir);
            ProjectPath = Path.Combine(projectDir, "project.rscp");
            Store = new SongToneEditStore(Path.Combine(tempDir, "song-tones"));

            var logger = new FakeAppLogger();
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), State, Config, FakeLocalization.KeysOnly());
            var selection = new SelectionService();
            History = new HistoryService(logger);
            Editor = new ChartEditorViewModel(
                State, transport, History, selection,
                new TempoService(logger), new TrackContextRegistry(State, selection), FakeLocalization.KeysOnly());

            var project = new ChartProject { AudioFileName = "song.ogg" };
            project.ToneDefinitions.Add(Definition(Old));
            project.ToneDefinitions.Add(Definition(Other));
            project.Arrangements.Add(new Arrangement
            {
                Name = "Lead Guitar", ToneBase = Other, ToneA = Other, ToneB = Old, StartingTone = Other
            });
            Editor.ApplyProject(project, resetHistory: true);
            State.CurrentProjectPath = ProjectPath;
            State.CurrentAudioFileName = project.AudioFileName;

            Session = new ProjectSessionService(
                new FakeAudioPlaybackService(), Config, logger, new FakeFileStorageService(),
                new RscpOnlyDocumentService(), State, transport, Editor, new TempoService(logger),
                new NoOpCrashRecoveryService(), History, new NoOpSaveCompanionService(),
                uiDispatcher: new TestUiDispatcher(),
                localization: FakeLocalization.English(),
                songToneEdits: Store,
                pathRekey: new FakeProjectPathRekey());
        }

        public string ProjectPath { get; }
        public SongToneEditStore Store { get; }
        public ProjectState State { get; } = new();
        public FakeConfigurationService Config { get; } = new();
        public HistoryService History { get; }
        public ChartEditorViewModel Editor { get; }
        public ProjectSessionService Session { get; }

        public static Harness Create(string tempDir) => new(tempDir);

        public void SeedStore(string song, string key, float gainDb, bool withInstrument)
        {
            Assert.True(Store.Save(song, key, key, Rig(gainDb)).Ok);
            if (withInstrument) Assert.True(Store.SaveInstrument(song, key, NeckOverride).Ok);
        }

        public void Rename(string from, string to)
        {
            var plan = ToneKeyEdits.PlanRename(State.ActiveProject!, from, to);
            Assert.True(plan.IsReady, $"{from} -> {to}: {plan.Outcome}");
            Assert.True(History.Do(plan.Action!));
        }

        private static ToneDefinition Definition(string key) =>
            new() { Key = key, Name = key, Json = RsToneJsonWriter.Write(RsToneAuthoring.CreateDefaultTone(key, key, RsGearCatalog.Shared)) };
    }

    /// <summary>Writes a real <c>.rscp</c>; the exports are not this test's subject.</summary>
    private sealed class RscpOnlyDocumentService : IProjectDocumentService
    {
        public ChartProject LoadChart(string filePath) => new RscpChartSerializer().Load(filePath);

        public void SaveChart(string filePath, ChartProject project, double? audioLengthMs = null)
            => new RscpChartSerializer().Save(filePath, project);

        public void RefreshArrangementFileManifest(ChartProject project) { }

        public void SaveDlcBuilderXmlBundle(string projectPath, ChartProject project, double? audioLengthMs = null) { }

        public ChartProject AssembleProject(ChartProject? baseProject, double bpm, string audioFileName,
            IEnumerable<LyricPhrase> lyrics) => baseProject ?? new ChartProject();

        public ChartProject CreateNewProject(string title, string artist, string audioFileName)
            => new() { AudioFileName = audioFileName };
    }

    private sealed class NoOpSaveCompanionService : ISaveCompanionService
    {
        public string? WriteEof(string projectPath, ChartProject snapshot) => null;

        public string? WriteDlcBuilderProject(string projectPath, ChartProject snapshot, double? audioLengthMs) => null;

        public Task<string?> BuildPsarcAsync(string projectPath, ChartProject snapshot,
            double? audioLengthMs, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class NoOpCrashRecoveryService : ICrashRecoveryService
    {
        public string SnapshotRootDirectory => string.Empty;
        public Func<bool>? HasUnsavedChanges { get; set; }
        public void Start() { }
        public IReadOnlyList<RecoverableSession> FindRecoverableSessions() => [];
        public void DiscardSession(RecoverableSession session) { }
        public void NotifyProjectSaved(string projectPath) { }
        public void NotifyProjectSwitched() { }
        public void NotifyCleanShutdown() { }
        public void NotifyProjectChanged() { }
        public void Dispose() { }
    }
}
