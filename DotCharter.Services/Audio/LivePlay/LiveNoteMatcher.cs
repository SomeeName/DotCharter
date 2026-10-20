namespace DotCharter.Services.Audio.LivePlay;

/// <summary>
///     Chart-aware matcher for live guitar input. Because the chart already
///     says what should sound and when, live detection is a verification
///     problem, not blind transcription: each expected note/chord opens a
///     hit window around its chart time, and detected pitch candidates
///     inside that window judge it.
///     <para>
///     v6 is VERIFICATION-FIRST: a hit requires the expected pitch itself
///     (octave-tolerant for known detector errors) to be a sufficiently
///     SALIENT candidate of the attack — weak candidates (ring-out of
///     earlier strings, harmonic leakage) don't count, single notes have no
///     chroma shortcut, chords need real coverage of their tones, and
///     pitchless sounds (mutes, slaps, pops) require the attack to actually
///     LOOK percussive via its technique flags.
///     </para>
///     <para>
///     PITCH is the gate; TECHNIQUE is lenient. Correct playing must be able
///     to reach 100%: physics-shaped tolerance stays (slides accept the
///     traversed range, bends the reached range, harmonics their overtone
///     pitches), palm-muted sounds relax the salience requirement once the
///     analyzer confirms the damping (a chug has little pitch left to
///     extract), legato transitions use a softer floor than picked attacks,
///     and a technique mismatch can at worst halve the score — never miss.
///     </para>
///     Pure and UI-free so the scoring rules are unit-testable.
/// </summary>
public sealed class LiveNoteMatcher
{
    private const LiveTechniqueFlags PercussiveEvidence =
        LiveTechniqueFlags.Percussive | LiveTechniqueFlags.PalmMute |
        LiveTechniqueFlags.Slap | LiveTechniqueFlags.Pop;

    private readonly List<ExpectedSound> _expected = [];
    private int _cursor;

    /// <summary>How early a correct attack may land and still count (ms).
    /// Detection timestamps are sample-accurate against the transport
    /// (capture/host clock sync), so the window covers PLAYER timing only.</summary>
    public double HitWindowEarlyMs { get; init; } = 150;

    /// <summary>How late a correct attack may land and still count (ms).</summary>
    public double HitWindowLateMs { get; init; } = 250;

    /// <summary>|detected − expected| at or under which a hit grades
    /// <see cref="LiveTimingTier.Perfect"/>. Event times entering the matcher
    /// are already latency-offset-compensated by the caller (the UI subtracts
    /// the calibrated round-trip before <c>Offer</c>), so this measures
    /// player timing only.</summary>
    public double PerfectWindowMs { get; init; } = 45;

    /// <summary>|detected − expected| at or under which a hit grades
    /// <see cref="LiveTimingTier.Great"/>; anything later (but still inside
    /// the hit window) grades <see cref="LiveTimingTier.Good"/>.</summary>
    public double GreatWindowMs { get; init; } = 95;

    /// <summary>Minimum normalized salience for a candidate to verify a
    /// single note — ring-out and harmonic leakage sit below this.</summary>
    public float MinNoteSalience { get; init; } = 0.25f;

    /// <summary>Minimum normalized salience for a candidate to count as one
    /// chord tone (chords split energy across strings, so slightly lower).</summary>
    public float MinChordToneSalience { get; init; } = 0.20f;

    /// <summary>Salience floor for hammer-on/pull-off notes: legato
    /// transitions put far less energy into the string than a pick, so the
    /// right pitch legitimately shows up softer.</summary>
    public float MinLegatoSalience { get; init; } = 0.15f;

    /// <summary>Fraction of a chord's distinct tones that must be verified.</summary>
    public double ChordCoverage { get; init; } = 0.6;

    /// <summary>Absolute chroma energy share a chord tone must reach in the
    /// chroma FALLBACK path (used only when pitch candidates missed).
    /// v7.1: 0.10 → 0.08 — a 4-5-pitch-class chord plus broadband attack
    /// spreads shares thin; honest strums sat just under the old bar.</summary>
    public float ChordChromaThreshold { get; init; } = 0.08f;

    /// <summary>Relative-to-frame-max acceptance for the chroma fallback.
    /// v7.1: 0.5 → 0.35 — the root class routinely towers over the rest,
    /// so half-of-max rejected legitimate mid-voicing tones.</summary>
    public float ChordChromaRelative { get; init; } = 0.35f;

    /// <summary>Spectral centroid below which an attack reads as palm-muted
    /// (legacy verification for callers that pass no technique flags).</summary>
    public float PalmMuteCentroidHz { get; init; } = 2600f;

    /// <summary>v7: how strictly chord voicings must be reproduced. Strict
    /// requires every distinct tone at its exact pitch and disables the
    /// chroma fallback; Lenient halves the coverage requirement.</summary>
    public LiveChordVoicingStrictness ChordStrictness { get; init; } = LiveChordVoicingStrictness.Standard;

    /// <summary>v7: string-aware verification. The chart knows the string, so
    /// the drop-pedal-aware pitch map (tuning + capo + the engine's input
    /// pitch shift all live in front of detection) gives the EXACT sounding
    /// pitch — octave tolerance for single notes is dropped, only physically
    /// legitimate variants (slide/bend range, harmonic overtones) remain.</summary>
    public bool StringAwareVerification { get; init; }

    /// <summary>Semitones every expected pitch moves: a song shifted to the player's
    /// tuning sounds the charted frets that much higher. 0 = as charted.</summary>
    public int TransposeSemitones { get; init; }

    /// <summary>Detection v8 (CQT chart-verifier) mode. When set, judging is
    /// PULL-based: instead of matching pushed pitch candidates, each expected
    /// sound is verified against the rolling quarter-tone CQT when its hit
    /// window closes (in <see cref="AdvanceTo"/>). The candidate paths
    /// (<see cref="OfferPitches"/>/<see cref="OfferChroma"/>) short-circuit so
    /// the two evidence models never both judge a sound — but the attack ONSETS
    /// offered there still time a confirmed hit (<see cref="LiveAttackOnsets"/>).
    /// Null = the classic candidate-match path.</summary>
    public Func<ExpectedSound, ChartVerifyOutcome>? ChartVerify { get; init; }

    /// <summary>True when running in the CQT chart-verifier mode.</summary>
    public bool ChartVerifyMode => ChartVerify != null;

    /// <summary>
    ///     CQT mode's EARLY check: asked on every tick for a due, still-open pitched sound, and
    ///     a confirmation credits it at once (<see cref="CreditConfirmedEarly" />). It must demand
    ///     more than <see cref="ChartVerify" /> — evidence of a fresh attack, not mere presence —
    ///     because it gets several chances where the final check gets one. Null = hits are
    ///     credited when the window closes.
    /// </summary>
    public Func<ExpectedSound, ChartVerifyOutcome>? ChartVerifyEarly { get; init; }

    /// <summary>CQT mode: a charted pinch harmonic may ALSO be credited from pushed attack
    /// evidence (<see cref="OfferPitches" />), not only by the CQT. See
    /// <c>LiveCqtConfig.PinchHarmonicAttackEvidence</c>.</summary>
    public bool PinchHarmonicAttackEvidence { get; init; }

    /// <summary>The early check is not asked before this many ms after a sound's time.</summary>
    public double EarlyCreditAfterMs { get; init; }

    /// <summary>Consecutive ticks the early check must confirm before an early credit.</summary>
    public int EarlyCreditConfirmations { get; init; } = 1;

    /// <summary>
    ///     How late an ATTACK may land and still time a hit. Null = <see cref="HitWindowLateMs" />,
    ///     which it is in the classic mode; in CQT mode that is the verdict horizon (~378 ms, the
    ///     frames the CQT needs), and this stays the preset's own attack edge, so an attack
    ///     grades on the same window in both modes.
    /// </summary>
    public double? AttackWindowLateMs { get; init; }

    /// <summary>
    /// CQT mode: a confirmed pitched sound is credited AT ONCE, untimed, and the onset
    ///     that times it GRADES it when the analyzer reports it (~190 ms after the attack; the
    ///     CQT confirms ~100 ms after the note) — a <see cref="LivePlayGrade" />. One no onset
    ///     grades before its window closes, the run stops or seeks stays untimed. Paid in two
    ///     parts so the score never moves down: <see cref="LivePlayStats.CreditFloor" />, then
    ///     the rest. Off, only an onset already reported can time it. See
    ///     <see cref="LiveAttackOnsets" />.
    ///     <para>
    /// On, the detector's PROVISIONAL onsets (<see cref="OfferProvisionalOnset" />)
    ///         also time an awaiting hit — for its CUE only: a <see cref="LivePlayGradeStage.Provisional" />
    ///         grade at the credit (<see cref="ProvisionalGrades" />) or when the onset arrives, and
    ///         the finalized onset then confirms or corrects it. The score, the tier counters and
    ///         the timing collector stay the finalized onset's, so no stat moves.
    ///     </para>
    /// </summary>
    public bool AwaitAttackOnsets { get; init; }

    /// <summary>
    /// CQT mode, behind the setting: rules on a credited single note's bend, slide or
    ///     vibrato from its pitch contour — <see cref="LiveCqtVerifier.JudgeTechnique" /> in the
    ///     app; null while its audio has not arrived. A NotMet only downgrades the hit (half
    ///     score), Unknown is a full hit; see <see cref="LiveTechniqueChecks" />. Null = off, which
    ///     is today's judging exactly.
    /// </summary>
    public Func<ExpectedSound, TechniqueContourVerdict?>? JudgeContour { get; init; }

    /// <summary>Which techniques <see cref="JudgeContour" /> rules on: the player's
    /// per-technique switches. A technique left out is never judged — a sound that asks only
    /// for it is a plain hit, and one that asks for it with another is ruled on the other
    /// alone. All by default; the app passes the settings.</summary>
    public ContourTechniques JudgedTechniques { get; init; } = ContourTechniques.All;

    /// <summary>The contour rulings the last <see cref="AdvanceTo" /> made. Reused —
    /// consume before calling it again.</summary>
    public IReadOnlyList<LiveTechniqueJudgment> TechniqueJudgments => _techniqueChecks.Resolved;

    /// <summary>The provisional grades the last <see cref="AdvanceTo" /> gave hits it credited:
    /// a hit a provisional onset already times shows its tier and EARLY/LATE with the
    /// "Hit". Cues only — see <see cref="LivePlayGradeStage.Provisional" />. Reused — consume
    /// before calling it again.</summary>
    public IReadOnlyList<LivePlayGrade> ProvisionalGrades => _provisionalGrades;

    private readonly LiveAttackOnsets _onsets = new();

    // The detector's provisional onsets, kept apart: they time cues, never grades.
    private readonly LiveAttackOnsets _provisionalOnsets = new();
    private readonly List<LivePlayGrade> _provisionalGrades = [];
    private readonly LiveTechniqueChecks _techniqueChecks = new();

    private double TimingLateEdgeMs => AttackWindowLateMs ?? HitWindowLateMs;

    public LivePlayStats Stats { get; } = new();

    public IReadOnlyList<ExpectedSound> Expected => _expected;

    /// <summary>
    ///     Chart time through which every sound is already resolved and can never
    ///     be judged again this run — the cursor horizon (<c>index &lt; _cursor</c>
    ///     ⇔ resolved). Drives the DIMMED "dead note" rendering during a
    ///     resume-rewind replay, where the playhead sits BEHIND this horizon.
    ///     <see cref="double.NegativeInfinity" /> when nothing is judged.
    /// </summary>
    public double ResolvedThroughMs =>
        _cursor > 0 ? _expected[_cursor - 1].TimeMs : double.NegativeInfinity;

    private readonly LiveExpectedTimeline _timeline = new();

    /// <summary>Builds the expected timeline from one arrangement level
    /// (whole song at a single difficulty).</summary>
    public void Load(Arrangement arrangement, ChartLevel level, IList<BeatMarker> beats)
    {
        _cursor = 0;
        Stats.Reset();
        _techniqueChecks.Clear();
        _timeline.Build(_expected, arrangement, level, beats, TransposeSemitones);
        Stats.TotalSounds = _expected.Count;
    }

    /// <summary>
    ///     Builds the expected timeline phrase by phrase, asking
    ///     <paramref name="selectLevel"/> which difficulty level to load for
    ///     each phrase iteration (Rocksmith-style leveled play). Charts
    ///     without phrase data fall back to one whole-song span at the
    ///     highest available level. See <see cref="LiveExpectedTimeline" />.
    /// </summary>
    public void Load(Arrangement arrangement, IList<BeatMarker> beats, LiveDifficultySelector selectLevel)
    {
        _cursor = 0;
        Stats.Reset();
        _techniqueChecks.Clear();
        _timeline.Build(_expected, arrangement, beats, selectLevel, TransposeSemitones);
        Stats.TotalSounds = _expected.Count;
    }

    // Phrase iterations the playhead has already reached, and the level they
    // were loaded at. Reused — Reload runs at phrase boundaries, not per tick.
    private readonly Dictionary<int, int> _frozenLevels = [];

    /// <summary>
    ///     Rebuilds the expected timeline with new per-phrase levels WITHOUT
    ///     losing the run: verdicts already judged before the playhead's
    ///     horizon are carried over (matched by time/string/kind), the stats
    ///     are replayed from them and the cursor lands back on the horizon.
    ///     This is what lets dynamic difficulty swap LATER iterations of a
    ///     phrase to a new level mid-song.
    ///     <para>
    ///         <b>Only later ones.</b> `DynamicDifficultyEngine` keys levels by
    ///         phrase NAME, so the iteration whose own accuracy just moved the
    ///         ladder is rebuilt by that move as well — and rebuilding
    ///         every span at the phrase's current level, the played ones
    ///         included, would be wrong. Levelling UP would make notes appear BEHIND the cursor, which
    ///         can never be judged: `TotalSounds` would grow, coverage fall (a run can
    ///         drop under `PracticeProfileService.MinCoverageForBestAccuracy`
    ///         that way) and a full combo become unreachable. Levelling DOWN
    ///         would delete notes the player had already HIT, so `Hits` and `Score`
    ///         would go DOWN mid-run — the scoreboard moving backwards over playing
    ///         that never happened. Any iteration the playhead has reached keeps
    ///         the level it was played at.
    ///     </para>
    /// </summary>
    public void Reload(Arrangement arrangement, IList<BeatMarker> beats,
        LiveDifficultySelector selectLevel, double playheadMs)
    {
        var horizon = playheadMs - HitWindowLateMs;
        var judged = new Dictionary<(double TimeMs, int StringIndex, bool IsChord),
            (LivePlayVerdict Verdict, LiveTimingTier Tier)>();
        var frozen = _frozenLevels;
        frozen.Clear();
        foreach (var sound in _expected)
        {
            if (sound.Verdict is { } v && sound.TimeMs < horizon)
                judged.TryAdd((sound.TimeMs, sound.StringIndex, sound.IsChord), (v, sound.TimingTier));
            // The whole iteration is frozen the moment ANY of it is behind the
            // horizon: a difficulty change part-way through one phrase would add
            // or remove notes under the player's hands.
            if (sound.PhraseIterationIndex >= 0 && sound.TimeMs < horizon)
                frozen[sound.PhraseIterationIndex] = sound.LevelIndex;
        }

        var bestStreak = Stats.BestStreak;

        Load(arrangement, beats, span => frozen.TryGetValue(span.IterationIndex, out var played)
            ? played
            : selectLevel(span));

        foreach (var sound in _expected)
        {
            // A carried hit that was still awaiting its onset arrives untimed (its tier
            // None, nothing awaiting on the fresh sound): resolved, never lost.
            if (judged.TryGetValue((sound.TimeMs, sound.StringIndex, sound.IsChord), out var j))
            {
                sound.Verdict = j.Verdict;
                sound.TimingTier = j.Tier;
            }
        }

        Stats.RecomputeFrom(_expected);
        Stats.PreserveBestStreak(bestStreak);

        _cursor = 0;
        while (_cursor < _expected.Count && _expected[_cursor].TimeMs < horizon) _cursor++;
    }

    /// <summary>
    ///     Repositions after a seek, Rocksmith-style: every verdict at or
    ///     past the new playhead's judgement horizon is cleared and the
    ///     region is replayed, so seeking back resets your progress there
    ///     instead of adding more notes to the totals. Verdicts before the
    ///     target are kept.
    /// </summary>
    public void Rewind(double playheadMs)
    {
        var horizon = playheadMs - HitWindowLateMs;
        foreach (var sound in _expected)
        {
            // A kept hit's technique check ends Unknown, a full hit.
            _techniqueChecks.Forget(sound, verdictCleared: sound.TimeMs >= horizon);
            if (sound.TimeMs >= horizon)
            {
                sound.Verdict = null;
                sound.TimingTier = LiveTimingTier.None;
                sound.EarlyConfirmStreak = 0;
            }

            // A hit kept across the seek that was still waiting for its onset stays
            // credited and resolves untimed: its onset can never arrive.
            sound.AwaitingGrade = false;
            sound.ProvisionalAttackMs = double.NaN;
        }

        // Onsets heard before a seek belong to another part of the song.
        _onsets.Clear();
        _provisionalOnsets.Clear();
        _techniqueChecks.Clear();
        Stats.RecomputeFrom(_expected);

        _cursor = 0;
        while (_cursor < _expected.Count && _expected[_cursor].TimeMs < horizon) _cursor++;
    }

    /// <summary>
    ///     Re-arms an A/B loop pass: every sound from <paramref name="loopStartMs" />
    ///     onward is unjudged again and the cursor parks exactly there.
    ///     <para>
    ///         <b>Not <see cref="Rewind" />, and the difference is the whole
    ///         point.</b> Rewind's horizon is <c>playhead − HitWindowLateMs</c>,
    ///         because a seek lands the player mid-window and the sounds around
    ///         the landing must be earnable again. A loop pass is the opposite
    ///         situation: the region is a hard boundary the player chose, and
    ///         `LivePlaySessionService` refuses to judge anything before it while
    ///         the count-in pre-roll runs. So re-arming the hit-window band just
    ///         BEFORE the loop start handed those sounds back to a run that could
    ///         not possibly earn them — <c>AdvanceTo</c> expired every one of them
    ///         as the pre-roll ended, into the run's accuracy, the "you missed
    ///         these" list and (which no rewind undoes) the rock meter's drain,
    ///         once per pass. They stay outside the loop and stay unjudged.
    ///     </para>
    /// </summary>
    public void RewindToLoop(double loopStartMs)
    {
        foreach (var sound in _expected)
        {
            _techniqueChecks.Forget(sound, verdictCleared: sound.TimeMs >= loopStartMs);
            if (sound.TimeMs >= loopStartMs)
            {
                sound.Verdict = null;
                sound.TimingTier = LiveTimingTier.None;
                sound.EarlyConfirmStreak = 0;
            }

            sound.AwaitingGrade = false; // as in Rewind: kept hits resolve untimed
            sound.ProvisionalAttackMs = double.NaN;
        }

        _onsets.Clear();
        _provisionalOnsets.Clear();
        _techniqueChecks.Clear();
        Stats.RecomputeFrom(_expected);

        _cursor = 0;
        while (_cursor < _expected.Count && _expected[_cursor].TimeMs < loopStartMs) _cursor++;
    }

    // Reused across AdvanceTo calls — the 30 Hz tick almost always returns
    // it empty, so allocating a fresh list per call was pure GC churn.
    private readonly List<LivePlayEvent> _advanceEvents = [];

    /// <summary>
    ///     Moves the judgement horizon forward and emits a miss for every
    ///     expected sound whose hit window closed without a matching attack.
    ///     Call ONLY while the transport is actually playing — judging a
    ///     stationary or scrubbed playhead manufactures phantom misses.
    ///     The returned list is REUSED by the next call — consume it before
    ///     calling AdvanceTo again (the matcher is single-threaded).
    /// </summary>
    public List<LivePlayEvent> AdvanceTo(double playheadMs)
    {
        var events = _advanceEvents;
        events.Clear();
        _provisionalGrades.Clear();
        while (_cursor < _expected.Count)
        {
            var sound = _expected[_cursor];
            if (sound.TimeMs + HitWindowLateMs >= playheadMs) break;
            if (sound.Verdict == null)
            {
                // CQT mode: a pitched sound still open is pull-verified against
                // the rolling CQT now. Pitchless sounds need timbre evidence
                // (OfferPitches), so they are never verified.
                var o = ChartVerify is { } verify && !LivePitchAcceptance.NeedsTimbreEvidence(sound) ? verify(sound) : (ChartVerifyOutcome?)null;
                if (o is { Confirmed: true } c)
                {
                    // The window is closing: no onset reported later may grade it.
                    events.Add(CreditConfirmed(sound, JudgeTechnique(sound, c.Techniques, c.CentroidHz),
                        c.Confidence, playheadMs, mayAwaitGrade: false));
                }
                else
                {
                    // A miss has no attack timestamp: it IS the window expiring.
                    // The tier stays None and the direction must too. Classify is
                    // called rather than defaulted so the gate is exercised:
                    // signing `playheadMs - sound.TimeMs` would label every miss a
                    // ~250 ms "late".
                    sound.Verdict = LivePlayVerdict.Miss;
                    sound.TimingTier = LiveTimingTier.None;
                    Stats.Apply(LivePlayVerdict.Miss);
                    // What the verifier did find rides along for the monitor; it decides nothing.
                    events.Add(new LivePlayEvent(LivePlayVerdict.Miss, sound, playheadMs, 0f, LiveTimingTier.None,
                        LiveTiming.Classify(LivePlayVerdict.Miss, LiveTimingTier.None, playheadMs - sound.TimeMs))
                    { MissEvidence = o?.Confidence });
                }
            }
            else if (sound.AwaitingGrade)
            {
                // Credited at confirmation, and no onset graded it before its window
                // closed: it stays untimed and is paid the rest of the untimed base.
                // Nothing is emitted — it was shown when it was credited.
                ResolveUntimed(sound);
            }

            _cursor++;
        }

        CreditConfirmedEarly(playheadMs, events);
        if (JudgeContour is { } judge) _techniqueChecks.ResolveDue(playheadMs, judge, Stats);
        // No open sound's window reaches back past the cursor's.
        var pruneBeforeMs = _cursor < _expected.Count
            ? _expected[_cursor].TimeMs - HitWindowEarlyMs
            : double.PositiveInfinity;
        _onsets.PruneBefore(pruneBeforeMs);
        _provisionalOnsets.PruneBefore(pruneBeforeMs);
        return events;
    }

    /// <summary>
    /// Ends every grade still pending: each of those hits stays credited and is graded
    ///     untimed (<see cref="LiveTimingTier.None" />, the rest of the untimed base). Called when
    ///     the run stops, so a hit credited within the onset's reporting delay of the end is
    ///     neither lost nor left half-paid.
    /// </summary>
    public void ResolvePendingGrades()
    {
        // ...and every technique still being judged is ruled on with what was heard.
        _techniqueChecks.ResolveAll(JudgeContour, Stats);
        for (var i = _cursor; i < _expected.Count; i++)
        {
            if (_expected[i].AwaitingGrade) ResolveUntimed(_expected[i]);
        }
    }

    private void ResolveUntimed(ExpectedSound sound)
    {
        // A provisional cue no finalized onset confirmed stays on screen; the stats stay untimed.
        sound.AwaitingGrade = false;
        sound.ProvisionalAttackMs = double.NaN;
        Stats.Grade(sound.PaidVerdict!.Value, LiveTimingTier.None, sound.CreditMultiplier);
    }

    /// <summary>
    ///     Credits a CQT-confirmed sound, graded from the reported attack onset that best times
    ///     it (<see cref="LiveAttackOnsets" />) — or untimed when none can. The CQT confirms WHAT
    ///     was played, never WHEN: with no onset the tier is None and the event carries the
    ///     playhead, which <see cref="LiveTiming.Classify" /> refuses to sign. A timed one carries
    ///     its attack time, exactly as a classic-path hit does.
    ///     <para>
    ///         With <see cref="AwaitAttackOnsets" /> and <paramref name="mayAwaitGrade" />, an
    ///         untimed credit is provisional: it pays <see cref="LivePlayStats.CreditFloor" />
    ///         now and waits for its onset (<see cref="GradeAwaitingHits" />). Otherwise it is
    ///         untimed for good and pays the full untimed base (see <see cref="LivePlayStats" />).
    ///         An awaiting hit a provisional onset already times gets its cue now
    /// (<see cref="ProvisionalGrades" />).
    ///     </para>
    /// </summary>
    private LivePlayEvent CreditConfirmed(ExpectedSound sound, LivePlayVerdict verdict, float confidence,
        double playheadMs, bool mayAwaitGrade)
    {
        var timed = _onsets.TryClaimBest(sound, HitWindowEarlyMs, TimingLateEdgeMs, out var atMs);
        if (!timed) atMs = playheadMs;
        var tier = timed ? ClassifyTiming(atMs - sound.TimeMs) : LiveTimingTier.None;
        sound.Verdict = verdict;
        sound.TimingTier = tier;
        sound.AwaitingGrade = !timed && AwaitAttackOnsets && mayAwaitGrade;
        // A technique still to be judged is paid as a mismatch until it is.
        if (LiveTechniqueChecks.Wants(sound, verdict, JudgeContour != null, JudgedTechniques))
            _techniqueChecks.Add(sound, JudgedTechniques);
        var paid = sound.PaidVerdict!.Value;
        sound.CreditMultiplier = Stats.Credit(paid);
        // The streak the meters saw this hit at, so a later downgrade takes back exactly what
        // the hit gave over a half hit.
        sound.CreditStreak = Stats.Streak;
        if (!sound.AwaitingGrade) Stats.Grade(paid, tier, sound.CreditMultiplier);
        else if (_provisionalOnsets.TryClaimBest(sound, HitWindowEarlyMs, TimingLateEdgeMs, out var provisionalMs))
            _provisionalGrades.Add(StampProvisional(sound, provisionalMs));
        return new LivePlayEvent(verdict, sound, atMs, confidence, tier,
            LiveTiming.Classify(verdict, tier, atMs - sound.TimeMs));
    }

    /// <summary>Times an awaiting hit's CUE from a provisional onset. Pays nothing.</summary>
    private LivePlayGrade StampProvisional(ExpectedSound sound, double attackMs)
    {
        sound.ProvisionalAttackMs = attackMs;
        return GradeOf(sound, attackMs, LivePlayGradeStage.Provisional);
    }

    private LivePlayGrade GradeOf(ExpectedSound sound, double attackMs, LivePlayGradeStage stage)
    {
        var verdict = sound.Verdict!.Value;
        var tier = ClassifyTiming(attackMs - sound.TimeMs);
        return new LivePlayGrade(sound, verdict, attackMs, tier, LiveTiming.Classify(verdict, tier, attackMs - sound.TimeMs))
        {
            Stage = stage
        };
    }

    /// <summary>
    /// CQT mode: the attack detector just FIRED at <paramref name="timeMs" /> — a
    ///     provisional onset, ~170 ms before the analyzed event that finalizes it reaches
    ///     <see cref="OfferPitches" /> with the same <paramref name="key" /> (its detector position).
    ///     It is kept for a hit the CQT has not credited yet (<see cref="CreditConfirmed" /> looks
    ///     here when no finalized onset times it) and otherwise gives the awaiting hit it times best
    ///     — with every awaiting hit at that chart time — a <see cref="LivePlayGradeStage.Provisional" />
    ///     cue in <paramref name="graded" />. Never a judgment, never a grade: no stat moves. A
    ///     no-op unless <see cref="AwaitAttackOnsets" />.
    /// </summary>
    public void OfferProvisionalOnset(long key, double timeMs, List<LivePlayGrade>? graded = null)
    {
        if (!AwaitAttackOnsets) return;
        var onset = _provisionalOnsets.Add(timeMs, [], key);
        if (_provisionalOnsets.ClaimForAwaitingHit(onset, _expected, _cursor, HitWindowEarlyMs, TimingLateEdgeMs,
                untimedOnly: true) is not { } best)
        {
            return;
        }

        for (var i = _cursor; i < _expected.Count; i++)
        {
            var sound = _expected[i];
            if (sound.TimeMs - best.TimeMs > SimultaneousToleranceMs) break;
            if (!sound.AwaitingGrade || !double.IsNaN(sound.ProvisionalAttackMs) ||
                best.TimeMs - sound.TimeMs > SimultaneousToleranceMs)
            {
                continue;
            }

            var grade = StampProvisional(sound, timeMs);
            graded?.Add(grade);
        }
    }

    /// <summary>The provisional onset <paramref name="key" /> will produce no event: the
    /// analyzer rejected it, or reset before finalizing it. It times nothing further; a cue it
    /// already gave stays (the finalized grade, if one comes, still confirms or corrects it).</summary>
    public void WithdrawProvisionalOnset(long key) => _provisionalOnsets.RemoveKey(key);

    /// <summary>
    ///     CQT mode: credit a pitched sound the moment the verifier CONFIRMS it, instead of
    ///     when its hit window closes. The window's late edge is 250 ms, so every CQT hit used
    ///     to land a quarter of a second after the player played it; Rocksmith shows a hit as
    ///     it is played, and so does this now. Every due, still-open sound is re-asked on each
    ///     tick — the verifier answers "not yet" until the audio for it has arrived, so nothing
    ///     is credited before it is heard — and only a MISS still waits for the window to close
    ///     (<see cref="AdvanceTo" />'s loop above). The verdict is the one the close would give
    ///     from the same evidence: the confirmation window is chart-relative, so asking earlier
    ///     can only see less of it, never different audio.
    ///     <para>With <see cref="AwaitAttackOnsets" /> the hit is credited here even when its
    /// attack onset has not been reported yet — it nearly never has — and that onset
    ///     grades it when it is (<see cref="GradeAwaitingHits" />). The credit must never wait
    ///     for the grade: holding it for the onset showed every CQT hit ~200 ms after the attack
    ///     instead of ~110 ms.</para>
    ///     <para>Ordering: in a fast run the NEXT sound can be credited before the previous one
    ///     closes as a miss — the streak then breaks one sound later than strict order would give, which is
    ///     how a game that shows hits as they happen behaves.</para>
    /// </summary>
    private void CreditConfirmedEarly(double playheadMs, List<LivePlayEvent> events)
    {
        if (ChartVerifyEarly is not { } verify) return;
        for (var i = _cursor; i < _expected.Count; i++)
        {
            var sound = _expected[i];
            if (sound.TimeMs + EarlyCreditAfterMs > playheadMs) break;
            if (sound.Verdict != null || LivePitchAcceptance.NeedsTimbreEvidence(sound)) continue;

            var o = verify(sound);
            if (!o.Confirmed)
            {
                sound.EarlyConfirmStreak = 0;
                continue;
            }

            if (++sound.EarlyConfirmStreak < EarlyCreditConfirmations) continue;

            events.Add(CreditConfirmed(sound, JudgeTechnique(sound, o.Techniques, o.CentroidHz), o.Confidence,
                playheadMs, mayAwaitGrade: true));
        }
    }

    /// <summary>
    ///     CQT mode: an attack onset was just reported. It is kept for sounds the CQT has not
    ///     confirmed yet — claimed already if the pushed path judged a sound with it — and
    ///     otherwise GRADES the credited hit awaiting its grade that it times best, together with
    ///     every awaiting hit at that same chart time. Each grade goes to
    ///     <paramref name="graded" />; none is a new judgment. It retires its own provisional onset
    /// (<paramref name="onsetKey" />), and a hit that one gave a cue is
    ///     <see cref="LivePlayGradeStage.Confirmed" /> or <see cref="LivePlayGradeStage.Corrected" />.
    /// </summary>
    private void GradeAwaitingHits(ReadOnlySpan<LivePitch> pitches, double timeMs, LivePlayEvent? first,
        List<LivePlayGrade>? graded, long onsetKey)
    {
        _provisionalOnsets.RemoveKey(onsetKey);
        var onset = _onsets.Add(timeMs, pitches);
        if (first is { } judged)
        {
            _onsets.Claim(onset, judged.Sound.TimeMs);
            return;
        }

        if (_onsets.ClaimForAwaitingHit(onset, _expected, _cursor, HitWindowEarlyMs, TimingLateEdgeMs) is not { } best)
            return;
        for (var i = _cursor; i < _expected.Count; i++)
        {
            var sound = _expected[i];
            if (sound.TimeMs - best.TimeMs > SimultaneousToleranceMs) break;
            if (!sound.AwaitingGrade || best.TimeMs - sound.TimeMs > SimultaneousToleranceMs) continue;

            var grade = GradeOf(sound, timeMs, LivePlayGradeStage.Final);
            if (!double.IsNaN(sound.ProvisionalAttackMs))
            {
                // The cue already shown from the provisional onset: does the finalized attack
                // land in the same tier on the same side?
                var shown = GradeOf(sound, sound.ProvisionalAttackMs, LivePlayGradeStage.Provisional);
                grade = grade with
                {
                    Stage = shown.Tier == grade.Tier && shown.Direction == grade.Direction
                        ? LivePlayGradeStage.Confirmed
                        : LivePlayGradeStage.Corrected
                };
                sound.ProvisionalAttackMs = double.NaN;
            }

            sound.AwaitingGrade = false;
            sound.TimingTier = grade.Tier;
            Stats.Grade(sound.PaidVerdict!.Value, grade.Tier, sound.CreditMultiplier);
            graded?.Add(grade);
        }
    }

    /// <summary>
    ///     Chart times no further apart than this are ONE attack — a double stop,
    ///     an octave, a chord the chart happens to spell as separate notes. See
    ///     <see cref="OfferPitches" />. Deliberately tight: two notes a beat
    ///     subdivision apart are two attacks and must be earned separately.
    /// </summary>
    public const double SimultaneousToleranceMs = 1.0;

    /// <summary>
    ///     Offers one detected attack/legato event: its salience-ranked pitch
    ///     candidates plus the analyzer's technique classification.
    ///     <para>
    ///         <b>One attack may resolve EVERY expected sound at the same chart
    ///         time</b> (within <see cref="SimultaneousToleranceMs" />), not just
    ///         the first. A double stop — two strings struck together — is ONE
    ///         onset carrying both pitches, but the chart may spell it as two
    ///         separate <c>note</c>s rather than as a chord, and RS-XML, EOF and
    ///         the Guitar Pro importers all do exactly that. Judging only
    ///         the first and <c>return</c>ing would leave the second sound never offered
    ///         anything else (there is no second onset), so it would sit unjudged until
    ///         <see cref="AdvanceTo" /> expires it. A perfectly played double stop
    ///         would therefore score 50% and BREAK THE STREAK, every time, and the
    ///         multiplier with it.
    ///     </para>
    ///     <para>
    ///         Each sibling is still verified on its own evidence, so a double stop
    ///         where only one string sounded still misses the other. The extra
    ///         judgments are appended to <paramref name="alsoJudged" /> when the
    ///         caller wants to surface them; the score, the streak and the section
    ///         tallies are correct whether it passes one or not.
    ///     </para>
    ///     <para>
    ///         CQT mode: the onset may instead GRADE hits already credited
    ///         (<see cref="AwaitAttackOnsets" />). Those are not judgments — they go to
    ///         <paramref name="graded" />, and the stats are graded whether or not the
    ///         caller passes it. <paramref name="onsetKey" /> is the detector position of the
    /// provisional onset this event finalizes (-1 = none).
    ///     </para>
    /// </summary>
    public LivePlayEvent? OfferPitches(ReadOnlySpan<LivePitch> pitches, double timeMs,
        LiveTechniqueFlags techniques = LiveTechniqueFlags.None, float centroidHz = 0f,
        List<LivePlayEvent>? alsoJudged = null, List<LivePlayGrade>? graded = null, long onsetKey = -1)
    {
        LivePlayEvent? first = null;

        // Inlined hit-window walk (was a WindowAt yield-iterator — one
        // state-machine allocation per detected attack).
        for (var i = _cursor; i < _expected.Count; i++)
        {
            var sound = _expected[i];
            if (sound.TimeMs - HitWindowEarlyMs > timeMs) break;
            // _expected is sorted by TimeMs, so once we are past the first
            // judgment's own chart time we have left its attack behind.
            if (first is { } judged && sound.TimeMs - judged.Sound.TimeMs > SimultaneousToleranceMs) break;
            if (sound.TimeMs + HitWindowLateMs < timeMs) continue;
            if (sound.Verdict != null) continue;
            // CQT mode is a hybrid: pitched sounds are pull-verified against the
            // rolling CQT in AdvanceTo (this onset only TIMES them, below); only
            // pitchless sounds (mutes, unpitched slides) still need this pushed
            // percussive/timbre evidence.
            if (ChartVerifyMode && !LivePitchAcceptance.NeedsTimbreEvidence(sound) &&
                !(PinchHarmonicAttackEvidence && sound.PinchHarmonic))
            {
                continue;
            }

            if (!Verifies(sound, pitches, techniques, out var confidence)) continue;

            var verdict = JudgeTechnique(sound, techniques, centroidHz);
            var deltaMs = timeMs - sound.TimeMs;
            var tier = ClassifyTiming(deltaMs);
            sound.Verdict = verdict;
            sound.TimingTier = tier;
            Stats.Apply(verdict, tier);
            var ev = new LivePlayEvent(verdict, sound, timeMs, confidence, tier,
                LiveTiming.Classify(verdict, tier, deltaMs));
            if (first == null) first = ev;
            else alsoJudged?.Add(ev);
        }

        if (ChartVerifyMode) GradeAwaitingHits(pitches, timeMs, first, graded, onsetKey);
        return first;
    }

    /// <summary>Offers a single detected pitch (legacy/legato path) — treated
    /// as a lone full-salience candidate.</summary>
    public LivePlayEvent? OfferNote(int midi, double timeMs, float centroidHz = 0f,
        List<LivePlayGrade>? graded = null)
    {
        Span<LivePitch> one = [new LivePitch(midi, 0f, 1f)];
        return OfferPitches(one, timeMs, LiveTechniqueFlags.None, centroidHz, graded: graded);
    }

    /// <summary>
    ///     Chroma FALLBACK for chords only: when the multi-F0 stage missed
    ///     tones (dense voicings, doubled strings), a chord may still verify
    ///     via pitch-class energy — under stricter thresholds than v5, and
    ///     never for single notes (that shortcut hit wrong notes constantly).
    /// </summary>
    public LivePlayEvent? OfferChroma(ReadOnlySpan<float> chroma, double timeMs, float centroidHz = 0f,
        LiveTechniqueFlags techniques = LiveTechniqueFlags.None)
    {
        if (ChartVerifyMode) return null;
        if (chroma.Length != 12) return null;
        // Strict voicing practice: pitch-class energy can't prove a VOICING
        // (it collapses octaves), so the fallback is off entirely.
        if (ChordStrictness == LiveChordVoicingStrictness.Strict) return null;

        var maxChroma = 0f;
        foreach (var c in chroma)
        {
            if (c > maxChroma)
                maxChroma = c;
        }

        for (var i = _cursor; i < _expected.Count; i++)
        {
            var sound = _expected[i];
            if (sound.TimeMs - HitWindowEarlyMs > timeMs) break;
            if (sound.TimeMs + HitWindowLateMs < timeMs) continue;
            if (sound.Verdict != null || !sound.IsChord || LivePitchAcceptance.PitchIsUnreliable(sound)) continue;

            // Same percussive veto as the pitch path: a chug's broadband
            // chroma spills energy into every pitch class, so without this
            // the fallback would clear clean chords under muted strumming.
            if (!sound.PalmMute && (techniques & PercussiveEvidence) != 0) continue;

            var classes = _chromaClasses;
            classes.Clear();
            foreach (var m in sound.MidiNotes) classes.Add(((m % 12) + 12) % 12);

            var present = 0;
            foreach (var pc in classes)
            {
                if (chroma[pc] >= ChordChromaThreshold ||
                    (maxChroma > 0f && chroma[pc] >= maxChroma * ChordChromaRelative))
                {
                    present++;
                }
            }

            if (present < Math.Ceiling(classes.Count * EffectiveChordCoverage())) continue;

            var verdict = JudgeTechnique(sound, techniques, centroidHz);
            var deltaMs = timeMs - sound.TimeMs;
            var tier = ClassifyTiming(deltaMs);
            sound.Verdict = verdict;
            sound.TimingTier = tier;
            Stats.Apply(verdict, tier);
            return new LivePlayEvent(verdict, sound, timeMs, present / (float)classes.Count, tier,
                LiveTiming.Classify(verdict, tier, deltaMs));
        }

        return null;
    }

    /// <summary>Timing grade from the signed detected−expected delta (ms).
    /// Symmetric: early and late count alike.</summary>
    private LiveTimingTier ClassifyTiming(double deltaMs)
    {
        var abs = Math.Abs(deltaMs);
        if (abs <= PerfectWindowMs) return LiveTimingTier.Perfect;
        return abs <= GreatWindowMs ? LiveTimingTier.Great : LiveTimingTier.Good;
    }

    // Reused per OfferChroma candidate — chords have ≤6 notes.
    private readonly HashSet<int> _chromaClasses = [];

    private double EffectiveChordCoverage() => ChordStrictness switch
    {
        LiveChordVoicingStrictness.Lenient => Math.Min(ChordCoverage, 0.5),
        LiveChordVoicingStrictness.Strict => 1.0,
        _ => ChordCoverage
    };

    private bool Verifies(ExpectedSound sound, ReadOnlySpan<LivePitch> pitches, LiveTechniqueFlags techniques,
        out float confidence)
    {
        confidence = 1f;

        // Pitchless sounds: the attack itself must LOOK percussive/damped —
        // a cleanly pitched attack where a chuck/slap belongs is not a match.
        // (Slap/pop notes still carry pitch, so a pitched match also counts.)
        if (sound.FullMute || (sound.IsChord && sound.MidiNotes.Length == 0))
            return (techniques & PercussiveEvidence) != 0;

        if (sound.UnpitchedSlide)
            return pitches.Length > 0 || (techniques & PercussiveEvidence) != 0;

        if (sound.MidiNotes.Length == 0)
            return sound.SlapOrPop && (techniques & PercussiveEvidence) != 0;

        // v6.2 PERCUSSIVE VETO: an attack the analyzer classified as
        // percussive/damped (muted chug, scratch, slap, pop) must never
        // verify a clean pitched sound. Broadband noise still yields pitch
        // candidates — salience is normalized to the strongest candidate,
        // so a noise-only frame always carries a 1.0-salience candidate
        // that can land inside the accepted set by chance. The attack's
        // own timbre gates first: chugs only count where the chart wants
        // something muted or transient-heavy.
        if (!sound.PalmMute && !sound.SlapOrPop && (techniques & PercussiveEvidence) != 0)
            return false;

        // Palm mutes damp the string, so the pitch evidence is legitimately
        // weak. Once the analyzer CONFIRMS the damping, a matching candidate
        // counts at any salience — and a fully choked attack that yields no
        // candidates at all still verifies, because a hard chug has no pitch
        // left to extract. Wrong-pitch candidates never verify: leniency is
        // on the salience, not on which note was played.
        var damped = sound.PalmMute &&
                     (techniques & (LiveTechniqueFlags.PalmMute | LiveTechniqueFlags.Percussive)) != 0;
        if (damped && pitches.IsEmpty) return true;

        if (sound.IsChord)
        {
            // Distinct chord tones, each verified by a sufficiently salient
            // candidate within an octave (detector octave errors are real).
            // Strict voicing practice drops the octave tolerance AND the
            // secondary-tone leniency: the exact pitch of every distinct
            // tone must be fully salient.
            //
            // v7.1: a real strum splits energy very unevenly — the bass
            // string dominates the multi-F0 stage and upper tones come back
            // at a fraction of its salience. Requiring EVERY counted tone at
            // the full floor made honestly played chords miss, so outside
            // Strict: (a) only the strongest tone needs the full floor,
            // additional tones count at half of it; (b) a chord of 3+ tones
            // also verifies root-anchored — correct bass tone plus any other
            // chord tone — because wrong chords almost always differ in the
            // bass while dense voicings hide their middle tones.
            var strict = ChordStrictness == LiveChordVoicingStrictness.Strict;
            // Palm-muted chords: same rationale as palm-muted notes below —
            // halve the tone floor even without analyzer-confirmed damping.
            var toneFloor = damped ? 0f :
                sound.PalmMute ? MinChordToneSalience * 0.5f : MinChordToneSalience;
            var weakFloor = strict ? toneFloor : toneFloor * 0.5f;
            var octaveTolerant = !strict;
            var distinct = new HashSet<int>(sound.MidiNotes);
            var rootMidi = int.MaxValue;
            foreach (var m in distinct) rootMidi = Math.Min(rootMidi, m);

            var present = 0;
            var strong = 0;
            var rootPresent = false;
            foreach (var m in distinct)
            {
                foreach (var p in pitches)
                {
                    if (p.Salience < weakFloor) continue;
                    // Same physics-shaped tolerance the single-note path has always
                    // had: the octave (detector error), the range a chord slide
                    // traverses, the range a bend reaches, and — for a harmonic
                    // strum — the overtone the node actually rings at. Before this,
                    // a chord tone only ever matched EXACTLY or at ±12, so a
                    // natural-harmonic chord (which sounds at +19/+24, not +12)
                    // could not verify a single one of its tones.
                    if (!LivePitchAcceptance.Accepts(sound, m, p.Midi, octaveTolerant)) continue;
                    present++;
                    if (p.Salience >= toneFloor) strong++;
                    if (m == rootMidi) rootPresent = true;
                    break;
                }
            }

            // A damped chug rarely exposes more than its lowest string, so
            // one verified tone is enough once the damping itself is real.
            var needed = damped ? 1 : (int)Math.Ceiling(distinct.Count * EffectiveChordCoverage());
            var anchored = strong >= 1 || damped;
            var verified = damped
                ? present >= 1
                : anchored && (present >= Math.Max(1, needed) ||
                               (!strict && rootPresent && distinct.Count >= 3 && present >= 2));
            if (verified)
            {
                confidence = present / (float)distinct.Count;
                return true;
            }

            return sound.SlapOrPop && (techniques & PercussiveEvidence) != 0;
        }

        // Single note: the expected pitch (or a physically legitimate variant
        // — slide range, bend range, harmonic overtone, octave error) must be
        // a salient candidate of THIS attack. Legato transitions land softer
        // than picked attacks and get the lower floor. String-aware mode
        // knows the exact sounding pitch of the chart string (tuning + capo
        // + drop pedal all sit in front of detection) and drops the octave
        // tolerance for notes with a known string.
        // Palm-muted chart notes are legitimately pitch-weak even when the
        // analyzer did NOT confirm the damping (short decay defeats the
        // percussive classifier on soft mutes) — use the legato floor for
        // them instead of the full note floor. Wrong pitches still never
        // verify; leniency is on the salience only.
        var floor = damped ? 0f :
            sound.PalmMute ? MinLegatoSalience :
            sound.Legato ? MinLegatoSalience : MinNoteSalience;
        var octaves = !(StringAwareVerification && sound.StringIndex >= 0);
        foreach (var p in pitches)
        {
            if (p.Salience < floor) continue;
            var matched = false;
            foreach (var m in sound.MidiNotes)
            {
                if (LivePitchAcceptance.Accepts(sound, m, p.Midi, octaves))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched) continue;
            confidence = p.Salience;
            return true;
        }

        return sound.SlapOrPop && (techniques & PercussiveEvidence) != 0;
    }

    private LivePlayVerdict JudgeTechnique(ExpectedSound sound, LiveTechniqueFlags techniques, float centroidHz)
    {
        // Technique can only downgrade a hit (half score), never turn it
        // into a miss. Palm mutes are the one judged case, and only on
        // POSITIVE contrary evidence — an attack that is spectrally bright
        // where a mute belongs. A merely missing flag stays a full Hit:
        // analyzer false negatives must not punish correct playing.
        if (!sound.PalmMute) return LivePlayVerdict.Hit;
        if ((techniques & (LiveTechniqueFlags.PalmMute | LiveTechniqueFlags.Percussive)) != 0)
            return LivePlayVerdict.Hit;
        return centroidHz > PalmMuteCentroidHz ? LivePlayVerdict.HitWrongTechnique : LivePlayVerdict.Hit;
    }
}
