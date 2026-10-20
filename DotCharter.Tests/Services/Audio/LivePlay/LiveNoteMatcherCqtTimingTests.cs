using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     #329: a CQT-confirmed hit is graded Perfect/Great/Good (and signed early/late) from the
///     attack onset the input analyzer reported, the evidence the classic path grades from.
///     <para>
///         The order matters and is the realistic one: the CQT confirms a note ~100 ms after its
///         chart time, the analyzer REPORTS its onset ~190 ms after the attack
///         (<c>JuceGuitarAnalyzer.LatencySamples</c>). So the hit is CREDITED at confirmation —
///         shown, streak, combo, exactly when it was before #329 — untimed, and the onset GRADES
///         it when it is reported (<see cref="LivePlayGrade" />;
///         <see cref="LiveNoteMatcher.AwaitAttackOnsets" />, on in the factory's CQT matcher). One
///         no onset grades stays untimed when its window closes.
///     </para>
///     <para>
///         Score: <see cref="LivePlayStats.CreditFloor" /> (50) at the credit, the rest of the
///         tier's base at the grade — or of the untimed 100 at the close — so the score never
///         moves down.
///     </para>
/// </summary>
public sealed class LiveNoteMatcherCqtTimingTests
{
    private const double Bpm = 120;
    private const double MsPerBeat = 60000.0 / Bpm;

    // LiveCqtConfig.EarlyCreditAfterMs is 90: from then on the early check is asked.
    private const double ConfirmedAt = 100;

    private static List<BeatMarker> Beats(int count = 32)
        => [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * MsPerBeat, Bpm = Bpm })];

    private static Arrangement Chart(params (double Beat, int Fret)[] notes)
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        arr.Levels.Add(new ChartLevel());
        foreach (var (beat, fret) in notes)
        {
            var whole = (int)Math.Floor(beat);
            var quarters = (int)Math.Round((beat - whole) * 4);
            arr.Levels[0].Notes.Add(new GuitarNote
            {
                Position = new FractionalPosition(whole, new Fraction(quarters, 4)),
                Fret = fret,
                StringIndex = 0
            });
        }

        return arr;
    }

    /// <summary>The matcher the app runs in CQT mode (the Normal preset), over a verifier
    /// that confirms every pitched sound.</summary>
    private static LiveNoteMatcher CqtMatcher(Arrangement arr, LiveHitWindowPreset preset = LiveHitWindowPreset.Normal,
        float centroidHz = 0f)
    {
        Func<ExpectedSound, ChartVerifyOutcome> confirm = _ => new ChartVerifyOutcome(true, 0.9f, CentroidHz: centroidHz);
        var matcher = LiveHitWindows.CreateMatcher(preset, chartVerify: confirm, chartVerifyEarly: confirm);
        matcher.Load(arr, arr.Levels[0], Beats());
        return matcher;
    }

    // E2 + fret on the low E string.
    private static int Midi(int fret) => 40 + fret;

    /// <summary>The analyzer reporting an attack: returns its grades (and asserts it judged nothing new).</summary>
    private static List<LivePlayGrade> Onset(LiveNoteMatcher matcher, int midi, double attackMs)
    {
        var graded = new List<LivePlayGrade>();
        Assert.Null(matcher.OfferNote(midi, attackMs, graded: graded));
        return graded;
    }

    /// <summary>The credit, at confirmation: shown and counted right then, untimed, the floor paid.</summary>
    private static LivePlayEvent CreditedAt(LiveNoteMatcher matcher, double playheadMs)
    {
        var credit = Assert.Single(matcher.AdvanceTo(playheadMs));
        Assert.Equal(LivePlayVerdict.Hit, credit.Verdict);
        Assert.Equal(LiveTimingTier.None, credit.Tier);
        Assert.Equal(LiveTimingDirection.None, credit.Direction);
        Assert.Equal(playheadMs, credit.DetectedAtMs, 6);
        return credit;
    }

    [Theory]
    [InlineData(30, LiveTimingTier.Perfect, LiveTimingDirection.Late)]   // ≤ 45
    [InlineData(-60, LiveTimingTier.Great, LiveTimingDirection.Early)]   // ≤ 95
    [InlineData(120, LiveTimingTier.Good, LiveTimingDirection.Late)]     // inside the 250 ms window
    public void AConfirmedSound_IsCreditedAtConfirmation_AndGradedByTheOnsetReportedAfterIt(double onsetDeltaMs,
        LiveTimingTier tier, LiveTimingDirection direction)
    {
        var matcher = CqtMatcher(Chart((2, 3)));                         // 1000 ms

        // The CQT confirms first: the hit counts NOW — before #329's fix it waited for the onset.
        CreditedAt(matcher, 1000 + ConfirmedAt);
        Assert.Equal(1, matcher.Stats.Hits);
        Assert.Equal(1, matcher.Stats.Streak);
        Assert.Equal(LivePlayStats.CreditFloor(LivePlayVerdict.Hit), matcher.Stats.Score);
        Assert.Equal(LiveTimingTier.None, matcher.Expected[0].TimingTier);

        // The analyzer reports the attack ~190 ms after it happened: it grades, it does not judge.
        var grade = Assert.Single(Onset(matcher, Midi(3), 1000 + onsetDeltaMs));

        Assert.Equal(tier, grade.Tier);
        Assert.Equal(direction, grade.Direction);
        Assert.Equal(1000 + onsetDeltaMs, grade.AttackAtMs, 6);          // the attack, as on the classic path
        Assert.Equal(tier, matcher.Expected[0].TimingTier);
        Assert.Equal(LivePlayStats.BaseScore(tier), matcher.Stats.Score);
        Assert.Equal(1, matcher.Stats.PerfectCount + matcher.Stats.GreatCount + matcher.Stats.GoodCount);
        Assert.Equal(1, matcher.Stats.Hits);                              // still ONE hit

        Assert.Empty(matcher.AdvanceTo(2000));                            // never judged or paid twice
        Assert.Equal(1, matcher.Stats.Judged);
        Assert.Equal(LivePlayStats.BaseScore(tier), matcher.Stats.Score);
    }

    /// <summary>No onset in the window: the hit, credited at confirmation, stays untimed — tier
    /// None, no direction — and is paid the rest of the untimed 100 when its window closes, with
    /// nothing new emitted (it was shown when it was credited).</summary>
    [Fact]
    public void AHitNoOnsetGrades_StaysUntimed_AndIsPaidTheRestWhenItsWindowCloses()
    {
        var matcher = CqtMatcher(Chart((2, 3)));

        CreditedAt(matcher, 1000 + ConfirmedAt);
        Assert.Empty(matcher.AdvanceTo(1000 + matcher.HitWindowLateMs - 1));
        Assert.Equal(50, matcher.Stats.Score);
        Assert.Empty(matcher.AdvanceTo(1000 + matcher.HitWindowLateMs + 1));

        Assert.Equal(100, matcher.Stats.Score);
        Assert.Equal(1, matcher.Stats.Hits);
        Assert.Equal(0, matcher.Stats.PerfectCount + matcher.Stats.GreatCount + matcher.Stats.GoodCount);
        Assert.Empty(Onset(matcher, Midi(3), 1000 + 30));                 // too late to grade it now
    }

    /// <summary>An attack grades on the preset's own late edge (250 ms), not on the CQT's
    /// verdict horizon (~378 ms): an onset 300 ms late times nothing, exactly as it would hit
    /// nothing on the classic path.</summary>
    [Fact]
    public void AnOnsetPastThePresetsLateEdge_TimesNothing()
    {
        var matcher = CqtMatcher(Chart((2, 3)));
        Assert.True(matcher.HitWindowLateMs > 300, "the CQT horizon is past the attack edge");

        CreditedAt(matcher, 1000 + ConfirmedAt);
        Assert.Empty(Onset(matcher, Midi(3), 1300));
        Assert.Empty(matcher.AdvanceTo(1000 + matcher.HitWindowLateMs + 1));

        Assert.Equal(LiveTimingTier.None, matcher.Expected[0].TimingTier);
        Assert.Equal(100, matcher.Stats.Score);
    }

    /// <summary>An onset reported BEFORE the CQT confirms (an early attack) is kept, and the
    /// confirmation is credited graded at once — nothing left to wait for.</summary>
    [Fact]
    public void AnOnsetAlreadyReported_GradesTheConfirmationAtOnce()
    {
        var matcher = CqtMatcher(Chart((2, 3)));

        Assert.Empty(Onset(matcher, Midi(3), 960));                       // nothing confirmed yet
        var hit = Assert.Single(matcher.AdvanceTo(1000 + ConfirmedAt));

        Assert.Equal(LiveTimingTier.Perfect, hit.Tier);
        Assert.Equal(LiveTimingDirection.Early, hit.Direction);
        Assert.Equal(960, hit.DetectedAtMs, 6);
        Assert.Equal(100, matcher.Stats.Score);
    }

    /// <summary>
    ///     A player consistently 70 ms late through a 16th-note run (125 ms apart): every onset
    ///     sits NEARER the next note than its own, so nearest-alone hands it to the next note and
    ///     calls a dragging player early. The onset's pitch candidates name its own note, which is
    ///     what keeps each one there (<see cref="LiveAttackOnsets.PitchMismatchPenaltyMs" />).
    /// </summary>
    [Fact]
    public void AConsistentlyLatePlayer_IsTimedAgainstTheirOwnNotes_InAFastRun()
    {
        var matcher = CqtMatcher(Chart((2, 3), (2.25, 5), (2.5, 7)));    // 1000, 1125, 1250 ms

        CreditedAt(matcher, 1000 + ConfirmedAt);
        CreditedAt(matcher, 1125 + ConfirmedAt);
        CreditedAt(matcher, 1250 + ConfirmedAt);

        var grades = new List<LivePlayGrade>();
        grades.AddRange(Onset(matcher, Midi(3), 1070));
        grades.AddRange(Onset(matcher, Midi(5), 1195));
        grades.AddRange(Onset(matcher, Midi(7), 1320));

        Assert.Equal(3, grades.Count);
        Assert.All(grades, g =>
        {
            Assert.Equal(70, g.AttackAtMs - g.Sound.TimeMs, 6);
            Assert.Equal(LiveTimingDirection.Late, g.Direction);
        });
    }

    /// <summary>One onset times ONE attack: a second, later note that never got an onset of
    /// its own is not graded from the first note's.</summary>
    [Fact]
    public void AnOnset_TimesOnlyTheAttackItBelongsTo()
    {
        var matcher = CqtMatcher(Chart((2, 3), (2.25, 3)));               // same pitch, 125 ms apart

        CreditedAt(matcher, 1000 + ConfirmedAt);
        Assert.Single(Onset(matcher, Midi(3), 1010));
        CreditedAt(matcher, 1125 + ConfirmedAt);
        Assert.Empty(matcher.AdvanceTo(1125 + matcher.HitWindowLateMs + 1));

        Assert.Equal(LiveTimingTier.None, matcher.Expected[1].TimingTier);
        Assert.Equal(200, matcher.Stats.Score);                           // Perfect 100 + untimed 100
    }

    /// <summary>A double stop the chart spells as two notes is ONE attack: its onset grades both.</summary>
    [Fact]
    public void ADoubleStop_IsGradedByItsOneOnset()
    {
        var arr = Chart((2, 3));
        arr.Levels[0].Notes.Add(new GuitarNote { Position = new FractionalPosition(2), Fret = 2, StringIndex = 1 });
        var matcher = CqtMatcher(arr);
        var also = new List<LivePlayEvent>();
        var graded = new List<LivePlayGrade>();

        Assert.Equal(2, matcher.AdvanceTo(1000 + ConfirmedAt).Count);    // both credited at confirmation
        Assert.Null(matcher.OfferPitches([new LivePitch(Midi(3), 0f, 1f)], 1020, alsoJudged: also, graded: graded));

        Assert.Empty(also);
        Assert.Equal(2, graded.Count);
        Assert.All(graded, g => Assert.Equal(LiveTimingTier.Perfect, g.Tier));
        Assert.Equal(2, matcher.Stats.PerfectCount);
        Assert.Equal(2, matcher.Stats.Hits);
    }

    /// <summary>A seek BACK over a credited hit clears it like any judgment there
    /// (Rocksmith's rewind semantics), its pending grade with it, and forgets every onset
    /// heard before the seek: they belong to playing that is being replayed.</summary>
    [Fact]
    public void ARewindOverACreditedHit_ClearsItAndItsPendingGrade()
    {
        var matcher = CqtMatcher(Chart((2, 3)));
        CreditedAt(matcher, 1000 + ConfirmedAt);

        matcher.Rewind(playheadMs: 500);

        // The old onset arrives late: nothing is waiting for it any more.
        Assert.Empty(Onset(matcher, Midi(3), 1030));
        Assert.Equal(0, matcher.Stats.Judged);
        Assert.Equal(0, matcher.Stats.Score);
    }

    /// <summary>A seek that KEEPS a hit still waiting for its onset (forward, past its
    /// window) keeps it credited and resolves it untimed: the hit is never dropped.</summary>
    [Fact]
    public void ASeekThatKeepsAWaitingHit_ResolvesItUntimed()
    {
        var matcher = CqtMatcher(Chart((2, 3)));
        CreditedAt(matcher, 1000 + ConfirmedAt);

        matcher.Rewind(playheadMs: 3000);

        Assert.Equal(1, matcher.Stats.Hits);
        Assert.Equal(100, matcher.Stats.Score);
        Assert.Equal(LiveTimingTier.None, matcher.Expected[0].TimingTier);
        Assert.Empty(Onset(matcher, Midi(3), 1030));
    }

    /// <summary>Stopping (the session calls <see cref="LiveNoteMatcher.ResolvePendingGrades" />)
    /// resolves a waiting hit untimed: credited, full untimed base, no grade afterwards.</summary>
    [Fact]
    public void ResolvingPendingGrades_PaysTheUntimedRest()
    {
        var matcher = CqtMatcher(Chart((2, 3)));
        CreditedAt(matcher, 1000 + ConfirmedAt);

        matcher.ResolvePendingGrades();

        Assert.Equal(1, matcher.Stats.Hits);
        Assert.Equal(100, matcher.Stats.Score);
        Assert.Empty(Onset(matcher, Midi(3), 1030));
        Assert.Equal(100, matcher.Stats.Score);
    }

    /// <summary>A wrong-technique hit is halved in both parts: 25 at the credit, the rest of
    /// Great's halved 40 at the grade.</summary>
    [Fact]
    public void AWrongTechniqueHit_IsHalvedInBothParts()
    {
        var arr = Chart((2, 3));
        arr.Levels[0].Notes[0].Mute = MuteType.Palm;                      // charted muted, played bright
        var matcher = CqtMatcher(arr, centroidHz: 5200f);

        var credit = Assert.Single(matcher.AdvanceTo(1000 + ConfirmedAt));
        Assert.Equal(LivePlayVerdict.HitWrongTechnique, credit.Verdict);
        Assert.Equal(25, matcher.Stats.Score);

        Assert.Single(Onset(matcher, Midi(3), 1060));                     // Great
        Assert.Equal(40, matcher.Stats.Score);
    }

    /// <summary>
    ///     The score scheme over a whole run: whatever order credits, grades and window closes
    ///     come in, the score NEVER moves down, and it ends exactly where scoring each hit at
    ///     once with its final tier (at the multiplier it was credited at) would.
    /// </summary>
    [Fact]
    public void TheScore_NeverMovesDown_AndEndsWhereImmediateScoringWould()
    {
        var notes = Enumerable.Range(0, 40).Select(i => (Beat: 2.0 + i * 0.5, Fret: 3 + (i % 5))).ToArray();
        var matcher = CqtMatcher(Chart(notes));
        var reference = new LivePlayStats();
        var last = 0L;

        void Check()
        {
            Assert.True(matcher.Stats.Score >= last, $"score moved down: {last} -> {matcher.Stats.Score}");
            last = matcher.Stats.Score;
        }

        for (var i = 0; i < notes.Length; i++)
        {
            var chartMs = notes[i].Beat * MsPerBeat;
            matcher.AdvanceTo(chartMs + ConfirmedAt);
            Check();
            // Every third note's onset never comes; the others land at varied offsets.
            if (i % 3 != 2) Onset(matcher, Midi(notes[i].Fret), chartMs + (i % 4 * 40) - 30);
            Check();
        }

        matcher.AdvanceTo(notes[^1].Beat * MsPerBeat + 2000);
        Check();

        foreach (var sound in matcher.Expected) reference.Apply(sound.Verdict!.Value, sound.TimingTier);
        Assert.Equal(reference.Score, matcher.Stats.Score);
        Assert.Equal(reference.PerfectCount, matcher.Stats.PerfectCount);
        Assert.Equal(reference.GreatCount, matcher.Stats.GreatCount);
        Assert.Equal(reference.GoodCount, matcher.Stats.GoodCount);
        Assert.True(matcher.Stats.PerfectCount + matcher.Stats.GreatCount + matcher.Stats.GoodCount >= 20,
            "too few grades — the run is not exercising the scheme");
    }

    /// <summary>The factory grades from onsets in CQT mode only, and grades attacks on the
    /// preset's own late edge while the verdict horizon stays the CQT's.</summary>
    [Theory]
    [InlineData(LiveHitWindowPreset.Lenient, 350.0)]
    [InlineData(LiveHitWindowPreset.Normal, 250.0)]
    [InlineData(LiveHitWindowPreset.Strict, 160.0)]
    public void TheFactory_AwaitsOnsetsInCqtModeOnly(LiveHitWindowPreset preset, double attackLateMs)
    {
        Func<ExpectedSound, ChartVerifyOutcome> v = _ => new ChartVerifyOutcome(true, 1f);
        var cqt = LiveHitWindows.CreateMatcher(preset, chartVerify: v, chartVerifyEarly: v);
        var classic = LiveHitWindows.CreateMatcher(preset);

        Assert.True(cqt.AwaitAttackOnsets);
        Assert.Equal(attackLateMs, cqt.AttackWindowLateMs);
        Assert.False(classic.AwaitAttackOnsets);
        Assert.Equal(classic.HitWindowLateMs, classic.AttackWindowLateMs);
    }

    /// <summary>
    ///     Now that CQT runs carry real timings, the latency calibrator may learn from them — the
    ///     deltas are the attack detector's onsets, the same evidence a classic run teaches it
    ///     with. A CQT run consistently 40 ms late moves the offset up, the way a classic run
    ///     does; the untimed credits (and untimed hits) still teach nothing
    ///     (<c>LiveTimingAnalyticsTests.AnAllUntimedRun_TeachesTheLatencyCalibratorNothing</c>).
    /// </summary>
    [Fact]
    public void ATimedCqtRun_TeachesTheLatencyCalibrator()
    {
        var notes = Enumerable.Range(0, 30).Select(i => (Beat: 2.0 + i, Fret: 3 + (i % 5))).ToArray();
        var matcher = CqtMatcher(Chart(notes));
        var collector = new LiveTimingCollector();

        for (var i = 0; i < notes.Length; i++)
        {
            var chartMs = notes[i].Beat * MsPerBeat;
            foreach (var ev in matcher.AdvanceTo(chartMs + ConfirmedAt)) collector.Add(ev);
            Assert.Equal(i, collector.Count);                             // an untimed credit counts nothing
            foreach (var grade in Onset(matcher, Midi(notes[i].Fret), chartMs + 40 + (i % 3)))
                collector.Add(grade.AsTimedHit());
        }

        var report = collector.BuildReport();
        var decision = LatencyBiasCalibrator.Evaluate(currentOffsetMs: 20, baselineOffsetMs: 20, timing: report);

        Assert.Equal(30, report.HitCount);
        Assert.Equal(41, report.MedianMs, 0);
        Assert.True(decision.Adjusted, decision.Reason);
        Assert.True(decision.NewOffsetMs > 20, decision.Reason);
    }
}
