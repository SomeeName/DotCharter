using DotCharter.Analysis.Lyrics;

namespace DotCharter.Tests.Analysis.Lyrics;

/// <summary>
///     The alignment core, driven by hand-built emission matrices so every
///     expected span is known exactly. No model, no audio, no tolerance windows —
///     if one of these moves, the dynamic program changed.
/// </summary>
public sealed class CtcForcedAlignerTests
{
    private const int Blank = 0, A = 1, B = 2, Z = 3;
    private const int Width = 4;

    /// <summary>Star lives one past the model's own width — see <see cref="LyricVocabulary" />.</summary>
    private const int Star = Width;

    /// <summary>A frame where the named symbols carry the given probability and the rest share what's left.</summary>
    private static double[] Frame(params (int Symbol, double Probability)[] weights)
    {
        var frame = new double[Width];
        var assigned = weights.Sum(w => w.Probability);
        var rest = Math.Max(1e-9, 1.0 - assigned) / (Width - weights.Length);

        for (var i = 0; i < Width; i++) frame[i] = rest;
        foreach (var (symbol, probability) in weights) frame[symbol] = probability;

        return frame;
    }

    private static float[] LogProbs(params double[][] frames) =>
        frames.SelectMany(f => f.Select(p => (float)Math.Log(p))).ToArray();

    private static double[] Dominant(int symbol) => Frame((symbol, 0.97));

    [Fact]
    public void TwoSyllablesSplitTheFramesWhereTheEmissionsChange()
    {
        var frames = LogProbs(
            Dominant(A), Dominant(A), Dominant(A),
            Dominant(B), Dominant(B), Dominant(B));

        var result = CtcForcedAligner.Align(frames, frameCount: 6, Width, [A, B], Blank);

        Assert.True(result.Succeeded, result.Failure);
        Assert.Equal(0, result.Tokens[0].StartFrame);
        Assert.Equal(2, result.Tokens[0].EndFrame);
        Assert.Equal(3, result.Tokens[1].StartFrame);
        Assert.Equal(5, result.Tokens[1].EndFrame);
    }

    /// <summary>
    ///     The one condition that makes a doubled letter two sounds instead of
    ///     one: CTC collapses repeats, so <c>a a</c> is only distinguishable from
    ///     <c>a</c> when a blank frame separates them. The aligner must therefore
    ///     refuse to run the two tokens together.
    /// </summary>
    [Fact]
    public void ADoubledTokenIsSeparatedByABlankFrame()
    {
        // EVERY frame favours 'a' — including the middle one, which the blank
        // has to claim anyway. That is deliberate: giving the middle frame to
        // the blank makes this path strictly WORSE by emission score, so the
        // only thing that can produce it is the structural constraint. An
        // earlier version of this test made the middle frame blank-dominant,
        // and then the scores alone forced the right answer — it passed with
        // the constraint deleted.
        var frames = LogProbs(Dominant(A), Dominant(A), Dominant(A));

        var result = CtcForcedAligner.Align(frames, frameCount: 3, Width, [A, A], Blank);

        Assert.True(result.Succeeded, result.Failure);
        Assert.Equal(0, result.Tokens[0].EndFrame);
        Assert.Equal(2, result.Tokens[1].StartFrame);
    }

    /// <summary>
    ///     Two identical tokens need three frames, not two — the blank between
    ///     them is mandatory, so the feasibility check has to count it.
    /// </summary>
    [Fact]
    public void ADoubledTokenNeedsAFrameForItsSeparatingBlank()
    {
        var frames = LogProbs(Dominant(A), Dominant(A));

        var result = CtcForcedAligner.Align(frames, frameCount: 2, Width, [A, A], Blank);

        Assert.False(result.Succeeded);
        Assert.Contains("Too many syllables", result.Failure);
    }

    /// <summary>
    ///     <b>The reason the star token exists.</b> Here the instrumental intro
    ///     (frames 0-5) emits a confident letter the lyrics never use, and
    ///     crucially rates <c>a</c> ABOVE blank — so the cheapest way to explain
    ///     those frames without a wildcard is to start the first syllable at
    ///     frame 0. That is the failure this feature would otherwise ship: the
    ///     first sung syllable dragged back across the intro.
    /// </summary>
    [Fact]
    public void WithoutAStarAnInstrumentalIntroDragsTheFirstSyllableToTheStart()
    {
        var intro = Frame((Z, 0.97), (A, 0.015), (Blank, 0.005));
        var frames = LogProbs(intro, intro, intro, intro, intro, intro,
            Dominant(A), Dominant(A), Dominant(A));

        var result = CtcForcedAligner.Align(frames, frameCount: 9, Width, [A], Blank);

        Assert.True(result.Succeeded, result.Failure);
        Assert.Equal(0, result.Tokens[0].StartFrame);
    }

    /// <summary>
    ///     A penalised star absorbs the intro and stops exactly where the
    ///     syllable really begins.
    /// </summary>
    [Fact]
    public void APenalisedStarAbsorbsTheIntroAndTheSyllableKeepsItsRealOnset()
    {
        var intro = Frame((Z, 0.97), (A, 0.015), (Blank, 0.005));
        var frames = LogProbs(intro, intro, intro, intro, intro, intro,
            Dominant(A), Dominant(A), Dominant(A));

        var result = CtcForcedAligner.Align(
            frames, frameCount: 9, Width, [Star, A], Blank, Star, Math.Log(0.05));

        Assert.True(result.Succeeded, result.Failure);
        Assert.Equal(6, result.Tokens[1].StartFrame);
    }

    /// <summary>
    ///     The degeneracy at the shipping value, pinned deliberately. At
    ///     <c>log(1.0) = 0</c> the star is greater than or equal to every real
    ///     emission, so the best path keeps feeding it frames right up to the
    ///     last moment the syllable can still be placed — here eating two thirds
    ///     of a sustained vowel and reporting the onset two frames late.
    ///     <para>
    ///         <b>This is a known cost that we accept, not a bug awaiting a
    ///         fix.</b> Penalising the star cures it and measures WORSE overall
    ///         (90.4% → 88.9% of onsets within 100 ms over the hand-synced
    ///         corpus), because the same penalty stops the star from absorbing
    ///         long instrumentals. The test exists so that anyone who "fixes"
    ///         this has to look at the number first.
    ///     </para>
    /// </summary>
    [Fact]
    public void AnUnpenalisedStarEatsTheFrontOfTheSyllableItPrecedes()
    {
        var intro = Frame((Z, 0.97), (A, 0.015), (Blank, 0.005));
        var frames = LogProbs(intro, intro, intro, intro, intro, intro,
            Dominant(A), Dominant(A), Dominant(A));

        var result = CtcForcedAligner.Align(
            frames, frameCount: 9, Width, [Star, A], Blank, Star, starLogProb: 0.0);

        Assert.True(result.Succeeded, result.Failure);
        Assert.Equal(8, result.Tokens[1].StartFrame);
    }

    /// <summary>
    ///     …and the penalty must not be so large that the star stops doing its
    ///     job: it still has to beat a token that is nowhere in the audio.
    /// </summary>
    [Fact]
    public void APenalisedStarStillOutbidsATokenTheAudioDoesNotSupport()
    {
        var intro = Frame((Z, 0.999), (A, 0.0001), (Blank, 0.0001));
        var frames = LogProbs(intro, intro, intro, intro,
            Dominant(A), Dominant(A));

        var result = CtcForcedAligner.Align(
            frames, frameCount: 6, Width, [Star, A], Blank, Star, Math.Log(0.05));

        Assert.True(result.Succeeded, result.Failure);
        Assert.Equal(4, result.Tokens[1].StartFrame);
    }

    /// <summary>
    ///     A structural guarantee callers depend on: the skip transition only
    ///     ever jumps over a BLANK state, so no token can be passed over. A
    ///     syllable therefore always has a span, and a short one is a confidence
    ///     signal rather than a missing result.
    /// </summary>
    [Fact]
    public void EveryTokenReceivesAtLeastOneFrameEvenWhenTheAudioContradictsIt()
    {
        // Every frame screams Z; none of the tokens are ever the best choice.
        var frames = LogProbs(Enumerable.Range(0, 10).Select(_ => Dominant(Z)).ToArray());

        var result = CtcForcedAligner.Align(frames, frameCount: 10, Width, [A, B, A, B], Blank);

        Assert.True(result.Succeeded, result.Failure);
        Assert.All(result.Tokens, span => Assert.True(span.EndFrame >= span.StartFrame,
            $"token span {span.StartFrame}..{span.EndFrame} is empty"));
    }

    [Fact]
    public void SpansAreMonotonicAndNeverOverlap()
    {
        var frames = LogProbs(
            Dominant(A), Dominant(B), Dominant(A), Dominant(Blank),
            Dominant(B), Dominant(A), Dominant(B), Dominant(A));

        var result = CtcForcedAligner.Align(frames, frameCount: 8, Width, [A, B, A, B], Blank);

        Assert.True(result.Succeeded, result.Failure);
        for (var i = 1; i < result.Tokens.Count; i++)
        {
            Assert.True(result.Tokens[i].StartFrame > result.Tokens[i - 1].EndFrame,
                $"token {i} starts at {result.Tokens[i].StartFrame} but token {i - 1} ends at " +
                $"{result.Tokens[i - 1].EndFrame}");
        }
    }

    [Fact]
    public void AnEmptyTokenSequenceSucceedsWithNoSpans()
    {
        var result = CtcForcedAligner.Align(LogProbs(Dominant(A)), frameCount: 1, Width, [], Blank);

        Assert.True(result.Succeeded, result.Failure);
        Assert.Empty(result.Tokens);
    }

    [Fact]
    public void AudioWithNoFramesIsReportedRatherThanCrashing()
    {
        var result = CtcForcedAligner.Align([], frameCount: 0, Width, [A], Blank);

        Assert.False(result.Succeeded);
        Assert.Contains("no emission frames", result.Failure);
    }

    /// <summary>
    ///     The backpointer store packs four cells per byte. This drives enough
    ///     frames and states that every bit position inside a byte is exercised,
    ///     so a shift/mask error cannot hide behind a small case.
    /// </summary>
    [Fact]
    public void ThePackedBackpointerStoreSurvivesEveryBitOffset()
    {
        var pattern = new[] { A, B, Z, A, B };
        var frames = LogProbs(Enumerable.Range(0, 60).Select(i => Dominant(pattern[i % pattern.Length])).ToArray());
        var tokens = Enumerable.Range(0, 15).Select(i => pattern[i % 3 == 0 ? 0 : i % pattern.Length]).ToList();

        var result = CtcForcedAligner.Align(frames, frameCount: 60, Width, tokens, Blank);

        Assert.True(result.Succeeded, result.Failure);
        Assert.Equal(tokens.Count, result.Tokens.Count);
        for (var i = 1; i < result.Tokens.Count; i++)
            Assert.True(result.Tokens[i].StartFrame > result.Tokens[i - 1].EndFrame);
    }

    [Fact]
    public void TheSameInputAlwaysProducesTheSameAlignment()
    {
        var frames = LogProbs(
            Dominant(A), Dominant(B), Dominant(Z), Dominant(A), Dominant(B), Dominant(A));

        var first = CtcForcedAligner.Align(frames, 6, Width, [A, B, A], Blank);
        var second = CtcForcedAligner.Align(frames, 6, Width, [A, B, A], Blank);

        Assert.Equal(first.Tokens, second.Tokens);
        Assert.Equal(first.TotalLogProb, second.TotalLogProb);
    }
}
