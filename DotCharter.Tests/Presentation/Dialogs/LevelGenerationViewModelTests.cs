using System.Collections.ObjectModel;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Editing.Sync;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     Level generation stays open for the run, drives a status-bar task pill,
///     and closes (answers) only when the work is actually done. Staying open is
///     load-bearing rather than cosmetic: the modal scrim and
///     <c>GlobalChordGate</c> are what stop the live project being edited
///     between the coordinator's snapshot and its history node — so Esc is
///     REFUSED mid-run (<c>ICancelableDialog.TryCancel</c>).
/// </summary>
public sealed class LevelGenerationViewModelTests
{
    [Fact]
    public async Task Apply_StaysOpenWhileGenerating_AndClosesOnlyWhenTheWorkIsDone()
    {
        var maintenance = new GatedMaintenance();
        var vm = CreateViewModel(maintenance, out _);

        var apply = Apply(vm);
        Assert.True(maintenance.Started.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
            "generation never started");

        // Mid-run: the dialog has not answered, and it says it is working.
        Assert.False(vm.Result.IsCompleted);
        Assert.True(vm.IsBusy);

        maintenance.Release.Set();
        await apply;

        Assert.False(vm.IsBusy);
        Assert.True(ChartEditHarness.Answer(vm.Result));
    }

    /// <summary>The pill is the surface that carries the work OUTSIDE the
    /// dialog — and it must be cleared however the run ends.</summary>
    [Fact]
    public async Task Apply_BeginsAndCompletesExactlyOneStatusBarTask()
    {
        var maintenance = new GatedMaintenance();
        var vm = CreateViewModel(maintenance, out var statusBar);
        maintenance.Release.Set();

        await Apply(vm);

        Assert.Equal(["level-generation"], statusBar.Begun);
        Assert.Equal(["level-generation"], statusBar.Completed);
    }

    [Fact]
    public async Task Apply_WhenTheToolThrows_StillClearsTheBusyStateAndThePill()
    {
        var maintenance = new GatedMaintenance { Throw = true };
        var vm = CreateViewModel(maintenance, out var statusBar);
        maintenance.Release.Set();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Apply(vm));

        Assert.False(vm.IsBusy);
        Assert.False(ChartEditHarness.Answer(vm.Result));
        Assert.Equal(["level-generation"], statusBar.Completed);
    }

    /// <summary>Generation is ONE transaction that cannot be half-applied, so
    /// there is nothing to cancel — closing mid-run would only hide a stall that
    /// is still happening.</summary>
    [Fact]
    public async Task Cancel_WhileGenerating_DoesNotCloseTheDialogOutFromUnderTheRun()
    {
        var maintenance = new GatedMaintenance();
        var vm = CreateViewModel(maintenance, out _);

        var apply = Apply(vm);
        Assert.True(maintenance.Started.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
            "generation never started");

        vm.CancelCommand.Execute(null);
        Assert.False(vm.TryCancel(), "Esc must be refused while the run is in flight");
        Assert.False(vm.Result.IsCompleted);

        maintenance.Release.Set();
        await apply;
    }

    [Fact]
    public void Cancel_WhileIdle_ClosesTheDialog()
    {
        var vm = CreateViewModel(new GatedMaintenance(), out _);

        vm.CancelCommand.Execute(null);

        Assert.False(ChartEditHarness.Answer(vm.Result));
    }

    /// <summary>The dialog asks for the clamped INTEGER count, not the raw
    /// slider double.</summary>
    [Fact]
    public async Task Apply_ClampsAndRoundsTheRequestedLevelCount()
    {
        var maintenance = new GatedMaintenance();
        var vm = CreateViewModel(maintenance, out _);
        vm.LevelCount = 999;
        maintenance.Release.Set();

        await Apply(vm);

        Assert.Equal((int)vm.Maximum, maintenance.RequestedLevels);
    }

    // --------------------------------------------------------------- fixtures

    [Fact]
    public void The_subtitle_names_the_arrangement_and_the_levels_a_run_replaces()
    {
        var state = new ProjectState();
        var arrangement = new Arrangement { Name = "Rhythm" };
        arrangement.Levels.Add(new ChartLevel());
        arrangement.Levels.Add(new ChartLevel());
        state.ActiveArrangement = arrangement;

        var vm = new LevelGenerationViewModel(new GatedMaintenance(), state, FakeLocalization.English(),
            new FakeAppLogger());

        Assert.Contains("Rhythm", vm.Subtitle, StringComparison.Ordinal);
        Assert.Contains("2", vm.Subtitle, StringComparison.Ordinal);
    }

    [Fact]
    public void Conforms_to_its_contract()
    {
        // The rule waits for an async verb to finish, so the gate starts open, and
        // the run reports no progress: Progress<T> would call back on a pool thread
        // AFTER the task completed and land either side of the rule's after-read.
        var maintenance = new GatedMaintenance { ReportsProgress = false };
        maintenance.Release.Set();
        Assert.Empty(ContractConformanceTests.Violations(typeof(ILevelGenerationScreen),
            CreateViewModel(maintenance, out _)));
    }

    private static Task Apply(LevelGenerationViewModel vm) =>
        ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.ApplyCommand).ExecuteAsync(null);

    private static LevelGenerationViewModel CreateViewModel(
        IChartMaintenanceService maintenance, out RecordingStatusBar statusBar)
    {
        statusBar = new RecordingStatusBar();
        return new LevelGenerationViewModel(maintenance, new ProjectState(), FakeLocalization.English(),
            new FakeAppLogger(), statusBar);
    }

    private sealed class RecordingStatusBar : IStatusBarService
    {
        public List<string> Begun { get; } = [];
        public List<string> Completed { get; } = [];

        public ObservableCollection<StatusBarMessage> RecentMessages { get; } = [];
        public ObservableCollection<StatusBarTask> ActiveTasks { get; } = [];

        public StatusBarTask BeginTask(string id, string label, bool indeterminate = true, Action? cancel = null)
        {
            Begun.Add(id);
            return new StatusBarTask(id, label, indeterminate);
        }

        public void ReportProgress(string id, double progress, string? newLabel = null) { }
        public void CompleteTask(string id) => Completed.Add(id);
        public void ClearMessages() { }
    }

    /// <summary>Blocks inside the async tool so the test can look at the dialog
    /// mid-run — the whole point of the change is what is on screen then.</summary>
    private sealed class GatedMaintenance : IChartMaintenanceService
    {
        public ManualResetEventSlim Started { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public int RequestedLevels { get; private set; }
        public bool Throw { get; init; }
        public bool ReportsProgress { get; init; } = true;

        public async Task<bool> GenerateLevelsAsync(int levelCount,
            IProgress<LevelGenerationProgress>? progress = null)
        {
            RequestedLevels = levelCount;
            Started.Set();
            await Task.Run(() => Release.Wait(TimeSpan.FromSeconds(10)));
            if (Throw) throw new InvalidOperationException("level generation exploded");
            if (ReportsProgress) progress?.Report(new LevelGenerationProgress(levelCount, levelCount));
            return true;
        }

        // Nothing else on the coordinator is reachable from this dialog.
        public bool GenerateLevels(int levelCount) => throw new NotSupportedException();
        public void ImproveChart() => throw new NotSupportedException();
        public void RemoveDoubleStopChordNames() => throw new NotSupportedException();
        public bool RunAsSingleUndoStep(Action mutate) => throw new NotSupportedException();
        public void DeleteActiveArrangement() => throw new NotSupportedException();
        public void DeleteActiveVocalTrack() => throw new NotSupportedException();
        public void AutoFixChart() => throw new NotSupportedException();
        public void SquashLevels() => throw new NotSupportedException();
        public int ApplyDlcBuilderFixes(IReadOnlyList<Arrangement>? arrangements = null) => throw new NotSupportedException();
        public int FixExportFinding(DotCharter.Services.Export.Validation.ExportFixKind kind, Arrangement arrangement) =>
            throw new NotSupportedException();
        public DotCharter.Services.Editing.Tones.ToneKeyRepairProposal? RepairUndefinedToneKey(string undefinedKey) =>
            throw new NotSupportedException();
        public int PropagatePhraseAtPlayhead() => throw new NotSupportedException();
        public bool ApplyMidiDraft(DotCharter.Services.Parsing.Midi.MidiChartDraft draft, bool replaceBeatGrid)
            => throw new NotSupportedException();
        public bool Refret() => throw new NotSupportedException();
        public void GenerateFhp() => throw new NotSupportedException();
        public void GenerateFhpForSelection() => throw new NotSupportedException();
        public void GenerateHandShapesForArrangement() => throw new NotSupportedException();
        public void ApplyHandShapeTemplateToSelectedChords() => throw new NotSupportedException();
        public void SetDefaultStartSilence(double seconds) => throw new NotSupportedException();
        public Task AddSilenceAtBeginningAsync(double seconds) => throw new NotSupportedException();
        public Task AddSilenceAtEndAsync(double seconds) => throw new NotSupportedException();
        public void AddMeasuresAtStart(int measureCount) => throw new NotSupportedException();
        public int CopyStructureToOtherArrangements() => throw new NotSupportedException();
        public ArrangementSyncSummary SyncArrangement(Arrangement source, Arrangement target) =>
            throw new NotSupportedException();
        public void AddMeasuresAtEnd(int measureCount) => throw new NotSupportedException();
        public void InsertBeatAtPlayhead() => throw new NotSupportedException();
        public void RemoveBeatAtPlayhead() => throw new NotSupportedException();
        public bool AlignBeatGridToNotes(double offsetMs) => throw new NotSupportedException();
        public bool ToggleBeatMapLock() => throw new NotSupportedException();
        public void InsertVocalAtPlayhead() => throw new NotSupportedException();
        public void ToggleSelectedChordSplit() => throw new NotSupportedException();
        public void ToggleSelectedChordOnlyBox() => throw new NotSupportedException();
        public void ToggleSelectedChordPassOtherNotes() => throw new NotSupportedException();
        public void ConvertSelectedNotesToPowerChord(bool big) => throw new NotSupportedException();
        public void ToggleTwoFretOctaveFingering() => throw new NotSupportedException();
        public void TogglePowerChordFingering() => throw new NotSupportedException();
        public void MergeSelectedNotesIntoChords() => throw new NotSupportedException();
        public void ConvertMutedStrumsToChords() => throw new NotSupportedException();
        public void SplitSelectedChordsIntoNotes() => throw new NotSupportedException();
        public void ToggleGhostGems() => throw new NotSupportedException();
        public void MakeTappedHarmonic() => throw new NotSupportedException();
    }
}
