using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     The auto-calibration policy. Its guards are the point: a player who genuinely
///     drags produces the same positive median as an under-compensated audio device,
///     so the rules that keep those two apart are what these tests pin.
/// </summary>
public class LatencyBiasCalibratorTests
{
    /// <summary>
    ///     A run whose median really is <paramref name="medianMs" /> and whose spread is
    ///     about <paramref name="stdDevMs" />. Deltas are laid out symmetrically around
    ///     the median over an odd number of hits, so the middle sample IS the median —
    ///     jittering alternately by ±spread would instead park the median on a tail.
    ///     Sign convention: negative = early.
    /// </summary>
    private static LiveTimingReport Run(double medianMs, int hits = 61, double stdDevMs = 15)
    {
        if (hits % 2 == 0) hits++; // odd, so there is a true middle sample

        var collector = new LiveTimingCollector();

        // Uniform over [-a, a] has stddev a/sqrt(3), so pick a to hit the target spread.
        var halfWidth = stdDevMs * Math.Sqrt(3);

        for (var i = 0; i < hits; i++)
        {
            var t = hits == 1 ? 0 : (2.0 * i / (hits - 1)) - 1.0; // -1 .. +1
            collector.Add(i * 500.0, medianMs + (halfWidth * t));
        }

        return collector.BuildReport();
    }

    [Fact]
    public void A_consistently_late_run_raises_the_offset()
    {
        // Late (+) means we are under-compensating for the device's latency.
        var decision = LatencyBiasCalibrator.Evaluate(
            currentOffsetMs: 50, baselineOffsetMs: 50, Run(medianMs: 20));

        Assert.True(decision.Adjusted);
        Assert.True(decision.NewOffsetMs > 50, $"offset should rise, got {decision.NewOffsetMs}");
    }

    [Fact]
    public void A_consistently_early_run_lowers_the_offset()
    {
        var decision = LatencyBiasCalibrator.Evaluate(
            currentOffsetMs: 50, baselineOffsetMs: 50, Run(medianMs: -20));

        Assert.True(decision.Adjusted);
        Assert.True(decision.NewOffsetMs < 50, $"offset should fall, got {decision.NewOffsetMs}");
    }

    [Fact]
    public void It_only_moves_part_of_the_way_in_one_run()
    {
        // One song is not proof. A full jump would let a single bad night retune the game.
        var decision = LatencyBiasCalibrator.Evaluate(50, 50, Run(medianMs: 20));

        Assert.True(decision.Adjusted);
        Assert.True(decision.NewOffsetMs < 70,
            $"a single run moved the whole residual ({decision.NewOffsetMs}) — should be partial");
    }

    [Fact]
    public void An_inconsistent_run_teaches_nothing()
    {
        // THE guard against learning sloppiness. Latency is a CONSTANT: it shows up as a
        // tight distribution sitting off-centre. A wide spread is a player problem.
        var decision = LatencyBiasCalibrator.Evaluate(
            50, 50, Run(medianMs: 25, stdDevMs: LatencyBiasCalibrator.MaxSpreadMs + 20));

        Assert.False(decision.Adjusted);
        Assert.Equal(50, decision.NewOffsetMs);
        Assert.Contains("inconsistent", decision.Reason);
    }

    [Fact]
    public void A_short_run_teaches_nothing()
    {
        var decision = LatencyBiasCalibrator.Evaluate(
            50, 50, Run(medianMs: 25, hits: LatencyBiasCalibrator.MinHits - 5));

        Assert.False(decision.Adjusted);
        Assert.Equal(50, decision.NewOffsetMs);
    }

    [Fact]
    public void A_run_with_no_timed_hits_teaches_nothing()
    {
        // Real case: every hit CQT pull-verified, which is deliberately untimed.
        Assert.False(LatencyBiasCalibrator.Evaluate(50, 50, null).Adjusted);
        Assert.False(LatencyBiasCalibrator.Evaluate(50, 50, LiveTimingReport.Empty).Adjusted);
    }

    [Fact]
    public void An_already_centred_run_does_not_churn_the_config()
    {
        var decision = LatencyBiasCalibrator.Evaluate(50, 50, Run(medianMs: 1));

        Assert.False(decision.Adjusted);
        Assert.Contains("centred", decision.Reason);
    }

    [Fact]
    public void Bad_habits_cannot_walk_the_offset_away_from_the_measurement()
    {
        // The player drags by 40 ms every run, consistently. Without a leash the offset
        // would keep climbing and quietly erase their timing error from the scoring.
        // It must converge to the measured baseline + the cap, and stop there.
        var offset = 50.0;
        const double baseline = 50.0;

        for (var run = 0; run < 60; run++)
        {
            var decision = LatencyBiasCalibrator.Evaluate(offset, baseline, Run(medianMs: 40));
            offset = decision.NewOffsetMs;
        }

        Assert.True(offset <= baseline + LatencyBiasCalibrator.MaxDriftFromBaselineMs + 0.05,
            $"offset ran away to {offset} — the leash to the measured baseline failed");
    }

    [Fact]
    public void A_real_device_change_still_converges_inside_the_leash()
    {
        // Swapping a 128-frame ASIO buffer for a 512-frame WASAPI one is worth tens of
        // ms — well within the cap, so the learner must actually get there.
        var offset = 50.0;
        const double baseline = 50.0;

        for (var run = 0; run < 30; run++)
        {
            // As the offset approaches the truth, the residual shrinks.
            var residual = 75.0 - offset;
            var decision = LatencyBiasCalibrator.Evaluate(offset, baseline, Run(medianMs: residual));
            if (!decision.Adjusted) break;
            offset = decision.NewOffsetMs;
        }

        Assert.InRange(offset, 70, 80); // converged on the true ~75 ms
    }

    [Fact]
    public void The_offset_never_goes_negative()
    {
        // It is a latency compensation; a negative one is meaningless and would shift
        // detections into the future.
        var decision = LatencyBiasCalibrator.Evaluate(2, 0, Run(medianMs: -60));

        Assert.True(decision.NewOffsetMs >= 0, $"offset went negative: {decision.NewOffsetMs}");
    }
}

/// <summary>The device key that decides WHICH learned offset applies.</summary>
public class AudioDeviceKeyTests
{
    [Fact]
    public void The_same_setup_produces_the_same_key()
    {
        Assert.Equal(
            AudioDeviceKey.From("ASIO", "Focusrite", "Focusrite", 48000, 128),
            AudioDeviceKey.From("ASIO", "Focusrite", "Focusrite", 48000, 128));
    }

    [Theory]
    // Every one of these genuinely changes the round-trip, so each must get its own
    // learned offset rather than inheriting the previous setup's.
    [InlineData("WASAPI", "Focusrite", "Focusrite", 48000, 128)]
    [InlineData("ASIO", "Other input", "Focusrite", 48000, 128)]
    [InlineData("ASIO", "Focusrite", "Other output", 48000, 128)]
    [InlineData("ASIO", "Focusrite", "Focusrite", 44100, 128)]
    [InlineData("ASIO", "Focusrite", "Focusrite", 48000, 512)]
    public void A_different_setup_produces_a_different_key(
        string driver, string input, string output, int rate, int buffer)
    {
        Assert.NotEqual(
            AudioDeviceKey.From("ASIO", "Focusrite", "Focusrite", 48000, 128),
            AudioDeviceKey.From(driver, input, output, rate, buffer));
    }

    [Fact]
    public void Missing_device_names_still_key_stably()
    {
        var a = AudioDeviceKey.From(null, null, null, 0, 0);
        var b = AudioDeviceKey.From("", "  ", null, 0, 0);

        Assert.Equal(a, b); // both are "unknown setup", not two different ones
    }

    [Fact]
    public void A_device_name_containing_the_separator_cannot_forge_another_key()
    {
        var sneaky = AudioDeviceKey.From("ASIO", "a|b", "c", 48000, 128);
        var plain = AudioDeviceKey.From("ASIO", "a", "b|c", 48000, 128);

        Assert.NotEqual(sneaky, plain);
    }

    // The drop tuner puts a pitch shifter in front of the capture ring, which moves
    // the true input latency. If it is not in the key, a run with the pedal engaged
    // teaches an offset that then gets applied to every ordinary run.
    [Fact]
    public void Engaging_the_drop_tuner_produces_a_different_key()
    {
        Assert.NotEqual(
            AudioDeviceKey.From("ASIO", "Focusrite", "Focusrite", 48000, 128),
            AudioDeviceKey.From("ASIO", "Focusrite", "Focusrite", 48000, 128, dropTuneSemitones: -2));
    }

    [Fact]
    public void Different_drop_amounts_and_modes_each_key_separately()
    {
        var dropTwo = AudioDeviceKey.From("ASIO", "F", "F", 48000, 128, dropTuneSemitones: -2);
        var dropFive = AudioDeviceKey.From("ASIO", "F", "F", 48000, 128, dropTuneSemitones: -5);
        var dropTwoAlt = AudioDeviceKey.From("ASIO", "F", "F", 48000, 128, dropTuneSemitones: -2, dropTuneMode: 2);

        Assert.NotEqual(dropTwo, dropFive);
        Assert.NotEqual(dropTwo, dropTwoAlt); // a different lag ceiling is a different latency
    }

    // Backward compatibility: the pedal is off in the overwhelming majority of runs,
    // and those must keep matching the offsets users have ALREADY learned. So a
    // disengaged tuner has to produce byte-for-byte the pre-existing key.
    [Fact]
    public void A_disengaged_drop_tuner_leaves_the_key_untouched()
    {
        Assert.Equal(
            AudioDeviceKey.From("ASIO", "Focusrite", "Focusrite", 48000, 128),
            AudioDeviceKey.From("ASIO", "Focusrite", "Focusrite", 48000, 128,
                                dropTuneSemitones: 0, dropTuneMode: 1));
    }
}
