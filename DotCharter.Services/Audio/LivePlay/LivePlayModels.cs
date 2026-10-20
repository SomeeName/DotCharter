using DotCharter.Services.Gameplay.Scoring;

namespace DotCharter.Services.Audio.LivePlay;

/// <summary>
///     Technique classification of one detected input event. Heuristic —
///     meant for the detection monitor and for LENIENT verification (a flag
///     can widen what counts as a hit, never narrow it).
/// </summary>
[Flags]
public enum LiveTechniqueFlags
{
    None = 0,
    /// <summary>Energy concentrated low — palm-muted or otherwise damped attack.</summary>
    PalmMute = 1 << 0,
    /// <summary>Thin, near-pure spectrum: natural/artificial harmonic ring.</summary>
    Harmonic = 1 << 1,
    /// <summary>Harmonic-like with a high fundamental — pinch-harmonic squeal.</summary>
    PinchHarmonic = 1 << 2,
    /// <summary>Onset without a stable pitch (muted strum, scratch, percussive hit).</summary>
    Percussive = 1 << 3,
    /// <summary>Percussive with a hard transient and low spectral weight (bass slap).</summary>
    Slap = 1 << 4,
    /// <summary>Percussive with a hard transient and bright spectral weight (bass pop).</summary>
    Pop = 1 << 5,
    /// <summary>Pitched attack without a fresh pick transient — hammer-on,
    /// pull-off, tap or slide continuation.</summary>
    Legato = 1 << 6
}

/// <summary>One detected pitch candidate with its relative strength
/// (salience, normalized so the strongest candidate of an event is 1).</summary>
public readonly record struct LivePitch(int Midi, float FrequencyHz, float Salience);

/// <summary>
///     One phrase iteration of the arrangement as a time span, for picking
///     a difficulty level per phrase (Rocksmith-style dynamic difficulty).
///     <see cref="MaxDifficulty"/> is the highest level index that has
///     content for this phrase.
/// </summary>
public readonly record struct LivePhraseSpan(
    int IterationIndex, string PhraseName, double StartMs, double EndMs, int MaxDifficulty);

/// <summary>Chooses the difficulty level index to load for one phrase
/// iteration. Return values are clamped to the phrase's
/// <see cref="LivePhraseSpan.MaxDifficulty"/> and the arrangement's levels.</summary>
public delegate int LiveDifficultySelector(LivePhraseSpan phrase);

/// <summary>
///     How strictly a chord's VOICING must be reproduced (v7).
///     Lenient forgives missing tones, Standard is the v6 behavior, Strict
///     requires every distinct tone at its exact pitch (no octave shortcut,
///     no chroma fallback) — for players drilling exact voicings.
/// </summary>
public enum LiveChordVoicingStrictness
{
    Lenient,
    Standard,
    Strict
}

/// <summary>What the player was supposed to play at one chart position.</summary>
public sealed class ExpectedSound
{
    public double TimeMs { get; init; }
    /// <summary>Sounding MIDI notes (tuning, capo and fret already applied).</summary>
    public int[] MidiNotes { get; init; } = [];
    public bool IsChord { get; init; }
    public bool PalmMute { get; init; }
    public bool Harmonic { get; init; }
    /// <summary>Fully-muted sounds carry no pitch — matched by onset only.</summary>
    public bool FullMute { get; init; }
    public bool PinchHarmonic { get; init; }
    /// <summary>Bass slap or pop stroke — transient-heavy, pitch unreliable.</summary>
    public bool SlapOrPop { get; init; }
    /// <summary>Hammer-on / pull-off / tap: the attack is soft (no pick transient).</summary>
    public bool Legato { get; init; }
    /// <summary>Sounding MIDI of the slide target; the pitch may legitimately be
    /// anywhere between <see cref="MidiNotes"/>[0] and this during the window.</summary>
    public int? SlideToMidi { get; init; }
    /// <summary>Unpitched slides accept any pitched attack.</summary>
    public bool UnpitchedSlide { get; init; }

    /// <summary>
    ///     Signed semitone displacement of a CHORD slide — a strum slides as ONE
    ///     SHAPE, so every tone moves by the same fret delta and no single
    ///     absolute target can describe it (<see cref="SlideToMidi"/>, which is
    ///     absolute, would widen the upper tones down towards the lowest tone's
    ///     destination). 0 = no chord slide. Single notes keep using
    ///     <see cref="SlideToMidi"/>; the two are never both set.
    /// </summary>
    public int SlideSemis { get; init; }
    /// <summary>Largest chart bend in semitones — widens accepted pitch upward.
    /// Also what <see cref="LiveTechniqueTally" /> reads as "this sound is a
    /// bend": any value above 0.</summary>
    public int MaxBendSemis { get; init; }

    /// <summary>Tremolo-picked. It does NOT change matching (the attack is an
    /// ordinary picked one) — it is carried so the technique report card can
    /// grade tremolo, which it could not before.</summary>
    public bool Tremolo { get; init; }

    /// <summary>Charted vibrato. Tallied for the technique report card, and judged from the
    /// pitch contour when <see cref="LiveNoteMatcher.JudgeContour" /> is on.</summary>
    public bool Vibrato { get; init; }

    /// <summary>The bend, slide and vibrato a single note asks its pitch CONTOUR to show;
    /// empty for chords, harmonics and plain notes. Its window is clamped to the next
    /// sound by <see cref="LiveExpectedTimeline" />.</summary>
    public TechniqueContourExpectation TechniqueContour { get; internal set; }

    /// <summary>What the contour judge said about this hit; null when it was not asked (the
    /// setting is off, the sound asks for no contour technique, or it is not a hit).</summary>
    public TechniqueContourVerdict? ContourVerdict { get; internal set; }

    public string Label { get; init; } = string.Empty;

    /// <summary>Chart string of the source note (lowest string for chords);
    /// -1 when unknown. Carried for the 3D hit/miss feedback markers.</summary>
    public int StringIndex { get; init; } = -1;

    /// <summary>Bit <c>i</c> set ⇔ chart string <c>i</c> carries one of this sound's PITCHED
    /// tones (a fully muted chord string is not one). 0 when no string is known. Lets the
    /// analysis monitor attribute a judgment to every string it was judged on.</summary>
    public int StringMask { get; init; }

    /// <summary>Chart fret of the source note (lowest fretted fret for
    /// chords; 0 = open). Carried for the 3D hit/miss feedback markers.</summary>
    public int Fret { get; init; }

    /// <summary>Difficulty level index this sound was loaded from.</summary>
    public int LevelIndex { get; init; }

    /// <summary>Phrase iteration this sound belongs to (-1 when the chart has
    /// no phrase data). Lets per-phrase accuracy drive dynamic difficulty.</summary>
    public int PhraseIterationIndex { get; init; } = -1;

    /// <summary>Judgement for this sound in the current run; null = not yet
    /// judged (window still open or playhead hasn't reached it). Cleared by
    /// <see cref="LiveNoteMatcher.Rewind"/> for everything past the seek
    /// target, which is what makes seeking back "reset progress" instead of
    /// inflating totals.</summary>
    public LivePlayVerdict? Verdict { get; internal set; }

    /// <summary>Timing tier of the judgment; <see cref="LiveTimingTier.None"/>
    /// for misses, untimed hits (CQT-confirmed, no attack onset could time
    /// them) and unjudged sounds.
    /// Kept alongside <see cref="Verdict"/> so stats replays
    /// (<see cref="LivePlayStats.RecomputeFrom"/>) rebuild tier-weighted
    /// scores deterministically; cleared with the verdict on rewind.</summary>
    public LiveTimingTier TimingTier { get; internal set; }

    /// <summary>Consecutive ticks the EARLY check has confirmed this still-open sound
    /// (<see cref="LiveNoteMatcher.EarlyCreditConfirmations" />); cleared with the verdict.</summary>
    internal int EarlyConfirmStreak { get; set; }

    /// <summary>CQT mode: this hit is ALREADY credited (<see cref="Verdict" /> set, tier None,
    /// <see cref="LivePlayStats.CreditFloor" /> paid) and waits for the attack onset that grades
    /// it (<see cref="LiveNoteMatcher.AwaitAttackOnsets" />). Cleared by the grade, by its
    /// window closing (graded untimed) and with the verdict.</summary>
    internal bool AwaitingGrade { get; set; }

    /// <summary>While <see cref="AwaitingGrade" />: the PROVISIONAL onset that timed the cue
    /// already shown for this hit, NaN when none has. Never scored — the finalized onset
    /// grades, confirming or correcting it. Cleared with <see cref="AwaitingGrade" />.</summary>
    internal double ProvisionalAttackMs { get; set; } = double.NaN;

    /// <summary>The multiplier the hit was credited at; its grade pays the rest at the same one.</summary>
    internal int CreditMultiplier { get; set; }

    /// <summary>A credited hit whose technique the contour judge has not ruled on yet:
    /// <see cref="Verdict" /> says Hit, and it is PAID as a technique mismatch until the ruling
    /// pays the rest (<see cref="LivePlayStats.UpgradeTechnique" />), so the score never moves
    /// down. Cleared by the ruling, by a seek and with the verdict.</summary>
    internal bool AwaitingTechnique { get; set; }

    /// <summary>The streak right after this hit was credited — what the rock and crowd meters
    /// were fed with it, so a later technique downgrade can take back exactly the hit's extra
    /// over a half hit.</summary>
    internal int CreditStreak { get; set; }

    /// <summary>The verdict the score has paid for so far — see <see cref="AwaitingTechnique" />.</summary>
    internal LivePlayVerdict? PaidVerdict => AwaitingTechnique ? LivePlayVerdict.HitWrongTechnique : Verdict;
}

/// <summary>
/// The contour judge's ruling on a hit already credited. <b>Not a judgment</b>: the
///     hit was counted when it was credited, and a ruling never turns it into a miss — a
///     <see cref="TechniqueContourVerdict.NotMet" /> only downgrades it to
///     <see cref="LivePlayVerdict.HitWrongTechnique" /> (half score), which the HUD shows the way
///     it shows a palm mute played open; the rock and crowd meters and the 3D marker follow it.
/// </summary>
/// <param name="Sound">The hit ruled on.</param>
/// <param name="Verdict">What its contour showed.</param>
/// <param name="StreakAtCredit">The streak the meters were fed when the hit was credited; a
/// NotMet takes back the hit's extra over a half hit AT that streak.</param>
public readonly record struct LiveTechniqueJudgment(ExpectedSound Sound, TechniqueContourVerdict Verdict,
    int StreakAtCredit = 0);

/// <summary>
/// Which judging path produced a run. Recorded on the run and on the per-detector
///     bests, so the local leaderboard never ranks one detector's runs against the other's: the
///     two hear differently (the CQT verifier credits ~92 % of a real DI performance, the classic
///     path ~60 %), so the same playing scores differently.
///     <see cref="Unknown" /> is every run recorded by an older build, which did not record it — and a
///     CQT run of that time paid every hit the untimed 100, so it ranks fairly against neither.
/// </summary>
public enum LiveDetectorMode
{
    Unknown = 0,

    /// <summary>The classic path: pushed attack events and their pitch candidates.</summary>
    Classic,

    /// <summary>Detection v8, the CQT chart verifier — the default.</summary>
    Cqt
}

/// <summary>The persisted spelling of <see cref="LiveDetectorMode" />.</summary>
public static class LiveDetectorModes
{
    /// <summary>
    ///     The store key of a detector; null for <see cref="LiveDetectorMode.Unknown" />. A string
    ///     rather than the enum's number, like the technique names, so the store stays readable and
    ///     a store written by a later build with a detector this one does not know still loads.
    /// </summary>
    public static string? StoreKey(LiveDetectorMode mode) => mode switch
    {
        LiveDetectorMode.Classic => "Classic",
        LiveDetectorMode.Cqt => "Cqt",
        _ => null
    };
}

public enum LivePlayVerdict
{
    Hit,
    /// <summary>Pitch matched but a technique didn't (e.g. palm mute expected, open attack played).</summary>
    HitWrongTechnique,
    Miss
}

/// <summary>
///     Timing grade of a hit, from |detected − expected| ms. The event time
///     entering the matcher is already latency-offset-compensated (the UI
///     subtracts the <see cref="LatencyCalibrationSession"/> round-trip offset
///     before <c>Offer</c>), so the delta measures PLAYER timing only.
///     <see cref="None"/> = no timing evidence: misses, and hits the pull-based
///     CQT chart-verifier confirmed (it confirms WHAT was played against the
///     chart time, not WHEN) that no reported attack onset could time
///     (<see cref="LiveAttackOnsets"/>) — those pay the full base score,
///     exactly like pre-tier scoring.
/// </summary>
public enum LiveTimingTier
{
    None = 0,
    Good,
    Great,
    Perfect
}

/// <summary>
///     Which SIDE of the beat a hit landed on — the sign that
///     <see cref="LiveTimingTier"/> throws away (it grades |delta|, so a 60 ms
///     rush and a 60 ms drag both read "Great"). Purely informational: it does
///     not touch scoring, which stays magnitude-only by design.
///     <see cref="None"/> means "no honest answer exists", NOT "on time" — see
///     <see cref="LiveTiming.Classify"/>.
/// </summary>
public enum LiveTimingDirection
{
    None = 0,
    Early,
    OnTime,
    Late
}

/// <summary>Pure timing-direction classification, shared by the matcher, the
/// HUD and the 3D feedback markers so they can never disagree.</summary>
public static class LiveTiming
{
    /// <summary>
    ///     Signs a hit's timing as Early/OnTime/Late from the signed
    ///     detected−expected delta (negative = the player was early).
    ///     Returns <see cref="LiveTimingDirection.None"/> whenever no honest
    ///     timestamp exists, which is the whole subtlety here:
    ///     <list type="bullet">
    ///         <item>a MISS's <c>DetectedAtMs</c> is the window expiry, not a detection;</item>
    ///         <item>an UNTIMED CQT hit (<see cref="LiveTimingTier.None"/>: no attack onset
    ///               could time it) is stamped with the playhead — the CQT confirms WHAT
    ///               was played, never WHEN the attack landed.</item>
    ///     </list>
    ///     Both would otherwise report a large, entirely fictional "late".
    ///     Callers must render <see cref="LiveTimingDirection.None"/> as no
    ///     label at all rather than folding it into on-time.
    /// </summary>
    public static LiveTimingDirection Classify(LivePlayVerdict verdict, LiveTimingTier tier, double signedDeltaMs)
    {
        if (verdict == LivePlayVerdict.Miss || tier == LiveTimingTier.None) return LiveTimingDirection.None;
        if (signedDeltaMs < -LiveTimingReport.OnTimeWindowMs) return LiveTimingDirection.Early;
        if (signedDeltaMs > LiveTimingReport.OnTimeWindowMs) return LiveTimingDirection.Late;
        return LiveTimingDirection.OnTime;
    }
}

/// <summary>One judged sound. <paramref name="Confidence"/> (0..1) is how
/// strong the verifying evidence was — the salience of the matching pitch
/// candidate for notes, the verified-tone fraction for chords, 0 for a miss.
/// <paramref name="Tier"/> is the timing grade (None for misses and untimed
/// hits) so the HUD can flash Perfect/Great/Good per judgment;
/// <paramref name="Direction"/> is the same judgment's early/late SIDE (None
/// wherever the tier is None — there is no honest attack time to sign).</summary>
public readonly record struct LivePlayEvent(
    LivePlayVerdict Verdict, ExpectedSound Sound, double DetectedAtMs, float Confidence = 0f,
    LiveTimingTier Tier = LiveTimingTier.None,
    LiveTimingDirection Direction = LiveTimingDirection.None)
{
    /// <summary>
    /// A MISS only: the evidence (0..1) the final verifier DID find for the sound before
    ///     it fell short — the CQT single-note <c>1 − gate/level</c> of a bin that cleared its floor
    ///     but failed the peak test, or a chord's weighted coverage below its bar. Null where no
    ///     verifier looked (the classic path, a pitchless sound) and on every hit. It is REPORTED
    ///     for the analysis monitor (<see cref="LiveDetectionCertainty" />) and decides nothing.
    /// </summary>
    public float? MissEvidence { get; init; }
}

/// <summary>
/// The TIMING of a hit that was already credited (CQT mode). The CQT confirms a note
///     ~100 ms after its chart time and the hit is credited — shown, streak, combo — right then,
///     untimed; the attack onset that times it is REPORTED ~200 ms after the attack, and this is
///     that onset grading the hit retroactively.
///     <para>
///         <b>Not a judgment, and a different type on purpose.</b> Every consumer of
///         <see cref="LivePlayEvent" /> counts one event as one judged sound (hits, streak, rock
///         and crowd meters, section tallies, hit markers); a grade fed to any of them would count
///         the hit twice. It feeds only the timing consumers: the timing collector
///         (<see cref="AsTimedHit" />), the tier counters (already in
///         <see cref="LivePlayStats" />) and the EARLY/LATE and tier cues.
///     </para>
/// </summary>
public readonly record struct LivePlayGrade(
    ExpectedSound Sound, LivePlayVerdict Verdict, double AttackAtMs, LiveTimingTier Tier,
    LiveTimingDirection Direction)
{
    /// <summary>Which grade of the hit this is. <see cref="LivePlayGradeStage.Final" />
    /// unless a provisional onset timed the hit first.</summary>
    public LivePlayGradeStage Stage { get; init; }

    /// <summary>Whether the tier flash and the EARLY/LATE cue should show for it: every stage
    /// but <see cref="LivePlayGradeStage.Confirmed" />, which only repeats what is on screen.</summary>
    public bool ShowsCue => Stage != LivePlayGradeStage.Confirmed;

    /// <summary>The hit as a classic-path timed hit carries it — attack time and all — for the
    /// timing consumers (<see cref="LiveTimingCollector.Add(in LivePlayEvent)" />).</summary>
    public LivePlayEvent AsTimedHit() => new(Verdict, Sound, AttackAtMs, 0f, Tier, Direction);
}

/// <summary>
/// Where a <see cref="LivePlayGrade" /> sits in the hit's timing. A CQT hit is graded
///     by its FINALIZED attack onset, which the analyzer reports ~190 ms after the attack; the
///     detector's PROVISIONAL onset for that attack arrives ~20 ms after it, so the cue can be
///     shown with the "Hit" and the finalized onset then confirms or corrects it.
///     <para>
///         Only the finalized onset ever pays the score, counts a tier or reaches the timing
///         collector (and through it the latency calibrator): a provisional grade is a CUE.
///     </para>
/// </summary>
public enum LivePlayGradeStage
{
    /// <summary>The finalized onset graded a hit nothing had timed before — the only stage
    /// when no earlier stage timed it. Pays, counts, collects and shows.</summary>
    Final = 0,

    /// <summary>A provisional onset timed a hit still awaiting its finalized onset. Shows the
    /// cue only: nothing is paid, counted or collected.</summary>
    Provisional,

    /// <summary>The finalized onset graded a provisionally timed hit with the SAME tier and
    /// direction: pays, counts and collects; the cue already on screen stands.</summary>
    Confirmed,

    /// <summary>The finalized onset moved the hit to another tier or direction than its
    /// provisional cue: pays, counts and collects the finalized grade and shows it.</summary>
    Corrected,
}

/// <summary>
///     Result of the CQT chart-verifier (Detection v8) for one expected sound.
///     <paramref name="Confirmed"/> is the Binary-Match-Mask outcome (per-note,
///     or coverage-credited for chords); <paramref name="Techniques"/> /
///     <paramref name="CentroidHz"/> carry optional evidence so the matcher can
///     reuse its technique-downgrade path (default None/0 ⇒ a plain Hit).
/// </summary>
public readonly record struct ChartVerifyOutcome(
    bool Confirmed, float Confidence,
    LiveTechniqueFlags Techniques = LiveTechniqueFlags.None, float CentroidHz = 0f);

/// <summary>
///     Running score for one live-play session. Rocksmith-style per-song
///     accounting: the denominator is the chart's fixed sound count, and the
///     numbers are always derivable from the per-sound verdicts, so a seek
///     back simply clears verdicts and replays them into fresh totals.
/// </summary>
public sealed class LivePlayStats
{
    public int Hits { get; private set; }
    public int Misses { get; private set; }
    public int Streak { get; private set; }
    public int BestStreak { get; private set; }
    public long Score { get; private set; }

    /// <summary>Hits graded <see cref="LiveTimingTier.Perfect"/>.</summary>
    public int PerfectCount { get; private set; }

    /// <summary>Hits graded <see cref="LiveTimingTier.Great"/>.</summary>
    public int GreatCount { get; private set; }

    /// <summary>Hits graded <see cref="LiveTimingTier.Good"/>.</summary>
    public int GoodCount { get; private set; }

    /// <summary>Total sounds in the chart — fixed per song.</summary>
    public int TotalSounds { get; internal set; }
    public int Judged => Hits + Misses;

    /// <summary>Hits over judged sounds. <b>Zero judged is 0, not 1</b> — see
    /// <see cref="AccuracyMath" />; gate a readout on
    /// <see cref="IsMeasured" /> rather than reading a number that means
    /// nothing yet.</summary>
    public double Accuracy => AccuracyMath.FromCounts(Hits, Misses);

    /// <summary>Whether <see cref="Accuracy" /> means anything yet.</summary>
    public bool IsMeasured => AccuracyMath.IsMeasured(Hits, Misses);
    public double SongProgress => TotalSounds == 0 ? 0.0 : Judged / (double)TotalSounds;
    public int Multiplier => 1 + Math.Min(3, Streak / 10);

    /// <summary>Base score of one hit by timing tier. Untimed hits
    /// (<see cref="LiveTimingTier.None"/>: CQT-confirmed, no onset) pay the full
    /// 100 — identical to pre-tier scoring — so enabling the chart-verifier
    /// mode never deflates a run. A technique mismatch still halves the base
    /// (never misses), on top of the tier weighting. A CQT hit credited before
    /// its onset is reported pays it in two parts (<see cref="CreditFloor"/>).</summary>
    internal static int BaseScore(LiveTimingTier tier) => tier switch
    {
        LiveTimingTier.Great => 80,
        LiveTimingTier.Good => 50,
        _ => 100 // Perfect and untimed (None) hits
    };

    private static int TechniqueScaled(int baseScore, LivePlayVerdict verdict) =>
        verdict == LivePlayVerdict.HitWrongTechnique ? baseScore / 2 : baseScore;

    /// <summary>
    /// What a hit credited BEFORE its grade is known pays at once: the least any grade
    ///     can pay — <see cref="LiveTimingTier.Good" />'s base, halved for a technique mismatch.
    ///     The grade pays the rest (<see cref="Grade" />), so the hit's total is exactly
    ///     <see cref="BaseScore" /> of the tier it ends on and the score never moves DOWN: a later
    ///     grade can only add.
    /// </summary>
    internal static int CreditFloor(LivePlayVerdict verdict) =>
        TechniqueScaled(BaseScore(LiveTimingTier.Good), verdict);

    internal void Apply(LivePlayVerdict verdict, LiveTimingTier tier = LiveTimingTier.None)
    {
        if (verdict == LivePlayVerdict.Miss)
        {
            Misses++;
            Streak = 0;
            return;
        }

        Grade(verdict, tier, Credit(verdict));
    }

    /// <summary>
    ///     Counts a hit whose timing grade comes later: hit, streak and combo now, and
    ///     <see cref="CreditFloor" /> at the multiplier it was earned at — which it returns, for
    ///     <see cref="Grade" />. <see cref="Apply" /> is the two back to back.
    /// </summary>
    internal int Credit(LivePlayVerdict verdict)
    {
        Hits++;
        Streak++;
        BestStreak = Math.Max(BestStreak, Streak);
        var multiplier = Multiplier;
        Score += CreditFloor(verdict) * multiplier;
        return multiplier;
    }

    /// <summary>
    ///     Grades a hit <see cref="Credit" /> counted: its tier, and the rest of its base above the
    ///     floor at the multiplier it was credited at. <see cref="LiveTimingTier.None" /> (its
    ///     window closed with no onset, or the run stopped first) pays the untimed full base.
    /// </summary>
    internal void Grade(LivePlayVerdict verdict, LiveTimingTier tier, int creditMultiplier)
    {
        switch (tier)
        {
            case LiveTimingTier.Perfect: PerfectCount++; break;
            case LiveTimingTier.Great: GreatCount++; break;
            case LiveTimingTier.Good: GoodCount++; break;
        }

        Score += (TechniqueScaled(BaseScore(tier), verdict) - CreditFloor(verdict)) * creditMultiplier;
    }

    /// <summary>Deterministically rebuilds every counter (except
    /// <see cref="BestStreak"/>, which survives as a session-best) by
    /// replaying the recorded verdicts in chart order. A hit still awaiting its
    /// grade is replayed as credited (the floor), and keeps waiting.</summary>
    /// <summary>
    /// A hit credited while its technique was still being judged was paid as a
    ///     technique mismatch; the contour found nothing missing, so this pays the rest at the
    ///     multiplier it was credited at. Before its timing grade it lifts the credit floor; after
    ///     it, the graded base.
    /// </summary>
    internal void UpgradeTechnique(bool awaitingGrade, LiveTimingTier tier, int creditMultiplier) =>
        Score += (awaitingGrade
            ? CreditFloor(LivePlayVerdict.Hit) - CreditFloor(LivePlayVerdict.HitWrongTechnique)
            : TechniqueScaled(BaseScore(tier), LivePlayVerdict.Hit) -
              TechniqueScaled(BaseScore(tier), LivePlayVerdict.HitWrongTechnique)) * creditMultiplier;

    internal void RecomputeFrom(IEnumerable<ExpectedSound> soundsInChartOrder)
    {
        var best = BestStreak;
        Hits = 0; Misses = 0; Streak = 0; Score = 0;
        PerfectCount = 0; GreatCount = 0; GoodCount = 0;
        foreach (var sound in soundsInChartOrder)
        {
            if (sound.PaidVerdict is not { } v) continue;
            if (sound.AwaitingGrade) sound.CreditMultiplier = Credit(v);
            else Apply(v, sound.TimingTier);
        }

        BestStreak = Math.Max(best, BestStreak);
    }

    internal void Reset()
    {
        Hits = 0; Misses = 0; Streak = 0; BestStreak = 0; Score = 0;
        PerfectCount = 0; GreatCount = 0; GoodCount = 0;
    }

    /// <summary>Keeps a session-best streak alive across a matcher reload.</summary>
    internal void PreserveBestStreak(int best) => BestStreak = Math.Max(BestStreak, best);
}
