using Avalonia.Controls;

namespace DotCharter.UI.Views.Shell;

/// <summary>
///     A view that hides sub-surfaces behind <c>IsVisible</c> — settings tabs,
///     Play Mode screens, the closed dialogs — and therefore makes the user pay
///     each one's first layout on their first switch into it.
/// </summary>
/// <remarks>
///     <para>
///         Implement this and <c>MainWindow.PrewarmTopLevelViews</c> realizes the
///         surfaces after the first frame, at
///         <c>DispatcherPriority.Background</c>, one per dispatcher turn (see
///         <see cref="HiddenSubtreePrewarmer" />). Nothing else is required: the
///         shell discovers implementors through its deferred hosts.
///     </para>
///     <para>
///         Return the surfaces in the order the user is most likely to reach
///         them; the drain is interruptible and the earlier entries win.
///         <b>Return only what is hidden right now</b> — a visible surface has
///         already been laid out and re-realizing it is wasted work
///         (<see cref="HiddenSubtreePrewarmer.HiddenChildrenOf" /> and
///         <see cref="HiddenSubtreePrewarmer.HiddenContentOf" /> do the filtering).
///     </para>
/// </remarks>
public interface IPrewarmableSurfaces
{
    /// <summary>Called on the UI thread after the first frame. Cheap: it only
    /// enumerates, it never lays anything out.</summary>
    IReadOnlyList<Control> HiddenSurfaces();
}
