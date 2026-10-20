using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     <see cref="TechniqueContourJudge" /> on synthetic pitch contours (#645): cents from the
///     charted pitch per hop, NaN where no pitch was heard. Each rule is pinned on both sides of
///     its threshold, and every case the contour cannot settle is Unknown — never NotMet,
///     because an analyzer false negative must not punish correct playing.
/// </summary>
public sealed class TechniqueContourJudgeTests
{
    // The CQT hop at 44.1 kHz: 256 samples.
    private const double Hop = 256 * 1000.0 / 44100;

    private static float[] Track(double ms, Func<double, double> centsAt)
    {
        var n = (int)(ms / Hop) + 1;
        var t = new float[n];
        for (var i = 0; i < n; i++) t[i] = (float)centsAt(i * Hop);
        return t;
    }

    // A full-step bend: flat for 100 ms, a 150 ms rise to `peakCents`, held.
    private static float[] Bend(double peakCents, double ms = 700) =>
        Track(ms, t => t < 100 ? 0 : t < 250 ? peakCents * (t - 100) / 150 : peakCents);

    private static TechniqueContourExpectation BendOf(double semis, double peakAfterMs = 250, double windowMs = 700) =>
        new(ContourTechniques.Bend, semis, peakAfterMs, 0, windowMs);

    [Fact]
    public void A_bend_that_reaches_its_step_is_met() =>
        Assert.Equal(TechniqueContourVerdict.Met,
            TechniqueContourJudge.Judge(Bend(200), Hop, BendOf(2)));

    [Fact]
    public void A_bend_must_reach_seventy_percent_of_the_charted_step()
    {
        // 70 % of a full step is 140 cents: just over is met, just under is not.
        Assert.Equal(TechniqueContourVerdict.Met, TechniqueContourJudge.Judge(Bend(145), Hop, BendOf(2)));
        Assert.Equal(TechniqueContourVerdict.NotMet, TechniqueContourJudge.Judge(Bend(130), Hop, BendOf(2)));
        // A half-step bend asks for 70 cents.
        Assert.Equal(TechniqueContourVerdict.Met, TechniqueContourJudge.Judge(Bend(80), Hop, BendOf(1)));
    }

    [Fact]
    public void An_unbent_note_where_a_bend_is_charted_is_not_met()
    {
        var flat = Track(700, _ => 3 * Math.Sin(_ / 40.0)); // a held note's small jitter
        Assert.Equal(TechniqueContourVerdict.NotMet, TechniqueContourJudge.Judge(flat, Hop, BendOf(2)));
    }

    [Fact]
    public void A_pre_bend_that_starts_high_is_met_from_the_charted_pitch()
    {
        // Bent before the pick: the attack is already at the step, nothing rises after it.
        var prebent = Track(500, _ => 195);
        Assert.Equal(TechniqueContourVerdict.Met,
            TechniqueContourJudge.Judge(prebent, Hop, BendOf(2, peakAfterMs: 0, windowMs: 500)));
    }

    [Fact]
    public void A_bend_that_arrives_after_its_peak_plus_the_late_slack_is_not_met()
    {
        // The pitch only gets there 400 ms after the charted peak (250 ms of slack).
        var late = Track(1200, t => t < 650 ? 0 : 200);
        Assert.Equal(TechniqueContourVerdict.NotMet,
            TechniqueContourJudge.Judge(late, Hop, BendOf(2, peakAfterMs: 250, windowMs: 1200)));
        // ...but a late player is forgiven by the slack.
        var slightlyLate = Track(1200, t => t < 420 ? 0 : 200);
        Assert.Equal(TechniqueContourVerdict.Met,
            TechniqueContourJudge.Judge(slightlyLate, Hop, BendOf(2, peakAfterMs: 250, windowMs: 1200)));
    }

    [Fact]
    public void A_single_glitch_hop_cannot_fake_a_bend()
    {
        var glitch = Track(700, _ => 0);
        glitch[40] = 200;
        Assert.Equal(TechniqueContourVerdict.NotMet, TechniqueContourJudge.Judge(glitch, Hop, BendOf(2)));
    }

    [Fact]
    public void A_slide_that_travels_half_its_interval_is_met()
    {
        // Charted three frets up; the pitch walks there over 300 ms.
        var slide = Track(500, t => t < 100 ? 0 : Math.Min(300, (t - 100) * 1.0));
        var expect = new TechniqueContourExpectation(ContourTechniques.Slide, 0, 0, 3, 500);
        Assert.Equal(TechniqueContourVerdict.Met, TechniqueContourJudge.Judge(slide, Hop, expect));

        // Only one fret of the three: under half the interval.
        var short1 = Track(500, t => t < 100 ? 0 : Math.Min(100, (t - 100) * 1.0));
        Assert.Equal(TechniqueContourVerdict.NotMet, TechniqueContourJudge.Judge(short1, Hop, expect));
    }

    [Fact]
    public void A_slide_down_is_judged_in_its_own_direction()
    {
        var expect = new TechniqueContourExpectation(ContourTechniques.Slide, 0, 0, -4, 500);
        var down = Track(500, t => -Math.Min(400, t * 1.2));
        var up = Track(500, t => Math.Min(400, t * 1.2));
        Assert.Equal(TechniqueContourVerdict.Met, TechniqueContourJudge.Judge(down, Hop, expect));
        Assert.Equal(TechniqueContourVerdict.NotMet, TechniqueContourJudge.Judge(up, Hop, expect));
    }

    [Fact]
    public void A_held_note_where_a_slide_is_charted_is_not_met()
    {
        var expect = new TechniqueContourExpectation(ContourTechniques.Slide, 0, 0, 2, 500);
        Assert.Equal(TechniqueContourVerdict.NotMet,
            TechniqueContourJudge.Judge(Track(500, _ => 0), Hop, expect));
    }

    private static readonly TechniqueContourExpectation VibratoOf =
        new(ContourTechniques.Vibrato, 0, 0, 0, 1000);

    [Fact]
    public void A_vibrato_as_the_CQT_hears_it_is_met()
    {
        // 5 Hz at ±15 cents, and the ±5 a subtle player's vibrato reads as through the CQT's
        // long kernels (measured on real DI: TechniqueContourJudge.VibratoMinSwingCents).
        Assert.Equal(TechniqueContourVerdict.Met, TechniqueContourJudge.Judge(
            Track(1000, t => 15 * Math.Sin(2 * Math.PI * 5 * t / 1000)), Hop, VibratoOf));
        Assert.Equal(TechniqueContourVerdict.Met, TechniqueContourJudge.Judge(
            Track(1000, t => 5 * Math.Sin(2 * Math.PI * 5 * t / 1000)), Hop, VibratoOf));
        // A slow, 2.5 Hz vibrato survives the detrend.
        Assert.Equal(TechniqueContourVerdict.Met, TechniqueContourJudge.Judge(
            Track(1000, t => 10 * Math.Sin(2 * Math.PI * 2.5 * t / 1000)), Hop, VibratoOf));
    }

    [Fact]
    public void A_vibrato_too_shallow_or_too_short_lived_is_not_met()
    {
        // ±2 cents: a held note's own jitter (2–6 cents on real DI), under the swing.
        var shallow = Track(1000, t => 2 * Math.Sin(2 * Math.PI * 5 * t / 1000));
        Assert.Equal(TechniqueContourVerdict.NotMet, TechniqueContourJudge.Judge(shallow, Hop, VibratoOf));

        // One wobble, then held: a single cycle is not a vibrato.
        var once = Track(1000, t => t is > 100 and < 300 ? 20 * Math.Sin(2 * Math.PI * 5 * (t - 100) / 1000) : 0);
        Assert.Equal(TechniqueContourVerdict.NotMet, TechniqueContourJudge.Judge(once, Hop, VibratoOf));
    }

    [Fact]
    public void A_flat_note_where_vibrato_is_charted_is_not_met() =>
        Assert.Equal(TechniqueContourVerdict.NotMet,
            TechniqueContourJudge.Judge(Track(1000, _ => 0), Hop, VibratoOf));

    [Fact]
    public void A_vibrato_on_top_of_a_bend_is_still_seen()
    {
        // The slow rise is the trend; the wobble rides it.
        var bentVibrato = Track(1000, t => Math.Min(200, t) + 15 * Math.Sin(2 * Math.PI * 5 * t / 1000));
        Assert.Equal(TechniqueContourVerdict.Met, TechniqueContourJudge.Judge(bentVibrato, Hop, VibratoOf));
    }

    [Fact]
    public void Too_short_a_note_is_unknown_for_every_technique()
    {
        var shortNote = Track(150, _ => 0);
        Assert.Equal(TechniqueContourVerdict.Unknown,
            TechniqueContourJudge.Judge(shortNote, Hop, VibratoOf with { WindowAfterMs = 150 }));
        // The contour ends before the charted bend peak: it never got the chance.
        Assert.Equal(TechniqueContourVerdict.Unknown,
            TechniqueContourJudge.Judge(shortNote, Hop, BendOf(2, peakAfterMs: 300)));
    }

    [Fact]
    public void An_unpitched_contour_is_unknown_for_every_technique()
    {
        var silent = Track(1000, _ => double.NaN);
        Assert.Equal(TechniqueContourVerdict.Unknown, TechniqueContourJudge.Judge(silent, Hop, BendOf(2)));
        Assert.Equal(TechniqueContourVerdict.Unknown, TechniqueContourJudge.Judge(silent, Hop, VibratoOf));
        Assert.Equal(TechniqueContourVerdict.Unknown, TechniqueContourJudge.Judge(silent, Hop,
            new TechniqueContourExpectation(ContourTechniques.Slide, 0, 0, 2, 1000)));

        // Mostly unpitched (a pitch in one hop of three) is still too little to rule on.
        var sparse = Track(700, _ => 0);
        for (var i = 0; i < sparse.Length; i++)
        {
            if (i % 3 != 0) sparse[i] = float.NaN;
        }

        Assert.Equal(TechniqueContourVerdict.Unknown, TechniqueContourJudge.Judge(sparse, Hop, BendOf(2)));
    }

    [Fact]
    public void A_sound_that_asks_for_no_technique_is_unknown() =>
        Assert.Equal(TechniqueContourVerdict.Unknown,
            TechniqueContourJudge.Judge(Bend(200), Hop, default));

    [Fact]
    public void Several_techniques_combine_missing_wins_then_unknown_then_met()
    {
        var bendAndVibrato = new TechniqueContourExpectation(
            ContourTechniques.Bend | ContourTechniques.Vibrato, 2, 250, 0, 1000);
        // Bent, with a vibrato on top: both met.
        var both = Track(1000, t => (t < 250 ? 0.8 * t : 200) + 15 * Math.Sin(2 * Math.PI * 5 * t / 1000));
        Assert.Equal(TechniqueContourVerdict.Met, TechniqueContourJudge.Judge(both, Hop, bendAndVibrato));
        // Bent but held still: the missing vibrato downgrades.
        Assert.Equal(TechniqueContourVerdict.NotMet,
            TechniqueContourJudge.Judge(Bend(200, 1000), Hop, bendAndVibrato));
        // Bent, and too short for a vibrato to be judged: Unknown, not Met.
        var shortBend = bendAndVibrato with { WindowAfterMs = 420 };
        Assert.Equal(TechniqueContourVerdict.Unknown,
            TechniqueContourJudge.Judge(Bend(200, 420), Hop, shortBend));
    }

    [Fact]
    public void The_evidence_ends_at_the_bend_peak_plus_slack_or_the_window()
    {
        Assert.Equal(250 + TechniqueContourJudge.BendLateSlackMs, BendOf(2, 250, 2000).EvidenceAfterMs);
        Assert.Equal(300, BendOf(2, 250, 300).EvidenceAfterMs);
        Assert.Equal(800, new TechniqueContourExpectation(ContourTechniques.Slide, 0, 0, 2, 800).EvidenceAfterMs);
        Assert.Equal(TechniqueContourJudge.VibratoEvidenceMs,
            (VibratoOf with { WindowAfterMs = 4000 }).EvidenceAfterMs);
    }
}
