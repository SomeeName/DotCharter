namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     The visual playhead's latency compensation, and the double-count that made
///     the picture trail the sound.
///
///     <para>The playback service subtracts <c>EstimatedOutputLatencyMs +
///     AudioDelayMs</c> from every position report — the device's own latency is
///     therefore compensated automatically, continuously, without anyone touching a
///     setting. The Settings → Audio "Measure" button then ALSO copied that same
///     measured latency into <c>AudioDelayMs</c>, making the total <c>2 × L</c> and
///     pushing the playhead a full output latency BEHIND the audio — the exact
///     opposite of what a button called "Measure" is for. ~7 ms on ASIO, 40–60 ms on
///     a shared-mode WASAPI device.</para>
///
///     <para>These tests pin the MODEL rather than the button, because that is what
///     went wrong: two places each believed they owned the device-latency term.</para>
/// </summary>
public sealed class LatencyCompensationTests
{
    /// <summary>The production formula, mirrored: JuceAudioPlaybackService's
    /// <c>ApplyLatencyCompensation</c>.</summary>
    private static double Compensate(double rawMs, double deviceLatencyMs, double userTrimMs)
    {
        var comp = deviceLatencyMs + userTrimMs;
        var adj = rawMs - comp;
        return adj < 0.0 ? 0.0 : adj;
    }

    /// <summary>With the trim left alone, the device latency is compensated exactly
    /// once. This is the correct, out-of-the-box state.</summary>
    [Fact]
    public void DeviceLatency_IsCompensatedExactlyOnce_WhenTheTrimIsZero()
    {
        const double raw = 10_000;
        const double deviceLatency = 7.25; // a real ASIO figure

        Assert.Equal(raw - 7.25, Compensate(raw, deviceLatency, userTrimMs: 0), 3);
    }

    /// <summary>
    ///     THE REGRESSION. Copying the measured device latency into the user trim —
    ///     which is precisely what the old MeasureLatency() did — compensates it
    ///     twice, so the playhead lands one whole output latency late.
    /// </summary>
    [Fact]
    public void CopyingTheMeasuredLatencyIntoTheTrim_CompensatesItTwice()
    {
        const double raw = 10_000;
        const double deviceLatency = 7.25;

        var correct = Compensate(raw, deviceLatency, userTrimMs: 0);
        var doubleCounted = Compensate(raw, deviceLatency, userTrimMs: deviceLatency); // the bug

        // The playhead reports an EARLIER song position than it should, i.e. the
        // picture lags the sound by exactly one output latency.
        Assert.Equal(correct - deviceLatency, doubleCounted, 3);
        Assert.True(doubleCounted < correct);
    }

    /// <summary>On a shared-mode WASAPI device the same mistake is an order of
    /// magnitude worse — this is why it reads as "the video is out of sync",
    /// not as "it feels a touch loose".</summary>
    [Fact]
    public void OnAHighLatencyDevice_TheDoubleCountIsGrossly_Visible()
    {
        const double raw = 10_000;
        const double wasapiLatency = 50.0;

        var doubleCounted = Compensate(raw, wasapiLatency, userTrimMs: wasapiLatency);

        Assert.Equal(raw - 100.0, doubleCounted, 3); // 100 ms behind, not 50
    }

    /// <summary>The trim keeps working as a trim — it is an EXTRA offset on top of
    /// the automatic device compensation (for display lag or taste), not a second
    /// place to record the device latency.</summary>
    [Theory]
    [InlineData(0, 10_000 - 7.25)]
    [InlineData(20, 10_000 - 27.25)]
    [InlineData(-20, 10_000 + 12.75)] // negative pulls the playhead ahead
    public void TheTrim_IsAnExtraOffsetOnTopOfTheDeviceLatency(double trim, double expected)
        => Assert.Equal(expected, Compensate(10_000, deviceLatencyMs: 7.25, userTrimMs: trim), 3);

    /// <summary>Compensation may never drive the playhead negative at song start.</summary>
    [Fact]
    public void CompensationNeverGoesNegative()
        => Assert.Equal(0.0, Compensate(rawMs: 3, deviceLatencyMs: 50, userTrimMs: 0), 3);
}
