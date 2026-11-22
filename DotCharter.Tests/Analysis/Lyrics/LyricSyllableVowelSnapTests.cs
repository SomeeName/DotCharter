using DotCharter.Analysis.Lyrics;

namespace DotCharter.Tests.Analysis.Lyrics;

/// <summary>#869: a syllable cut by the rules starts on its vowel frame, never elsewhere.</summary>
public sealed class LyricSyllableVowelSnapTests
{
    private static LyricVocabulary Vocab() => LyricVocabulary.FromTokens(["<pad>", "a", "b", "c", "d"]);

    private static CtcEmissions Spell(string letters)
    {
        var vocab = Vocab();
        var width = vocab.WidthWithStar;
        var frames = letters.Length * 5;
        var data = new float[frames * width];
        for (var f = 0; f < frames; f++)
        {
            var winner = f % 5 == 0 && vocab.TryGetId(letters[f / 5], out var id) ? id : vocab.BlankId;
            for (var s = 0; s < width; s++)
                data[f * width + s] = (float)Math.Log(s == winner ? 0.9 : 0.1 / (width - 1));
        }

        return new CtcEmissions(data, frames, width, 0.02);
    }

    private static LyricSheet Sheet(bool split, params string[] syllables) => new(
        [.. syllables.Select((t, i) => new LyricSyllable(t, i < syllables.Length - 1, i == syllables.Length - 1, 0) { SplitByRules = split })], 1);

    private static IReadOnlyList<AlignedSyllable> Align(LyricSheet sheet, string letters)
    {
        var result = LyricAligner.Align(sheet, Vocab(), Spell(letters), new LyricAlignmentOptions { StarLogProb = -20.0 });
        Assert.True(result.Succeeded, result.Failure);
        return result.Syllables;
    }

    [Fact]
    public void A_split_syllable_starts_on_its_vowel_frame()
    {
        var plain = Align(Sheet(false, "ba", "cda"), "bacda");
        var snapped = Align(Sheet(true, "ba", "cda"), "bacda");

        Assert.Equal(0.2, plain[1].StartSeconds, 6);
        Assert.Equal(0.4, snapped[1].StartSeconds, 6);
    }

    [Fact]
    public void Onsets_stay_monotonic_and_inside_the_word()
    {
        var snapped = Align(Sheet(true, "ba", "cda", "dcda"), "bacdadcda");

        Assert.True(snapped[0].StartSeconds <= snapped[1].StartSeconds);
        Assert.True(snapped[1].StartSeconds <= snapped[2].StartSeconds);
        Assert.All(snapped, s => Assert.True(s.EndSeconds >= s.StartSeconds));
        Assert.True(snapped[2].StartSeconds <= snapped[2].EndSeconds);
    }

    [Fact]
    public void Without_vowel_evidence_the_start_stands()
    {
        var plain = Align(Sheet(false, "ba", "cd", "dc"), "bacddc");
        var snapped = Align(Sheet(true, "ba", "cd", "dc"), "bacddc");

        Assert.Equal(plain[1].StartSeconds, snapped[1].StartSeconds, 6);
        Assert.Equal(plain[2].StartSeconds, snapped[2].StartSeconds, 6);
        Assert.Equal(plain[0].StartSeconds, snapped[0].StartSeconds, 6);
    }

    [Fact]
    public void A_words_first_syllable_is_never_moved()
    {
        var plain = Align(Sheet(false, "cda", "ba"), "cdaba");
        var snapped = Align(Sheet(true, "cda", "ba"), "cdaba");

        Assert.Equal(plain[0].StartSeconds, snapped[0].StartSeconds, 6);
    }
}
