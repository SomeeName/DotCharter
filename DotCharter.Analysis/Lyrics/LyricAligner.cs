namespace DotCharter.Analysis.Lyrics;

/// <summary>A frames × symbols block of CTC log-probabilities over a whole song.</summary>
/// <param name="LogProbs">Row-major, <c>FrameCount * Width</c> entries, log-softmax (all ≤ 0).</param>
/// <param name="FrameCount">Frames.</param>
/// <param name="Width">Symbols per frame — the model's own output width.</param>
/// <param name="SecondsPerFrame">
///     Audio seconds each frame covers. <b>Derive it as
///     <c>audioSeconds / FrameCount</c>, never from the model's nominal stride.</b>
///     wav2vec2's convolutional front end is usually quoted at 20 ms, but the
///     exact frame count also depends on padding and on how the runner chunked
///     the audio; dividing the real duration by the real frame count absorbs all
///     of it and cannot drift over a four-minute song.
/// </param>
public sealed record CtcEmissions(float[] LogProbs, int FrameCount, int Width, double SecondsPerFrame);

/// <summary>Tuning knobs for the fold from token spans up to syllable times.</summary>
public sealed record LyricAlignmentOptions
{
    public static LyricAlignmentOptions Default { get; } = new();

    /// <summary>
    ///     Added to every onset. Negative moves lyrics EARLIER.
    ///     <para>
    ///         Kept at zero until measured. There is a real asymmetry to exploit —
    ///         listeners tolerate a lyric about 0.33 s early but only about
    ///         0.22 s late, so a small negative bias should be worth more than it
    ///         costs — but the size of the correction is a property of this
    ///         model on this material, and guessing it would bake an unmeasured
    ///         constant into every chart.
    ///     </para>
    /// </summary>
    public double OnsetBiasSeconds { get; init; }

    /// <summary>
    ///     Added on top of <see cref="OnsetBiasSeconds" /> to syllables that OPEN
    ///     ON A VOWEL. CTC fires on the confident vowel, and a sung vowel that
    ///     follows another vowel (a-i) or a breath has no consonant to anchor the
    ///     frame, so its span starts late. Negative moves them EARLIER. Zero for
    ///     a model whose vowel-initial error has not been measured.
    /// </summary>
    public double VowelInitialOnsetBiasSeconds { get; init; }

    /// <summary>
    ///     Cost of one frame of wildcard.
    ///     <para>
    ///         <b>Measured, and the measurement overruled the theory.</b>
    ///         <see cref="CtcForcedAligner.Align" /> documents a real degeneracy
    ///         at 0.0: the star is ≥ every emission, so it eats the front of the
    ///         syllable it precedes. Penalising it fixes exactly that and makes
    ///         the product WORSE, because it also makes a long instrumental too
    ///         expensive to absorb — and the aligner then stretches real
    ///         syllables across the gap instead, which is a far bigger error than
    ///         the frame or two the degeneracy costs.
    ///     </para>
    ///     <para>
    ///         Over the 2,036 hand-synced romaji syllables in
    ///         <c>TestData/EOF</c> (<c>LocalOnly_LyricAlignmentProbe</c>):
    ///         <c>log(1.00)</c> lands <b>90.4%</b> of onsets within 100 ms and
    ///         97.1% within 200 ms, at a mean signed error of −1 ms;
    ///         <c>log(0.05)</c> drops those to 88.9% and 95.1% and blows the mean
    ///         out to +119 ms — the signature of a handful of syllables dragged
    ///         across whole sections. Keep it at 0.0 unless a measurement says
    ///         otherwise.
    ///     </para>
    /// </summary>
    public double StarLogProb { get; init; }

    /// <summary>
    ///     Emit the model's own word-boundary symbol between words, when it has
    ///     one. See <see cref="LyricVocabulary.WordDelimiterId" /> — MMS-FA has
    ///     none, so this is a no-op for the shipping model.
    /// </summary>
    public bool InsertWordDelimiters { get; init; } = true;
}

/// <summary>One syllable placed on the song's timeline.</summary>
/// <param name="SyllableIndex">Index into <see cref="LyricSheet.Syllables" />.</param>
/// <param name="StartSeconds">Onset.</param>
/// <param name="EndSeconds">End of the frames CTC assigned to it.</param>
/// <param name="Confidence">
///     Mean per-frame probability of this syllable's own letters, 0..1.
///     <para>
///         Low ABSOLUTE values are normal and not a fault: CTC emits a letter on
///         one frame of its span and blank on the rest, so the mean is diluted by
///         frames where blank is the correct answer. Compare it against a
///         calibrated threshold — see
///         <c>LyricAutoSyncService.LowConfidenceThreshold</c> — never against an
///         intuition about what a probability "should" look like.
///     </para>
///     <see cref="IsInterpolated" /> syllables carry zero.
/// </param>
/// <param name="IsInterpolated">
///     True when the syllable contributed no alignable characters and its time
///     was filled in from its neighbours instead of being measured.
/// </param>
public readonly record struct AlignedSyllable(
    int SyllableIndex,
    double StartSeconds,
    double EndSeconds,
    double Confidence,
    bool IsInterpolated);

/// <summary>Result of aligning a whole sheet against a whole song.</summary>
public sealed record LyricAlignmentResult(
    IReadOnlyList<AlignedSyllable> Syllables,
    IReadOnlyDictionary<char, int> DroppedCharacters,
    string? Failure)
{
    public bool Succeeded => Failure == null;

    public static LyricAlignmentResult Failed(string reason) =>
        new([], new Dictionary<char, int>(), reason);
}

/// <summary>
///     Ties the pieces together: a parsed sheet plus a song's CTC emissions in,
///     syllable onsets out. Pure and deterministic — the same emissions and the
///     same text always produce the same times.
/// </summary>
public static class LyricAligner
{
    public static LyricAlignmentResult Align(
        LyricSheet sheet,
        LyricVocabulary vocabulary,
        CtcEmissions emissions,
        LyricAlignmentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(emissions);
        options ??= LyricAlignmentOptions.Default;

        if (sheet.Syllables.Count == 0) return LyricAlignmentResult.Failed("There are no lyrics to align.");

        var tokenization = RomanizedLyricTokenizer.Tokenize(sheet, vocabulary, options.InsertWordDelimiters);
        if (tokenization.Tokens.Count == 0)
            return LyricAlignmentResult.Failed("None of the lyric text can be aligned.");

        // Every syllable dropping out means the sheet is in a script this model
        // has no columns for — worth saying plainly, because the alternative
        // symptom is an alignment that merely looks inaccurate.
        if (tokenization.UnalignableSyllableCount == sheet.Syllables.Count)
        {
            return LyricAlignmentResult.Failed(
                "None of the lyrics are in the roman alphabet. Paste them as romaji (for Japanese) " +
                "or as ordinary text (for English).");
        }

        var alignment = CtcForcedAligner.Align(
            emissions.LogProbs.AsSpan(), emissions.FrameCount, emissions.Width,
            tokenization.Tokens, vocabulary.BlankId, vocabulary.StarId, options.StarLogProb);

        if (!alignment.Succeeded)
            return LyricAlignmentResult.Failed(alignment.Failure!);

        var spf = emissions.SecondsPerFrame;
        var bias = options.OnsetBiasSeconds;
        var placed = new AlignedSyllable[sheet.Syllables.Count];
        var measured = new bool[sheet.Syllables.Count];
        var previousStart = 0.0;

        foreach (var syllable in tokenization.Syllables)
        {
            if (syllable.TokenCount == 0) continue;

            var first = alignment.Tokens[syllable.TokenStart];
            var last = alignment.Tokens[syllable.TokenStart + syllable.TokenCount - 1];

            var score = 0.0;
            for (var t = 0; t < syllable.TokenCount; t++) score += alignment.Tokens[syllable.TokenStart + t].Score;

            // A per-class shift may move a start earlier than its predecessor's; the
            // order the trellis found is kept.
            var startBias = SyllableOnsetClassifier.Classify(sheet.Syllables[syllable.SyllableIndex].Text) == SyllableOnsetClass.Vowel
                ? bias + options.VowelInitialOnsetBiasSeconds
                : bias;
            var start = Math.Max(previousStart, Math.Max(0.0, first.StartFrame * spf + startBias));
            previousStart = start;

            placed[syllable.SyllableIndex] = new AlignedSyllable(
                syllable.SyllableIndex,
                start,
                Math.Max(start, (last.EndFrame + 1) * spf + bias),
                Math.Exp(score / syllable.TokenCount),
                IsInterpolated: false);
            measured[syllable.SyllableIndex] = true;
        }

        FillInterpolated(placed, measured, spf);

        return new LyricAlignmentResult(placed, tokenization.DroppedCharacters, null);
    }

    /// <summary>
    ///     Give a syllable that contributed no letters a plausible time instead
    ///     of leaving it at zero, where it would land before the song starts and
    ///     read as a wildly wrong alignment rather than as a gap. Such a
    ///     syllable is squeezed into the space after its nearest measured
    ///     predecessor; runs of them share that space evenly.
    /// </summary>
    private static void FillInterpolated(AlignedSyllable[] placed, bool[] measured, double secondsPerFrame)
    {
        for (var i = 0; i < placed.Length; i++)
        {
            if (measured[i]) continue;

            var runEnd = i;
            while (runEnd < placed.Length && !measured[runEnd]) runEnd++;

            var before = i > 0 ? placed[i - 1].EndSeconds : 0.0;
            var after = runEnd < placed.Length ? placed[runEnd].StartSeconds : before + secondsPerFrame;
            if (after < before) after = before;

            var count = runEnd - i;
            var step = (after - before) / (count + 1);

            for (var k = 0; k < count; k++)
            {
                var start = before + step * k;
                placed[i + k] = new AlignedSyllable(
                    i + k, start, start + step, Confidence: 0.0, IsInterpolated: true);
            }

            i = runEnd - 1;
        }
    }
}
