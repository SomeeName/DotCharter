using DotCharter.UI.Docking;

namespace DotCharter.UI.Views.Shell;

/// <summary>
/// The dock layout as it was when a gameplay run started, put back when it ends.
///     <para>A run hides the editor panes through their visibility FLAGS, and a flag only
///     knows "shown" or "hidden": turning it back on adds the pane to its tool dock, so an
///     Inspector the user had floated came back docked. The view model keeps restoring the
///     flags; this restores where each pane lived (floating at its bounds, pinned, the
///     active tab) once the flags are back.</para>
/// </summary>
internal sealed class GameplayDockSnapshot
{
    private DockLayoutState? _preRun;

    /// <summary>Call BEFORE the run hides the panes.</summary>
    public void BeforeGameplay(params DockLayoutController?[] controllers)
    {
        var state = new DockLayoutState();
        foreach (var controller in controllers) controller?.CaptureState(state);
        _preRun = state.Panes.Count == 0 ? null : state;
    }

    /// <summary>Call AFTER the run has put the visibility flags back.</summary>
    public void AfterGameplay(params DockLayoutController?[] controllers)
    {
        if (_preRun is not { } state) return;
        _preRun = null;
        foreach (var controller in controllers) controller?.RestoreState(state);
    }
}
