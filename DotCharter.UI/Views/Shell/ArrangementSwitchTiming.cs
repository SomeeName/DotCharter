using Avalonia.Threading;

namespace DotCharter.UI.Views.Shell;

/// <summary>
///     Times each arrangement switch the USER makes, from the change to the first
///     <see cref="DispatcherPriority.ApplicationIdle" /> tick. An open
///     also moves <see cref="ProjectState.ActiveArrangement" />: it parks on the first
///     arrangement, then the view memory restores the remembered one. Counted as
///     switches, those logged the whole open (deserialize, audio decode, stem load)
///     as the first switch, up to 7.5 s while a real switch took under
///     200 ms. So a change is a switch only when it leaves an arrangement of
///     the open project and no open is in progress.
/// </summary>
public static class ArrangementSwitchTiming
{
    /// <summary>Starts the switch timing on every user switch of <paramref name="state" />.</summary>
    public static void Observe(ProjectState state)
    {
        var sequence = 0L;
        state.PropertyChanging += (_, e) =>
        {
            if (e.PropertyName == nameof(ProjectState.ActiveArrangement) && IsUserSwitch(state))
                SurfaceShowTiming.Begin($"Arrangement switch #{++sequence}", DispatcherPriority.ApplicationIdle);
        };
    }

    /// <summary>
    ///     Read while the change is ANNOUNCED (<c>PropertyChanging</c>), so
    ///     <see cref="ProjectState.ActiveArrangement" /> is still the outgoing one.
    /// </summary>
    public static bool IsUserSwitch(ProjectState state) =>
        !state.IsOpeningProject
        && state.ActiveArrangement is { } outgoing
        && state.ActiveProject?.Arrangements.Contains(outgoing) == true;
}
