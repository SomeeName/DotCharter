using DotCharter.UI.Services;

namespace DotCharter.Tests.UI;

/// <summary>
///     <b>The Auto arm of the interface-scale setting is a pure function of the
///     shell's client height, and this is its table.</b>
///     <para>
///         It matters more than an ordinary policy test because the shape of
///         the fault it prevents is invisible: an Auto scale that is
///         <i>nearly</i> right is a UI that is the wrong size by 5 %, which
///         nobody reports, and one that flaps across a step boundary is a full
///         shell re-layout on every window-size delivery, which reads as "the
///         app stutters when I drag the edge" rather than as a scale bug.
///     </para>
/// </summary>
public sealed class AutoUiScaleTests
{
    /// <summary>The shipped table. 1080 is the baseline the fixed pixel sizes
    /// were authored against, 1440 and 2160 are the two displays that provoked
    /// the setting, and the rest are the boundaries.</summary>
    [Theory]
    // A window SHORTER than the baseline never shrinks: Auto only grows.
    [InlineData(600, 1.00)]
    [InlineData(900, 1.00)]
    [InlineData(1080, 1.00)]
    // 1200 => 1.1111 => 1.10 (nearest 0.05).
    [InlineData(1200, 1.10)]
    // The owner's monitor. 1440/1080 = 1.3333, which is 1.35 on the grid — the
    // brief said "1.33", and 1.35 is that number quantised, not a different one.
    [InlineData(1440, 1.35)]
    [InlineData(1600, 1.50)]
    [InlineData(1800, 1.65)]
    // 4K. 2160/1080 is exactly 2.0, which is also the ceiling.
    [InlineData(2160, 2.00)]
    // …and past the ceiling it stays there rather than growing off-screen.
    [InlineData(2880, 2.00)]
    [InlineData(4320, 2.00)]
    public void ForHeight_IsTheShippedTable(double clientHeight, double expected) => Assert.Equal(expected, AutoUiScale.ForHeight(clientHeight), 4);

    /// <summary>A window mid-teardown reports zeroes and a window with no
    /// platform impl reports NaN. Both reach this from a size-changed handler,
    /// and both must answer 1.0 rather than putting a NaN into a layout
    /// transform (which makes the whole shell measure to nothing).</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ADegenerateHeight_IsIdentity(double clientHeight) => Assert.Equal(1.0, AutoUiScale.ForHeight(clientHeight));

    /// <summary>Everything the function can return is ON the 0.05 grid. Swept
    /// rather than sampled, because the fault is a value like
    /// 1.3500000000000001 reaching a ScaleTransform and defeating every
    /// "has it changed?" comparison downstream.</summary>
    [Fact]
    public void EveryAnswer_IsOnTheGrid()
    {
        for (var height = 400.0; height <= 4400; height += 1)
        {
            var scale = AutoUiScale.ForHeight(height);
            var steps = scale / AutoUiScale.Step;
            Assert.True(Math.Abs(steps - Math.Round(steps)) < 1e-9,
                $"height {height} gave {scale}, which is not a multiple of {AutoUiScale.Step}.");
            Assert.InRange(scale, AutoUiScale.MinScale, AutoUiScale.MaxScale);
        }
    }

    /// <summary>
    ///     The hysteresis band. A wobble smaller than half a step plus the
    ///     margin holds the current value; a full step moves.
    ///     <para>
    ///         The second half is what stops the band from being a freeze: a
    ///         margin of half a step or more would mean the quantised target is
    ///         never far enough away to adopt, and Auto would latch on its first
    ///         answer for ever while every test above still passed.
    ///     </para>
    /// </summary>
    [Fact]
    public void Next_HoldsInsideTheBand_AndMovesOutsideIt()
    {
        // Settled at 1440 => 1.35.
        var current = AutoUiScale.ForHeight(1440);
        Assert.Equal(1.35, current, 4);

        // A maximise/restore that only moves the title bar and the task bar:
        // raw 1.3148 at 1420, 0.035 away — inside the band, no re-layout.
        Assert.Equal(1.35, AutoUiScale.Next(1420, current), 4);
        Assert.Equal(1.35, AutoUiScale.Next(1460, current), 4);

        // A real resize of a full step: raw 1.2778 at 1380, 0.072 away.
        Assert.Equal(1.30, AutoUiScale.Next(1380, current), 4);
        Assert.Equal(1.40, AutoUiScale.Next(1512, current), 4);
    }

    /// <summary>The band must be strictly narrower than a step, or a UI that
    /// settles anywhere can never leave. Asserted on the CONSTANTS, so a later
    /// widening of the margin fails here and not in a user's window.</summary>
    [Fact]
    public void TheBand_IsNarrowerThanAStep()
    {
        Assert.True(AutoUiScale.Step / 2 + AutoUiScale.HysteresisMargin < AutoUiScale.Step,
            "the stickiness band is a full step or wider, so a settled Auto scale can never change again.");
    }

    /// <summary>Auto is stable under iteration: feeding a settled height back
    /// in never moves it. Anything else is a re-layout on every size
    /// delivery.</summary>
    [Theory]
    [InlineData(1080)]
    [InlineData(1392)]
    [InlineData(1440)]
    [InlineData(2160)]
    public void Next_IsAFixedPointOnceSettled(double height)
    {
        var scale = AutoUiScale.ForHeight(height);
        for (var i = 0; i < 10; i++)
        {
            var next = AutoUiScale.Next(height, scale);
            Assert.Equal(scale, next, 9);
            scale = next;
        }
    }

    /// <summary>1080p keeps a global scale of exactly 1.0 (owner, 2026-09-12):
    /// the fixed layouts were authored there and a global scale above 1.0 risks
    /// overflowing them, so the start screen's own fill — not this — is what
    /// answers a small hero at 1080p.</summary>
    [Fact]
    public void A1080pWindow_IsNeverScaledUpGlobally()
    {
        // The whole plausible range of client heights inside a 1080-tall
        // display: full-screen, maximised under a task bar, and windowed.
        foreach (var height in new[] { 1080.0, 1040, 1000, 960, 900, 800 })
            Assert.Equal(1.0, AutoUiScale.ForHeight(height));
    }
}
