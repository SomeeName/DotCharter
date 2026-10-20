using System.Globalization;
using DotCharter.Rendering.Diagnostics;
using DotCharter.UI.Controls;

namespace DotCharter.Tests.UI;

/// <summary>
///     <b>The 3D preview's corner HUD, whose two numbers measured two different
///     things — differently in each of the loop's two states.</b>
///
///     <para>
///         <b>Parked.</b> <c>"10 fps · p50 7,0 / p99 7,1 ms"</c> was reported as
///         a render regression: 7 ms frames at 10 fps is a 90 ms/frame stall
///         that is not there. Both numbers were right. The loop was idle-gated
///         — a ≤10 Hz trickle — and every trickle frame comes out of a park, so
///         the driver substitutes a nominal delta and records nothing; the
///         percentiles were the last second the loop really ran.
///     </para>
///     <para>
///         <b>Active.</b> <c>"34 fps · p50 7,0 / p99 48,4 ms"</c> was reported
///         next, and that one WAS an instrumentation bug: the sample was taken
///         before <c>RenderFrame</c>, so declined attempts (composed
///         back-pressure) fed the percentiles while counting for nothing in the
///         fps tally. The driver samples present-to-present now, so
///         <c>fps ≈ 1000 / p50</c> — pinned below as arithmetic the formatter
///         is allowed to assume.
///     </para>
///
///     <para>
///         <c>InvariantCulture</c> is passed explicitly — the app formats with
///         <c>CurrentCulture</c> (the owner's decimal comma) and a test must not
///         depend on the machine's locale.
///     </para>
/// </summary>
public class Preview3DStatsLineTests
{
    private static readonly FrameTimeSummary Active = new(512, 7.0, 7.1, 9.4);

    /// <summary>The reported active line's real shape: 34 presents in the
    /// second, ~29 ms apart, under a 63 fps cap.</summary>
    private static readonly FrameTimeSummary Paced = new(512, 29.4, 48.4, 61.0);

    private static string Format(
        int frames, int idleFrames, FrameTimeSummary stats, int cap = 0, int declined = 0) =>
        Preview3DStatsLine.Format(frames, idleFrames, 10, stats, cap, declined, 0.09, 16, 2200,
            CultureInfo.InvariantCulture);

    // ---- active ---------------------------------------------------------------

    [Fact]
    public void ARunningLoop_NamesTheIntervalAndThePacing()
    {
        Assert.Equal(
            "143 fps · frame p50 7.0 / p99 7.1 ms · uncapped · gpu 0.09 ms · 16 draws · 2200 verts",
            Format(143, 0, Active));
    }

    /// <summary>
    ///     The owner's active line, as it reads now. The cap alone does not
    ///     explain 34 fps under a 63 fps ceiling — the declines do, so they are
    ///     printed whenever there are any.
    /// </summary>
    [Fact]
    public void APacedLoop_PrintsTheCapAndTheDeclinesThatHoldItBelowTheCap()
    {
        Assert.Equal(
            "34 fps · frame p50 29.4 / p99 48.4 ms · cap 63 · 12 declined · gpu 0.09 ms · 16 draws · 2200 verts",
            Format(34, 0, Paced, cap: 63, declined: 12));
    }

    /// <summary>
    ///     <b>The regression itself, as arithmetic.</b> The rate and the p50 in
    ///     one line must be reconcilable: 1000/p50 has to land near the printed
    ///     fps. The shipped pair (34 fps beside 7,0 ms ⇒ 143) failed this by 4x,
    ///     and no wording would have fixed it — the driver had to sample a
    ///     different interval.
    /// </summary>
    [Theory]
    [InlineData(34, 29.4)]
    [InlineData(143, 7.0)]
    [InlineData(60, 16.7)]
    public void ThePrintedRate_AndThePrintedInterval_Reconcile(int fps, double p50Ms)
    {
        var line = Format(fps, 0, new FrameTimeSummary(512, p50Ms, p50Ms * 1.5, p50Ms * 2), cap: 63);

        Assert.StartsWith($"{fps} fps · frame p50 ", line);
        var impliedFps = 1000.0 / p50Ms;
        Assert.True(Math.Abs(impliedFps - fps) <= fps * 0.15,
            $"the line says {fps} fps beside {p50Ms} ms/frame, which is {impliedFps:F0} fps — " +
            "those cannot both be on one line.");
    }

    [Fact]
    public void NoDeclines_LeavesTheDeclineClauseOff() => Assert.DoesNotContain("declined", Format(60, 0, Paced, cap: 63));

    // ---- parked ---------------------------------------------------------------

    /// <summary>The first reported line. It must no longer claim an fps, and it
    /// must say which second the percentiles came from.</summary>
    [Fact]
    public void AParkedLoop_SaysIdleHz_AndDatesItsPercentiles()
    {
        var line = Format(10, 10, Active);

        Assert.Equal(
            "idle 10 Hz · last active frame p50 7.0 / p99 7.1 ms · trickle 10 Hz · " +
            "gpu 0.09 ms · 16 draws · 2200 verts",
            line);
        Assert.DoesNotContain(" fps", line);
    }

    /// <summary>The trickle rate is a SETTING (Graphics → idle Hz, 1..10), so
    /// both the rate and the pacing clause print the configured value.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(10)]
    public void TheIdleLine_PrintsTheConfiguredTrickleRate(int idleHz)
    {
        var line = Preview3DStatsLine.Format(idleHz, idleHz, idleHz, Active, 63, 0, 0.09, 16, 2200,
            CultureInfo.InvariantCulture);

        Assert.StartsWith($"idle {idleHz} Hz · ", line);
        Assert.Contains($"trickle {idleHz} Hz", line);
        // A cap is meaningless while parked — the trickle is what paces it.
        Assert.DoesNotContain("cap 63", line);
    }

    /// <summary>
    ///     One real frame in the window is enough to make the second an fps
    ///     second: the percentiles then have a sample from it, so labelling it
    ///     "idle" would be the same lie in the other direction.
    /// </summary>
    [Fact]
    public void AWindowWithAnyRealFrame_IsAnFpsWindow() => Assert.StartsWith("11 fps · frame p50 ", Format(11, 10, Active));

    [Fact]
    public void ASecondWithNoFramesAtAll_IsNotIdle() => Assert.StartsWith("0 fps · ", Format(0, 0, Active));

    /// <summary>Before the loop has presented a single non-trickle frame there
    /// is no interval to print, and printing 0,0 ms would read as a perfect
    /// one. The "last active" tag goes with it — there is no last active.</summary>
    [Fact]
    public void WithNoSamplesYet_ThePercentilesAreBlank()
    {
        var line = Format(10, 10, default);

        Assert.Equal(
            "idle 10 Hz · frame p50 — / p99 — ms · trickle 10 Hz · gpu 0.09 ms · 16 draws · 2200 verts",
            line);
        Assert.DoesNotContain("last active", line);
    }

    /// <summary>The tail is a machine-readable half that the [3DStats] log line
    /// embeds verbatim; it must not drift with the rate label.</summary>
    [Fact]
    public void TheTail_IsTheSameOnBothArms()
    {
        const string tail = " · gpu 0.09 ms · 16 draws · 2200 verts";
        Assert.EndsWith(tail, Format(143, 0, Active));
        Assert.EndsWith(tail, Format(10, 10, Active));
    }

    /// <summary>The editor's preview always shows the line, an immersive run only with the
    /// detection monitor open, and DOTCHARTER_3D_HIDE_STATS (the README captures) never.</summary>
    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, true, false)]
    public void Stats_line_shows_in_the_editor_hides_in_a_run_and_when_the_environment_hides_it(
        bool gameplay, bool monitorOpen, bool hidden, bool shown) =>
        Assert.Equal(shown, Preview3DStatsLine.Shown(gameplay, monitorOpen, hidden));
}
