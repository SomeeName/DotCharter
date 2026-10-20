using System.Globalization;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Services.Ai.Match.Similarity;

/// <summary>
///     One feature block's share of a candidate's distance, beside the same block's
///     share for the information-free reference rig — so a headline number can never
///     be shown without saying which part of the sound it came from.
/// </summary>
/// <param name="Name">The block, as <see cref="ToneFeatureLayout.Blocks" /> names it.</param>
/// <param name="Weight">Its weight in the metric.</param>
/// <param name="Contribution">Its weighted contribution to <c>distance²</c>.</param>
/// <param name="ReferenceContribution">The same for the reference rig, or NaN when
/// there was no reference render. This is what makes a block's number readable: a
/// block is only evidence when the candidate beat the reference ON IT.</param>
/// <param name="Share">This block's fraction of <c>distance²</c>, in <c>[0,1]</c>.</param>
/// <param name="TargetGated">The TARGET's values for this block are all exactly
/// zero — the shape the extractor produces when it had nothing to measure. A block
/// in that state contributes a distance, and the distance means nothing.</param>
public sealed record MatchScoreBlock(
    string Name,
    double Weight,
    double Contribution,
    double ReferenceContribution,
    double Share,
    bool TargetGated);

/// <summary>Why a match could not be given a percentage. A distinct STATE, never
/// 0 % and never a percentage.</summary>
public static class MatchUnrated
{
    /// <summary>No information-free reference rig was rendered (the budget was gone
    /// before the anchor could be taken), so there is no 0 % end of the scale.</summary>
    public const string NoReference = "no-reference";

    /// <summary>The reference rig is already within <see cref="MatchScale.MinAnchorSpread" />
    /// of the target, so the scale has no room left: every candidate would land in the
    /// same place and a percentage would be rounding.</summary>
    public const string ReferenceTooClose = "reference-too-close";

    /// <summary>The target carried less playing than a tone can be measured over
    /// (see <see cref="MatchTargetAudio.MinContent" />).</summary>
    public const string TargetTooShort = "target-too-short";

    /// <summary>A weighted feature block of the TARGET was all-gated — every value
    /// exactly zero, which is what the extractor answers when it had nothing to
    /// measure.</summary>
    public const string BlockGated = "block-gated";

    /// <summary>The distance itself is not a number.</summary>
    public const string NoDistance = "no-distance";
}

/// <summary>
///     A candidate's score: a percentage, or a named reason there is none.
///     <para>
///         <b><see cref="Percent" /> is null for UNRATED, and that is a third state
///         rather than a zero.</b> 0 % means "measured, and no better than an
///         information-free rig"; null means "this run could not put a number on it",
///         and the two must never be shown the same way.
///     </para>
/// </summary>
/// <param name="Percent">0-100, or null when <see cref="UnratedReason" /> is set.</param>
/// <param name="UnratedReason">One of <see cref="MatchUnrated" />, or null.</param>
/// <param name="Distance">The measured feature distance this score is derived from.</param>
/// <param name="SelfDistance">
///     The target against a disjoint window of ITSELF, NaN when the span was too
///     short. <b>Reported, and deliberately NOT part of the scale</b> - see
///     <see cref="MatchScale" />: it is the target's own CONTENT variation, it is
///     routinely larger than a candidate's distance, and using it as the 100 % end is
///     exactly what made the old formula report 100 % for everything.
/// </param>
/// <param name="ReferenceDistance">The 0 % anchor: the information-free reference rig
/// against the same target. NaN when none was rendered.</param>
/// <param name="Blocks">The per-block breakdown behind the number.</param>
public sealed record MatchScore(
    int? Percent,
    string? UnratedReason,
    double Distance,
    double SelfDistance,
    double ReferenceDistance,
    IReadOnlyList<MatchScoreBlock> Blocks)
{
    /// <summary>True when a number was justified.</summary>
    public bool IsRated => Percent is not null;

    /// <summary>
    ///     Where the target's own self-distance lands ON THIS SCALE, or null when
    ///     there is no self-distance or no scale. It is the CAVEAT, not the score: a
    ///     candidate at or above it is as close as this target's own two windows are
    ///     to each other, i.e. inside the noise of the comparison, and a UI should say
    ///     so rather than imply the remaining percent is reachable.
    /// </summary>
    public int? SelfDistancePercent => double.IsNaN(SelfDistance) || double.IsNaN(ReferenceDistance) ||
                                       ReferenceDistance <= MatchScale.MinAnchorSpread
        ? null
        : (int)Math.Round(Math.Clamp(1.0 - SelfDistance / ReferenceDistance, 0.0, 1.0) * 100.0);

    /// <summary>True when this candidate is at or past the target's own variation -
    /// "this metric cannot tell you it is better than this".</summary>
    public bool WithinTargetNoise => IsRated && SelfDistancePercent is { } floor && Percent >= floor;

    /// <summary>The blocks that carry most of the distance, worst first - the "why"
    /// behind the headline, capped so a rationale line stays a line.</summary>
    /// <param name="count">How many blocks to name.</param>
    public IReadOnlyList<MatchScoreBlock> WorstBlocks(int count = 3)
        => Blocks.OrderByDescending(b => b.Share).Take(Math.Max(0, count)).ToList();

    /// <summary>The blocks where this candidate is WORSE than the information-free
    /// reference rig - the honest "what did the search not get" list. Empty when there
    /// was no reference.</summary>
    public IReadOnlyList<MatchScoreBlock> BlocksWorseThanReference()
        => Blocks.Where(b => !double.IsNaN(b.ReferenceContribution) &&
                             b.Contribution > b.ReferenceContribution)
            .OrderByDescending(b => b.Contribution - b.ReferenceContribution)
            .ToList();
}

/// <summary>
///     <b>The scale a match percentage is read against, and the whole of the honesty
///     argument.</b>
///     <para>
///         <c>percent = 100 * clamp(1 - distance / referenceDistance, 0, 1)</c>.
///     </para>
///     <para>
///         <b>0 % is MEASURED, on this target, on this run:</b> it is
///         <see cref="ReferenceRig" /> - an information-free
///         <c>overdrive -&gt; amp -&gt; cab</c> at catalogue defaults - rendered over
///         the caller's own DI and scored against the same target. "No better than
///         plugging into an amp." <b>100 % is distance ZERO</b>, which in this metric
///         is the definition of a perfect match: the candidate's feature vector IS the
///         target's. Nothing between them is invented.
///     </para>
///     <para>
///         <b>What it replaced, and why the old one had to go.</b>
///         <c>100 * floor / max(distance, floor)</c>, where <c>floor</c> was the
///         target's SELF-DISTANCE (its first 5 s against its last 5 s). On real
///         material that floor sits ABOVE every candidate's distance, so the
///         expression saturates and everything reads 100 %. Measured on the
///         owner's Polkadot Stingray track, 90-120 s of the separated guitar: self
///         0.4389, and the five candidates at 0.3793 / 0.4222 / 0.4729 / 0.4850 /
///         0.4891 - two of them BELOW the "floor". On this scale (reference 0.6966)
///         the same five are <b>46 / 39 / 32 / 30 / 30 %</b>.
///     </para>
///     <para>
///         <b>The self-distance is not even ORDERED correctly, so it could not be
///         rescued.</b> Measured the same day on a target that is a render of a known
///         rig through this pipeline: self 0.2934 against a reference distance of
///         0.1309 - the information-free rig is more than twice as close to the target
///         as the target's own two windows are to each other, because those windows
///         differ in CONTENT (the reference DI medley is four different performances)
///         and content is what the metric is built not to hear. A scale with that as
///         its good end runs backwards. It is still REPORTED -
///         <see cref="MatchScore.SelfDistancePercent" /> - as the caveat it is.
///     </para>
///     <para>
///         <b>Calibration, both ends, one measurement:</b> a known rig against its own
///         render scores <b>100 %</b> (distance 0.0000) and a deliberately wrong rig
///         (<c>acpre</c> alone) scores <b>0 %</b> (distance 0.6446, worse than the
///         reference's 0.1309). The reference rig scored against itself is exactly 0 %
///         by construction, which is the cheapest non-vacuity check there is.
///     </para>
///     <para>
///         <b>No headline number without the breakdown.</b> Every score carries
///         <see cref="MatchScore.Blocks" />: each feature block's weighted
///         contribution, its share, and the SAME block's contribution for the
///         reference rig - so "46 %" is always readable as "the dynamics block is 41 %
///         of what is left, and it is still worse than the generic rig's".
///     </para>
///     <para>
///         <b>When it cannot justify a number it says UNRATED</b>
///         (<see cref="MatchUnrated" />): no reference render, a reference already
///         inside <see cref="MinAnchorSpread" /> of the target, a target under
///         <see cref="MatchTargetAudio.MinContent" />, an all-gated target block, or a
///         distance that is not a number. Unrated is a STATE, not 0 and not a
///         percentage.
///     </para>
///     <para>
///         <b>The score of a candidate is a pure function of three vectors</b> - the
///         target's, the candidate's and the reference rig's - so it does not depend
///         on the render budget, the wall clock, how many candidates were tried or
///         which seed CMA-ES used. A tighter budget changes WHICH candidates exist,
///         never what a given one scores. <c>MatchScaleTests</c> pins that.
///     </para>
/// </summary>
public static class MatchScale
{
    /// <summary>
    ///     How far the reference rig must be from the target before a percentage means
    ///     anything, in raw distance units.
    ///     <para>
    ///         <b>Small on purpose.</b> It is a DEGENERACY guard, not a quality bar:
    ///         its job is to catch "the scale is a point" - a target the
    ///         information-free rig already reproduces, where every candidate would
    ///         round to the same number - and not to decide that a narrow scale is a
    ///         bad one. A reference distance of 0.13 (measured on a render of a known
    ///         rig) is a perfectly usable scale; one of 0.001 is reporting rounding.
    ///     </para>
    /// </summary>
    public const double MinAnchorSpread = 0.02;

    /// <summary>
    ///     The information-free rig: <c>overdrive → amp → cab</c> at catalogue
    ///     defaults. It knows nothing about the song, and it is the 0 % end of the
    ///     scale.
    ///     <para>
    ///         <b>Three modules and not zero.</b> An empty rig (the dry DI) is a worse
    ///         anchor than it looks: a dry electric guitar is so far from any produced
    ///         record that almost anything beats it, which would make every score high
    ///         for the same reason the old formula made every score 100. A plain
    ///         amp-and-cab is what a player gets by plugging in, so beating THAT is the
    ///         claim the feature actually makes.
    ///     </para>
    ///     <para>
    ///         It is the same shape the audio eval already calls <c>floor-generic</c>,
    ///         and that test now reads it from here — one definition, so the eval's
    ///         floor and the app's 0 % cannot drift apart.
    ///     </para>
    /// </summary>
    public static DcRigPreset ReferenceRig() => new([
        new DcRigModule("overdrive", new Dictionary<string, float>(StringComparer.Ordinal)),
        new DcRigModule("amp", new Dictionary<string, float>(StringComparer.Ordinal)),
        new DcRigModule("cab", new Dictionary<string, float>(StringComparer.Ordinal))
    ]);

    /// <summary>
    ///     Scores one candidate against the two anchors and produces the per-block
    ///     breakdown behind the number.
    /// </summary>
    /// <param name="target">The target's features.</param>
    /// <param name="candidate">The candidate render's features.</param>
    /// <param name="reference">The reference rig render's features, or null when none
    /// was rendered (the score is then <see cref="MatchUnrated.NoReference" />).</param>
    /// <param name="similarity">The metric. Its extractor must match both vectors'.
    /// Only the DISTANCE comes from it; the breakdown is always taken from
    /// <see cref="ToneFeatureLayout" />, which is the one place the blocks live.</param>
    /// <param name="selfDistance">The target's self-distance, or NaN. REPORTED, not
    /// used in the scale - see the type doc.</param>
    /// <param name="targetTooShort">The caller already knows the target carried less
    /// than <see cref="MatchTargetAudio.MinContent" /> of playing.</param>
    public static MatchScore Score(
        ToneFeatures target,
        ToneFeatures candidate,
        ToneFeatures? reference,
        IToneSimilarity similarity,
        double selfDistance,
        bool targetTooShort = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(similarity);

        var distance = similarity.Distance(target, candidate);
        var referenceDistance = reference is null ? double.NaN : similarity.Distance(target, reference);
        var blocks = Breakdown(target, candidate, reference);

        var reason = UnratedReason(distance, referenceDistance, blocks, targetTooShort);
        if (reason is not null)
            return new MatchScore(null, reason, distance, selfDistance, referenceDistance, blocks);

        // 100 % is distance ZERO - the candidate's vector IS the target's - and 0 % is
        // the information-free rig. Both ends are defined without guessing what a
        // perfect match "should" score.
        var raw = 1.0 - distance / referenceDistance;
        return new MatchScore(
            (int)Math.Round(Math.Clamp(raw, 0.0, 1.0) * 100.0),
            null, distance, selfDistance, referenceDistance, blocks);
    }

    /// <summary>
    ///     Each block's weighted contribution to the SQUARED distance, in
    ///     <see cref="ToneFeatureLayout.Blocks" /> order — the one implementation, which
    ///     <see cref="ToneSimilarity.Explain" /> also calls. They sum to
    ///     <c>Distance²</c> for the real metric.
    /// </summary>
    /// <param name="a">One vector.</param>
    /// <param name="b">The other. Same length, same extractor.</param>
    public static IReadOnlyList<(string Block, double Contribution)> BlockContributions(
        ToneFeatures a, ToneFeatures b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var parts = new List<(string, double)>(ToneFeatureLayout.Blocks.Length);
        foreach (var block in ToneFeatureLayout.Blocks)
        {
            if (block.Offset + block.Count > a.Vector.Length ||
                block.Offset + block.Count > b.Vector.Length)
            {
                parts.Add((block.Name, 0.0));
                continue;
            }

            double sum = 0;
            for (var i = block.Offset; i < block.Offset + block.Count; i++)
            {
                var d = a.Vector[i] - (double)b.Vector[i];
                sum += d * d;
            }

            parts.Add((block.Name, block.Weight * (sum / block.Count)));
        }

        return parts;
    }

    /// <summary>
    ///     The per-block breakdown. <c>Share</c> is normalised by the SUM of the
    ///     contributions rather than by <c>distance²</c> — identical for the real
    ///     metric, and self-consistent for a fake one, so a share is always "how much
    ///     of this comparison is this block" and never a number that fails to add up.
    /// </summary>
    private static IReadOnlyList<MatchScoreBlock> Breakdown(
        ToneFeatures target, ToneFeatures candidate, ToneFeatures? reference)
    {
        var mine = BlockContributions(target, candidate);
        var theirs = reference is null ? null : BlockContributions(target, reference);
        var total = mine.Sum(p => p.Contribution);

        var blocks = new List<MatchScoreBlock>(mine.Count);
        for (var i = 0; i < mine.Count; i++)
        {
            var layout = ToneFeatureLayout.Blocks[i];
            blocks.Add(new MatchScoreBlock(
                mine[i].Block,
                layout.Weight,
                mine[i].Contribution,
                theirs is null ? double.NaN : theirs[i].Contribution,
                total > 0 ? mine[i].Contribution / total : 0.0,
                IsGated(target, layout)));
        }

        return blocks;
    }

    /// <summary>A block whose TARGET values are every one exactly zero. Real audio
    /// does not produce that; the extractor does, when it had nothing to measure.</summary>
    private static bool IsGated(ToneFeatures target, ToneFeatureLayout.Block block)
    {
        if (block.Offset + block.Count > target.Vector.Length) return false;
        for (var i = block.Offset; i < block.Offset + block.Count; i++)
        {
            if (target.Vector[i] != 0f)
                return false;
        }

        return true;
    }

    private static string? UnratedReason(
        double distance, double referenceDistance,
        IReadOnlyList<MatchScoreBlock> blocks, bool targetTooShort)
    {
        if (targetTooShort) return MatchUnrated.TargetTooShort;
        if (double.IsNaN(distance)) return MatchUnrated.NoDistance;
        if (double.IsNaN(referenceDistance)) return MatchUnrated.NoReference;
        // A weighted block with nothing in it on the TARGET side: it contributes a
        // distance, and the distance is about the absence rather than about the tone.
        foreach (var block in blocks)
        {
            if (block.TargetGated && block.Weight > 0.0)
                return MatchUnrated.BlockGated;
        }

        if (referenceDistance <= MinAnchorSpread) return MatchUnrated.ReferenceTooClose;
        return null;
    }

    /// <summary>One line naming the anchors, for a result's notes and for a report
    /// header. Invariant culture — it is a trail, not a label.</summary>
    /// <param name="score">Any candidate's score (the anchors are the run's, not the
    /// candidate's).</param>
    public static string DescribeScale(MatchScore score)
    {
        ArgumentNullException.ThrowIfNull(score);
        var line = string.Create(CultureInfo.InvariantCulture,
            $"scale: 0 % = the information-free reference rig at {score.ReferenceDistance:0.0000}, " +
            $"100 % = distance 0; the target's own self-distance is {score.SelfDistance:0.0000}") +
            (score.SelfDistancePercent is { } p ? $" (= {p} %)" : " (none)");
        return score.IsRated ? line : line + $" (unrated: {score.UnratedReason})";
    }
}
