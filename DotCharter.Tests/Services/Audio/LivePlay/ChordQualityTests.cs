using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     Chord scoring by harmonic function rather than by headcount.
///     <para>
///     The bug: a flat ⌈n/2⌉ coverage threshold cannot tell a major chord from a minor
///     one. A charted C major (C–E–G) needs 2 of 3 tones, so <b>C + G alone passes</b> —
///     and the third, the only tone that carries the chord's quality, is never required.
///     A player fingering C MINOR scored a clean HIT against a charted C MAJOR.
///     </para>
///     <para>
///     Ubisoft shipped exactly this in Rocksmith 2014 and said so publicly. We reproduced
///     it independently.
///     </para>
/// </summary>
public sealed class ChordQualityTests
{
    // MIDI: C3 = 48, Eb3 = 51, E3 = 52, G3 = 55, Bb3 = 58, B3 = 59.
    private const int C = 48, Eb = 51, E = 52, G = 55, Bb = 58;

    private static ChartVerifyOutcome Judge(int[] charted, int[] sounded,
                                            double coverage = 0.5, bool requireQuality = true)
        => ChordQuality.Judge(charted, sounded, coverage, requireQuality);

    // ── the money test ──────────────────────────────────────────────────────────

    [Fact]
    public void A_minor_chord_does_not_score_a_hit_against_a_charted_major()
    {
        // Charted C major. The player fingers C minor: root and fifth land, the major
        // third never sounds (they played Eb instead). Under the old flat ⌈3/2⌉ = 2 rule
        // this was a HIT — root + fifth was enough.
        var outcome = Judge(charted: [C, E, G], sounded: [C, G]);

        Assert.False(outcome.Confirmed,
            "a chord whose quality-defining third never sounded is not that chord");
    }

    [Fact]
    public void A_major_chord_played_correctly_still_scores() => Assert.True(Judge(charted: [C, E, G], sounded: [C, E, G]).Confirmed);

    [Fact]
    public void The_third_alone_carries_more_weight_than_the_fifth()
    {
        // Root + third (no fifth) should credit; root + fifth (no third) should not.
        // The fifth is nearly redundant — it is implied by the root's own harmonics,
        // which is also why it is the tone most likely to confirm from bleed alone.
        Assert.True(Judge(charted: [C, E, G], sounded: [C, E]).Confirmed);
        Assert.False(Judge(charted: [C, E, G], sounded: [C, G]).Confirmed);
    }

    // ── the trap: guitar charts are FULL of power chords ─────────────────────────

    [Fact]
    public void A_power_chord_has_no_third_and_is_completely_unaffected() =>
        // C5 = root + fifth. There is no quality tone to demand, so the rule must not
        // apply — if it did, we would break most of the rock canon.
        Assert.True(Judge(charted: [C, G], sounded: [C, G]).Confirmed);

    [Fact]
    public void A_power_chord_with_an_octave_root_still_scores()
    {
        Assert.True(Judge(charted: [C, G, C + 12], sounded: [C, G, C + 12]).Confirmed);
        // …and the doubled root does not let you skip the fifth entirely.
        Assert.True(Judge(charted: [C, G, C + 12], sounded: [C, C + 12]).Confirmed);
    }

    // ── it must not become harsh ─────────────────────────────────────────────────

    [Fact]
    public void A_missed_fifth_does_not_fail_an_otherwise_correct_chord() =>
        // Ubisoft's own lesson: demanding every tone "not only feels robotic as a player
        // but sounds robotic too". A player who nails root+third+seventh but whose fifth
        // is buried must still be credited.
        Assert.True(Judge(charted: [C, E, G, Bb], sounded: [C, E, Bb]).Confirmed);

    [Fact]
    public void A_minor_chord_charted_as_minor_scores()
    {
        // The rule is about the chart's quality tone, not about "major" specifically.
        Assert.True(Judge(charted: [C, Eb, G], sounded: [C, Eb, G]).Confirmed);
        // …and playing MAJOR against a charted MINOR is likewise rejected.
        Assert.False(Judge(charted: [C, Eb, G], sounded: [C, G]).Confirmed);
    }

    [Fact]
    public void An_ambiguous_chord_with_several_thirds_demands_none_of_them()
    {
        // Two thirds (e.g. a chart that voices both a minor and a major third — a blues
        // #9 shape). There is no single unambiguous tone to insist on, so fall back to
        // weighted coverage rather than inventing a requirement.
        var outcome = Judge(charted: [C, Eb, E, G], sounded: [C, Eb, G]);
        Assert.True(outcome.Confirmed);
    }

    [Fact]
    public void The_rule_can_be_turned_off() =>
        // Escape hatch: with the requirement off, the old behaviour returns.
        Assert.True(Judge(charted: [C, E, G], sounded: [C, G], requireQuality: false).Confirmed);

    // ── weights ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 1.0)]    // root
    [InlineData(4, 1.0)]    // major third
    [InlineData(3, 1.0)]    // minor third
    [InlineData(7, 0.3)]    // fifth — nearly free
    [InlineData(10, 0.8)]   // minor seventh
    [InlineData(6, 0.8)]    // tritone
    public void Tones_are_weighted_by_harmonic_function(int interval, double expected)
        => Assert.Equal(expected, ChordQuality.WeightOf(interval), 3);

    [Fact]
    public void Confidence_reports_the_weighted_fraction_that_actually_sounded()
    {
        // C major: weights 1.0 + 1.0 + 0.3 = 2.3. Root + third = 2.0 / 2.3 ≈ 0.87.
        var outcome = Judge(charted: [C, E, G], sounded: [C, E]);
        Assert.InRange(outcome.Confidence, 0.86f, 0.88f);
    }
}
