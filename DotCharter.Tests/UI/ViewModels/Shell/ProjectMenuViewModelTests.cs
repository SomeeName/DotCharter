using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Session;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.UI.ViewModels.Shell;

/// <summary>
///     The File menu's project verbs, Open Recent and the file-drop funnel left the
///     shell for <see cref="ProjectMenuViewModel" />. The seam that matters most is
///     the one it wires on construction: the session's "may I replace the open
///     project?" question is answered by the hosted unsaved-changes dialog, and an
///     unwired prompt would let File ▸ Open throw away unsaved work.
/// </summary>
public sealed class ProjectMenuViewModelTests
{
    [Fact]
    public async Task DroppingAProjectAfterResults_UnwindsThePreviousRunsTransport()
    {
        using var h = new DotCharter.Tests.UI.ViewModels.PlayMode.PlayModeReviewHarness();
        var arrangement = h.State.ActiveArrangement!;
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(-1));
        h.Vm.SelectedFlowArrangement = new DotCharter.UI.ViewModels.PlayMode.ArrangementOption(arrangement, FakeLocalization.English());
        h.Vm.DropTuner.IsOn = true;
        h.Vm.PracticeSpeedPercent = 50;
        h.Begin();
        Assert.Equal(0.5, h.Transport.PlaybackSpeed);
        Assert.Equal(-1, h.Transport.InputPitchShiftSemis);
        h.FinishForResults();
        var next = new ChartProject();
        var session = new CheckpointsViewModelTests.FakeSession
        {
            LoadFileHandler = _ =>
            {
                h.State.ActiveProject = next;
                return Task.CompletedTask;
            }
        };
        using var project = Build(session, new AnsweringDialogs(UnsavedChangesChoice.Cancel), new ExportTargetWriter(), h.State);
        // Only the real editor handler is needed; unchanged editor flags avoid unrelated shell services.
        var shell = (MainWindowViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
        typeof(MainWindowViewModel).GetField("<PlayMode>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(shell, h.Vm);
        var shellSource = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Shell", "MainWindowViewModel.cs"));
        var handler = System.Text.RegularExpressions.Regex.Match(shellSource, @"Project\.ProjectOpened \+= (\w+);").Groups[1].Value;
        var showEditor = typeof(MainWindowViewModel).GetMethod(handler, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        project.ProjectOpened += showEditor.CreateDelegate<Action>(shell);

        await project.LoadFile("next.dcproj");

        Assert.Same(next, h.State.ActiveProject);
        Assert.Equal(1, h.Transport.PlaybackSpeed);
        Assert.Equal(0, h.Transport.InputPitchShiftSemis);
        Assert.False(h.Live.IsActive);
        Assert.True(shell.IsChartEditorVisible);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpeningAProject_RequestsTheEditorOnlyAfterTheLoadCompletes(bool alreadyOpen)
    {
        var state = new ProjectState
        {
            ActiveProject = alreadyOpen ? new ChartProject() : null,
            CurrentProjectPath = Path.Combine("songs", "project.dcproj")
        };
        var loaded = new ChartProject();
        var loadFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new CheckpointsViewModelTests.FakeSession
        {
            LoadFileHandler = path =>
            {
                Assert.Equal(state.CurrentProjectPath, path);
                state.ActiveProject = loaded;
                return loadFinished.Task;
            }
        };
        using var project = Build(session, new AnsweringDialogs(UnsavedChangesChoice.Cancel),
            new ExportTargetWriter(), state);
        var requests = 0;
        project.ProjectOpened += () =>
        {
            Assert.Same(loaded, state.ActiveProject);
            requests++;
        };

        var opening = project.LoadFile(state.CurrentProjectPath);
        Assert.False(opening.IsCompleted);
        Assert.Equal(0, requests);

        loadFinished.SetResult();
        await opening;

        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData("project.dcproj", false)]
    [InlineData("project.dcproj", true)]
    [InlineData("audio.wav", false)]
    [InlineData("audio.wav", true)]
    public async Task ALoadThatDoesNotReplaceTheProject_DoesNotRequestTheEditor(string file, bool alreadyOpen)
    {
        // A declined discard prompt or failed parse leaves the graph unchanged;
        // attaching/auditioning audio also leaves it unchanged.
        var state = new ProjectState { ActiveProject = alreadyOpen ? new ChartProject() : null };
        var previous = state.ActiveProject;
        var session = new CheckpointsViewModelTests.FakeSession();
        using var project = Build(session, new AnsweringDialogs(UnsavedChangesChoice.Cancel),
            new ExportTargetWriter(), state);
        var requests = 0;
        project.ProjectOpened += () => requests++;

        await project.LoadFile(file);

        Assert.Same(previous, state.ActiveProject);
        Assert.Equal(0, requests);
    }

    [Theory]
    [InlineData(UnsavedChangesChoice.Discard, true)]
    [InlineData(UnsavedChangesChoice.Cancel, false)]
    public async Task Construction_HandsTheSessionItsDiscardPrompt_WhichAsksTheHostedDialog(
        UnsavedChangesChoice answer, bool proceeds)
    {
        var session = new CheckpointsViewModelTests.FakeSession();
        var dialogs = new AnsweringDialogs(answer);
        Build(session, dialogs, new ExportTargetWriter());

        Assert.NotNull(session.ConfirmDiscardAsync);
        Assert.Equal(proceeds, await session.ConfirmDiscardAsync!());
        Assert.Equal([typeof(IUnsavedChangesScreen)], dialogs.Opened);
    }

    /// <summary>#751 P4 review: the close and replace prompts' Save is a PROMPT save, which applies the staged drafts
    /// the prompt counted before it writes (a plain user save would drop them silently).</summary>
    [Fact]
    public async Task ThePromptsSave_IsAPromptSave_SoStagedDraftsAreApplied()
    {
        var session = new CheckpointsViewModelTests.FakeSession();
        using var project = Build(session, new AnsweringDialogs(UnsavedChangesChoice.Save), new ExportTargetWriter());

        Assert.True(await session.ConfirmDiscardAsync!());
        Assert.True(await project.SaveProjectForCloseAsync());

        Assert.Equal([SaveInitiator.Prompt, SaveInitiator.Prompt], session.SaveInitiators);
    }

    /// <summary>The window-close gate asks the session for unsaved WORK, not its dirty flag: a draft
    /// still in a text box is unsaved work while the flag reads clean.</summary>
    [Fact]
    public void TheCloseGate_AsksTheSessionForUnsavedWork_NotItsDirtyFlag()
    {
        var session = new CheckpointsViewModelTests.FakeSession { IsProjectDirty = false, PendingDraft = true };
        using var project = Build(session, new AnsweringDialogs(UnsavedChangesChoice.Cancel), new ExportTargetWriter());

        Assert.True(project.HasUnsavedWork());
        Assert.Equal(1, session.UnsavedWorkAsked);
    }

    [Fact]
    public async Task ARecentProjectThatVanished_SaysSo_InsteadOfLoading()
    {
        var state = new ProjectState();
        var (writer, recorder) = RecordingProxy.Create<IExportTargetWriter>();
        var project = Build(new CheckpointsViewModelTests.FakeSession(), new AnsweringDialogs(UnsavedChangesChoice.Cancel),
            writer, state);

        await project.OpenRecentProjectAsync(Path.Combine("songs", "gone.rscp"));

        Assert.Equal(FakeLocalization.English().Format("Status_RecentProjectMissing", "gone.rscp"),
            state.MainDisplayMessage);
        Assert.Contains(recorder.Calls, c => c.StartsWith("FileExists(", StringComparison.Ordinal));
    }

    private static ProjectMenuViewModel Build(CheckpointsViewModelTests.FakeSession session, IDialogs dialogs,
        IExportTargetWriter writer, ProjectState? state = null)
    {
        state ??= new ProjectState();
        var logger = new FakeAppLogger();
        var localization = FakeLocalization.English();
        var (maintenance, _) = RecordingProxy.Create<IChartMaintenanceService>();
        return new ProjectMenuViewModel(session, state, new ImportMenuViewModel(logger, ImportMenuViewModelTests.Offer(state, logger), ImportMenuViewModelTests.CleanupOffer()),
            maintenance, dialogs, new TestUiDispatcher(), logger, localization,
            DotCharter.Services.Plugins.PackageFeatures.All, new FakeFileStorageService(),
            exportFiles: writer);
    }

    /// <summary>Answers every dialog with one value and records which contract was opened.</summary>
    private sealed class AnsweringDialogs(UnsavedChangesChoice answer) : IDialogs
    {
        public List<Type> Opened { get; } = [];

        public bool HasOpenDialog => false;

        public event EventHandler<DialogChange>? Changed
        {
            add { }
            remove { }
        }

        public Task<TResult?> ShowAsync<TContract, TResult>(Action<TContract>? init = null)
            where TContract : class, IDialogScreen<TResult>
        {
            Opened.Add(typeof(TContract));
            return Task.FromResult((TResult?)(object)answer);
        }

        public Task<bool> MessageAsync(string title, string message, bool isConfirmation = false) => Task.FromResult(false);

        public bool CloseTop() => false;
    }
}
