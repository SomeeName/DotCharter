using DotCharter.UI.Services;

namespace DotCharter.Tests.UI;

/// <summary>
///     Pure tests for the global UI-scale controller (0.13.0 accessibility). It
///     is the decoupling channel between Settings and the shell's
///     LayoutTransform, so its clamp bounds and change-coalescing are what the
///     live preview relies on.
/// </summary>
public sealed class UiScaleControllerTests
{
    [Fact]
    public void Clamp_BoundsRange_AndRejectsNonFinite()
    {
        Assert.Equal(UiScaleController.MinScale, UiScaleController.Clamp(0.1));
        Assert.Equal(UiScaleController.MaxScale, UiScaleController.Clamp(5.0));
        Assert.Equal(1.0, UiScaleController.Clamp(double.NaN));
        Assert.Equal(1.0, UiScaleController.Clamp(double.PositiveInfinity));
        Assert.Equal(1.25, UiScaleController.Clamp(1.25));
    }

    [Fact]
    public void Set_RaisesOnChange_ClampsAndCoalescesNoOps()
    {
        double? received = null;
        var count = 0;
        void Handler(double s)
        {
            received = s;
            count++;
        }

        try
        {
            UiScaleController.Set(1.0); // normalize starting point
            UiScaleController.ScaleChanged += Handler;

            UiScaleController.Set(1.5);
            Assert.Equal(1.5, UiScaleController.Scale);
            Assert.Equal(1.5, received);
            Assert.Equal(1, count);

            UiScaleController.Set(1.5); // unchanged → no event
            Assert.Equal(1, count);

            UiScaleController.Set(99); // clamps to max, still a change
            Assert.Equal(UiScaleController.MaxScale, UiScaleController.Scale);
            Assert.Equal(2, count);
        }
        finally
        {
            UiScaleController.ScaleChanged -= Handler;
            UiScaleController.Set(1.0);
        }
    }

    /// <summary>
    ///     <b><c>Set</c> must NOT leave Auto and <c>SetManual</c> must.</b>
    ///     <para>
    ///         That split is the whole reason there are two methods, and
    ///         collapsing them is a real bug in EITHER direction. If
    ///         <c>Set</c> cleared the mode, Settings' and Play Mode's LOAD paths
    ///         — which push the persisted value through it — would turn Auto off
    ///         on every open, so the setting would silently un-set itself. If
    ///         <c>SetManual</c> did not clear it, Auto would overwrite a slider
    ///         drag on the next window resize.
    ///     </para>
    /// </summary>
    [Fact]
    public void Set_LeavesTheMode_WhileSetManual_LeavesAuto()
    {
        try
        {
            UiScaleController.SetAutoMode(true);
            UiScaleController.Set(1.35);
            Assert.True(UiScaleController.IsAuto, "a plain Set() cancelled Auto - the load paths use it.");
            Assert.Equal(1.35, UiScaleController.Scale);

            UiScaleController.SetManual(1.25);
            Assert.False(UiScaleController.IsAuto, "a user gesture did not leave Auto.");
            Assert.Equal(1.25, UiScaleController.Scale);
        }
        finally
        {
            UiScaleController.SetAutoMode(false);
            UiScaleController.Set(1.0);
        }
    }

    /// <summary>The mode event fires on a real flip and never on a no-op - the
    /// shell answers it by re-deriving and re-laying-out the whole
    /// window.</summary>
    [Fact]
    public void AutoModeChanged_FiresOncePerFlip()
    {
        var flips = new List<bool>();
        void Handler(bool auto) => flips.Add(auto);

        try
        {
            UiScaleController.SetAutoMode(false);
            UiScaleController.AutoModeChanged += Handler;

            UiScaleController.SetAutoMode(true);
            UiScaleController.SetAutoMode(true); // no-op
            UiScaleController.SetAutoMode(false);

            Assert.Equal([true, false], flips);
        }
        finally
        {
            UiScaleController.AutoModeChanged -= Handler;
            UiScaleController.SetAutoMode(false);
            UiScaleController.Set(1.0);
        }
    }

    /// <summary>
    ///     <c>ApplyAutoForHeight</c> is a no-op under the manual arm, and
    ///     derives under Auto.
    ///     <para>
    ///         The manual half is the load-bearing one: the shell calls this
    ///         from <c>SizeChanged</c>, so without the guard every window resize
    ///         would silently overwrite a factor the user chose by hand.
    ///     </para>
    /// </summary>
    [Fact]
    public void ApplyAutoForHeight_OnlyActsUnderAuto()
    {
        try
        {
            UiScaleController.SetManual(1.25);
            UiScaleController.ApplyAutoForHeight(1440);
            Assert.Equal(1.25, UiScaleController.Scale);

            UiScaleController.SetAutoMode(true);
            UiScaleController.ApplyAutoForHeight(1440);
            Assert.Equal(AutoUiScale.ForHeight(1440), UiScaleController.Scale, 4);

            // A 1080p window is exactly 1.0 - Auto never scales the baseline up.
            UiScaleController.ApplyAutoForHeight(1080);
            Assert.Equal(1.0, UiScaleController.Scale, 4);
        }
        finally
        {
            UiScaleController.SetAutoMode(false);
            UiScaleController.Set(1.0);
        }
    }
}
