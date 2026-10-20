using System.Diagnostics;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Services.Audio.Calibration;
using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.Calibration;

/// <summary>
///     The loopback calibration's channel search over a SYNTHETIC two-channel
///     capture: the click sits on one channel only, the search is told which
///     channel is configured, and the question is whether it hears the click —
///     on the configured channel, or by falling back and saying so.
///     <para>
///         Owner report 2026-08-30: guitar on the interface's second input,
///         both channels open, "no click heard" while the meter lit. The
///         analysed ring was channel 0's.
///     </para>
/// </summary>
public sealed class LoopbackChannelSearchTests
{
    private const int SampleRate = 48_000;
    private const int BlockSamples = SampleRate / 10;      // 100 ms per click window
    private const int ClickAtSample = SampleRate / 20;     // the echo lands 50 ms in
    private const double ClickRoundTripMs = 50.0;

    private static long Ms(double ms) => (long)(ms / 1000.0 * Stopwatch.Frequency);

    /// <summary>A two-channel capture window with a click on ONE channel.</summary>
    private static float[][] CaptureWithClickOn(int channel)
    {
        var buf = new[] { new float[BlockSamples], new float[BlockSamples] };
        for (var i = 0; i < 96; i++)
            buf[channel][ClickAtSample + i] = (i & 1) == 0 ? 0.8f : -0.8f;
        return buf;
    }

    /// <summary>The onset finder a calibration needs: the first sample over a
    /// threshold in a window, as a host tick relative to the click.</summary>
    private static long? FirstOnsetTicks(float[] window, long clickTicks)
    {
        for (var i = 0; i < window.Length; i++)
        {
            if (Math.Abs(window[i]) > 0.5f)
                return clickTicks + Ms(i * 1000.0 / SampleRate);
        }

        return null;
    }

    /// <summary>
    ///     Drives one run to completion or exhaustion: every time the search
    ///     wants a click, the "engine" serves the window of whichever channel the
    ///     search is currently listening on, and any onset in it is offered back.
    /// </summary>
    private static LoopbackChannelSearch Run(int configuredChannel, float[][] capture)
    {
        var search = new LoopbackChannelSearch(
            GuitarInputChannels.CalibrationChannelOrder(configuredChannel, capture.Length),
            static () => new LatencyCalibrationSession { ClicksTarget = 3, MaxMissedEchoes = 2 });
        var now = 0L;
        search.Start(now);
        for (var pump = 0; pump < 400 && search.State is not (LatencyCalibrationState.Complete or LatencyCalibrationState.Failed); pump++)
        {
            now += Ms(100);
            if (search.ShouldEmitClick(now))
            {
                search.NotifyClickEmitted(now);
                // The ring is routed to the channel the search listens on.
                if (FirstOnsetTicks(capture[search.CurrentChannel], now) is { } onset)
                    search.OfferOnset(onset);
            }

            search.Advance(now);
        }

        return search;
    }

    [Fact]
    public void ClickOnConfiguredChannel_IsHeardThere_WithoutFallingBack()
    {
        var search = Run(configuredChannel: 1, CaptureWithClickOn(1));

        Assert.Equal(LatencyCalibrationState.Complete, search.State);
        Assert.Equal(1, search.HeardOnChannel);
        Assert.False(search.FellBack);
        Assert.Empty(search.ChannelsGivenUpOn);
        Assert.Equal(ClickRoundTripMs, search.ResultMs!.Value, 0.5);
    }

    [Fact]
    public void ClickOnTheOtherChannel_IsFoundByFallback_AndReportedAsSuch()
    {
        // Configured channel 1 (the guitar's), click only on channel 0.
        var search = Run(configuredChannel: 1, CaptureWithClickOn(0));

        Assert.Equal(LatencyCalibrationState.Complete, search.State);
        Assert.Equal(0, search.HeardOnChannel);
        Assert.True(search.FellBack, "a click heard on a non-configured channel must be reported as a fallback");
        Assert.Equal(1, search.PreferredChannel);
        Assert.Equal([1], search.ChannelsGivenUpOn.ToArray());
        Assert.Equal(ClickRoundTripMs, search.ResultMs!.Value, 0.5);
    }

    [Fact]
    public void NoClickAnywhere_ExhaustsEveryChannel_AndFails()
    {
        var silent = new[] { new float[BlockSamples], new float[BlockSamples] };
        var search = Run(configuredChannel: 0, silent);

        Assert.Equal(LatencyCalibrationState.Failed, search.State);
        Assert.True(search.Exhausted);
        Assert.Null(search.HeardOnChannel);
        Assert.Equal([0, 1], search.ChannelsGivenUpOn.ToArray());
    }

    [Fact]
    public void Advance_ReportsTheChannelSwitch_ExactlyOnce()
    {
        var search = new LoopbackChannelSearch([1, 0],
            static () => new LatencyCalibrationSession { MaxMissedEchoes = 0, EchoTimeoutMs = 100 });
        search.Start(0);
        search.NotifyClickEmitted(0);
        Assert.False(search.Advance(Ms(50)));          // still waiting
        Assert.True(search.Advance(Ms(200)));           // timed out ⇒ moved to channel 0
        Assert.Equal(0, search.CurrentChannel);
        Assert.Equal(LatencyCalibrationState.Settling, search.State);
        Assert.False(search.Advance(Ms(250)));          // a switch is not repeated
    }
}
