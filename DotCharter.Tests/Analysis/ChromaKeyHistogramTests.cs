using DotCharter.Analysis.Key;

namespace DotCharter.Tests.Analysis;

/// <summary>
///     #822: a song with no notes gets its key from the audio's summed chroma. Synthetic
///     cadences, rendered as sine chords, must read as their key; silence is unknown.
/// </summary>
public sealed class ChromaKeyHistogramTests
{
    private const int SampleRate = 16000;

    [Fact]
    public void AMajorCadence_ReadsAsItsKey()
    {
        // D major: D F# A | G B D | A C# E | D F# A (held), MIDI around the guitar's middle.
        var audio = Render(([62, 66, 69], 1), ([55, 59, 62], 1), ([57, 61, 64], 1), ([50, 62, 66, 69], 3));

        var estimate = SongKeyEstimator.FromHistogram(ChromaKeyHistogram.FromMono(audio, SampleRate, TestContext.Current.CancellationToken));

        Assert.NotNull(estimate);
        Assert.Equal(new ChordKeySignature(2, false), estimate.Value.Best);
    }

    [Fact]
    public void AMinorCadence_ReadsAsItsKey()
    {
        // A minor: A C E | D F A | E G# B | A C E (held).
        var audio = Render(([57, 60, 64], 1), ([62, 65, 69], 1), ([52, 56, 59, 64], 1), ([45, 57, 60, 64], 3));

        var estimate = SongKeyEstimator.FromHistogram(ChromaKeyHistogram.FromMono(audio, SampleRate, TestContext.Current.CancellationToken));

        Assert.NotNull(estimate);
        Assert.Equal(new ChordKeySignature(0, true), estimate.Value.Best);
    }

    [Fact]
    public void Silence_IsUnknown()
    {
        Assert.Null(SongKeyEstimator.FromHistogram(ChromaKeyHistogram.FromMono(new float[SampleRate * 4], SampleRate, TestContext.Current.CancellationToken)));
        Assert.Null(SongKeyEstimator.FromHistogram(ChromaKeyHistogram.FromMono([], SampleRate, TestContext.Current.CancellationToken)));
    }

    // Each chord as equal sines for so many seconds.
    private static float[] Render(params (int[] Midi, double Seconds)[] chords)
    {
        var samples = new List<float>();
        foreach (var (midi, seconds) in chords)
        {
            var count = (int)(seconds * SampleRate);
            for (var i = 0; i < count; i++)
            {
                var t = (double)i / SampleRate;
                var v = midi.Sum(m => Math.Sin(2 * Math.PI * 440 * Math.Pow(2, (m - 69) / 12.0) * t));
                samples.Add((float)(0.2 * v / midi.Length));
            }
        }

        return [.. samples];
    }
}
