using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Diagnostics;
using DotCharter.Services.Editing.Linting;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     Batch lint is the one power tool that walks a whole folder tree and
///     REWRITES <c>.rscp</c> files the user never opened, with no undo. The run
///     can be stopped, the bulk overwrite is confirmed with its file count
///     before the first byte, and what it wrote is reported afterwards.
///     Close and Esc stay live for the whole run: they cancel it.
/// </summary>
public sealed class BatchLintViewModelTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dotcharter-batchlint-vm-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    // ------------------------------------------------------------ cancellation

    [Fact]
    public async Task Cancel_while_busy_cancels_the_run_and_keeps_the_dialog_open()
    {
        var lint = new GatedLintService();
        var (vm, _) = Create(lint);

        var run = Run(vm);
        // A DEADLOCK DETECTOR, not a budget — see GatedLintService.
        Assert.True(lint.Started.Wait(GatedLintService.Rendezvous, TestContext.Current.CancellationToken),
            "the batch never started");

        vm.CancelCommand.Execute(null);
        Assert.False(vm.TryCancel(), "Esc during a run must cancel the run, not close over it");
        lint.Release.Set();
        await run;

        Assert.True(lint.ReleaseWasSignalled,
            "the fake lint service gave up waiting for the test, so the token it read was not this test's cancel");
        Assert.True(lint.TokenWasCanceled, "the run's cancellation token was never signalled");
        Assert.False(vm.Result.IsCompleted, "cancelling the RUN closed the dialog");
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Cancel_while_idle_closes_the_dialog()
    {
        var (vm, _) = Create(new RecordingLintService());

        vm.CancelCommand.Execute(null);

        Assert.False(ChartEditHarness.Answer(vm.Result));
    }

    // ----------------------------------------------------- the pre-flight gate

    [Fact]
    public async Task Run_with_save_requested_and_the_user_declining_never_reaches_the_service()
    {
        var lint = new RecordingLintService();
        var (vm, prompt) = Create(lint, answer: false);
        vm.ApplyAutoFixes = true;
        vm.SaveFixedProjects = true;

        await Run(vm);

        var (_, _, kind, confirmation) = Assert.Single(prompt.Shown);
        Assert.True(confirmation);
        Assert.Equal(MessageDialogKind.Warning, kind);
        Assert.Equal(0, lint.RunCount);
        Assert.Equal(FakeLocalization.English()["BatchLint_OverwriteDeclined"], vm.Status);
    }

    /// <summary>…and the prompt is told how many files are at stake.</summary>
    [Fact]
    public async Task Run_with_save_requested_names_the_candidate_count_then_writes()
    {
        WriteDummyProject("second.rscp");
        var lint = new RecordingLintService();
        var (vm, prompt) = Create(lint);
        vm.ApplyAutoFixes = true;
        vm.SaveFixedProjects = true;

        await Run(vm);

        Assert.Contains(FakeLocalization.English().Plural(
            "BatchLint_ConfirmOverwriteOne", "BatchLint_ConfirmOverwriteFmt", 2, _tempDir), prompt.Shown[0].Message);
        Assert.Equal(1, lint.RunCount);
        Assert.True(lint.LastOptions!.SaveFixedProjects);
    }

    /// <summary>A validate-only run writes nothing, so it is not gated — a prompt for a read-only sweep is noise.</summary>
    [Fact]
    public async Task Run_without_saving_is_not_gated_by_the_overwrite_confirmation()
    {
        var lint = new RecordingLintService();
        var (vm, prompt) = Create(lint, answer: false);

        await Run(vm);

        Assert.Empty(prompt.Shown);
        Assert.Equal(1, lint.RunCount);
        Assert.False(lint.LastOptions!.SaveFixedProjects);
    }

    [Fact]
    public void Turning_the_fixes_off_clears_the_overwrite_opt_in()
    {
        var (vm, _) = Create(new RecordingLintService());
        vm.ApplyAutoFixes = true;
        vm.SaveFixedProjects = true;

        vm.ApplyAutoFixes = false;

        Assert.False(vm.SaveFixedProjects);
        Assert.False(vm.CanSaveFixedProjects);
        vm.SaveFixedProjects = true;
        Assert.False(vm.SaveFixedProjects);
    }

    // ------------------------------------------------------ the after-the-fact

    [Fact]
    public async Task A_run_that_wrote_reports_what_it_overwrote_and_lists_each_file()
    {
        var lint = new RecordingLintService
        {
            Result = new ChartLintBatchResult(
            [
                new ChartLintReport(Path.Combine(_tempDir, "a.rscp"), null,
                [
                    new ArrangementLintReport("Lead", 3, [], new Dictionary<string, int> { ["duplicatesRemoved"] = 2 })
                ], saved: true)
            ])
        };
        var (vm, prompt) = Create(lint);
        vm.ApplyAutoFixes = true;
        vm.SaveFixedProjects = true;

        await Run(vm);

        var report = prompt.Shown[^1];
        Assert.False(report.Confirmation);
        Assert.Contains("duplicatesRemoved × 2", report.Message);
        var row = Assert.Single(vm.Results);
        Assert.Equal(("a.rscp", true, false), (row.FileName, row.Saved, row.Failed));
        Assert.True(vm.HasResults);

        vm.CancelCommand.Execute(null);
        Assert.True(ChartEditHarness.Answer(vm.Result));
    }

    [Fact]
    public void DescribeOverwrite_with_no_fixes_is_just_the_headline() => Assert.DoesNotContain("\n", Create(new RecordingLintService()).Vm.DescribeOverwrite(new ChartLintBatchResult([])));

    [Fact]
    public async Task A_missing_folder_says_so_and_runs_nothing()
    {
        var lint = new RecordingLintService();
        var (vm, _) = Create(lint, folder: Path.Combine(_tempDir, "gone"));

        await Run(vm);

        Assert.Equal(0, lint.RunCount);
        Assert.Equal(FakeLocalization.English()["BatchLint_NoFolder"], vm.Status);
    }

    /// <summary>The folder and the auto-fix choice carry over to the next open; the overwrite opt-in does not.</summary>
    [Fact]
    public void The_folder_and_the_fix_choice_carry_over_but_the_overwrite_does_not()
    {
        var memory = new DialogMemory { LintFolderPath = _tempDir };
        var first = Create(new RecordingLintService(), memory: memory).Vm;
        first.ApplyAutoFixes = true;
        first.SaveFixedProjects = true;

        var second = Create(new RecordingLintService(), memory: memory).Vm;

        Assert.Equal(_tempDir, second.FolderPath);
        Assert.True(second.ApplyAutoFixes);
        Assert.False(second.SaveFixedProjects);
    }

    [Fact]
    public void Conforms_to_its_contract()
    {
        var (vm, _) = Create(new RecordingLintService());
        Assert.Empty(ContractConformanceTests.Violations(typeof(IBatchLintScreen), vm));
    }

    // --------------------------------------------------------------- fixtures

    private (BatchLintViewModel Vm, ScriptedPrompt Prompt) Create(IBatchChartLintService lint, bool answer = true,
        string? folder = null, DialogMemory? memory = null)
    {
        WriteDummyProject("first.rscp");
        var prompt = new ScriptedPrompt(answer);
        memory ??= new DialogMemory { LintFolderPath = folder ?? _tempDir };
        return (new BatchLintViewModel(lint, new FakeFileStorageService(), new FakeConfigurationService(),
            new ExportTargetWriter(), prompt, memory, new DialogBasics(FakeLocalization.English(), new FakeAppLogger())), prompt);
    }

    private static Task Run(BatchLintViewModel vm) => ((IAsyncRelayCommand)vm.RunCommand).ExecuteAsync(null);

    /// <summary>The lint services below never open the file — the enumeration is all the view model reads.</summary>
    private void WriteDummyProject(string name) => File.WriteAllText(Path.Combine(_tempDir, name), "not a real project");

    private sealed class RecordingLintService : IBatchChartLintService
    {
        public int RunCount { get; private set; }

        public ChartLintOptions? LastOptions { get; private set; }

        public ChartLintBatchResult Result { get; init; } = new([]);

        public ChartLintBatchResult Run(IEnumerable<string> projectFilePaths, ChartLintOptions? options = null,
            IProgress<ChartLintProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            RunCount++;
            LastOptions = options;
            return Result;
        }
    }

    /// <summary>Blocks inside Run so the test can press Cancel mid-batch, then reports whether its token was signalled.</summary>
    private sealed class GatedLintService : IBatchChartLintService
    {
        /// <summary>A DEADLOCK DETECTOR: a green run crosses in microseconds and never reads it.</summary>
        public static readonly TimeSpan Rendezvous = TimeSpan.FromSeconds(60);

        public ManualResetEventSlim Started { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public bool TokenWasCanceled { get; private set; }

        /// <summary>False when the wait EXPIRED, which would make the token reading an artefact of a starved box.</summary>
        public bool ReleaseWasSignalled { get; private set; }

        public ChartLintBatchResult Run(IEnumerable<string> projectFilePaths, ChartLintOptions? options = null,
            IProgress<ChartLintProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Started.Set();
            ReleaseWasSignalled = Release.Wait(Rendezvous);
            TokenWasCanceled = cancellationToken.IsCancellationRequested;
            return new ChartLintBatchResult([], TokenWasCanceled);
        }
    }
}
