using DotCharter.Analysis.Lyrics;

namespace DotCharter.Tests.Analysis.Lyrics;

/// <summary>
///     The pooled path: a word only one head heard drowns in the other head's blank in the
///     geometric blend, but the linear pool keeps it, and where the two paths disagree by more
///     than <see cref="LyricPooledPath.AgreementSeconds" /> the pooled placement wins.
/// </summary>
public sealed class LyricPooledPathTests
{
    private const double SecondsPerFrame = 0.02;
    private const string Letters = "abcdefghijklmnopqrstuvwxyz";
    private static readonly LyricVocabulary Vocabulary = LyricVocabulary.FromTokens(["<blank>", .. Letters.Select(c => c.ToString())]);

    [Fact]
    public void ALetterOnlyTheSecondaryHeardSurvivesThePoolButNotTheBlend()
    {
        var primary = Head(10, blankFrom: 0, blankTo: 10);
        var secondary = Head(10, ("m", 5));

        var blended = CtcEmissionBlender.Blend(primary, secondary, Vocabulary, Vocabulary, 0.6);

        Assert.NotNull(blended.Pooled);
        Assert.Equal(Vocabulary.BlankId, ArgMax(blended, 5));
        Vocabulary.TryGetId('m', out var m);
        Assert.Equal(m, ArgMax(blended.Pooled!, 5));
        Assert.Equal(1.0, Enumerable.Range(0, blended.Width).Sum(k => Math.Exp(blended.Pooled!.LogProbs[5 * blended.Width + k])), 1e-4);
    }

    [Fact]
    public void ALineOnlyTheSecondaryHeardIsPlacedWhereItWasSung()
    {
        var sheet = LyricSheetParser.Parse("hello there\nmumble words\nfinal chorus");
        var primary = Head(400, blankFrom: 150, blankTo: 250, ("hellothere", 20), ("finalchorus", 300));
        var secondary = Head(400, ("hellothere", 20), ("mumblewords", 200), ("finalchorus", 300));
        var blended = CtcEmissionBlender.Blend(primary, secondary, Vocabulary, Vocabulary, 0.6);

        var result = LyricAligner.Align(sheet, Vocabulary, blended, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Failure);
        Assert.Equal(0.4, result.Syllables[0].StartSeconds, 0.05);
        Assert.Equal(4.0, result.Syllables[2].StartSeconds, 0.1);
        Assert.Equal(6.0, result.Syllables[4].StartSeconds, 0.05);
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(50, true)]
    public void ThePooledSpansReplaceTheBlendsOnlyWhereTheyDisagree(int shift, bool pooledWins)
    {
        var tokenization = RomanizedLyricTokenizer.Tokenize(LyricSheetParser.Parse("ab cd"), Vocabulary);
        var blended = new CtcAlignment([.. Enumerable.Range(0, tokenization.Tokens.Count).Select(t => new TokenSpan(100 + 10 * t, 100 + 10 * t, -1))], -4, null);
        var pooled = new CtcAlignment([.. blended.Tokens.Select(s => new TokenSpan(s.StartFrame + shift, s.EndFrame + shift, -2))], -8, null);

        var merged = LyricPooledPath.Merge(blended, pooled, tokenization, SecondsPerFrame);

        var letters = tokenization.Syllables.SelectMany(s => Enumerable.Range(s.TokenStart, s.TokenCount)).ToList();
        Assert.Equal(4, letters.Count);
        Assert.Equal(letters.Select(t => (pooledWins ? pooled : blended).Tokens[t]), letters.Select(t => merged.Tokens[t]));
    }

    private static int ArgMax(CtcEmissions emissions, int frame)
    {
        var best = 0;
        for (var k = 1; k < emissions.Width; k++)
            if (emissions.LogProbs[frame * emissions.Width + k] > emissions.LogProbs[frame * emissions.Width + best]) best = k;

        return best;
    }

    private static CtcEmissions Head(int frames, params (string Text, int Frame)[] sung) => Head(frames, 0, 0, sung);

    /// <summary>Blank at 0.9 (0.99999 inside the deaf window), each sung letter on its own frame two frames apart at 0.9.</summary>
    private static CtcEmissions Head(int frames, int blankFrom, int blankTo, params (string Text, int Frame)[] sung)
    {
        var width = Vocabulary.SymbolCount;
        var probabilities = new double[frames * width];
        for (var f = 0; f < frames; f++) Fill(probabilities, f, width, 0, f >= blankFrom && f < blankTo ? 0.99999 : 0.9);
        foreach (var (text, start) in sung)
        {
            for (var i = 0; i < text.Length; i++)
            {
                Vocabulary.TryGetId(text[i], out var id);
                Fill(probabilities, start + 2 * i, width, id, 0.9);
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
