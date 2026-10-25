using DotCharter.Analysis.Lyrics;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Audio.Lyrics;

namespace DotCharter.Tests.Services.Audio.Lyrics;

/// <summary>
///     <see cref="LyricAutoSyncService.BuildPhrases" /> with a vocal envelope: the
///     end of a syllable follows the voice in both directions, inside the same
///     bounds the span rule has always had. Pure — synthetic envelopes, no model.
/// </summary>
public sealed class LyricVocalEnvelopeBuildTests
{
    private const double Hop = 0.010;

    /// <summary>120 BPM: one beat every 500 ms, 32 beats = 16 s.</summary>
    private static List<BeatMarker> Beats() =>
        [.. Enumerable.Range(0, 32).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    /// <summary>The default minimum gap on that grid: 1/16 beat.</summary>
    private const double MinGapMs = 31.25;

    private static VocalEnvelope Voiced(double seconds, params (double From, double To)[] voiced)
    {
        var levels = new float[(int)Math.Round(seconds / Hop)];
        for (var k = 0; k < levels.Length; k++)
        {
            var t = k * Hop;
            if (voiced.Any(v => t >= v.From - 1e-9 && t < v.To - 1e-9)) levels[k] = 0.5f;
        }

        return new VocalEnvelope(levels, Hop);
    }

    private static List<LyricPhrase> Build(
        string text, VocalEnvelope? envelope, LyricAutoSyncOptions? options = null,
        params (double Start, double End)[] times)
    {
        var sheet = LyricSheetParser.Parse(text);
        Assert.Equal(times.Length, sheet.Syllables.Count);

        var aligned = times
            .Select((t, i) => new AlignedSyllable(i, t.Start, t.End, 0.9, IsInterpolated: false))
            .ToList();

        return LyricAutoSyncService.BuildPhrases(
            sheet, aligned, Beats(), options ?? LyricAutoSyncOptions.Default, envelope);
    }

    private static double EndMs(LyricEvent lyric) => lyric.EndPosition.GetPositionMs(Beats());

    private static double StartMs(LyricEvent lyric) => lyric.Position.GetPositionMs(Beats());

    [Fact]
    public void AnInWordSpanStopsWhereTheVoiceGoesSilent()
    {
        // "i" of "i-tsu": the voice holds to 1.4 s, breathes until 1.95 s, then "tsu".
        var envelope = Voiced(4.0, (1.0, 1.4), (1.95, 2.5));

        var withVoice = Build("i-tsu", envelope, null, (1.0, 1.05), (2.0, 2.1));
        var without = Build("i-tsu", null, null, (1.0, 1.05), (2.0, 2.1));

        Assert.Equal(1400.0, EndMs(withVoice[0].Lyrics[0]), 15.0);
        Assert.Equal(2000.0 - MinGapMs, EndMs(without[0].Lyrics[0]), 1.0);
    }

    [Fact]
    public void AnInWordSpanOverContinuousVoiceIsUnchanged()
    {
        var envelope = Voiced(4.0, (1.0, 2.5));

        var withVoice = Build("i-tsu", envelope, null, (1.0, 1.05), (2.0, 2.1));
        var without = Build("i-tsu", null, null, (1.0, 1.05), (2.0, 2.1));

        Assert.Equal(EndMs(without[0].Lyrics[0]), EndMs(withVoice[0].Lyrics[0]), 1e-6);
    }

    [Fact]
    public void APhraseFinalSyllableHeldOverASustainedVoiceExtendsToTheFade()
    {
        // "no" is the last syllable of line one: CTC heard it 1.5-1.6 s, the voice
        // holds until 3.0 s and fades out by 3.5 s; the next phrase starts at 6 s.
        var levels = new float[600];
        for (var k = 100; k < 350; k++)
            levels[k] = k < 300 ? 0.5f : 0.5f * (1 - (k - 300) / 50f);
        var envelope = new VocalEnvelope(levels, Hop);

        var withVoice = Build("a no\nb", envelope, null, (1.0, 1.1), (1.5, 1.6), (6.0, 6.2));
        var without = Build("a no\nb", null, null, (1.0, 1.1), (1.5, 1.6), (6.0, 6.2));

        Assert.Equal(1600.0, EndMs(without[0].Lyrics[1]), 2.0);
        Assert.InRange(EndMs(withVoice[0].Lyrics[1]), 3300.0, 3450.0);
        Assert.Equal(EndMs(withVoice[0].Lyrics[1]), withVoice[0].EndPosition.GetPositionMs(Beats()), 1e-6);
    }

    [Fact]
    public void APhraseFinalSyllableOverSilenceKeepsItsCtcEnd()
    {
        var envelope = Voiced(8.0, (1.0, 1.1), (1.5, 1.6));

        var phrases = Build("a no\nb", envelope, null, (1.0, 1.1), (1.5, 1.6), (6.0, 6.2));

        Assert.Equal(1600.0, EndMs(phrases[0].Lyrics[1]), 2.0);
    }

    [Fact]
    public void TheExtensionNeverReachesTheNextOnsetMinusTheMinimumGap()
    {
        // The voice fades only at 3.5 s, but the next phrase starts at 3.0 s: with
        // the phrase gap spanned the end runs to the bound and no further.
        var options = LyricAutoSyncOptions.Default with { SpanPhraseEndToNextPhrase = true };
        var envelope = Voiced(8.0, (1.0, 3.5));

        var phrases = Build("a no\nb", envelope, options, (1.0, 1.1), (1.5, 1.6), (3.0, 3.2));

        Assert.Equal(3000.0 - MinGapMs, EndMs(phrases[0].Lyrics[1]), 1.0);
    }

    [Fact]
    public void TheExtensionNeverPassesMaxSyllableSeconds()
    {
        var options = LyricAutoSyncOptions.Default with { MaxSyllableSeconds = 1.0 };
        var envelope = Voiced(8.0, (1.0, 6.0));

        var phrases = Build("i-tsu", envelope, options, (1.0, 1.05), (5.0, 5.1));

        Assert.Equal(2000.0, EndMs(phrases[0].Lyrics[0]), 1.0);
    }

    [Fact]
    public void FollowingTheVoiceNeverMovesAnOnset()
    {
        var envelope = Voiced(8.0, (1.0, 1.4), (1.95, 2.5));
        var times = new[] { (1.0, 1.05), (2.0, 2.1), (2.6, 2.7) };

        var withVoice = Build("i-tsu mo", envelope, null, times);
        var without = Build("i-tsu mo", null, null, times);

        Assert.Equal(
            without.SelectMany(p => p.Lyrics).Select(StartMs),
            withVoice.SelectMany(p => p.Lyrics).Select(StartMs));
    }

    [Fact]
    public void ASyllableIsNeverShorterThanItsCtcEnd()
    {
        // Silence right after the onset, but CTC heard the syllable until 1.3 s.
        var envelope = Voiced(4.0, (1.0, 1.05), (1.95, 2.5));

        var phrases = Build("i-tsu", envelope, null, (1.0, 1.3), (2.0, 2.1));

        Assert.Equal(1300.0, EndMs(phrases[0].Lyrics[0]), 1.0);
    }
}
