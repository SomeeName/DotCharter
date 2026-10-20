using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     The wall-clock ↔ transport mapping. These two clocks only tick together at full
///     speed, and every practice feature runs at less than that — so this is where the
///     flagship practice feature used to silently break the flagship judging feature.
/// </summary>
public class LiveTimeMappingTests
{
    private const int SampleRate = 48000;

    [Fact]
    public void At_full_speed_the_two_clocks_are_the_same() =>
        // The old (unscaled) behaviour must be preserved exactly at speed 1 — this is
        // the case every existing test and every measured tuning constant assumes.
        Assert.Equal(1000 - 165 - 50, LiveTimeMapping.ToTransportMs(1000, 165, 50, 1.0));

    [Fact]
    public void At_practice_speed_a_wall_clock_age_is_worth_less_song_time()
    {
        // 60% speed: one real second is 0.6 song-seconds. An attack 165 ms ago (wall)
        // happened only 99 ms ago in the song.
        var transportMs = LiveTimeMapping.ToTransportMs(playheadMs: 1000, ageMs: 165, latencyOffsetMs: 0, speed: 0.6);

        Assert.Equal(1000 - (165 * 0.6), transportMs, 6);
    }

    [Fact]
    public void The_unscaled_maths_would_have_missed_by_most_of_a_hit_window()
    {
        // Why this bug mattered rather than being a rounding detail. Typical analyzer
        // age (~165 ms) + a calibrated 50 ms offset, at the practice ladder's 60%:
        const double playhead = 10_000, age = 165, latency = 50, speed = 0.6;

        var correct = LiveTimeMapping.ToTransportMs(playhead, age, latency, speed);
        var oldBuggy = playhead - age - latency; // what the code used to do

        var errorMs = Math.Abs(correct - oldBuggy);
        Assert.True(errorMs > 80, $"expected a large error, got {errorMs:0.#} ms");
    }

    [Fact]
    public void The_latency_offset_scales_too() =>
        // It is a wall-clock round trip (hear -> play -> captured), so in song time it
        // is worth less at slow speeds, exactly like the event age.
        Assert.Equal(1000 - (50 * 0.5), LiveTimeMapping.ToTransportMs(1000, 50, 0.5), 6);

    [Fact]
    public void A_faster_than_real_speed_scales_the_other_way() => Assert.Equal(1000 - ((100 + 20) * 2.0), LiveTimeMapping.ToTransportMs(1000, 100, 20, 2.0), 6);

    [Theory]
    [InlineData(0.0)]   // never set
    [InlineData(-1.0)]  // nonsense
    [InlineData(99.0)]  // absurd
    public void A_nonsense_speed_behaves_as_real_time_rather_than_reversing_time(double speed)
    {
        // A zero speed would collapse the mapping; a negative one would run the song
        // backwards. Both must degrade to "no scaling", not to garbage.
        Assert.Equal(1.0, LiveTimeMapping.NormalizeSpeed(speed));
        Assert.Equal(1000 - 165 - 50, LiveTimeMapping.ToTransportMs(1000, 165, 50, speed));
    }

    // ---- chart time -> the CAPTURE axis --------------------------------------
    //
    // The tests that used to live here pinned ToTransportSamples — the function that
    // mapped a capture position onto the transport sample axis so the CQT could be
    // "pushed already transport-aligned". They were correct arithmetic for a design that
    // cannot work, and passing them is exactly why the bug shipped: the CQT's Push takes
    // a WRITE CURSOR, so scaling it reset the transform on every push and note detection
    // confirmed nothing at any speed != 1.0. The function is gone; these test its inverse,
    // which is the mapping that is actually needed.
    //
    // The composition test — mapping + CQT together, which is what nobody wrote — lives
    // in LiveCqtSpeedTests.

    [Fact]
    public void At_full_speed_a_chart_time_is_its_own_capture_time()
    {
        // Anchor: capture sample 0 was transport 0 ms. Then chart 1000 ms == capture 1 s.
        var sec = LiveTimeMapping.ToCaptureSeconds(
            transportMs: 1000, anchorStreamPos: 0, anchorTransportMs: 0, sampleRate: SampleRate, speed: 1.0);

        Assert.Equal(1.0, sec, 6);
    }

    [Fact]
    public void At_practice_speed_a_chart_second_is_MORE_than_a_capture_second()
    {
        // At 60%, one second of song takes 1/0.6 = 1.667 s of real time to play. So a
        // note one song-second past the anchor was captured 1.667 s past it.
        var sec = LiveTimeMapping.ToCaptureSeconds(
            transportMs: 1000, anchorStreamPos: 0, anchorTransportMs: 0, sampleRate: SampleRate, speed: 0.6);

        Assert.Equal(1.0 / 0.6, sec, 6);
    }

    [Fact]
    public void The_anchor_offsets_both_axes()
    {
        // Capture sample 100_000 was transport 2000 ms. A chart time 500 ms later, at
        // half speed, is 1 real second further into the capture stream.
        var sec = LiveTimeMapping.ToCaptureSeconds(
            transportMs: 2500, anchorStreamPos: 100_000, anchorTransportMs: 2000,
            sampleRate: SampleRate, speed: 0.5);

        var expected = (100_000 + (0.5 / 0.5 * SampleRate)) / SampleRate;
        Assert.Equal(expected, sec, 6);
    }

    [Fact]
    public void Re_anchoring_mid_run_does_not_move_the_note_that_is_already_in_the_buffer()
    {
        // The pump re-anchors every tick. Two anchors taken at different points of the
        // same run must agree about where a given chart time sits in the capture stream —
        // otherwise the note would appear to slide as you play.
        const double speed = 0.6;

        // Anchor A: capture 0 <-> transport 0.
        var fromA = LiveTimeMapping.ToCaptureSeconds(3000, 0, 0, SampleRate, speed);

        // Anchor B, taken 10 real seconds later. By then the transport has advanced by
        // 10 * 0.6 = 6 song-seconds.
        var bStream = 10L * SampleRate;
        var fromB = LiveTimeMapping.ToCaptureSeconds(3000, bStream, 6000, SampleRate, speed);

        Assert.Equal(fromA, fromB, 6);
    }

    [Fact]
    public void A_nonsense_speed_does_not_collapse_the_capture_mapping()
    {
        // Speed 0 would divide by zero. It must degrade to real time.
        var sec = LiveTimeMapping.ToCaptureSeconds(1000, 0, 0, SampleRate, speed: 0.0);
        Assert.Equal(1.0, sec, 6);
    }
}
