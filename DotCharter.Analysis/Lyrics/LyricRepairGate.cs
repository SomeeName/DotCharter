using DotCharter.Core.Domain.Vocals;

namespace DotCharter.Analysis.Lyrics;

/// <summary>
///     The one place that decides whether a proposed start is taken; the repairs only propose.
///     For each syllable whose start would change:
///     G0, a start the model heard (score at least <see cref="LyricAlignmentReview.LowConfidenceThreshold" />)
///     never moves; G1, a start moves where its own letters (<see cref="LyricLetterEvidence" />)
///     reach that score and score higher than where it stood; G2, a start whose own letters are
///     not heard where it stands moves back to where its unbroken voice began when the next
///     syllable now starts on its old start, with its letters heard there, and opens on another
///     letter (the model heard the first syllable's slot late, at the second's); when its own
///     letters are heard inside the span G2 would give it, at the place its line's alignment gave
///     it, it goes there by G1 in the same pass (and stays put when they are no stronger there than
///     at G2's place). A line's opening and the previous line's last syllable are neighbours:
///     letters heard before the previous line's settled end are that line's and prove no G1
///     move of the opening. G3, settled
///     last and whatever the rules proposed: a line's unheard first syllable stranded before a
///     long silence moves to its line's voice (<see cref="LyricStrandedOpening" />), and goes back
///     when a later withdrawal leaves G3 placing it elsewhere or nowhere. Anything
///     else keeps its old start, end and score; when it was proposed more than one onset away and
///     carries no flag yet, it gets <c>Gap</c>. A taken start that would break the line's order
///     next to a kept one, or leave the line, is withdrawn too. Last, no syllable ends past the
///     next one's start: next to a moved start the end is cut back, and a move that would leave
///     less than one onset there is withdrawn and flagged. With <c>flagsOnly</c> nothing
///     moves: the verdicts only flag, which is how the final flag pass reads the final starts.
/// </summary>
internal static class LyricRepairGate
{
    private const double Floor = LyricAlignmentReview.LowConfidenceThreshold;

    private enum Verdict
    {
        Keep,
        Letters,
        Displacement,
        Structure
    }

    /// <summary>Settles the line's proposals in the pass.</summary>
    internal static void Apply(LyricRepairPass pass, int[] line, LyricVocalRegions voice, bool flagsOnly)
    {
        var proposals = line.Select(i => pass.Result[i]).ToArray();
        var verdicts = new Verdict[line.Length];
        var scores = new double[line.Length];
        var bounds = Bounds(pass, line);
        for (var k = 0; k < line.Length; k++) (verdicts[k], scores[k]) = Judge(pass, line[k], proposals[k], NotBefore(line, k, bounds));
        for (var k = line.Length - 1; k >= 0; k--)
        {
            if (verdicts[k] != Verdict.Keep || !IsDisplaced(pass, line, proposals, verdicts, k, voice)) continue;

            (proposals[k], verdicts[k], scores[k]) = OwnLetters(pass, line[k], proposals[k], NotBefore(line, k, bounds)) switch
            {
                null => (proposals[k], Verdict.Displacement, 0),
                { } own when own.Score > 0 => (own.Place, Verdict.Letters, own.Score),
                _ => (proposals[k], Verdict.Keep, 0)
            };
            if (verdicts[k] == Verdict.Keep) pass.Reasons[line[k]] |= LyricReviewReason.Gap;
        }
        while (Withdraw(pass, line, proposals, verdicts, voice, bounds))
            pass.CancellationToken.ThrowIfCancellationRequested();
        var pushed = Strand(pass, line, proposals, verdicts, voice, bounds);
        // Ends: every pass that changes anything turns a verdict back to Keep, and nothing turns one away from it.
        while (Fit(pass, line, proposals, verdicts, scores, pushed, bounds) || Withdraw(pass, line, proposals, verdicts, voice, bounds) ||
               Unstrand(pass, line, proposals, verdicts, voice, bounds, pushed))
        {
            pass.CancellationToken.ThrowIfCancellationRequested();
        }

        for (var k = 0; k < line.Length; k++)
        {
            var (i, old, proposal) = (line[k], pass.Aligned[line[k]], proposals[k]);
            var end = flagsOnly ? old.EndSeconds : End(pass, line, proposals, verdicts, k);
            if (verdicts[k] == Verdict.Keep || flagsOnly)
            {
                if (verdicts[k] == Verdict.Keep && pass.Reasons[i] == LyricReviewReason.None &&
                    Math.Abs(proposal.StartSeconds - old.StartSeconds) > pass.SameOnsetSeconds)
                {
                    pass.Reasons[i] = LyricReviewReason.Gap;
                }

                pass.Result[i] = old with { EndSeconds = end, ReviewReasons = pass.Reasons[i] };
                continue;
            }

            var confidence = verdicts[k] == Verdict.Letters ? Math.Max(proposal.Confidence, scores[k]) : proposal.Confidence;
            pass.Reasons[i] |= verdicts[k] == Verdict.Structure ? LyricReviewReason.Repaired | LyricReviewReason.Gap : LyricReviewReason.Repaired;
            pass.Result[i] = proposal with { EndSeconds = end, Confidence = confidence, ReviewReasons = pass.Reasons[i] };
        }
    }

    /// <summary>A syllable's span as the gate has settled it.</summary>
    private static AlignedSyllable Span(LyricRepairPass pass, int[] line, AlignedSyllable[] proposals, Verdict[] verdicts, int k) =>
        verdicts[k] == Verdict.Keep ? pass.Aligned[line[k]] : proposals[k];

    /// <summary>
    ///     Syllable <paramref name="k" />'s end once every start is settled: next to a moved start it
    ///     ends no later than the next syllable starts. An end is derived, not heard, so cutting it
    ///     is no move; two kept neighbours keep the ends they came with.
    /// </summary>
    private static double End(LyricRepairPass pass, int[] line, AlignedSyllable[] proposals, Verdict[] verdicts, int k)
    {
        var end = Span(pass, line, proposals, verdicts, k).EndSeconds;
        if (k + 1 == line.Length || (verdicts[k] == Verdict.Keep && verdicts[k + 1] == Verdict.Keep)) return end;

        return Math.Min(end, Span(pass, line, proposals, verdicts, k + 1).StartSeconds);
    }

    /// <summary>
    ///     No syllable may end past the next one's start. Where a move leaves an end past it and
    ///     cutting that end would leave less than one onset, the move is withdrawn and flagged (both,
    ///     when both moved), never the kept neighbour changed; G3's opening and its push stand or
    ///     fall together. A start taken on its letters (G1) whose end is cut is judged again on the
    ///     span it keeps: letters past the next start are the next syllable's. True when it withdrew.
    /// </summary>
    private static bool Fit(
        LyricRepairPass pass, int[] line, AlignedSyllable[] proposals, Verdict[] verdicts, double[] scores, bool pushed, (double Lower, double Upper) bounds)
    {
        var withdrawn = false;
        if (pushed && (verdicts[0] == Verdict.Structure) != (verdicts[1] == Verdict.Structure))
        {
            Restore(pass, line, verdicts, 0);
            Restore(pass, line, verdicts, 1);
            withdrawn = true;
        }
        for (var k = 0; k + 1 < line.Length; k++)
        {
            if (verdicts[k] == Verdict.Keep && verdicts[k + 1] == Verdict.Keep) continue;

            var (span, next) = (Span(pass, line, proposals, verdicts, k), Span(pass, line, proposals, verdicts, k + 1));
            if (span.EndSeconds <= next.StartSeconds + 1e-9) continue;

            var room = next.StartSeconds - span.StartSeconds >= pass.SameOnsetSeconds - 1e-6;
            if (room && verdicts[k] != Verdict.Letters) continue;
            if (room && Judge(pass, line[k], span with { EndSeconds = next.StartSeconds }, NotBefore(line, k, bounds)) is (Verdict.Letters, var score))
            {
                scores[k] = score;
                continue;
            }

            Restore(pass, line, verdicts, k);
            if (!room) Restore(pass, line, verdicts, k + 1);
            withdrawn = true;
        }

        return withdrawn;
    }

    private static void Restore(LyricRepairPass pass, int[] line, Verdict[] verdicts, int k)
    {
        if (verdicts[k] == Verdict.Keep) return;

        verdicts[k] = Verdict.Keep;
        pass.Reasons[line[k]] |= LyricReviewReason.Gap;
    }

    /// <summary>
    ///     G0 and G1: a heard start keeps its place; an unheard one moves onto its letters, counting
    ///     none heard before <paramref name="notBefore" />.
    /// </summary>
    private static (Verdict, double) Judge(LyricRepairPass pass, int index, AlignedSyllable proposal, double notBefore)
    {
        var old = pass.Aligned[index];
        if (!Moves(old, proposal) || old.Confidence >= Floor) return (Verdict.Keep, 0);

        var span = proposal.EndSeconds - proposal.StartSeconds;
        var there = LyricLetterEvidence.At(pass, index, proposal.StartSeconds, span, notBefore);
        var here = LyricLetterEvidence.At(pass, index, old.StartSeconds, span, notBefore);
        return there >= Floor && there > here ? (Verdict.Letters, there) : (Verdict.Keep, 0);
    }

    /// <summary>Letters before the previous line's settled end are its own: none of them proves a move of the line's opening.</summary>
    private static double NotBefore(int[] line, int k, (double Lower, double Upper) bounds) =>
        k == 0 && line[0] > 0 ? bounds.Lower : double.NegativeInfinity;

    /// <summary>
    ///     A displaced syllable's own letters: the place its line's alignment gave it, when that
    ///     lies inside the span the displacement gives it and G1 takes it against the old start,
    ///     with its score when they score higher there than at the displacement's start, and a
    ///     score of 0 when they do not (then neither place is proven). Null when there is no such place.
    /// </summary>
    private static (AlignedSyllable Place, double Score)? OwnLetters(LyricRepairPass pass, int index, AlignedSyllable displaced, double notBefore)
    {
        if (!pass.OwnPlaces.TryGetValue(index, out var own) || own.StartSeconds < displaced.StartSeconds - 1e-9 ||
            own.StartSeconds >= displaced.EndSeconds - 1e-9 || Judge(pass, index, own, notBefore) is not (Verdict.Letters, var score))
        {
            return null;
        }

        var atDisplaced = LyricLetterEvidence.At(pass, index, displaced.StartSeconds, own.EndSeconds - own.StartSeconds, notBefore);
        return (own, atDisplaced < score ? score : 0);
    }

    /// <summary>
    ///     G2: an unheard start moves back to where its unbroken voice began, because the next
    ///     syllable, heard, now starts where this one stood. Void when this one's own letters are
    ///     heard where it stands (scored as in G1), and when both open on the same letter: then
    ///     the next one's letters can be this one's own.
    /// </summary>
    private static bool IsDisplaced(
        LyricRepairPass pass, int[] line, AlignedSyllable[] proposals, Verdict[] verdicts, int k, LyricVocalRegions voice)
    {
        var old = pass.Aligned[line[k]];
        if (k + 1 == line.Length || !Moves(old, proposals[k]) || old.Confidence >= Floor || !voice.IsVoiced(old.StartSeconds)) return false;
        if (voice.OpeningVoiceStart(old.StartSeconds) is not { } voiceStart || Math.Abs(proposals[k].StartSeconds - voiceStart) > 1e-9)
            return false;

        var next = verdicts[k + 1] == Verdict.Keep ? pass.Aligned[line[k + 1]] : proposals[k + 1];
        var tokens = pass.Tokenization;
        return LyricLetterEvidence.At(pass, line[k], old.StartSeconds, proposals[k].EndSeconds - proposals[k].StartSeconds) < Floor &&
               Math.Abs(next.StartSeconds - old.StartSeconds) <= pass.SameOnsetSeconds &&
               LyricLetterEvidence.At(pass, line[k + 1], old.StartSeconds, next.EndSeconds - next.StartSeconds) >= Floor &&
               tokens.Tokens[tokens.Syllables[line[k]].TokenStart] != tokens.Tokens[tokens.Syllables[line[k + 1]].TokenStart];
    }

    /// <summary>
    ///     G3, once every other verdict is settled: the line's opening, and the next syllable when
    ///     it makes room; with no room both are flagged. True when it pushed the next one.
    /// </summary>
    private static bool Strand(
        LyricRepairPass pass, int[] line, AlignedSyllable[] proposals, Verdict[] verdicts, LyricVocalRegions voice,
        (double Lower, double Upper) bounds)
    {
        if (verdicts[0] != Verdict.Keep || line.Length < 2) return false;

        var final = (int k) => verdicts[k] == Verdict.Keep ? pass.Aligned[line[k]].StartSeconds : proposals[k].StartSeconds;
        if (LyricStrandedOpening.Place(pass, line, 0, final, verdicts[1] == Verdict.Keep, voice, bounds) is not { } stranded) return false;
        if (stranded.Moved is not { } moved)
        {
            pass.Reasons[line[0]] |= LyricReviewReason.Gap;
            pass.Reasons[line[1]] |= LyricReviewReason.Gap;
            return false;
        }

        (proposals[0], verdicts[0]) = (moved, Verdict.Structure);
        if (stranded.Pushed is not { } pushed) return false;

        (proposals[1], verdicts[1]) = (pushed, Verdict.Structure);
        return true;
    }

    /// <summary>
    ///     G3 again on the settled starts: a later withdrawal can take back the start the opening
    ///     was placed against, and an opening that G3 now places elsewhere, or not at all, goes
    ///     back with its push and is flagged. True when it did.
    /// </summary>
    private static bool Unstrand(
        LyricRepairPass pass, int[] line, AlignedSyllable[] proposals, Verdict[] verdicts, LyricVocalRegions voice,
        (double Lower, double Upper) bounds, bool pushed)
    {
        if (verdicts[0] != Verdict.Structure) return false;

        // A push is G3's own move: the pushed syllable counts at its old start, as when the opening was placed.
        var final = (int k) => (k == 1 && pushed) || verdicts[k] == Verdict.Keep ? pass.Aligned[line[k]].StartSeconds : proposals[k].StartSeconds;
        var again = LyricStrandedOpening.Place(pass, line, 0, final, pushed || verdicts[1] == Verdict.Keep, voice, bounds);
        if (again is { } placed && placed.Moved == proposals[0] && placed.Pushed == (pushed ? proposals[1] : null)) return false;

        Restore(pass, line, verdicts, 0);
        if (pushed) Restore(pass, line, verdicts, 1);
        return true;
    }

    /// <summary>
    ///     Takes back every move that leaves the line's bounds, has no length, breaks the order
    ///     next to a kept or taken neighbour, or lost its displacement proof. True when it did.
    /// </summary>
    private static bool Withdraw(
        LyricRepairPass pass, int[] line, AlignedSyllable[] proposals, Verdict[] verdicts, LyricVocalRegions voice,
        (double Lower, double Upper) bounds)
    {
        var withdrawn = false;
        for (var k = 0; k < line.Length; k++)
        {
            var p = proposals[k];
            if (verdicts[k] == Verdict.Keep ||
                (p.StartSeconds >= bounds.Lower - 1e-9 && p.EndSeconds <= bounds.Upper + 1e-9 && p.EndSeconds > p.StartSeconds + 1e-9))
            {
                continue;
            }

            verdicts[k] = Verdict.Keep;
            withdrawn = true;
        }
        for (var k = 0; k + 1 < line.Length; k++)
        {
            if (verdicts[k] == Verdict.Keep && verdicts[k + 1] == Verdict.Keep) continue;

            var start = verdicts[k] == Verdict.Keep ? pass.Aligned[line[k]].StartSeconds : proposals[k].StartSeconds;
            var next = verdicts[k + 1] == Verdict.Keep ? pass.Aligned[line[k + 1]].StartSeconds : proposals[k + 1].StartSeconds;
            if (next > start + 1e-9) continue;

            verdicts[k] = verdicts[k + 1] = Verdict.Keep;
            withdrawn = true;
        }
        for (var k = 0; k < line.Length; k++)
        {
            if (verdicts[k] != Verdict.Displacement || IsDisplaced(pass, line, proposals, verdicts, k, voice)) continue;

            verdicts[k] = Verdict.Keep;
            withdrawn = true;
        }

        return withdrawn;
    }

    /// <summary>A moved syllable stays after every end of the previous line and ends before the next line starts.</summary>
    private static (double Lower, double Upper) Bounds(LyricRepairPass pass, int[] line)
    {
        var lower = 0.0;
        for (var j = line[0] - 1; j >= 0 && pass.Sheet.Syllables[j].LineIndex == pass.Sheet.Syllables[line[0] - 1].LineIndex; j--)
            lower = Math.Max(lower, pass.Result[j].EndSeconds);
        var upper = line[^1] + 1 < pass.Aligned.Count ? pass.Aligned[line[^1] + 1].StartSeconds : pass.Envelope.DurationSeconds;
        return (lower, upper);
    }

    private static bool Moves(AlignedSyllable old, AlignedSyllable proposal) => Math.Abs(proposal.StartSeconds - old.StartSeconds) > 1e-9;
}
