namespace DotCharter.Analysis.Lyrics;

/// <summary>
///     Anchor-first forced alignment. The words the model clearly heard
///     (<see cref="LyricAnchors" />) cut the song into spans, and each span is force-aligned on
///     its own over its own frames, so a word the model did not hear can only move between its
///     two heard neighbours instead of being piled onto another line or another repeat. With no
///     anchors, or when a span has no path, the whole song is aligned as one path as before.
/// </summary>
internal static class AnchoredCtcAligner
{
    /// <summary>The voice has ended once the stem stays under this share of its loud (90th percentile) level.</summary>
    internal const double VoiceEndLevel = 0.05;

    /// <summary>Lyrics may still run this long past the voice's end (a fading last note).</summary>
    internal const double VoiceEndGraceSeconds = 1.5;

    /// <summary>
    ///     The frame the lyrics must be done by: the stem's last voiced frame plus a grace, or
    ///     the whole song without a stem. Frames after it are the stem's bleed of the band, and
    ///     an unheard last line must not be stretched over them.
    /// </summary>
    internal static int VoiceEndFrame(CtcEmissions emissions, VocalEnvelope? envelope)
    {
        if (envelope == null || envelope.FrameCount == 0) return emissions.FrameCount;
        var threshold = Math.Max(0.002, envelope.ReferenceLevel(0, envelope.DurationSeconds, 0.9) * VoiceEndLevel);
        var last = envelope.FrameCount - 1;
        while (last > 0 && envelope.Level(last) < threshold) last--;
        var end = (int)Math.Ceiling(((last + 1) * envelope.HopSeconds + VoiceEndGraceSeconds) / emissions.SecondsPerFrame);
        return Math.Clamp(end, 1, emissions.FrameCount);
    }

    internal static CtcAlignment Align(
        CtcEmissions emissions, LyricTokenization tokenization, LyricVocabulary vocabulary, List<LyricAnchor> found,
        double starLogProb, int endFrame, CancellationToken ct)
    {
        var whole = () => CtcForcedAligner.Align(emissions.LogProbs.AsSpan(), emissions.FrameCount, emissions.Width,
            tokenization.Tokens, vocabulary.BlankId, vocabulary.StarId, starLogProb, ct);
        var anchors = Thin(found, tokenization.Tokens, endFrame);

        var spans = new TokenSpan[tokenization.Tokens.Count];
        var total = 0.0;
        for (var k = 0; k <= anchors.Count; k++)
        {
            ct.ThrowIfCancellationRequested();
            var (firstToken, firstFrame) = k == 0 ? (0, 0) : (anchors[k - 1].TokenIndex, anchors[k - 1].Frame);
            var (endToken, lastFrame) = k == anchors.Count ? (tokenization.Tokens.Count, endFrame) : (anchors[k].TokenIndex, anchors[k].Frame);
            var tokens = tokenization.Tokens.Skip(firstToken).Take(endToken - firstToken).ToList();
            var frames = lastFrame - firstFrame;
            var span = emissions.LogProbs.AsSpan(firstFrame * emissions.Width, frames * emissions.Width);
            var part = CtcForcedAligner.Align(span, frames, emissions.Width, tokens, vocabulary.BlankId, vocabulary.StarId, starLogProb, ct);
            if (!part.Succeeded) return whole();

            total += part.TotalLogProb;
            for (var t = 0; t < tokens.Count; t++)
            {
                var s = part.Tokens[t];
                spans[firstToken + t] = new TokenSpan(s.StartFrame + firstFrame, s.EndFrame + firstFrame, s.Score);
            }
        }

        // Frames past the voice (the stem's bleed) belong to the last token's closing star.
        if (endFrame < emissions.FrameCount) spans[^1] = spans[^1] with { EndFrame = emissions.FrameCount - 1 };
        return new CtcAlignment(spans, total, null);
    }

    /// <summary>
    ///     Keep anchors in order and only where every span still holds the frames its tokens
    ///     need (one per token, plus a blank between two identical ones).
    /// </summary>
    private static List<LyricAnchor> Thin(List<LyricAnchor> anchors, IReadOnlyList<int> tokens, int endFrame)
    {
        var kept = new List<LyricAnchor>();
        var (lastToken, lastFrame) = (0, 0);
        foreach (var anchor in anchors)
        {
            if (anchor.Frame - lastFrame < Needed(tokens, lastToken, anchor.TokenIndex)) continue;
            if (endFrame - anchor.Frame < Needed(tokens, anchor.TokenIndex, tokens.Count)) continue;
            kept.Add(anchor);
            (lastToken, lastFrame) = (anchor.TokenIndex, anchor.Frame);
        }

        return kept;
    }

    private static int Needed(IReadOnlyList<int> tokens, int from, int to)
    {
        var needed = to - from;
        for (var i = from + 1; i < to; i++)
            if (tokens[i] == tokens[i - 1]) needed++;

        return needed;
    }
}
