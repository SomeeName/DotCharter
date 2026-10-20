using DotCharter.Rendering.Data.Render;

namespace DotCharter.Rendering.Data;

/// <summary>
///     Pure beat-pulse intensity for the 0.14.2 "beat-pulse lighting" effect:
///     the most recent beat at/before the playhead kicks the highway glow,
///     decaying with a quadratic ease-out over a fixed window. Measure
///     downbeats pulse at full strength, plain beats softer. No scene/engine
///     deps — unit-tested in <c>BeatPulseMathTests</c>.
/// </summary>
public static class BeatPulseMath
{
    /// <summary>How long one pulse takes to die out. Shorter than any sane
    /// beat interval (272 bpm at 220 ms) so pulses read as discrete flashes.</summary>
    public const double PulseWindowMs = 220.0;

    /// <summary>Relative strength of a non-measure beat.</summary>
    public const float BeatLevel = 0.55f;

    /// <summary>Intensity 0..1 at <paramref name="timeMs" /> from the beats
    /// visible in the snapshot window (which always spans the recent past —
    /// the 3D look-behind exceeds <see cref="PulseWindowMs" />).</summary>
    public static float Compute(RenderBeat[] beats, int beatCount, double timeMs)
    {
        for (var i = beatCount - 1; i >= 0; i--)
        {
            var beat = beats[i];
            if (beat.Time > timeMs) continue;

            var age = timeMs - beat.Time;
            if (age >= PulseWindowMs) return 0f;

            var k = 1f - (float)(age / PulseWindowMs);
            k *= k; // ease-out: sharp attack, soft tail
            return beat.IsMeasure ? k : k * BeatLevel;
        }

        return 0f;
    }
}
