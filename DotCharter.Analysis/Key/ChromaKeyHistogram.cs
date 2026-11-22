using DotCharter.Analysis.BeatMap;

namespace DotCharter.Analysis.Key;

/// <summary>
///     The pitch-class weight of a recording, for reading its key when the song has
///     no notes yet: the beat aligner's per-frame chroma (log magnitude folded
///     into 12 bins over 70 Hz–2.5 kHz, mean-removed, unit length, silence zero),
///     summed over every frame. Index 0 is C.
/// </summary>
public static class ChromaKeyHistogram
{
    /// <summary>One chroma frame every this many milliseconds; a key needs no finer grain.</summary>
    public const double HopMs = 200;

    /// <summary>The summed chroma of <paramref name="mono" />; all zeros for silence or an empty buffer.</summary>
    public static double[] FromMono(ReadOnlySpan<float> mono, int sampleRate, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        var sum = new double[12];
        if (mono.IsEmpty) return sum;

        var frames = Math.Max(1, (int)Math.Ceiling(mono.Length * 1000.0 / sampleRate / HopMs));
        foreach (var frame in RecordingAlignmentFeatures.Chroma(mono, sampleRate, HopMs, frames, ct))
        {
            for (var i = 0; i < 12; i++)
                sum[i] += frame[i];
        }

        return sum;
    }
}
