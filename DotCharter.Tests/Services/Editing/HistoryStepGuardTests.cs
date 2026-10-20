using DotCharter.Services.Editing;

namespace DotCharter.Tests.Services.Editing;

/// <summary>
///     <c>HistoryService</c> RETHROWS after raising <c>ActionFailed</c> — by
///     design, "a notification, not a handler". No user-facing verb decided what
///     to do with it, so an inverse that threw terminated the process:
///     <c>App</c>'s <c>AppDomain.UnhandledException</c> hook only logs, and the
///     dirty flag that <c>ActionFailed</c> had just set went down with the
///     window it was protecting.
/// </summary>
public sealed class HistoryStepGuardTests
{
    private sealed class ThrowingAction : IChartAction
    {
        public bool ThrowOnUndo { get; set; }
        public int UndoCount { get; private set; }
        public bool HasEffect => true;
        public void Execute() { }

        public void Undo()
        {
            UndoCount++;
            if (ThrowOnUndo) throw new InvalidOperationException("the inverse blew up");
        }
    }

    [Fact]
    public void AThrowingUndoIsReportedInsteadOfKillingTheSession()
    {
        var state = new ProjectState();
        var logger = new FakeAppLogger();
        var history = new HistoryService(logger);
        var action = new ThrowingAction();
        history.Do(action);
        action.ThrowOnUndo = true;

        var ok = HistoryStepGuard.Run(history.Undo, "Undo", state, logger, FakeLocalization.English());

        Assert.False(ok);
        Assert.Equal(1, action.UndoCount);
        Assert.False(string.IsNullOrWhiteSpace(state.MainDisplayMessage));
        Assert.Equal(StatusSeverity.Error, state.MainDisplaySeverity);
    }

    /// <summary>…and the dirty flag the failure raised must survive, since
    /// keeping the work is the whole reason <c>ActionFailed</c> exists.</summary>
    [Fact]
    public void TheProjectStaysDirtyAfterAFailedUndo()
    {
        var history = new HistoryService(new FakeAppLogger());
        using var dirty = new ProjectDirtyTracker(history);
        var action = new ThrowingAction();
        history.Do(action);
        dirty.MarkClean();
        Assert.False(dirty.IsDirty);

        action.ThrowOnUndo = true;
        HistoryStepGuard.Run(history.Undo, "Undo", new ProjectState(), new FakeAppLogger(), FakeLocalization.English());

        Assert.True(dirty.IsDirty);
    }

    [Fact]
    public void AStepThatSucceedsIsSilent()
    {
        var state = new ProjectState();
        var before = state.MainDisplayMessage;
        var history = new HistoryService(new FakeAppLogger());
        history.Do(new ThrowingAction());

        var ok = HistoryStepGuard.Run(history.Undo, "Undo", state, new FakeAppLogger(), FakeLocalization.English());

        Assert.True(ok);
        Assert.Equal(before, state.MainDisplayMessage);
        Assert.NotEqual(StatusSeverity.Error, state.MainDisplaySeverity);
    }

    /// <summary>The three verbs a user can reach the history with must all go
    /// through the guard — the fix is worth nothing at a call site that skipped
    /// it, and there is no way to construct that crash in the default gate.</summary>
    [Theory]
    [InlineData("DotCharter.Services/Editing/GlobalEditingCoordinator.cs", "_historyManager.Undo")]
    [InlineData("DotCharter.Services/Editing/GlobalEditingCoordinator.cs", "_historyManager.Redo")]
    [InlineData("DotCharter.UI/ViewModels/Editor/ChartEditorViewModel.cs", "HistoryManager.Undo")]
    [InlineData("DotCharter.UI/ViewModels/Editor/ChartEditorViewModel.cs", "HistoryManager.Redo")]
    [InlineData("DotCharter.Presentation/Screens/Dialogs/HistoryViewModel.cs", "_history.NavigateTo")]
    public void EveryUserFacingHistoryVerbIsGuarded(string relativePath, string call)
    {
        var source = UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), relativePath));

        // The bare invocation is what crashed; only the guarded form may remain.
        Assert.DoesNotContain(call + "();", source, StringComparison.Ordinal);
        Assert.Contains("HistoryStepGuard.Run(", source, StringComparison.Ordinal);
    }
}
