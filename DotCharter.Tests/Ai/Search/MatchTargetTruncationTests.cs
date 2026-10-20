using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Similarity;

namespace DotCharter.Tests.Ai.Search;

/// <summary>
///     The TARGET half of W5a: "match from audio produces truncated tones".
///     <para>
///         <b>The fixture is the failure, in miniature.</b> The owner's own project
///         has no tone changes, so the panel fell back to the first 30 s of the song
///         — and the separated guitar for that window is digital silence for its
///         first ten seconds and −70 dBFS for five more (measured 2026-09-16, 5 s
///         blocks over <c>stems/guitar.wav</c>: −180, −93.5, −17.3, −16.0, −70.7,
///         −30.2 dBFS). Half the clip the matcher was handed was nothing, and
///         nothing downstream said so: the extractor gates silent frames out of its
///         SPECTRAL blocks only, its dynamics and modulation blocks average the
///         whole buffer, and the self-distance floor slices the raw head — which was
///         all zeros.
///     </para>
///     <para>
///         The synthetic fixture below is that shape with a tone generator instead of
///         a band: <see cref="SilentThenPlaying" /> is 15 s of true zeros followed by
///         15 s of signal.
///     </para>
/// </summary>
public sealed class MatchTargetTruncationTests
{
    private const int Rate = 48_000;

    /// <summary>The owner's window in miniature: silence, then playing.</summary>
    private static MonoAudio SilentThenPlaying(double silentSeconds, double playingSeconds)
    {
        var silent = (int)(silentSeconds * Rate);
        var playing = (int)(playingSeconds * Rate);
        var x = new float[silent + playing];
        // A deterministic pluck-ish signal: a decaying sawtooth, no RNG, no clock.
        for (var i = 0; i < playing; i++)
        {
            var t = i / (double)Rate;
            var env = 0.5 + 0.5 * Math.Cos(2 * Math.PI * 2.0 * t);
            x[silent + i] = (float)(0.4 * env * Math.Sin(2 * Math.PI * 110.0 * t));
        }

        return new MonoAudio(x, Rate);
    }

    [Fact]
    public void ASilentLeadIn_IsCutOffTheTarget_AndReported()
    {
        var target = SilentThenPlaying(15, 15);

        var trimmed = MatchTargetAudio.Trim(target);

        Assert.InRange(trimmed.LeadingSilence.TotalSeconds, 14.9, 15.1);
        Assert.InRange(trimmed.Content.TotalSeconds, 14.5, 15.1);
        Assert.Equal(Rate, trimmed.Audio.SampleRate);
        Assert.NotNull(trimmed.Note);
        Assert.Contains("leading", trimmed.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFirstFiveSecondsOfAnUntrimmedWindow_AreTheAllZeroVector()
    {
        // Why the trim is load-bearing rather than cosmetic: this is exactly the
        // slice ToneMatcher.SelfDistance takes for the floor, and on the owner's
        // window it carries no audio at all. The extractor answers an all-zero
        // vector for it — a perfectly comparable number about nothing.
        var target = SilentThenPlaying(15, 15);
        var extractor = new ToneFeatureExtractor();

        var head = new MonoAudio(target.Samples[..(5 * Rate)], Rate);
        var trimmedHead = new MonoAudio(MatchTargetAudio.Trim(target).Audio.Samples[..(5 * Rate)], Rate);

        // Every block but LEVEL is zero: `Prepare` bails on an RMS under 1e-9 and
        // the two level entries are the only ones written before it does — they
        // report −80 dBFS, i.e. "as quiet as this scale goes". So the vector the
        // floor is measured from describes nothing except its own silence.
        var silent = extractor.Extract(head).Vector;
        for (var i = 0; i < silent.Length; i++)
        {
            if (i is not ToneFeatureLayout.LevelOffset and not (ToneFeatureLayout.LevelOffset + 1))
                Assert.Equal(0f, silent[i]);
        }

        Assert.Equal(-2f, silent[ToneFeatureLayout.LevelOffset]);

        // …and the trimmed head is not: something OUTSIDE the level pair is set.
        var kept = extractor.Extract(trimmedHead).Vector;
        Assert.Contains(
            kept.Where((_, i) => i is not ToneFeatureLayout.LevelOffset and not (ToneFeatureLayout.LevelOffset + 1)),
            v => v != 0f);
    }

    [Fact]
    public void AWindowThatIsEntirelySilent_HasNoContent_AndIsNotMeasurable()
    {
        var silence = new MonoAudio(new float[30 * Rate], Rate);

        var trimmed = MatchTargetAudio.Trim(silence);

        Assert.Empty(trimmed.Audio.Samples);
        Assert.Equal(TimeSpan.Zero, trimmed.Content);
        Assert.False(MatchTargetAudio.IsMeasurable(trimmed));
    }

    [Fact]
    public void AClipThatIsAllPlaying_IsHandedBackUNTOUCHED()
    {
        // The guard against the guard: a trim that fired on ordinary audio would
        // silently shorten every match in the app, and nothing else here would fail.
        var playing = SilentThenPlaying(0, 12);

        var trimmed = MatchTargetAudio.Trim(playing);

        Assert.Same(playing, trimmed.Audio);
        Assert.False(trimmed.WasTrimmed);
        Assert.Null(trimmed.Note);
        Assert.True(MatchTargetAudio.IsMeasurable(trimmed));
    }

    [Fact]
    public void ATrailingTailOfSilence_IsCutToo()
    {
        var target = SilentThenPlaying(2, 8);
        var padded = new float[target.Samples.Length + 6 * Rate];
        Array.Copy(target.Samples, padded, target.Samples.Length);

        var trimmed = MatchTargetAudio.Trim(new MonoAudio(padded, Rate));

        Assert.InRange(trimmed.LeadingSilence.TotalSeconds, 1.9, 2.1);
        Assert.InRange(trimmed.TrailingSilence.TotalSeconds, 5.9, 6.1);
        Assert.Contains("trailing", trimmed.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public void AClipUnderTheMinimumOfPlaying_IsNotMeasurable()
    {
        // One second of playing inside a thirty-second window: the panel refuses it
        // instead of reporting a confident percentage about one note.
        var target = SilentThenPlaying(29, 1);

        Assert.False(MatchTargetAudio.IsMeasurable(MatchTargetAudio.Trim(target)));
        Assert.True(MatchTargetAudio.IsMeasurable(MatchTargetAudio.Trim(SilentThenPlaying(27, 3))));
    }
}
