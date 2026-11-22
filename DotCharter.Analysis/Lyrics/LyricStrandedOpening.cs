namespace DotCharter.Analysis.Lyrics;

/// <summary>
///     G3 of <see cref="LyricRepairGate" />: a line's first syllable the model never heard, left
///     behind on the previous line's voice (or in silence) while the rest of its line is sung after
///     a long silence. It moves to where the voice holding the line's next syllable begins when
///     all hold: its own letters are not heard where it stands (as in G2); nothing began there for
///     it, because its old start is on unvoiced frames or on voice that began no later than one
///     onset after the previous syllable started; and between its old place and that voice lies
///     one unbroken silence, no voiced region at all, of at least a <c>Gap</c>
///     (<see cref="LyricAlignmentReview.MaxBlankGapSeconds" />); and its own letters are heard
///     nowhere inside the span it would get (they would prove a place of their own).
///     It ends where the next syllable starts; when that leaves less than one onset, the next
///     syllable, when itself unheard and kept where it stood, starts one onset later, as long as
///     it keeps a length and stays before the syllable after it; otherwise neither moves and both
///     are flagged.
/// </summary>
internal static class LyricStrandedOpening
{
    private const double Floor = LyricAlignmentReview.LowConfidenceThreshold;

    /// <summary>
    ///     The new spans of syllable <paramref name="k" /> and, when it has to make room, the next
    ///     one; null when G3 does not hold, no <c>Moved</c> when it holds but there is no room.
    ///     <paramref name="final" /> gives a syllable's start as the gate has settled it;
    ///     <paramref name="nextKept" /> says the next one keeps its old place.
    /// </summary>
    internal static (AlignedSyllable? Moved, AlignedSyllable? Pushed)? Place(
        LyricRepairPass pass, int[] line, int k, Func<int, double> final, bool nextKept, LyricVocalRegions voice,
        (double Lower, double Upper) bounds)
    {
        var old = pass.Aligned[line[k]];
        if (k + 1 >= line.Length || old.Confidence >= Floor) return null;
        if (SilenceEnd(pass, line[k], voice) is not { } from) return null;

        var nextStart = final(k + 1);
        var region = voice.RegionOf(nextStart);
        if (region < 0 || voice.Regions[region].Start <= from) return null;

        var target = voice.Regions[region].Start;
        if (target - from < LyricAlignmentReview.MaxBlankGapSeconds || target < bounds.Lower - 1e-9) return null;
        // One unbroken silence: any voice in between, a pick-up of the syllable's own included, keeps it.
        for (var r = 0; r < region; r++)
            if (voice.Regions[r].End > from + 1e-9) return null;

        var end = Math.Max(nextStart, target + pass.SameOnsetSeconds);
        if (end > bounds.Upper + 1e-9 || LyricLetterEvidence.At(pass, line[k], old.StartSeconds, end - target) >= Floor) return null;
        // Its letters heard inside the span it would get prove a place there: G3 is no guess to make then.
        for (var t = target; t < end - 1e-9; t += pass.Emissions.SecondsPerFrame)
            if (LyricLetterEvidence.At(pass, line[k], t, end - t) >= Floor) return null;

        var after = k + 2 < line.Length ? final(k + 2) : bounds.Upper;
        var next = pass.Aligned[line[k + 1]];
        if (end <= nextStart) return (old with { StartSeconds = target, EndSeconds = end }, null);
        if (!nextKept || next.Confidence >= Floor || end >= next.EndSeconds || end >= after) return (null, null);

        return (old with { StartSeconds = target, EndSeconds = end }, next with { StartSeconds = end });
    }

    /// <summary>
    ///     Where the silence after a stranded start begins: the start itself on unvoiced frames, or
    ///     the end of the voice it sits on when that voice began no later than one onset after the
    ///     previous syllable did (the previous line's tail; the song's first syllable has none).
    ///     Null otherwise.
    /// </summary>
    private static double? SilenceEnd(LyricRepairPass pass, int index, LyricVocalRegions voice)
    {
        var start = pass.Aligned[index].StartSeconds;
        var region = voice.RegionOf(start);
        if (region < 0) return voice.IsVoiced(start) ? null : start;
        if (index == 0 || voice.VoiceBegan(region) > pass.Result[index - 1].StartSeconds + pass.SameOnsetSeconds) return null;

        return voice.Regions[region].End;
    }
}
