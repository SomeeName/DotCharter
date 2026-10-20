using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;

namespace DotCharter.Tests.Rendering;

/// <summary>0.14.2 — pure beat-pulse intensity curve.</summary>
public class BeatPulseMathTests
{
    private static RenderBeat B(double timeMs, bool measure = false) => new() { Time = timeMs, IsMeasure = measure };

    [Fact]
    public void NoBeats_NoPulse() => Assert.Equal(0f, BeatPulseMath.Compute([], 0, 1000));

    [Fact]
    public void MeasureDownbeat_PulsesAtFullStrength_OnTheBeat()
    {
        var beats = new[] { B(1000, true) };
        Assert.Equal(1f, BeatPulseMath.Compute(beats, beats.Length, 1000), 3);
    }

    [Fact]
    public void PlainBeat_PulsesSofter()
    {
        var beats = new[] { B(1000) };
        Assert.Equal(BeatPulseMath.BeatLevel, BeatPulseMath.Compute(beats, beats.Length, 1000), 3);
    }

    [Fact]
    public void Pulse_DecaysToZero_AtTheWindowEnd()
    {
        var beats = new[] { B(1000, true) };
        var mid = BeatPulseMath.Compute(beats, beats.Length, 1000 + BeatPulseMath.PulseWindowMs / 2);
        Assert.InRange(mid, 0.2f, 0.3f); // quadratic ease-out: 0.25 at half-window
        Assert.Equal(0f, BeatPulseMath.Compute(beats, beats.Length, 1000 + BeatPulseMath.PulseWindowMs));
    }

    [Fact]
    public void FutureBeats_DoNotPulseYet()
    {
        var beats = new[] { B(2000, true) };
        Assert.Equal(0f, BeatPulseMath.Compute(beats, beats.Length, 1000));
    }

    [Fact]
    public void MostRecentBeat_Wins()
    {
        // A measure long past and a plain beat just now: the plain beat's
        // fresh pulse is what shows.
        var beats = new[] { B(0, true), B(1000) };
        Assert.Equal(BeatPulseMath.BeatLevel, BeatPulseMath.Compute(beats, beats.Length, 1000), 3);
    }
}
