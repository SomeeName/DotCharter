using DotCharter.UI.Controls;

namespace DotCharter.Tests.UI;

/// <summary>
///     The composed 3D path's idle gate vs. the visual-lead EMA.
///     <para>
///         On the composed path <c>ComposedPresentPump</c> publishes a
///         present-latency sample per completed present and
///         <see cref="VisualLeadTiming" /> folds it into an alpha=0.1 EMA, so the
///         lead is a slightly different double after every frame. The driver used
///         to feed the LED clock into the gate's <c>timeChanged</c> compare, which
///         made present → EMA → "the time changed" → render → present
///         self-sustaining: the 0.14 P6 park never triggered and a PAUSED editor
///         composed full frames at the fps cap forever. It now compares the UNLED
///         transport clock.
///     </para>
///     <para>
///         These drive the real <see cref="VisualLeadTiming" /> EMA and the real
///         <c>Preview3DFrameGate</c>, but they model the driver's loop rather than
///         running it: <c>Preview3DRenderDriver</c> needs a live Vulkan engine, an
///         audio service and a render thread, so the loop itself is not headlessly
///         testable. What IS pinned is the decision the loop feeds the gate.
///     </para>
/// </summary>
public sealed class Preview3DIdleGateLeadTests
{
    private const double PausedClockMs = 12_345.0;
    private const int Frames = 100;

    /// <summary>
    ///     <see cref="VisualLeadTiming" /> is process-global mutable state and these
    ///     two tests both feed its EMA, so without this reset they passed or failed on
    ///     execution ORDER alone: the parking test publishes exactly 8.0, and the EMA
    ///     does not move when the incoming sample already equals it, so the led-clock
    ///     test then saw a constant lead and parked after one render — asserting the
    ///     exact opposite of its point.
    /// </summary>
    public Preview3DIdleGateLeadTests() => VisualLeadTiming.ResetForTests();

    [Fact]
    public void A_paused_loop_parks_even_though_every_present_moves_the_lead_ema()
    {
        var lastRenderedRawMs = double.NaN;
        var renders = 0;

        for (var frame = 0; frame < Frames; frame++)
        {
            // What the driver feeds now: the UNLED transport clock, which is
            // exactly what stops moving when playback does.
            var decision = Preview3DFrameGate.Decide(
                isPlaying: false,
                renderRequested: false,
                // ReSharper disable once CompareOfFloatsByEqualityOperator
                timeChanged: PausedClockMs != lastRenderedRawMs,
                sizeChanged: false,
                cameraAnimating: false,
                msSinceLastRender: 0, // below the 10 Hz trickle interval
                maxFps: 0);

            if (decision != Preview3DFrameGate.FrameDecision.Render) continue;

            renders++;
            lastRenderedRawMs = PausedClockMs;
            PublishPresentSample(frame);
        }

        Assert.Equal(1, renders); // the first frame, then parked for good
    }

    [Fact]
    public void Feeding_the_led_clock_to_the_gate_never_parks()
    {
        // The pre-fix shape, kept as the proof that the fix is load-bearing.
        var lastRenderedLedMs = double.NaN;
        var renders = 0;

        for (var frame = 0; frame < Frames; frame++)
        {
            var ledMs = PausedClockMs + VisualLeadTiming.EffectiveLeadMs;
            var decision = Preview3DFrameGate.Decide(
                isPlaying: false,
                renderRequested: false,
                // ReSharper disable once CompareOfFloatsByEqualityOperator
                timeChanged: ledMs != lastRenderedLedMs,
                sizeChanged: false,
                cameraAnimating: false,
                msSinceLastRender: 0,
                maxFps: 0);

            if (decision != Preview3DFrameGate.FrameDecision.Render) continue;

            renders++;
            lastRenderedLedMs = ledMs;
            PublishPresentSample(frame);
        }

        Assert.True(renders > Frames / 2,
            $"the led-clock feed must keep re-rendering a paused board (rendered {renders}/{Frames})");
    }

    /// <summary>One completed composed present. Samples jitter like real ones,
    /// so the EMA never repeats a double exactly.</summary>
    private static void PublishPresentSample(int frame) =>
        VisualLeadTiming.PublishMeasuredPresentLatency(8.0 + frame % 5 * 0.7);
}
