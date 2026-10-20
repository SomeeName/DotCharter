using Avalonia;
using Dock.Avalonia.Controls;
using DotCharter.Tone.UI.ViewModels.Shell;
using DotCharter.UI.Docking;

namespace DotCharter.Tests.UI;

/// <summary>
///     A float window restored onto a monitor that is no longer there.
///
///     <para>
///         <c>DockLayoutController.SanitizeBound</c> already rejects the shapes
///         JSON invents — NaN, infinity, 1e12 — on the stated grounds that "a
///         window restored at NaN is nowhere and the pane simply reads as lost".
///         <b>x = 2560 is not garbage.</b> It is exactly where the user left the
///         Inspector on their second monitor, and on a laptop that has since been
///         undocked (or a screen re-plugged on the other side, where it becomes
///         negative) it reads IDENTICALLY: the View menu says the Inspector is
///         open, the bottom dock is empty, and the only cure is View → Reset
///         Layout, which the user has no reason to suspect. The ±20,000 clamp is
///         a garbage filter, not a placement rule — it never asked the machine
///         which monitors exist.
///     </para>
/// </summary>
public sealed class FloatWindowPlacementTests
{
    /// <summary>One 1920×1080 monitor at the origin, and nothing else.</summary>
    private static readonly IReadOnlyList<PixelRect> OneScreen =
        [new PixelRect(0, 0, 1920, 1080)];

    /// <summary>The same, plus the second screen to its right — the arrangement
    /// the float bounds were SAVED under.</summary>
    private static readonly IReadOnlyList<PixelRect> TwoScreens =
        [new PixelRect(0, 0, 1920, 1080), new PixelRect(1920, 0, 2560, 1440)];

    [Theory]
    // On the primary, plainly.
    [InlineData(200, 200, true)]
    // Where the second monitor used to be.
    [InlineData(2600, 300, false)]
    // Parked half off the right edge of the primary: the grab strip still
    // overlaps, so a placement the user chose is left alone.
    [InlineData(1850, 400, true)]
    // Above the top edge — the title bar is off-screen and unclickable.
    [InlineData(600, -400, false)]
    public void IsReachable_AsksWhetherTheTitleBarLandsOnAConnectedScreen(
        double x, double y, bool expected)
        => Assert.Equal(expected, FloatWindowPlacement.IsReachable(x, y, OneScreen));

    /// <summary>The very same origin is fine while the monitor is plugged in —
    /// which is what makes this a placement question rather than a sanitizer
    /// one, and what stops the fix from moving windows that are perfectly
    /// placed.</summary>
    [Fact]
    public void IsReachable_AcceptsASecondMonitorWhileItIsConnected()
    {
        Assert.True(FloatWindowPlacement.IsReachable(2600, 300, TwoScreens));
        Assert.False(FloatWindowPlacement.IsReachable(2600, 300, OneScreen));
    }

    /// <summary>An unavailable screen list (headless, or a top-level that is not
    /// up yet) must never REFUSE to restore a float window — that would be a
    /// worse bug than the one being prevented.</summary>
    [Fact]
    public void IsReachable_LeavesEverythingAloneWhenThePlatformSaysNothing()
        => Assert.True(FloatWindowPlacement.IsReachable(999_999, 999_999, []));

    /// <summary>The bug itself: the ORIGIN falls back to the primary screen, and
    /// the size the user chose survives — a size is not a placement.</summary>
    [Fact]
    public void RestoredFloatBounds_BringsAWindowBackFromAMonitorThatIsGone()
    {
        var bounds = DockLayoutController.RestoredFloatBounds(Saved(), "Inspector", OneScreen);

        Assert.Equal(200, bounds.X);
        Assert.Equal(200, bounds.Y);
        Assert.Equal(900, bounds.Width);
        Assert.Equal(700, bounds.Height);
    }

    /// <summary>…and with the monitor still attached, nothing moves. Without
    /// this the test above would pass against a <c>RestoredFloatBounds</c> that
    /// always returned the default origin.</summary>
    [Fact]
    public void RestoredFloatBounds_LeavesAWindowOnAConnectedMonitorWhereItWas()
    {
        var bounds = DockLayoutController.RestoredFloatBounds(Saved(), "Inspector", TwoScreens);

        Assert.Equal(2600, bounds.X);
        Assert.Equal(300, bounds.Y);
    }

    /// <summary>The sanitizer still runs FIRST — garbage is replaced before the
    /// screen question is asked, and the extent floors still apply.</summary>
    [Fact]
    public void RestoredFloatBounds_StillSanitizesBeforeItPlaces()
    {
        var saved = new DockPaneState
        {
            Mode = "Floating", FloatX = double.NaN, FloatY = 1e12, FloatWidth = 3, FloatHeight = null
        };

        var bounds = DockLayoutController.RestoredFloatBounds(saved, "Inspector", OneScreen);

        Assert.Equal(200, bounds.X);
        Assert.Equal(200, bounds.Y);
        Assert.Equal(320, bounds.Width);
        Assert.Equal(400, bounds.Height);
    }

    /// <summary>
    ///     The restore path must actually ASK. A correct pure helper is no use if
    ///     <c>RestoreState</c> never consults the monitor list — which is the
    ///     shape the whole bug had: a sanitizer that was right about everything it
    ///     was given, and was never given the screens.
    /// </summary>
    [Fact]
    public void RestoreState_PlacesAFloatingPaneThroughTheScreenList()
    {
        var controller = NewController();
        controller.Rebuild();
        var asked = 0;
        controller.ScreenBoundsProvider = () =>
        {
            asked++;
            return OneScreen;
        };

        var state = new DockLayoutState();
        state.Panes["Inspector"] = Saved();
        controller.RestoreState(state);

        Assert.True(asked > 0, "RestoreState never asked which monitors are connected.");
    }

    private static DockPaneState Saved() => new()
    {
        Mode = "Floating", FloatX = 2600, FloatY = 300, FloatWidth = 900, FloatHeight = 700
    };

    private sealed class FakePaneViewModel : ViewModelBase;

    private static DockLayoutController NewController()
    {
        var visible = true;
        return new DockLayoutController(
            new DockControl { Name = "Bottom" },
            [
                new DockPaneDefinition
                {
                    Id = "Inspector",
                    Title = "Inspector",
                    Context = new FakePaneViewModel(),
                    IsVisible = () => visible,
                    SetVisible = v => visible = v
                }
            ]);
    }
}
