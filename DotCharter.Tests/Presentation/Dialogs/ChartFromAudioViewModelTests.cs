using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Editing.DraftMerge;
using DotCharter.Services.Editing.FretAnchors;
using DotCharter.Services.Import.Audio;
using DotCharter.Tests.Analysis;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     The chart-from-audio wizard. Its analysis runs on a background thread and
///     writes nothing, which is the shape of every bug here: state kept for one
///     song, or one arrangement, outlived both — and one view model per open does
///     not change that for the chosen path, which <see cref="DialogMemory" /> keeps.
/// </summary>
public sealed partial class ChartFromAudioViewModelTests : IDisposable
{
    private const int Rate = AnalysisTestSignals.SampleRate;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private sealed class Harness
    {
        public Harness(ILocalizationService? localization = null)
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var config = new FakeConfigurationService();
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), State, config, FakeLocalization.KeysOnly());
            Editor = new ChartEditorViewModel(State, transport, History, new SelectionService(), new TempoService(logger),
                new TrackContextRegistry(State, new SelectionService()), FakeLocalization.KeysOnly());
            Dirty = new ProjectDirtyTracker(History);
            Services = new ChartEditDialogServices(Editor, History, State, config,
                localization ?? FakeLocalization.English(), logger);
        }

        public ProjectState State { get; } = new();
        public HistoryService History { get; }
        public ProjectDirtyTracker Dirty { get; }
        public ChartEditorViewModel Editor { get; }
        public ChartEditDialogServices Services { get; }
        public DialogMemory Memory { get; } = new();
        public FakeFileStorageService Storage { get; } = new();

        /// <summary>No stem until a test hands it one; the analyzer and the dialog share it, as in the app.</summary>
        public FakeStemLocator Stems { get; } = new();

        public FakeSongStemSeparation Separation { get; } = new();

        /// <summary>One open of the wizard, as the host would make it.</summary>
        public ChartFromAudioViewModel Open() => Open(Separation);

        /// <summary>One open over another separator — the app's own run, for the tests that need it.</summary>
        public ChartFromAudioViewModel Open(ISongStemSeparation separation) =>
            new(Services, Storage, new ExportTargetWriter(), new ChartFromAudioAnalyzer(Stems),
                new ChartFromAudioStemServices(Stems, separation), new ChartFromAudioMerge(Services, MergeHost), Memory);

        /// <summary>The merge card's review, as the app's one singleton (#647).</summary>
        public DraftMergeReviewHost MergeHost { get; } = new(new HistoryService(new FakeAppLogger()), new FretAnchorGenerationService());

        /// <summary>A 120 BPM grid and one standard-tuning arrangement, made active.</summary>
        public ChartProject LoadProject(string audioFileName)
        {
            var project = new ChartProject();
            foreach (var beat in AnalysisTestSignals.ConstantBeats(120, 16)) project.Beats.Add(beat);
            var arrangement = new Arrangement { Name = "Lead", StringCount = 6 };
            for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
            arrangement.Levels.Add(new ChartLevel());
            project.Arrangements.Add(arrangement);

            State.ActiveProject = project;
            State.ActiveArrangement = arrangement;
            State.CurrentAudioFileName = audioFileName;
            Separation.SongAudio = audioFileName;
            Editor.CurrentChart = project.Chart;
            Editor.ActiveArrangement = arrangement;
            Dirty.MarkClean();
            return project;
        }
    }

    /// <summary>Three open-string plucks as a 16-bit mono WAV — the fixture the analyzer's own tests draft from.</summary>
    private string WritePluckWav(string name)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, name);
        var buffer = new float[(int)(3.0 * Rate)];
        AnalysisTestSignals.AddPluck(buffer, 0.50, 110.00);
        AnalysisTestSignals.AddPluck(buffer, 1.00, 146.83);
        AnalysisTestSignals.AddPluck(buffer, 1.75, 196.00);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);
        var dataBytes = buffer.Length * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(Rate);
        writer.Write(Rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        foreach (var sample in buffer) writer.Write((short)(Math.Clamp(sample, -1f, 1f) * 30000));
        return path;
    }

    private static Task Analyze(ChartFromAudioViewModel vm)
    {
        vm.Sensitivity = 100; // keep almost everything: this asserts wiring, not detection
        return ((IAsyncRelayCommand)vm.AnalyzeCommand).ExecuteAsync(null);
    }

    /// <summary>A path chosen for one song must never be pre-filled, plausible, for the next.</summary>
    [Fact]
    public void Opening_on_another_project_forgets_the_previous_songs_audio()
    {
        var h = new Harness();
        var first = WritePluckWav("first.wav");
        h.LoadProject(first);
        Assert.Equal(first, h.Open().AudioPath);

        h.LoadProject(Path.Combine(_dir, "second.ogg"));

        Assert.Equal("", h.Open().AudioPath);
    }

    /// <summary>Re-opening on the SAME project keeps what the user picked — the reset is keyed on the project.</summary>
    [Fact]
    public void Reopening_on_the_same_project_keeps_a_hand_picked_path()
    {
        var h = new Harness();
        h.LoadProject(Path.Combine(_dir, "mix.ogg"));
        h.Open().AudioPath = WritePluckWav("hand-picked.wav");

        Assert.EndsWith("hand-picked.wav", h.Open().AudioPath);
    }

    /// <summary>Cancel during the one operation long enough to want cancelling closes at once.</summary>
    [Fact]
    public void Cancel_during_analysis_closes_the_dialog()
    {
        var h = new Harness();
        h.LoadProject(Path.Combine(_dir, "mix.ogg"));
        var vm = h.Open();
        vm.IsBusy = true;

        vm.CancelCommand.Execute(null);

        Assert.False(ChartEditHarness.Answer(vm.Result));
        Assert.False(vm.IsBusy);
    }

    /// <summary>…and a result still in flight lands on a dialog that is gone and leaves nothing for Apply.</summary>
    [Fact]
    public async Task A_draft_that_lands_after_Cancel_is_dropped()
    {
        var h = new Harness();
        h.LoadProject(WritePluckWav("mix.wav"));
        var vm = h.Open();
        vm.Sensitivity = 100;

        var run = ((IAsyncRelayCommand)vm.AnalyzeCommand).ExecuteAsync(null);
        vm.CancelCommand.Execute(null);
        await run;

        Assert.True(vm.Result.IsCompleted);
        Assert.False(vm.HasDraft);
        Assert.False(vm.IsBusy);
        Assert.Equal(0, vm.DraftNoteCount);
        vm.ApplyCommand.Execute(null);
        Assert.Single(h.State.ActiveProject!.Arrangements);
    }

    /// <summary>A greyed primary owes its reason, and it stands down while the busy panel or the status strip speaks.</summary>
    [Fact]
    public async Task The_greyed_primary_prints_its_reason_and_only_when_nothing_else_does()
    {
        var h = new Harness();
        h.LoadProject(WritePluckWav("mix.wav"));
        var vm = h.Open();
        Assert.False(vm.HasDraft);
        Assert.True(vm.NothingToApply);

        vm.IsBusy = true;
        Assert.False(vm.NothingToApply);
        vm.IsBusy = false;

        vm.Status = "anything at all";
        Assert.False(vm.NothingToApply);
        vm.Status = "";
        Assert.True(vm.NothingToApply);

        await Analyze(vm);
        Assert.True(vm.HasDraft);
        Assert.False(vm.NothingToApply);
        Assert.NotEqual("", vm.DraftSummary);
    }

    [Fact]
    public async Task Applying_with_no_project_open_says_so()
    {
        var h = new Harness();
        h.LoadProject(WritePluckWav("mix.wav"));
        var vm = h.Open();
        await Analyze(vm);
        Assert.True(vm.HasDraft);

        h.State.ActiveProject = null;
        vm.ApplyCommand.Execute(null);

        Assert.False(vm.Result.IsCompleted, "a refusal does not close the dialog");
        Assert.NotEqual("", vm.Status);
    }

    /// <summary>The frets were spelled from the arrangement active when the analysis started; another one discards the draft.</summary>
    [Fact]
    public async Task Applying_a_draft_after_the_arrangement_changed_discards_it()
    {
        var h = new Harness();
        var project = h.LoadProject(WritePluckWav("mix.wav"));
        var vm = h.Open();
        await Analyze(vm);
        Assert.True(vm.HasDraft, "the fixture should draft at least one note");

        var other = new Arrangement { Name = "Rhythm", StringCount = 6 };
        other.Levels.Add(new ChartLevel());
        project.Arrangements.Add(other);
        h.State.ActiveArrangement = other;

        vm.ApplyCommand.Execute(null);

        Assert.Equal(2, project.Arrangements.Count);
        Assert.False(vm.HasDraft);
        Assert.NotEqual("", vm.Status);
    }

    [Fact]
    public async Task Applying_a_draft_after_the_project_changed_discards_it()
    {
        var h = new Harness();
        h.LoadProject(WritePluckWav("mix.wav"));
        var vm = h.Open();
        await Analyze(vm);
        Assert.True(vm.HasDraft);

        var second = h.LoadProject(Path.Combine(_dir, "other.wav"));
        vm.ApplyCommand.Execute(null);

        Assert.Single(second.Arrangements);
        Assert.False(vm.HasDraft);
    }

    /// <summary>Unsaved changes are DERIVED from the undo history, so the draft goes through it: dirty, and Ctrl+Z rejects it.</summary>
    [Fact]
    public async Task An_applied_draft_marks_the_project_dirty_and_undo_removes_it()
    {
        var h = new Harness();
        var project = h.LoadProject(WritePluckWav("mix.wav"));
        var vm = h.Open();
        await Analyze(vm);
        Assert.False(h.Dirty.IsDirty);

        vm.ApplyCommand.Execute(null);

        Assert.Equal(2, project.Arrangements.Count);
        Assert.Same(project.Arrangements[1], h.State.ActiveArrangement);
        Assert.True(h.Dirty.IsDirty, "an added arrangement is an unsaved change");
        Assert.True(ChartEditHarness.Answer(vm.Result));

        h.History.Undo();
        Assert.Single(project.Arrangements);
        Assert.Same(project.Arrangements[0], h.State.ActiveArrangement);
    }

    [Fact]
    public async Task An_applied_draft_leaves_the_template_arrangement_untouched()
    {
        var h = new Harness();
        var project = h.LoadProject(WritePluckWav("mix.wav"));
        var vm = h.Open();
        await Analyze(vm);

        vm.ApplyCommand.Execute(null);

        var template = project.Arrangements[0];
        Assert.Equal("Lead", template.Name);
        Assert.Empty(template.Levels[0].Notes);
        Assert.NotEqual("Lead", project.Arrangements[1].Name);
        Assert.NotEmpty(project.Arrangements[1].Levels[0].Notes);
    }

    [Fact]
    public void Browse_offers_WAV_files_and_takes_the_pick()
    {
        var h = new Harness();
        h.LoadProject(Path.Combine(_dir, "mix.ogg"));
        var vm = h.Open();
        h.Storage.NextOpenFile = Path.Combine(_dir, "picked.wav");

        vm.BrowseAudioCommand.Execute(null);

        Assert.Equal(h.Storage.NextOpenFile, vm.AudioPath);
        Assert.Equal("*.wav", Assert.Single(Assert.Single(h.Storage.RequestedFilters)!).Patterns.Single());
    }

    [Fact]
    public void Conforms_to_its_contract()
    {
        var h = new Harness();
        h.LoadProject(Path.Combine(_dir, "mix.ogg"));
        Assert.Empty(ContractConformanceTests.Violations(typeof(IChartFromAudioScreen), h.Open()));
    }
}
