// The 3D preview's per-judgment feedback payload, drained by LivePlayFeedbackDrain on the render thread.

using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>One judged sound for the 3D preview's hit/miss feedback markers.
/// <paramref name="Tier"/> is the timing grade (None for misses and untimed
/// hits) so the 3D HUD can flash PERFECT/GREAT/GOOD at the instant of the hit,
/// alongside the existing early/late <paramref name="Direction"/>.
/// <paramref name="IsGrade"/>: not a judgment but the late timing grade of a hit
/// already shown (<see cref="LivePlayGrade"/>, CQT mode) — it drives the tier and
/// early/late cues only, never a second marker or a second crowd-meter verdict.
/// <paramref name="IsTechniqueDowngrade"/>: not a judgment but the contour judge's NotMet on a hit
/// already shown — it draws the wrong-technique marker at the ruling and takes the hit's
/// extra over a half hit back off the crowd meter (at <paramref name="StreakAtCredit"/>), never a
/// second verdict.</summary>
public readonly record struct LivePlayFeedbackEvent(
    double NoteTimeMs, double JudgedAtMs, int StringIndex, int Fret, bool IsChord, LivePlayVerdict Verdict,
    LiveTimingDirection Direction = LiveTimingDirection.None,
    LiveTimingTier Tier = LiveTimingTier.None,
    bool IsGrade = false,
    bool IsTechniqueDowngrade = false,
    int StreakAtCredit = 0)
{
    /// <summary>The feedback of one judged sound, as <c>OnJudged</c> enqueues it.</summary>
    public static LivePlayFeedbackEvent ForJudgment(in LivePlayEvent ev) => new(
        ev.Sound.TimeMs, ev.DetectedAtMs, ev.Sound.StringIndex, ev.Sound.Fret, ev.Sound.IsChord,
        ev.Verdict, ev.Direction, ev.Tier);

    /// <summary>The feedback of a late timing grade, as <c>OnGraded</c> enqueues it: stamped
    /// with the attack, like a classic-path hit's cue.</summary>
    public static LivePlayFeedbackEvent ForGrade(in LivePlayGrade grade) => new(
        grade.Sound.TimeMs, grade.AttackAtMs, grade.Sound.StringIndex, grade.Sound.Fret, grade.Sound.IsChord,
        grade.Verdict, grade.Direction, grade.Tier, IsGrade: true);

    /// <summary>The feedback of a technique downgrade, as <c>OnTechniqueJudged</c> enqueues it:
    /// stamped with the playhead of the ruling, untimed (it has no attack of its own).</summary>
    public static LivePlayFeedbackEvent ForTechniqueDowngrade(in LiveTechniqueJudgment check, double ruledAtMs) => new(
        check.Sound.TimeMs, ruledAtMs, check.Sound.StringIndex, check.Sound.Fret, check.Sound.IsChord,
        LivePlayVerdict.HitWrongTechnique, IsTechniqueDowngrade: true, StreakAtCredit: check.StreakAtCredit);
}
