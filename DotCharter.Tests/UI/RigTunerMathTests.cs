using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tests.UI;

/// <summary>
/// Pure display math behind the pedalboard's rig tuner
/// (<see cref="RigTunerMath"/>): chromatic nearest-note mapping, cents,
/// needle fraction and the strobe-wheel phase advance. The DSP itself
/// (TunerPitchEngine/VirtualStrobe/TunerDisplaySmoother) is covered by the
/// Services tuner-suite tests.
/// </summary>
public class RigTunerMathTests
{
    [Theory]
    [InlineData(440.0, 69)]   // A4
    [InlineData(82.41, 40)]   // E2 (low E)
    [InlineData(110.0, 45)]   // A2
    [InlineData(146.83, 50)]  // D3
    [InlineData(196.0, 55)]   // G3
    [InlineData(246.94, 59)]  // B3
    [InlineData(329.63, 64)]  // E4
    [InlineData(30.87, 23)]   // B0 (drop-tuned 5-string bass)
    public void NearestMidi_maps_standard_string_frequencies(double hz, int expected)
        => Assert.Equal(expected, RigTunerMath.NearestMidi(hz));

    [Fact]
    public void NearestMidi_rounds_to_the_closer_semitone()
    {
        // 452 Hz is ~46 cents sharp of A4 → still A4; 455 Hz is ~58 cents → A#4.
        Assert.Equal(69, RigTunerMath.NearestMidi(452));
        Assert.Equal(70, RigTunerMath.NearestMidi(455));
    }

    [Fact]
    public void NearestMidi_rejects_non_positive()
    {
        Assert.Equal(-1, RigTunerMath.NearestMidi(0));
        Assert.Equal(-1, RigTunerMath.NearestMidi(-5));
    }

    [Fact]
    public void FrequencyOfMidi_roundtrips_with_NearestMidi()
    {
        for (var midi = 23; midi <= 88; midi++)
        {
            var f = RigTunerMath.FrequencyOfMidi(midi);
            Assert.Equal(midi, RigTunerMath.NearestMidi(f));
            Assert.Equal(0.0, RigTunerMath.CentsOff(f, midi), 6);
        }
    }

    [Theory]
    [InlineData(440.0, 69, 0.0)]
    [InlineData(441.0, 69, 3.93)]   // slightly sharp
    [InlineData(439.0, 69, -3.94)]  // slightly flat
    public void CentsOff_signs_sharp_positive_flat_negative(double hz, int midi, double expected)
        => Assert.Equal(expected, RigTunerMath.CentsOff(hz, midi), 1);

    [Theory]
    [InlineData(0.0, 0.5)]     // in tune = centre
    [InlineData(-50.0, 0.0)]   // flat stop
    [InlineData(50.0, 1.0)]    // sharp stop
    [InlineData(25.0, 0.75)]
    [InlineData(-500.0, 0.0)]  // clamps beyond the stops
    [InlineData(500.0, 1.0)]
    public void NeedleFraction_maps_and_clamps(double cents, double expected)
        => Assert.Equal(expected, RigTunerMath.NeedleFraction(cents), 5);

    [Fact]
    public void AdvanceWheel_is_stationary_in_tune()
        => Assert.Equal(0.37, RigTunerMath.AdvanceWheel(0.37, cents: 0, deltaMs: 66), 9);

    [Fact]
    public void AdvanceWheel_direction_follows_the_sign()
    {
        var sharp = RigTunerMath.AdvanceWheel(0.5, cents: 10, deltaMs: 100);
        var flat = RigTunerMath.AdvanceWheel(0.5, cents: -10, deltaMs: 100);
        Assert.True(sharp > 0.5);
        Assert.True(flat < 0.5);
    }

    [Fact]
    public void AdvanceWheel_rate_is_one_turn_per_second_at_25_cents()
    {
        // 25 cents ⇒ exactly one revolution per second ⇒ phase returns home.
        var phase = 0.25;
        for (var i = 0; i < 10; i++)
            phase = RigTunerMath.AdvanceWheel(phase, cents: 25, deltaMs: 100);
        Assert.Equal(0.25, phase, 9);
    }

    [Fact]
    public void AdvanceWheel_wraps_into_unit_range_both_ways()
    {
        var up = RigTunerMath.AdvanceWheel(0.95, cents: 50, deltaMs: 100);
        Assert.InRange(up, 0.0, 1.0);

        var down = RigTunerMath.AdvanceWheel(0.05, cents: -50, deltaMs: 100);
        Assert.InRange(down, 0.0, 1.0);
    }
}
