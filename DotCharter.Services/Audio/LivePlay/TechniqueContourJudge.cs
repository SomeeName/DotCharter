namespace DotCharter.Services.Audio.LivePlay;

/// <summary>What the pitch contour says about a charted bend, slide or vibrato.</summary>
public enum TechniqueContourVerdict
{
    /// <summary>The contour cannot tell: too short, too little pitch, or not heard in time.
    /// Judged exactly like <see cref="Met" /> — an analyzer that cannot see a technique must
    /// never punish the player who played it.</summary>
    Unknown,

    /// <summary>The contour shows the technique.</summary>
    Met,

    /// <summary>The contour clearly shows the note WITHOUT the technique. The only verdict
    /// that downgrades the hit (half score), and it never turns a hit into a miss.</summary>
    NotMet
}

/// <summary>The techniques a pitch contour can show. A sound can ask for several.</summary>
[Flags]
public enum ContourTechniques
{
    None = 0,
    Bend = 1 << 0,
    Slide = 1 << 1,
    Vibrato = 1 << 2,

    /// <summary>Every technique the contour can show.</summary>
    All = Bend | Slide | Vibrato
}

/// <summary>
///     What a charted single note asks its pitch contour to show, built once per sound by
///     <see cref="LiveExpectedTimeline" />. Times are chart milliseconds after the note's own
///     time. The default (no technique) is every chord, harmonic and plain note.
/// </summary>
/// <param name="Techniques">Which techniques the chart asks for.</param>
/// <param name="BendSemis">The bend's largest step in semitones (1 = half step, 2 = full).</param>
/// <param name="BendPeakAfterMs">When the chart reaches that step, after the note's time
/// (0 for a pre-bend).</param>
/// <param name="SlideSemis">Signed semitones from the note to its slide target.</param>
/// <param name="WindowAfterMs">How long the note's own pitch can be followed: to its sustain's
/// end, or to the next charted sound when that comes first.</param>
public readonly record struct TechniqueContourExpectation(
    ContourTechniques Techniques, double BendSemis, double BendPeakAfterMs, int SlideSemis, double WindowAfterMs)
{
    public bool IsEmpty => Techniques == ContourTechniques.None;

    /// <summary>How long after the note's time the contour holds all its evidence: a bend
    /// waits for its peak plus the player's lateness, a slide for its whole window (it arrives
    /// at the end), a vibrato for at most <see cref="TechniqueContourJudge.VibratoEvidenceMs" />.
    /// Never longer than the window.</summary>
    public double EvidenceAfterMs
    {
        get
        {
            var ms = 0.0;
            if ((Techniques & ContourTechniques.Bend) != 0)
                ms = BendPeakAfterMs + TechniqueContourJudge.BendLateSlackMs;
            if ((Techniques & ContourTechniques.Slide) != 0) ms = Math.Max(ms, WindowAfterMs);
            if ((Techniques & ContourTechniques.Vibrato) != 0)
                ms = Math.Max(ms, TechniqueContourJudge.VibratoEvidenceMs);
            return Math.Min(WindowAfterMs, ms);
        }
    }
}

/// <summary>
/// Judges a bend, a slide or a vibrato from the played note's PITCH CONTOUR: cents
///     relative to the charted, unbent pitch, one value per CQT hop from the note's time on,
///     <see cref="float.NaN" /> where no pitch was heard. Pure — the contour comes from
///     <see cref="LiveCqtVerifier.JudgeTechnique" />, and every rule is a constant here.
///     <list type="bullet">
///         <item>bend: the pitch rises by at least <see cref="BendShare" /> of the charted step
///         by the bend's peak (plus <see cref="BendLateSlackMs" />);</item>
///         <item>slide: the pitch travels at least <see cref="SlideShare" /> of the charted
///         interval in the slide's direction, without first going the other way;</item>
///         <item>vibrato: at least <see cref="VibratoMinPeaks" /> pitch peaks with a trough between
///         them, every swing at least <see cref="VibratoMinSwingCents" /> cents.</item>
///     </list>
///     Anything it cannot see is <see cref="TechniqueContourVerdict.Unknown" />, never
///     <see cref="TechniqueContourVerdict.NotMet" />.
/// </summary>
public static class TechniqueContourJudge
{
    /// <summary>The techniques the player's per-technique switches turn on
    /// (<c>AppConfig.LivePlayJudgeBends</c> / <c>LivePlayJudgeSlides</c> / <c>LivePlayJudgeVibrato</c>).</summary>
    public static ContourTechniques Switched(bool bends, bool slides, bool vibrato) =>
        (bends ? ContourTechniques.Bend : ContourTechniques.None)
        | (slides ? ContourTechniques.Slide : ContourTechniques.None)
        | (vibrato ? ContourTechniques.Vibrato : ContourTechniques.None);

    /// <summary>Share of the charted bend step the pitch must reach.</summary>
    public const double BendShare = 0.7;

    /// <summary>How long after the charted peak the bend may still arrive: the Normal hit
    /// window's late edge, because a player who is late on the attack is late on the bend.</summary>
    public const double BendLateSlackMs = 250;

    /// <summary>Share of the charted slide interval the pitch must travel.</summary>
    public const double SlideShare = 0.5;

    /// <summary>A slide that first moves this far the WRONG way is not this slide.</summary>
    public const double SlideWrongWayCents = 50;

    /// <summary>Pitch peaks a vibrato needs — the issue's "two oscillations".</summary>
    public const int VibratoMinPeaks = 2;

    /// <summary>
    ///     How far the pitch must swing back from an extreme before it counts. <b>Measured, not
    ///     the issue's ~20 cents</b>: the CQT's long kernels (up to ~170 ms) smear a 5 Hz finger
    ///     vibrato, so on real DI (Guitar-TECHS) the contour of a played vibrato swings
    ///     9–30 cents (5–95 % range; the P2 player's median 12–19) while a held note's jitters
    ///     2–6. At 20 cents 44 % of one player's vibrato and 0 % of the other's read as met; at 6,
    ///     82 % and 32 %, and still none of 138 held notes. The second player's vibrato is too
    ///     shallow for this contour either way — see <c>docs/agents/services/live-play.md</c>.
    /// </summary>
    public const double VibratoMinSwingCents = 6;

    /// <summary>The pick's transient is skipped before a vibrato is looked for.</summary>
    public const double VibratoSkipAttackMs = 80;

    /// <summary>Shorter than this (after the attack), a vibrato is Unknown.</summary>
    public const double VibratoMinWindowMs = 350;

    /// <summary>A vibrato is judged on at most this much of its note: two cycles of even a slow
    /// vibrato fit, and the CQT's ring (~2.2 s) must still hold the note's start when the end
    /// arrives.</summary>
    public const double VibratoEvidenceMs = 1500;

    /// <summary>The slow trend (a bend, a slide, drift) removed before swings are counted. Long
    /// enough to keep a slow (2–3 Hz) vibrato: 300 ms took half of one player's out.</summary>
    public const double VibratoDetrendMs = 800;

    /// <summary>Share of a span's hops that must carry a pitch for it to be judged at all.</summary>
    public const double MinVoicedShare = 0.5;

    /// <summary>Hops at the start of the note that set its attack pitch.</summary>
    public const double AttackSpanMs = 40;

    /// <summary>Judges every technique the sound asks for: NotMet when any is clearly
    /// missing, Met when all are shown, Unknown otherwise.</summary>
    public static TechniqueContourVerdict Judge(ReadOnlySpan<float> cents, double msPerHop,
        in TechniqueContourExpectation expect)
    {
        if (expect.IsEmpty || msPerHop <= 0) return TechniqueContourVerdict.Unknown;

        var anyUnknown = false;
        if ((expect.Techniques & ContourTechniques.Bend) != 0 &&
            Fold(JudgeBend(cents, msPerHop, expect.BendSemis, expect.BendPeakAfterMs), ref anyUnknown))
        {
            return TechniqueContourVerdict.NotMet;
        }

        if ((expect.Techniques & ContourTechniques.Slide) != 0 &&
            Fold(JudgeSlide(cents, msPerHop, expect.SlideSemis), ref anyUnknown))
        {
            return TechniqueContourVerdict.NotMet;
        }

        if ((expect.Techniques & ContourTechniques.Vibrato) != 0 &&
            Fold(JudgeVibrato(cents, msPerHop), ref anyUnknown))
        {
            return TechniqueContourVerdict.NotMet;
        }

        return anyUnknown ? TechniqueContourVerdict.Unknown : TechniqueContourVerdict.Met;
    }

    // True when the verdict is NotMet; records an Unknown on the way.
    private static bool Fold(TechniqueContourVerdict verdict, ref bool anyUnknown)
    {
        anyUnknown |= verdict == TechniqueContourVerdict.Unknown;
        return verdict == TechniqueContourVerdict.NotMet;
    }

    /// <summary>A bend: the peak rise over the attack pitch (or over the charted pitch, for
    /// a pre-bend that starts high) must reach <see cref="BendShare" /> of the step by the
    /// charted peak plus <see cref="BendLateSlackMs" />.</summary>
    public static TechniqueContourVerdict JudgeBend(ReadOnlySpan<float> cents, double msPerHop,
        double bendSemis, double peakAfterMs)
    {
        if (bendSemis <= 0) return TechniqueContourVerdict.Unknown;
        var peakHop = (int)Math.Round(Math.Max(0, peakAfterMs) / msPerHop);
        // The contour stops before the bend could have arrived: nothing to say.
        if (peakHop >= cents.Length) return TechniqueContourVerdict.Unknown;

        var end = Math.Min(cents.Length, peakHop + (int)Math.Ceiling(BendLateSlackMs / msPerHop) + 1);
        var span = cents[..end];
        if (VoicedShare(span) < MinVoicedShare) return TechniqueContourVerdict.Unknown;
        // The bend's own moment must have been heard.
        var nearPeak = cents[Math.Max(0, peakHop - (int)(150 / msPerHop))..end];
        if (VoicedCount(nearPeak) < 3) return TechniqueContourVerdict.Unknown;

        var attack = AttackPitch(span, msPerHop);
        var rise = SmoothedMax(span) - Math.Min(0, attack);
        return rise >= BendShare * bendSemis * 100 ? TechniqueContourVerdict.Met : TechniqueContourVerdict.NotMet;
    }

    /// <summary>A slide: travel from the attack pitch towards the target covering
    /// <see cref="SlideShare" /> of the interval, never first going
    /// <see cref="SlideWrongWayCents" /> the other way.</summary>
    public static TechniqueContourVerdict JudgeSlide(ReadOnlySpan<float> cents, double msPerHop, int slideSemis)
    {
        if (slideSemis == 0 || VoicedCount(cents) < 5 || VoicedShare(cents) < 0.3)
            return TechniqueContourVerdict.Unknown;

        var direction = Math.Sign(slideSemis);
        var start = AttackPitch(cents, msPerHop);
        var best = double.NegativeInfinity;
        var worstBeforeBest = 0.0;
        var worstSoFar = 0.0;
        Span<float> window = stackalloc float[3];
        var filled = 0;
        foreach (var c in cents)
        {
            if (float.IsNaN(c)) continue;
            window[filled % 3] = c;
            filled++;
            if (filled < 3) continue;
            var progress = direction * (Median3(window[0], window[1], window[2]) - start);
            worstSoFar = Math.Min(worstSoFar, progress);
            if (progress > best)
            {
                best = progress;
                worstBeforeBest = worstSoFar;
            }
        }

        if (double.IsNegativeInfinity(best)) return TechniqueContourVerdict.Unknown;
        var travelled = best >= SlideShare * Math.Abs(slideSemis) * 100;
        return travelled && worstBeforeBest > -SlideWrongWayCents
            ? TechniqueContourVerdict.Met
            : TechniqueContourVerdict.NotMet;
    }

    /// <summary>A vibrato: after the attack, at least <see cref="VibratoMinPeaks" /> peaks with a
    /// trough between them, every swing <see cref="VibratoMinSwingCents" /> or more, once the
    /// slow trend is removed.</summary>
    public static TechniqueContourVerdict JudgeVibrato(ReadOnlySpan<float> cents, double msPerHop)
    {
        var skip = Math.Min(cents.Length, (int)Math.Ceiling(VibratoSkipAttackMs / msPerHop));
        var span = cents[skip..];
        if (span.Length * msPerHop < VibratoMinWindowMs || VoicedShare(span) < MinVoicedShare)
            return TechniqueContourVerdict.Unknown;

        // The voiced hops, de-glitched (median of three), then the slow trend removed.
        var voiced = new float[VoicedCount(span)];
        var n = 0;
        foreach (var c in span)
        {
            if (!float.IsNaN(c)) voiced[n++] = c;
        }

        var smooth = new float[n];
        for (var i = 0; i < n; i++)
            smooth[i] = Median3(voiced[Math.Max(0, i - 1)], voiced[i], voiced[Math.Min(n - 1, i + 1)]);

        // Hysteresis: an extreme is CONFIRMED once the pitch has come back from it by a whole
        // swing; until then it only follows the pitch further the same way.
        var half = Math.Max(1, (int)Math.Round(VibratoDetrendMs / msPerHop / 2));
        var extremes = 0;
        var direction = 0;
        double runMax = 0, runMin = 0;
        for (var i = 0; i < n; i++)
        {
            double sum = 0;
            var lo = Math.Max(0, i - half);
            var hi = Math.Min(n - 1, i + half);
            for (var k = lo; k <= hi; k++) sum += smooth[k];
            var v = smooth[i] - sum / (hi - lo + 1);
            if (i == 0)
            {
                runMax = runMin = v;
                continue;
            }

            runMax = Math.Max(runMax, v);
            runMin = Math.Min(runMin, v);
            if (direction >= 0 && runMax - v >= VibratoMinSwingCents)
            {
                extremes++; // a peak
                direction = -1;
                runMin = v;
            }
            else if (direction <= 0 && v - runMin >= VibratoMinSwingCents)
            {
                extremes++; // a trough
                direction = 1;
                runMax = v;
            }
        }

        // The first extreme is where the pitch starts to move (it only fixes the phase: a
        // single wobble is rest, peak, rest). N peaks after it make 2N confirmed extremes.
        return extremes >= 2 * VibratoMinPeaks
            ? TechniqueContourVerdict.Met
            : TechniqueContourVerdict.NotMet;
    }

    private static double AttackPitch(ReadOnlySpan<float> cents, double msPerHop)
    {
        var hops = Math.Max(1, (int)Math.Ceiling(AttackSpanMs / msPerHop));
        Span<float> first = stackalloc float[3];
        var n = 0;
        for (var i = 0; i < cents.Length && n < 3; i++)
        {
            if (float.IsNaN(cents[i])) continue;
            if (i >= hops && n > 0) break;
            first[n++] = cents[i];
        }

        return n switch
        {
            0 => 0,
            1 => first[0],
            2 => (first[0] + first[1]) / 2,
            _ => Median3(first[0], first[1], first[2])
        };
    }

    // The largest median-of-three over consecutive voiced hops: a single-hop glitch (a
    // neighbouring string's ring winning one frame) cannot fake a bend.
    private static double SmoothedMax(ReadOnlySpan<float> cents)
    {
        Span<float> window = stackalloc float[3];
        var filled = 0;
        var best = double.NegativeInfinity;
        foreach (var c in cents)
        {
            if (float.IsNaN(c)) continue;
            window[filled % 3] = c;
            filled++;
            if (filled >= 3) best = Math.Max(best, Median3(window[0], window[1], window[2]));
        }

        if (filled is > 0 and < 3)
        {
            for (var i = 0; i < filled; i++) best = Math.Max(best, window[i]);
        }

        return double.IsNegativeInfinity(best) ? 0 : best;
    }

    private static float Median3(float a, float b, float c) => Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));

    private static int VoicedCount(ReadOnlySpan<float> cents)
    {
        var n = 0;
        foreach (var c in cents)
        {
            if (!float.IsNaN(c)) n++;
        }

        return n;
    }

    private static double VoicedShare(ReadOnlySpan<float> cents) =>
        cents.IsEmpty ? 0 : VoicedCount(cents) / (double)cents.Length;
}
