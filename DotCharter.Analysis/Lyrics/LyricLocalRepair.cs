using DotCharter.Core.Domain.Vocals;

namespace DotCharter.Analysis.Lyrics;

/// <summary>
///     Rechecks a doubtful line against the voice, one voiced region at a time. An onset that
///     already sits on voiced frames stays in its region: only an onset on unvoiced frames is
///     re-homed, into the nearest one, so a rest inside a line is never closed. The rules here
///     only PROPOSE starts; <see cref="LyricRepairGate" /> takes a proposed start only with
///     evidence for it, and otherwise keeps the old one and flags it.
/// </summary>
internal static class LyricLocalRepair
{
    /// <summary>A first onset later than this after its voice began flags the opening and has it checked.</summary>
    private const double MaxOpeningLeadSeconds = 0.150;

    internal static IReadOnlyList<AlignedSyllable> Repair(
        LyricSheet sheet, LyricVocabulary vocabulary, CtcEmissions emissions,
        IReadOnlyList<AlignedSyllable> aligned, VocalEnvelope envelope,
        LyricAlignmentOptions options, CancellationToken cancellationToken)
    {
        var tokenization = RomanizedLyricTokenizer.Tokenize(sheet, vocabulary, options.InsertWordDelimiters);
        var moved = Pass(sheet, vocabulary, emissions, tokenization, aligned, envelope, options, false, cancellationToken);
        // The flags read the neighbours' starts (the voice between the lines, the gaps), so they
        // are taken once more, on the final starts: that pass flags and never moves. A repaired
        // syllable keeps the flags its repair was decided on; a kept one only what it came with.
        var final = moved.Select((s, i) => s with
        {
            ReviewReasons = s.StartSeconds != aligned[i].StartSeconds ? s.ReviewReasons : aligned[i].ReviewReasons
        }).ToArray();
        return Pass(sheet, vocabulary, emissions, tokenization, final, envelope, options, true, cancellationToken);
    }

    /// <summary>Runs every line through the rules and the gate; with <paramref name="flagsOnly" /> no start moves.</summary>
    private static AlignedSyllable[] Pass(
        LyricSheet sheet, LyricVocabulary vocabulary, CtcEmissions emissions, LyricTokenization tokenization,
        IReadOnlyList<AlignedSyllable> aligned, VocalEnvelope envelope, LyricAlignmentOptions options, bool flagsOnly,
        CancellationToken cancellationToken)
    {
        var reasons = LyricAlignmentReview.Evaluate(sheet, aligned, envelope);
        var result = aligned.Select((s, i) => s with { ReviewReasons = reasons[i] }).ToArray();
        var pass = new LyricRepairPass(sheet, vocabulary, emissions, tokenization, aligned, result, reasons, envelope, options, cancellationToken);
        foreach (var line in Enumerable.Range(0, sheet.Syllables.Count).GroupBy(i => sheet.Syllables[i].LineIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var indices = line.ToArray();
            if (indices.Any(i => tokenization.Syllables[i].TokenCount == 0)) continue;

            if (RepairLine(pass, indices) is { } voice) LyricRepairGate.Apply(pass, indices, voice, flagsOnly);
        }

        return result.Select((s, i) => s with { ReviewReasons = reasons[i] }).ToArray();
    }

    /// <summary>Writes the line's proposals into the pass; returns the line's voice when there is one.</summary>
    private static LyricVocalRegions? RepairLine(LyricRepairPass pass, int[] indices)
    {
        var (aligned, result, reasons) = (pass.Aligned, pass.Result, pass.Reasons);
        var before = indices[0] > 0 ? result[indices[0] - 1].EndSeconds : 0;
        var after = indices[^1] + 1 < aligned.Count ? aligned[indices[^1] + 1].StartSeconds : pass.Envelope.DurationSeconds;
        var voice = LyricVocalRegions.Find(pass.Envelope, before, after, pass.CancellationToken);
        if (voice == null) return null;

        var homes = Homes(aligned, indices, voice);
        if (homes.Count(h => h >= 0) * 2 < indices.Length) return voice;

        var opening = voice.OpeningVoiceStart(aligned[indices[0]].StartSeconds);
        FlagVoiceMismatch(aligned, result, reasons, indices, homes, opening);
        if (!indices.Any(i => reasons[i] != LyricReviewReason.None)) return voice;

        Adopt(aligned, indices, homes, voice);
        var resumes = ClaimEmptyRegions(pass, indices, homes, voice.Regions, after);
        for (var from = 0; from < indices.Length;)
        {
            var to = from;
            while (to + 1 < indices.Length && homes[to + 1] == homes[from]) to++;
            var region = voice.Regions[homes[from]];
            if (resumes[from]) StartWithTheVoice(pass, indices[from], region.Start);
            else RepairGroup(pass, indices[from..(to + 1)], region, from == 0 ? opening : null);
            from = to + 1;
        }

        return voice;
    }

    /// <summary>
    ///     A syllable whose measured span runs across a rest into a voiced region where no
    ///     syllable of its line starts may have been sung there: it is proposed where that voice
    ///     resumes, and the gate takes it only where its letters are. Voice that runs on into the
    ///     next line's first onset is that line's and is never proposed.
    /// </summary>
    private static bool[] ClaimEmptyRegions(
        LyricRepairPass pass, int[] indices, int[] homes, IReadOnlyList<(double Start, double End)> regions, double after)
    {
        var aligned = pass.Aligned;
        var claims = new bool[indices.Length];
        for (var k = 0; k < indices.Length; k++)
        {
            var s = aligned[indices[k]];
            var reach = homes[k];
            while (reach + 1 < regions.Count && regions[reach + 1].Start < s.EndSeconds - 1e-9) reach++;
            if (reach == homes[k] || Array.IndexOf(homes, reach) >= 0) continue;
            if (after - regions[reach].End < LyricVocalRegions.MinRestSeconds) continue;
            if (k + 1 < indices.Length && aligned[indices[k + 1]].StartSeconds <= regions[reach].Start) continue;

            homes[k] = reach;
            claims[k] = true;
        }

        return claims;
    }

    private static void StartWithTheVoice(LyricRepairPass pass, int index, double voiceStart)
    {
        var old = pass.Result[index];
        pass.Reasons[index] |= LyricReviewReason.Gap;
        pass.Result[index] = old with
        {
            StartSeconds = voiceStart,
            EndSeconds = Math.Max(old.EndSeconds, voiceStart),
            ReviewReasons = pass.Reasons[index]
        };
    }

    /// <summary>The region each onset sits on, -1 for unvoiced frames.</summary>
    private static int[] Homes(IReadOnlyList<AlignedSyllable> aligned, int[] indices, LyricVocalRegions voice)
    {
        var homes = indices.Select(i => voice.RegionOf(aligned[i].StartSeconds)).ToArray();
        // An unheard opening alone on the previous line's held note is not on its own voice.
        var first = aligned[indices[0]];
        if (homes[0] >= 0 && first.Confidence < LyricAlignmentReview.LowConfidenceThreshold &&
            voice.OpeningVoiceStart(first.StartSeconds) == null && homes.Skip(1).FirstOrDefault(h => h >= 0, -1) > homes[0])
        {
            homes[0] = -1;
        }

        return homes;
    }

    private static void FlagVoiceMismatch(
        IReadOnlyList<AlignedSyllable> aligned, AlignedSyllable[] result, LyricReviewReason[] reasons,
        int[] indices, int[] homes, double? opening)
    {
        if (opening is { } voiceStart && aligned[indices[0]].StartSeconds - voiceStart > MaxOpeningLeadSeconds)
        {
            reasons[indices[0]] |= LyricReviewReason.Gap;
            if (indices.Length > 1) reasons[indices[1]] |= LyricReviewReason.Gap;
        }
        for (var k = 0; k < indices.Length; k++)
        {
            if (homes[k] >= 0) continue;

            reasons[indices[k]] |= LyricReviewReason.Gap;
            if (k > 0) reasons[indices[k - 1]] |= LyricReviewReason.Gap;
        }
        foreach (var i in indices) result[i] = result[i] with { ReviewReasons = reasons[i] };
    }

    /// <summary>
    ///     An onset on unvoiced frames joins the region nearest in time, also one where no other
    ///     onset sits, so it is never carried across a rest to its neighbours. The stranded
    ///     opening sits on the previous line's note and joins the first region of its own line.
    /// </summary>
    private static void Adopt(IReadOnlyList<AlignedSyllable> aligned, int[] indices, int[] homes, LyricVocalRegions voice)
    {
        if (homes[0] < 0 && voice.RegionOf(aligned[indices[0]].StartSeconds) >= 0) homes[0] = homes.First(h => h >= 0);
        for (var k = 0; k < homes.Length; k++)
        {
            if (homes[k] >= 0) continue;

            var nearest = voice.Nearest(aligned[indices[k]].StartSeconds);
            homes[k] = k > 0 ? Math.Max(nearest, homes[k - 1]) : nearest;
        }
    }

    private static void RepairGroup(LyricRepairPass pass, int[] group, (double Start, double End) region, double? opening)
    {
        if (!group.Any(i => pass.Reasons[i] != LyricReviewReason.None)) return;

        var candidate = Realign(pass, group, region);
        if (IsLateOpening(pass.Aligned[group[0]], candidate, opening) &&
            RealignBehindOpening(pass, group, region, opening.GetValueOrDefault()) is { } behind)
        {
            if (candidate != null) pass.OwnPlaces[group[0]] = candidate[0];
            candidate = behind;
        }
        if (candidate == null) return;

        for (var k = 0; k < group.Length; k++) pass.Result[group[k]] = candidate[k] with { ReviewReasons = pass.Reasons[group[k]] };
    }

    /// <summary>A late first onset that stays late even when its group is aligned inside the voice.</summary>
    private static bool IsLateOpening(AlignedSyllable first, AlignedSyllable[]? realigned, double? opening) =>
        opening is { } voiceStart && first.StartSeconds - voiceStart > MaxOpeningLeadSeconds &&
        (realigned == null || realigned[0].StartSeconds - voiceStart > MaxOpeningLeadSeconds);

    /// <summary>
    ///     The model may have heard nothing while the line's voice began, so that the forced path
    ///     put the first syllable on the second one's letters. Proposed: the rest of the group
    ///     aligned without the first, and the first starting with the voice. Whether that holds
    ///     is the gate's displacement proof; a line of one syllable has nothing to prove it with.
    /// </summary>
    private static AlignedSyllable[]? RealignBehindOpening(LyricRepairPass pass, int[] group, (double Start, double End) region, double voiceStart)
    {
        if (group.Length == 1) return null;

        var rest = Realign(pass, group[1..], (voiceStart, region.End));
        return rest == null ? null : [pass.Result[group[0]] with { StartSeconds = voiceStart, EndSeconds = rest[0].StartSeconds }, .. rest];
    }

    private static AlignedSyllable[]? Realign(LyricRepairPass pass, int[] indices, (double Start, double End) region)
    {
        var (emissions, tokenization) = (pass.Emissions, pass.Tokenization);
        var spf = emissions.SecondsPerFrame;
        var firstFrame = Math.Clamp((int)Math.Ceiling(region.Start / spf - 1e-9), 0, emissions.FrameCount);
        var lastFrame = Math.Clamp((int)Math.Ceiling(region.End / spf - 1e-9), firstFrame, emissions.FrameCount);
        var first = tokenization.Syllables[indices[0]];
        var last = tokenization.Syllables[indices[^1]];
        var tokens = tokenization.Tokens.Skip(first.TokenStart).Take(last.TokenStart + last.TokenCount - first.TokenStart).ToArray();
        var path = CtcForcedAligner.Align(emissions.LogProbs.AsSpan(firstFrame * emissions.Width),
            lastFrame - firstFrame, emissions.Width, tokens, pass.Vocabulary.BlankId, cancellationToken: pass.CancellationToken);
        if (!path.Succeeded) return null;

        var result = new AlignedSyllable[indices.Length];
        var previous = 0.0;
        for (var k = 0; k < indices.Length; k++)
        {
            var i = indices[k];
            var map = tokenization.Syllables[i];
            var spans = path.Tokens.Skip(map.TokenStart - first.TokenStart).Take(map.TokenCount).ToArray();
            var start = Math.Max(previous, Math.Max(0, (firstFrame + spans[0].StartFrame) * spf + pass.OnsetBias(i)));
            var end = Math.Max(start, (firstFrame + spans[^1].EndFrame + 1) * spf + pass.Options.OnsetBiasSeconds);
            result[k] = new AlignedSyllable(i, start, end, Math.Exp(spans.Average(s => s.Score)), false);
            previous = start;
        }

        return result;
    }
}
