namespace DotCharter.Tests.Analysis;

/// <summary>Deterministic synthesized fixtures shared by the offline-analysis
/// tests: plucked notes, clicks and beat grids. No randomness — every call
/// yields bit-identical samples.</summary>
internal static class AnalysisTestSignals
{
    public const int SampleRate = 44100;

    /// <summary>Beat grid at a constant tempo, beat 0 at t=0.</summary>
    public static List<BeatMarker> ConstantBeats(double bpm, int count)
    {
        var msPerBeat = 60_000.0 / bpm;
        var beats = new List<BeatMarker>(count);
        for (var i = 0; i < count; i++)
            beats.Add(new BeatMarker { Time = i * msPerBeat, Bpm = bpm, IsMeasure = i % 4 == 0 });
        return beats;
    }

    /// <summary>Add a guitar-like pluck: sharp attack, four harmonics,
    /// exponential decay.</summary>
    public static void AddPluck(float[] buffer, double startSeconds, double frequencyHz,
        double durationSeconds = 0.45, float amplitude = 0.30f)
    {
        Span<double> harmonicAmps = [1.0, 0.45, 0.28, 0.15];
        var start = (int)(startSeconds * SampleRate);
        var length = Math.Min((int)(durationSeconds * SampleRate), buffer.Length - start);

        for (var n = 0; n < length; n++)
        {
            var t = (double)n / SampleRate;
            var envelope = Math.Exp(-t / 0.25);
            if (t < 0.002) envelope *= t / 0.002; // 2 ms attack ramp
            double sample = 0;
            for (var h = 0; h < harmonicAmps.Length; h++)
                sample += harmonicAmps[h] * Math.Sin(2.0 * Math.PI * frequencyHz * (h + 1) * t);
            buffer[start + n] += (float)(amplitude * envelope * sample);
        }
    }

    /// <summary>Add a short percussive click (1 kHz ping with fast decay).</summary>
    public static void AddClick(float[] buffer, double startSeconds, float amplitude)
    {
        var start = (int)(startSeconds * SampleRate);
        var length = Math.Min((int)(0.030 * SampleRate), buffer.Length - start);
        for (var n = 0; n < length; n++)
        {
            var t = (double)n / SampleRate;
            var envelope = Math.Exp(-t / 0.004);
            buffer[start + n] += (float)(amplitude * envelope * Math.Sin(2.0 * Math.PI * 1000.0 * t));
        }
    }
}
