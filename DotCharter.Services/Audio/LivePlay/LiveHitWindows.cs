namespace DotCharter.Services.Audio.LivePlay;

/// <summary>How tight the live-play judging is. Maps to the matcher's hit
/// window and salience thresholds — Lenient forgives sloppier timing and
/// weaker pitch evidence, Strict demands both.</summary>
public enum LiveHitWindowPreset
{
    Lenient,
    Normal,
    Strict
}

/// <summary>Factory mapping <see cref="LiveHitWindowPreset"/> to matcher
/// init properties (they are init-only, so presets construct the matcher).</summary>
public static class LiveHitWindows
{
    public static LiveNoteMatcher CreateMatcher(LiveHitWindowPreset preset,
        LiveChordVoicingStrictness chordStrictness = LiveChordVoicingStrictness.Standard,
        bool stringAware = false,
        Func<ExpectedSound, ChartVerifyOutcome>? chartVerify = null,
        Func<ExpectedSound, ChartVerifyOutcome>? chartVerifyEarly = null,
        int transposeSemitones = 0,
        Func<ExpectedSound, TechniqueContourVerdict?>? judgeContour = null,
        ContourTechniques judgedTechniques = ContourTechniques.All)
    {
        var (early, late, note, chord, legato, coverage) = preset switch
        {
            LiveHitWindowPreset.Lenient => (220.0, 350.0, 0.18f, 0.14f, 0.10f, 0.5),
            LiveHitWindowPreset.Strict => (100.0, 160.0, 0.32f, 0.26f, 0.20f, 0.75),
            _ => (150.0, 250.0, 0.25f, 0.20f, 0.15f, 0.6),
        };

        // Timing-tier windows scale with the preset: a Strict player is graded
        // Perfect only in a tighter band, Lenient forgives more.
        var (perfect, great) = preset switch
        {
            LiveHitWindowPreset.Lenient => (65.0, 130.0),
            LiveHitWindowPreset.Strict => (25.0, 60.0),
            _ => (45.0, 95.0),
        };

        // CQT mode: the rolling constant-Q needs the hit window's future frames — the chord
        // window plus half the longest kernel (LiveCqtConfig.FinalVerdictAfterMs, ~378 ms).
        // This read a fixed 230 ms, right for the 4096 kernel and the 140 ms window only; at
        // 8192 and the 250 ms chord window it cut every chord's window short. Hits do not
        // wait for it (ChartVerifyEarly credits them as they are confirmed); only misses do.
        // An ATTACK still grades on the preset's own late edge, so the two modes time a
        // hit on the same window.
        var attackLate = late;
        if (chartVerify != null) late = Math.Max(late, JuceSharp.Dsp.LiveCqtConfig.FinalVerdictAfterMs);

        return new LiveNoteMatcher
        {
            HitWindowEarlyMs = early,
            HitWindowLateMs = late,
            AttackWindowLateMs = attackLate,
            // CQT mode times a confirmed hit from the attack detector's onset, which is
            // reported ~190 ms after the attack — later than the CQT confirms.
            AwaitAttackOnsets = chartVerify != null,
            PerfectWindowMs = perfect,
            GreatWindowMs = great,
            MinNoteSalience = note,
            MinChordToneSalience = chord,
            MinLegatoSalience = legato,
            ChordCoverage = coverage,
            ChordStrictness = chordStrictness,
            StringAwareVerification = stringAware,
            TransposeSemitones = transposeSemitones,
            ChartVerify = chartVerify,
            ChartVerifyEarly = chartVerifyEarly,
            // The contour comes from the CQT: without it there is nothing to judge.
            // No technique switched on is the same as no judge at all.
            JudgeContour = chartVerify != null && judgedTechniques != ContourTechniques.None ? judgeContour : null,
            JudgedTechniques = judgedTechniques,
            EarlyCreditAfterMs = JuceSharp.Dsp.LiveCqtConfig.EarlyCreditAfterMs,
            EarlyCreditConfirmations = JuceSharp.Dsp.LiveCqtConfig.EarlyCreditConfirmations,
            PinchHarmonicAttackEvidence = chartVerify != null && JuceSharp.Dsp.LiveCqtConfig.PinchHarmonicAttackEvidence,
        };
    }

    public static LiveHitWindowPreset Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "lenient" => LiveHitWindowPreset.Lenient,
        "strict" => LiveHitWindowPreset.Strict,
        _ => LiveHitWindowPreset.Normal
    };
}
