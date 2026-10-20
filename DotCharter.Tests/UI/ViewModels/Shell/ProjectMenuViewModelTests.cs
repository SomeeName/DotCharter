using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing.Maintenance;
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
        return new ProjectMenuViewModel(session, state, new ImportMenuViewModel(logger, ImportMenuViewModelTests.Offer(state, logger)),
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
