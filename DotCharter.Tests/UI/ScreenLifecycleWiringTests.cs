namespace DotCharter.Tests.UI;

/// <summary>
///     <c>IScreenLifecycle</c> is DRIVEN, and the shell re-announces a screen
///     whose host became visible again.
///     <para>
///         The seam existed from the first navigation commit and nothing called
///         it — the exact shape (a declared contract with no caller) that this
///         repo has shipped four times as a dead feature. It is called now, and
///         the Home screen depends on it: its figures are live, its host holds
///         one screen and therefore never navigates again after the first show,
///         so without the shell's re-announcement the numbers would be whatever
///         they were at startup, forever, with nothing on screen saying so.
///     </para>
///     <para>
///         A source lint because the behaviour needs a DI container, a screen
///         catalog and a real <c>Panel</c>; the view-model half is proved for
///         real by <c>Presentation/HomeScreenTests.OnShown_ReReadsEveryFigure</c>.
///     </para>
/// </summary>
public sealed class ScreenLifecycleWiringTests
{
    [Fact]
    public void ScreenHost_AnnouncesEveryShowAndEveryHide()
    {
        var host = Source("DotCharter.UI", "Views", "Shell", "ScreenHost.cs");

        Assert.Contains("IScreenLifecycle", host, StringComparison.Ordinal);
        Assert.Contains(".OnShown()", host, StringComparison.Ordinal);
        Assert.Contains(".OnHidden()", host, StringComparison.Ordinal);

        // The re-announce door the shell uses for the tab axis, which no
        // navigation can see.
        Assert.Contains("public void NotifyReshown()", host, StringComparison.Ordinal);
    }

    [Fact]
    public void TheShell_ReAnnouncesHomeWhenItsTabBecomesVisible()
    {
        var layout = Source("DotCharter.UI", "Views", "Shell", "MainWindowShellLayout.cs");

        var at = layout.IndexOf("SelectedMainTabIndex", StringComparison.Ordinal);
        Assert.True(at > 0, "the shell no longer reacts to the tab index at all");

        Assert.Contains("IsHomeVisible", layout, StringComparison.Ordinal);
        Assert.Contains("NotifyReshown()", layout, StringComparison.Ordinal);
    }

    /// <summary>
    ///     …and the screen that needs it says so in its type. A view model that
    ///     quietly stopped implementing the seam would leave both assertions
    ///     above green while Home went stale.
    /// </summary>
    [Fact]
    public void TheHomeScreen_IsALifecycleScreen()
    {
        var type = typeof(DotCharter.Presentation.Screens.Home.HomeViewModel);
        Assert.True(typeof(DotCharter.Presentation.Navigation.IScreenLifecycle).IsAssignableFrom(type),
            "HomeViewModel no longer implements IScreenLifecycle — its live figures will freeze at " +
            "whatever they were when the app started, silently");
    }

    private static string Source(params string[] parts) =>
        UiSourceTree.ReadAllText(Path.Combine(new[] { TestPaths.RepoRoot() }.Concat(parts).ToArray()));
}
