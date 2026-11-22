using DotCharter.Analysis.Lyrics;

namespace DotCharter.Tests.Analysis.Lyrics;

/// <summary>
///     Anchor-first alignment: words the model clearly heard pin the spans the rest is
///     forced into, a repeated line never anchors to the wrong repeat, and an unheard last line
///     is not stretched over the stem's bleed after the voice has ended.
/// </summary>
public sealed class LyricAnchoredAlignmentTests
{
    private const double SecondsPerFrame = 0.02;
    private const string Letters = "abcdefghijklmnopqrstuvwxyz";
    private static readonly LyricVocabulary Vocabulary = LyricVocabulary.FromTokens(["<blank>", .. Letters.Select(c => c.ToString())]);

    [Fact]
    public void AnUnheardLastLineEndsWithTheVoiceNotOnTheBleedAfterIt()
    {
        var sheet = LyricSheetParser.Parse("hello there\nmumble words");
        var emissions = Emissions(400, ("hellothere", 20, 0.9), ("mumblewords", 300, 0.45));
        var levels = new float[800];
        for (var f = 40; f < 220; f++) levels[f] = 1f;
        for (var f = 600; f < 680; f++) levels[f] = 0.02f;

        var result = LyricAligner.Align(sheet, Vocabulary, emissions, cancellationToken: TestContext.Current.CancellationToken, envelope: new VocalEnvelope(levels, 0.01));

        Assert.True(result.Succeeded, result.Failure);
        Assert.Equal(0.4, result.Syllables[0].StartSeconds, 0.05);
        Assert.InRange(result.Syllables[2].StartSeconds, 0.6, 3.7);
        Assert.InRange(result.Syllables[3].StartSeconds, result.Syllables[2].StartSeconds, 3.7);
    }

    [Fact]
    public void WithoutAStemTheWholeSongStaysAvailable()
    {
        var sheet = LyricSheetParser.Parse("hello there\nmumble words");
        var emissions = Emissions(400, ("hellothere", 20, 0.9), ("mumblewords", 300, 0.45));

        var result = LyricAligner.Align(sheet, Vocabulary, emissions, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(6.0, result.Syllables[2].StartSeconds, 0.05);
    }

    [Fact]
    public void ARepeatedLineHeardOnItsOwnDoesNotAnchorToEitherRepeat()
    {
        var sheet = LyricSheetParser.Parse("shine on me\nalpha bravo\nshine on me");
        var emissions = Emissions(300, ("shineonme", 200, 0.9));

        Assert.Empty(Find(sheet, emissions));
    }

    [Fact]
    public void ARepeatedLineAnchorsToTheRepeatItsHeardNeighbourPicks()
    {
        var sheet = LyricSheetParser.Parse("shine on me\nalpha bravo\nshine on me");
        var emissions = Emissions(300, ("alphabravo", 150, 0.9), ("shineonme", 200, 0.9));
        var tokenization = RomanizedLyricTokenizer.Tokenize(sheet, Vocabulary);

        var anchors = Find(sheet, emissions);

        Assert.Equal([tokenization.Syllables[3].TokenStart, tokenization.Syllables[4].TokenStart, tokenization.Syllables[5].TokenStart],
            anchors.Select(a => a.TokenIndex));
        Assert.Equal(200, anchors[2].Frame);
    }

    [Fact]
    public void AnUnheardLineBetweenHeardLinesStaysBetweenThem()
    {
        var sheet = LyricSheetParser.Parse("hello there\nmumble words\nfinal chorus");
        var emissions = Emissions(400, ("hellothere", 20, 0.9), ("finalchorus", 300, 0.9));

        var result = LyricAligner.Align(sheet, Vocabulary, emissions, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0.4, result.Syllables[0].StartSeconds, 0.05);
        Assert.Equal(6.0, result.Syllables[4].StartSeconds, 0.05);
        Assert.InRange(result.Syllables[2].StartSeconds, result.Syllables[1].StartSeconds, result.Syllables[4].StartSeconds);
        Assert.InRange(result.Syllables[3].StartSeconds, result.Syllables[2].StartSeconds, result.Syllables[4].StartSeconds);
    }

    private static List<LyricAnchor> Find(LyricSheet sheet, CtcEmissions emissions) =>
        LyricAnchors.Find(emissions, sheet, RomanizedLyricTokenizer.Tokenize(sheet, Vocabulary), Vocabulary, TestContext.Current.CancellationToken);

    /// <summary>Blank everywhere, each sung letter on its own frame two frames apart at the given probability.</summary>
    private static CtcEmissions Emissions(int frames, params (string Text, int Frame, double Probability)[] sung)
    {
        var width = Vocabulary.SymbolCount;
        var probabilities = new double[frames * width];
        for (var f = 0; f < frames; f++) Fill(probabilities, f, width, 0, 0.9);
        foreach (var (text, start, probability) in sung)
        {
            for (var i = 0; i < text.Length; i++)
            {
                Vocabulary.TryGetId(text[i], out var id);
                Fill(probabilities, start + 2 * i, width, id, probability);
            }
        }

        return new CtcEmissions([.. probabilities.Select(p => (float)Math.Log(p))], frames, width, SecondsPerFrame);
    }

    private static void Fill(double[] probabilities, int frame, int width, int id, double probability)
    {
        for (var k = 0; k < width; k++) probabilities[frame * width + k] = (1 - probability) / (width - 1);
        probabilities[frame * width + id] = probability;
    }
}
