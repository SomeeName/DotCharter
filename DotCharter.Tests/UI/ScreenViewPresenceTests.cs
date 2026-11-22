using DotCharter.Presentation.Navigation;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.UI;

/// <summary>A screen is shown while any view shows it, in whatever order the views of a floated or redocked pane report.</summary>
public sealed class ScreenViewPresenceTests
{
    [Fact]
    public void ANewViewThatShowsTheScreenFirstKeepsItShownWhenTheOldViewLeaves()
    {
        var screen = new Screen();
        var (old, fresh) = (new ScreenViewPresence(), new ScreenViewPresence());
        old.Report(screen, true);

        fresh.Report(screen, true);
        old.Report(screen, false);
        old.Report(null, false);

        Assert.True(screen.IsShown);
        fresh.Report(screen, false);
        Assert.False(screen.IsShown);
    }

    [Fact]
    public void AViewBuiltHiddenDoesNotHideTheScreenAnotherViewShows()
    {
        var screen = new Screen();
        var (shown, hidden) = (new ScreenViewPresence(), new ScreenViewPresence());
        shown.Report(screen, true);

        hidden.Report(screen, false);

        Assert.True(screen.IsShown);
    }

    [Fact]
    public void AViewBuiltHiddenParksAScreenNoViewShows()
    {
        var screen = new Screen();

        new ScreenViewPresence().Report(screen, false);

        Assert.False(screen.IsShown);
        Assert.Equal(1, screen.Calls);
    }

    [Fact]
    public void AViewHandedAnotherScreenHidesTheOneItLeft()
    {
        var (first, second) = (new Screen(), new Screen());
        var view = new ScreenViewPresence();
        view.Report(first, true);

        view.Report(second, true);

        Assert.False(first.IsShown);
        Assert.True(second.IsShown);
    }

    [Fact]
    public void AVisibleViewReportsItsScreenShownAgainOnEveryReport()
    {
        var screen = new Screen();
        var view = new ScreenViewPresence();

        view.Report(screen, true);
        view.Report(screen, true);

        Assert.True(screen.IsShown);
        Assert.Equal(2, screen.Calls);
    }

    private sealed class Screen : IScreenLifecycle
    {
        public bool IsShown { get; private set; } = true;

        public int Calls { get; private set; }

        public void OnShown() => (IsShown, Calls) = (true, Calls + 1);

        public void OnHidden() => (IsShown, Calls) = (false, Calls + 1);
    }
}
