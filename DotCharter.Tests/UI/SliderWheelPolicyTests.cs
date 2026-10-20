using Avalonia.Input;
using DotCharter.UI.Controls;
using DotCharter.UI.Platform;

namespace DotCharter.Tests.UI;

/// <summary>
///     A <see cref="Avalonia.Controls.Slider" /> may not eat the mouse wheel of
///     the panel it sits in.
///     <para>
///         Avalonia's Slider handles <c>PointerWheelChanged</c> unconditionally,
///         so scrolling a pane DRAGS whichever slider is under the pointer —
///         reported on the mixer tab, where scrolling down moved the filter's Q.
///         A silent, undoable-only edit to a value nobody aimed at, and the same
///         fault the chord editor's finger ComboBox had: a control claiming the
///         wheel while the wheel belongs to the surface behind it.
///     </para>
/// </summary>
public sealed class SliderWheelPolicyTests
{
    /// <summary>
    ///     Over a slider, the host scrolls — ALWAYS, focus or not.
    ///     <para>
    ///         The first version of this rule exempted a FOCUSED slider, on the
    ///         "click it, then wheel it" convention. That shipped the bug again:
    ///         a mixer is a wall of sliders you click constantly, so one click
    ///         leaves a fader focused and every later scroll crossing it moves
    ///         it. Reported twice on the same Q slider, the second time as moving
    ///         "without even being touched" — true, because the arming click was
    ///         minutes earlier and on another control.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(true, false, true)]   // over a slider / the EQ plot, bare notch: the host scrolls
    [InlineData(true, true, false)]   // the control asked for this notch's modifier (the plot's Ctrl + wheel)
    [InlineData(false, false, false)] // not over a wheel-adjustable control at all
    public void TheWheelOverAWheelAdjustableControlGoesToTheHost(bool overAControl, bool controlTakesIt, bool expected) =>
        Assert.Equal(expected, SliderWheelPolicy.BelongsToTheScrollHost(overAControl, controlTakesIt));

    /// <summary>
    ///     #47: the EQ plot turned a BARE notch into a Q edit, so it has to be
    ///     wheel-adjustable for the policy AND ask for a modifier. A plot that
    ///     took <see cref="KeyModifiers.None" /> would put the bug
    ///     straight back.
    /// </summary>
    [Fact]
    public void TheEqPlotOnlyTakesTheWheelWithCtrl()
    {
        Assert.True(typeof(IWheelAdjustable).IsAssignableFrom(typeof(EqPlotControl)),
            "the EQ plot edits Q on the wheel, so the scroll-host policy must know it is wheel-adjustable.");
        Assert.False(EqPlotControl.IsQWheel(KeyModifiers.None));
        Assert.False(EqPlotControl.IsQWheel(KeyModifiers.Shift));
        Assert.True(EqPlotControl.IsQWheel(KeyModifiers.Control));
    }

    /// <summary>
    ///     #48: a bring-into-view raised inside a popup stops at the popup; one
    ///     raised anywhere else (keyboard focus moving off-screen) is untouched.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ABringIntoViewFromInsideAPopupStopsThere(bool insideThePopup, bool expected) =>
        Assert.Equal(expected, PopupBringIntoViewPolicy.StopsAtThePopup(insideThePopup));

    /// <summary>
    ///     Focus must not be consulted at all — the regression that reopened this
    ///     bug WAS a focus exemption, so pin its absence rather than only its
    ///     current result.
    /// </summary>
    [Fact]
    public void TheRuleDoesNotConsultFocus()
    {
        var source = UiSourceTree.ReadAllText(SourcePath("DotCharter.UI/Platform/SliderWheelPolicy.cs"));
        var code = source[source.IndexOf("public static class", StringComparison.Ordinal)..];

        Assert.DoesNotContain("IsKeyboardFocusWithin", code, StringComparison.Ordinal);
        Assert.DoesNotContain("IsFocused", code, StringComparison.Ordinal);
    }

    /// <summary>
    ///     TUNNEL, and it has to be: routed events go top-down on the tunnel, so
    ///     the scroll host sees the notch before the slider does. A bubble
    ///     handler arrives AFTER the slider has already moved, which is the whole
    ///     bug — the guard would be pure decoration.
    /// </summary>
    [Fact]
    public void ThePolicyIsInstalledOnTheTunnel()
    {
        var source = UiSourceTree.ReadAllText(SourcePath("DotCharter.UI/Platform/SliderWheelPolicy.cs"));

        Assert.Contains("RoutingStrategies.Tunnel", source);
        Assert.DoesNotContain("RoutingStrategies.Bubble", source);
    }

    /// <summary>
    ///     …and it is actually installed. A policy nothing calls is the
    ///     registered-but-never-resolved shape this repo has hit four times.
    /// </summary>
    [Fact]
    public void TheAppInstallsIt()
    {
        var app = UiSourceTree.ReadAllText(SourcePath("DotCharter.UI/App.axaml.cs"));
        Assert.Contains("SliderWheelPolicy.Apply()", app);
        Assert.Contains("PopupBringIntoViewPolicy.Apply()", app);
    }

    private static string SourcePath(string relative) =>
        Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
}
