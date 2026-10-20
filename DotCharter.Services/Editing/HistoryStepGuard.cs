using DotCharter.Localization;
using DotCharter.Services.State;
using ProjectState = DotCharter.Services.State.ProjectState;

namespace DotCharter.Services.Editing;

/// <summary>
///     Runs one user-invoked history step (undo, redo, or a jump in the History
///     dialog) and turns a failure into a status line instead of a dead process.
///     <para>
///         <b>Every history verb was an unguarded call, and the throw is a
///         DESIGNED one.</b> <c>HistoryService.RunOrReportFailure</c> raises
///         <c>ActionFailed</c> and then deliberately RETHROWS — its own doc
///         comment says "this is a notification, not a handler" and leaves the
///         decision to the caller. No caller decided: <c>GlobalEditingCoordinator
///         .Undo/Redo</c> (the keybind and menu funnel),
///         <c>ChartEditorViewModel.Undo/Redo</c> (the toolbar commands) and
///         <c>HistoryViewModel.JumpTo</c> all called straight through, and
///         <c>App</c>'s <c>AppDomain.UnhandledException</c> handler only LOGS —
///         it cannot stop the terminate. So an action whose inverse threw took
///         the whole editor down, which is the precise opposite of what
///         <c>ActionFailed</c> exists for: that event marks the project dirty so
///         the work is not silently discarded, and then the process died with
///         the work in it.
///     </para>
///     <para>
///         The chart may be half-reverted afterwards — that is exactly what the
///         dirty flag now says — so this reports LOUDLY (an error status the user
///         cannot miss) rather than swallowing. It never re-runs the step.
///     </para>
/// </summary>
public static class HistoryStepGuard
{
    /// <summary>Runs <paramref name="step" />; returns false when it threw.</summary>
    public static bool Run(Action step, string what, ProjectState? projectState, IAppLogger? logger,
        ILocalizationService? localization)
    {
        ArgumentNullException.ThrowIfNull(step);
        try
        {
            step();
            return true;
        }
        catch (Exception ex)
        {
            logger?.Error(AppLogCategory.History,
                $"{what} threw; the chart may be partially reverted. The project is marked dirty " +
                "(IHistoryService.ActionFailed) and the session continues.", ex);
            projectState?.SetMessage(
                localization?.Format("Status_HistoryStepFailed", ex.Message) ?? ex.Message,
                StatusSeverity.Error);
            return false;
        }
    }
}
