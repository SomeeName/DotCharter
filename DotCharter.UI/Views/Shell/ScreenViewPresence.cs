using System.Runtime.CompilerServices;
using DotCharter.Presentation.Navigation;

namespace DotCharter.UI.Views.Shell;

/// <summary>
///     One view's say in whether its screen is shown. A pane's view is built again on float and
///     redock, and the new view can show the screen before the old one lets go of it, or be built
///     hidden while the old one still shows it. So the screen is shown while ANY view shows it,
///     whatever order the views report in, and hidden when the last one stops. UI thread only.
/// </summary>
internal sealed class ScreenViewPresence
{
    private static readonly ConditionalWeakTable<IScreenLifecycle, Viewers> Shown = [];
    private IScreenLifecycle? _showing;

    /// <summary>This view now has <paramref name="screen" /> as its data and is, or is not, effectively visible.</summary>
    public void Report(IScreenLifecycle? screen, bool visible)
    {
        var showing = visible ? screen : null;
        var left = _showing;
        if (!ReferenceEquals(showing, left))
        {
            if (left is not null) Shown.GetOrCreateValue(left).Count--;
            if (showing is not null) Shown.GetOrCreateValue(showing).Count++;
            _showing = showing;
        }

        if (!ReferenceEquals(left, screen)) Tell(left);
        Tell(screen);
    }

    private static void Tell(IScreenLifecycle? screen)
    {
        if (screen is null) return;
        if (Shown.GetOrCreateValue(screen).Count > 0) screen.OnShown();
        else screen.OnHidden();
    }

    private sealed class Viewers
    {
        public int Count { get; set; }
    }
}
