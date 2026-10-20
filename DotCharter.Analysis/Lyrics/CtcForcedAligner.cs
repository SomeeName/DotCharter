namespace DotCharter.Analysis.Lyrics;

/// <summary>The frames one token was held for, and how well it scored there.</summary>
/// <param name="StartFrame">First frame inclusive.</param>
/// <param name="EndFrame">Last frame inclusive.</param>
/// <param name="Score">
///     Mean log-probability of this token's own symbol across its frames. A star
///     scores its flat wildcard cost rather than anything measured, so a caller
///     ranking confidence should ignore star spans.
/// </param>
public readonly record struct TokenSpan(int StartFrame, int EndFrame, double Score);

/// <summary>Outcome of a forced alignment.</summary>
/// <param name="Tokens">One span per input token, in order. Empty when <paramref name="Failure" /> is set.</param>
/// <param name="TotalLogProb">Log-probability of the winning path.</param>
/// <param name="Failure">Null on success; otherwise why no path exists.</param>
public sealed record CtcAlignment(IReadOnlyList<TokenSpan> Tokens, double TotalLogProb, string? Failure)
{
    public bool Succeeded => Failure == null;

    public static CtcAlignment Failed(string reason) => new([], double.NegativeInfinity, reason);
}

/// <summary>
///     Viterbi forced alignment of a known token sequence against CTC emissions.
///     Pure, deterministic and model-agnostic: it never sees audio, only a
///     frames × symbols matrix of log-probabilities.
///     <para>
///         The classic construction — the token sequence is interleaved with
///         blanks into an extended sequence
///         <c>[blank, t0, blank, t1, blank, …, tN-1, blank]</c> of length
///         <c>2N+1</c>, and the best monotone path through it is found by
///         dynamic programming. Three transitions are legal into state
///         <c>s</c>: stay at <c>s</c>, advance from <c>s-1</c>, or skip the
///         blank at <c>s-1</c> by jumping from <c>s-2</c>. The skip is allowed
///         only when <c>s</c> is a real label and <c>ext[s] != ext[s-2]</c> —
///         two identical adjacent tokens MUST have a blank frame between them or
///         CTC would collapse them into one. That single condition is what makes
///         a doubled letter (<c>tt</c>, <c>nn</c>) come out as two sounds rather
///         than one.
///     </para>
///     <para>
///         <b>Label states are never skipped, so every token gets at least one
///         frame.</b> The skip transition moves <c>s-2 → s</c> where <c>s</c> is
///         odd, so the state it passes over is always the even (blank) one. That
///         is a property worth knowing: the caller can rely on every token
///         having a non-empty span, and a token with a suspiciously short span
///         is a confidence signal rather than a missing result.
///     </para>
///     <para>
///         <b>Backpointers are packed two bits per cell.</b> There are only three
///         transitions, so a byte per cell wastes 75% of the largest allocation
///         in the feature. A four-minute song aligned against a full lyric sheet
///         is roughly 12,000 frames × 4,200 states = 50M cells: 50 MB as bytes,
///         12.5 MB packed. It matters because the trellis is the one structure
///         that scales with song length TIMES lyric length.
///     </para>
/// </summary>
public static class CtcForcedAligner
{
    /// <summary>
    ///     Refuse rather than allocate past this many trellis cells (~300 MB
    ///     packed). Reached only by a very long song aligned against a very long
    ///     sheet; the caller's remedy is to align section by section.
    /// </summary>
    public const long MaxTrellisCells = 1_200_000_000;

    /// <param name="logProbs">
    ///     Row-major frames × <paramref name="width" /> log-probabilities
    ///     (log-softmax output — every value ≤ 0).
    /// </param>
    /// <param name="frameCount">Number of frames in <paramref name="logProbs" />.</param>
    /// <param name="width">Symbols per frame.</param>
    /// <param name="tokens">The symbol sequence to force-align.</param>
    /// <param name="blankId">The CTC blank symbol.</param>
    /// <param name="starId">
    ///     Wildcard symbol, or -1 for none. It is <b>synthesised, not read</b>:
    ///     wherever the extended sequence holds it the emission is taken as
    ///     <paramref name="starLogProb" /> instead of being looked up. That keeps
    ///     <paramref name="logProbs" /> exactly the model's own output — no
    ///     widened copy of the matrix — and is why <c>starId</c> is normally
    ///     equal to <paramref name="width" /> rather than a column inside it.
    /// </param>
    /// <param name="starLogProb">
    ///     What one frame of wildcard costs.
    ///     <para>
    ///         <b>Zero (probability 1.0) is the reference implementation's value
    ///         and it is subtly harmful here.</b> A log-probability is never
    ///         positive, so a star at 0.0 is greater than or equal to EVERY real
    ///         emission — and the best path then hands the star every frame it is
    ///         allowed to take, stopping only where a token's own emission would
    ///         collapse. In practice that means the star eats the FRONT of the
    ///         first sustained syllable after it, pushing that onset later. Since
    ///         a star sits at every line boundary, the syllable it damages is the
    ///         first of each line — the one the charter looks at — and it damages
    ///         it in the direction listeners notice most (a late lyric is roughly
    ///         1.5× as objectionable as an early one).
    ///     </para>
    ///     <para>
    ///         <b>And yet 0.0 is still the right default — measured, not
    ///         assumed.</b> Penalising the star does remove the degeneracy, but a
    ///         penalty is charged per frame, so absorbing a 30-second
    ///         instrumental costs hundreds of nats and the best path starts
    ///         preferring to park a real syllable there instead. That trade is
    ///         badly one-sided: the degeneracy costs a frame or two at a line
    ///         start, the alternative costs whole sections.
    ///     </para>
    ///     <para>
    ///         Over the 2,036 hand-synced romaji syllables in
    ///         <c>TestData/EOF</c>, <c>log(1.00)</c> puts <b>90.4%</b> of onsets
    ///         within 100 ms (mean signed error −1 ms) against 88.9% for
    ///         <c>log(0.05)</c>, whose mean blows out to +119 ms. The knob stays
    ///         because it is the honest way to record that this was tested — do
    ///         not re-tune it without re-running
    ///         <c>LocalOnly_LyricAlignmentProbe</c>.
    ///     </para>
    /// </param>
    public static CtcAlignment Align(
        ReadOnlySpan<float> logProbs,
        int frameCount,
        int width,
        IReadOnlyList<int> tokens,
        int blankId,
        int starId = -1,
        double starLogProb = 0.0)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var n = tokens.Count;
        if (n == 0) return new CtcAlignment([], 0.0, null);
        if (frameCount <= 0) return CtcAlignment.Failed("The audio produced no emission frames.");
        if (logProbs.Length < (long)frameCount * width)
            return CtcAlignment.Failed("The emission matrix is smaller than its declared shape.");

        var stateCount = 2 * n + 1;

        // A path advances at most one label per frame, and two identical
        // adjacent tokens additionally need a blank between them.
        var minimumFrames = n;
        for (var i = 1; i < n; i++)
            if (tokens[i] == tokens[i - 1]) minimumFrames++;
        if (frameCount < minimumFrames)
        {
            return CtcAlignment.Failed(
                $"Too many syllables for the audio: {minimumFrames} frames are needed at minimum but " +
                $"only {frameCount} are available. Check that the lyrics belong to this song.");
        }

        var cells = (long)frameCount * stateCount;
        if (cells > MaxTrellisCells)
        {
            return CtcAlignment.Failed(
                $"The lyric sheet is too long to align against this song in one pass " +
                $"({frameCount} frames × {stateCount} states). Align it in sections.");
        }

        var ext = new int[stateCount];
        for (var i = 0; i < n; i++)
        {
            ext[2 * i] = blankId;
            ext[2 * i + 1] = tokens[i];
        }
        ext[stateCount - 1] = blankId;

        var back = new byte[(cells + 3) / 4];
        var previous = new double[stateCount];
        var current = new double[stateCount];
        Array.Fill(previous, double.NegativeInfinity);

        // A static local so the ref-like span can be threaded through it.
        static double Emission(
            ReadOnlySpan<float> lp, int width, int starId, double starLogProb, int frame, int symbol) =>
            symbol == starId ? starLogProb : lp[frame * width + symbol];

        previous[0] = Emission(logProbs, width, starId, starLogProb, 0, ext[0]);
        if (stateCount > 1) previous[1] = Emission(logProbs, width, starId, starLogProb, 0, ext[1]);

        for (var t = 1; t < frameCount; t++)
        {
            // Outside this band no path can both reach the state from the start
            // and still finish by the last frame, so those cells are dead.
            var remaining = frameCount - 1 - t;
            var sMin = (int)Math.Max(0, stateCount - 2 - 2L * remaining);
            var sMax = (int)Math.Min(stateCount - 1, 2L * t + 1);

            Array.Fill(current, double.NegativeInfinity);

            for (var s = sMin; s <= sMax; s++)
            {
                var best = previous[s];
                var choice = 0;

                if (s > 0 && previous[s - 1] > best)
                {
                    best = previous[s - 1];
                    choice = 1;
                }

                if (s > 1 && ext[s] != blankId && ext[s] != ext[s - 2] && previous[s - 2] > best)
                {
                    best = previous[s - 2];
                    choice = 2;
                }

                if (double.IsNegativeInfinity(best)) continue;

                current[s] = best + Emission(logProbs, width, starId, starLogProb, t, ext[s]);
                SetChoice(back, (long)t * stateCount + s, choice);
            }

            (previous, current) = (current, previous);
        }

        // A valid path ends on the final blank or on the final label.
        var last = stateCount - 1;
        var terminal = stateCount > 1 && previous[stateCount - 2] > previous[last] ? stateCount - 2 : last;
        if (double.IsNegativeInfinity(previous[terminal]))
            return CtcAlignment.Failed("No alignment path exists between these lyrics and this audio.");

        var totalLogProb = previous[terminal];

        // Walk the choices backwards to recover which state owned each frame.
        var path = new int[frameCount];
        var state = terminal;
        for (var t = frameCount - 1; t >= 0; t--)
        {
            path[t] = state;
            if (t > 0) state -= GetChoice(back, (long)t * stateCount + state);
        }

        var spans = new TokenSpan[n];
        var index = 0;
        for (var i = 0; i < n; i++)
        {
            var target = 2 * i + 1;
            while (index < frameCount && path[index] != target) index++;

            var start = index;
            var sum = 0.0;
            while (index < frameCount && path[index] == target)
            {
                sum += Emission(logProbs, width, starId, starLogProb, index, ext[target]);
                index++;
            }

            var end = index - 1;
            var length = end - start + 1;
            spans[i] = new TokenSpan(start, end, length > 0 ? sum / length : double.NegativeInfinity);
        }

        return new CtcAlignment(spans, totalLogProb, null);
    }

    private static void SetChoice(byte[] packed, long cell, int choice)
    {
        var slot = (int)(cell >> 2);
        var shift = (int)((cell & 3) * 2);
        packed[slot] = (byte)((packed[slot] & ~(0b11 << shift)) | (choice << shift));
    }

    private static int GetChoice(byte[] packed, long cell)
    {
        var slot = (int)(cell >> 2);
        var shift = (int)((cell & 3) * 2);
        return (packed[slot] >> shift) & 0b11;
    }
}
