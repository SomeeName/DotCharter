using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     #643: the attack detector's PROVISIONAL onset — reported ~20 ms after the attack, where the
///     finalized onset that grades a CQT hit is reported ~190 ms after it — times the hit's CUE
///     (tier flash, EARLY/LATE) at once, and the finalized onset then confirms or corrects it.
///     <para>
///         The onset stream is injected by hand in the realistic order: provisional onset, CQT
///         credit (~100 ms after the note), finalized onset. Only the finalized onset ever grades:
///         the score, the tier counters and the timing collector are exactly what they were
///         without the provisional onsets (<see cref="NoStatMoves_WithOrWithoutProvisionalOnsets" />).
///     </para>
/// </summary>
public sealed class LiveNoteMatcherProvisionalOnsetTests
{
    private const double Bpm = 120;
    private const double MsPerBeat = 60000.0 / Bpm;
    private const double ConfirmedAt = 100;     // the CQT confirms ~100 ms after the note

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

    /// <summary>The matcher the app runs in CQT mode, over a verifier that confirms everything.</summary>
    private static LiveNoteMatcher CqtMatcher(Arrangement arr)
    {
        Func<ExpectedSound, ChartVerifyOutcome> confirm = _ => new ChartVerifyOutcome(true, 0.9f);
        var matcher = LiveHitWindows.CreateMatcher(LiveHitWindowPreset.Normal, chartVerify: confirm,
            chartVerifyEarly: confirm);
        Assert.True(matcher.AwaitAttackOnsets);
        matcher.Load(arr, arr.Levels[0], Beats());
        return matcher;
    }

    private static int Midi(int fret) => 40 + fret;

    private static List<LivePlayGrade> Provisional(LiveNoteMatcher matcher, long key, double attackMs)
    {
        var graded = new List<LivePlayGrade>();
        matcher.OfferProvisionalOnset(key, attackMs, graded);
        return graded;
    }

    /// <summary>The analyzer's finalized event for provisional onset <paramref name="key" />.</summary>
    private static List<LivePlayGrade> Finalized(LiveNoteMatcher matcher, int midi, long key, double attackMs)
    {
        var graded = new List<LivePlayGrade>();
        Span<LivePitch> one = [new LivePitch(midi, 0f, 1f)];
        Assert.Null(matcher.OfferPitches(one, attackMs, graded: graded, onsetKey: key));
        return graded;
    }

    /// <summary>The CQT credit: the hit, untimed, paid the floor — exactly as before #643.</summary>
    private static void CreditedAt(LiveNoteMatcher matcher, double playheadMs)
    {
        var credit = Assert.Single(matcher.AdvanceTo(playheadMs));
        Assert.Equal(LivePlayVerdict.Hit, credit.Verdict);
        Assert.Equal(LiveTimingTier.None, credit.Tier);
        Assert.Equal(LiveTimingDirection.None, credit.Direction);
    }

    [Theory]
    [InlineData(30, LiveTimingTier.Perfect, LiveTimingDirection.Late)]
    [InlineData(-60, LiveTimingTier.Great, LiveTimingDirection.Early)]
    [InlineData(120, LiveTimingTier.Good, LiveTimingDirection.Late)]
    public void TheGrade_IsAvailableAtProvisionalTime_WithTheCredit(double deltaMs, LiveTimingTier tier,
        LiveTimingDirection direction)
    {
        var matcher = CqtMatcher(Chart((2, 3)));                         // 1000 ms

        // The detector fires ~20 ms after the attack: nothing is credited yet, so it waits.
        Assert.Empty(Provisional(matcher, key: 7, 1000 + deltaMs));

        // The CQT credits the hit, and the provisional onset times its cue IN THE SAME TICK.
        CreditedAt(matcher, 1000 + ConfirmedAt);
        var cue = Assert.Single(matcher.ProvisionalGrades);
        Assert.Equal(LivePlayGradeStage.Provisional, cue.Stage);
        Assert.Equal(tier, cue.Tier);
        Assert.Equal(direction, cue.Direction);
        Assert.Equal(1000 + deltaMs, cue.AttackAtMs, 6);
        Assert.True(cue.ShowsCue);

        // A cue, not a grade: nothing is paid or counted until the finalized onset.
        Assert.Equal(LivePlayStats.CreditFloor(LivePlayVerdict.Hit), matcher.Stats.Score);
        Assert.Equal(0, matcher.Stats.PerfectCount + matcher.Stats.GreatCount + matcher.Stats.GoodCount);
        Assert.Equal(LiveTimingTier.None, matcher.Expected[0].TimingTier);

        // The finalized onset lands a few ms off on the same side: it CONFIRMS — no second cue.
        var grade = Assert.Single(Finalized(matcher, Midi(3), key: 7, 1000 + deltaMs + 3));
        Assert.Equal(LivePlayGradeStage.Confirmed, grade.Stage);
        Assert.False(grade.ShowsCue);
        Assert.Equal(tier, grade.Tier);
        Assert.Equal(1000 + deltaMs + 3, grade.AttackAtMs, 6);           // the finalized attack is what counts
        Assert.Equal(LivePlayStats.BaseScore(tier), matcher.Stats.Score);
        Assert.Equal(tier, matcher.Expected[0].TimingTier);

        Assert.Empty(matcher.AdvanceTo(2000));
        Assert.Empty(matcher.ProvisionalGrades);
        Assert.Equal(LivePlayStats.BaseScore(tier), matcher.Stats.Score);
    }

    /// <summary>A finalized onset that moves the attack further than the tier width CORRECTS the
    /// cue: the grade carries the finalized tier and side and is shown, and it is what is paid.</summary>
    [Theory]
    [InlineData(10, 80, LiveTimingTier.Great, LiveTimingDirection.Late)]    // Perfect on time → Great late
    [InlineData(-40, -100, LiveTimingTier.Good, LiveTimingDirection.Early)] // Perfect early → Good early
    [InlineData(20, -20, LiveTimingTier.Perfect, LiveTimingDirection.Early)] // same tier, other side
    public void AFinalizedOnsetThatMovesTheAttack_CorrectsTheCue(double provisionalMs, double finalizedMs,
        LiveTimingTier tier, LiveTimingDirection direction)
    {
        var matcher = CqtMatcher(Chart((2, 3)));
        Provisional(matcher, key: 1, 1000 + provisionalMs);
        CreditedAt(matcher, 1000 + ConfirmedAt);
        var cue = Assert.Single(matcher.ProvisionalGrades);

        var grade = Assert.Single(Finalized(matcher, Midi(3), key: 1, 1000 + finalizedMs));

        Assert.True(cue.Tier != grade.Tier || cue.Direction != grade.Direction);
        Assert.Equal(LivePlayGradeStage.Corrected, grade.Stage);
        Assert.True(grade.ShowsCue);
        Assert.Equal(tier, grade.Tier);
        Assert.Equal(direction, grade.Direction);
        Assert.Equal(LivePlayStats.BaseScore(tier), matcher.Stats.Score);
        Assert.Equal(tier, matcher.Expected[0].TimingTier);
    }

    /// <summary>A hit credited BEFORE its provisional onset arrives (a slow detector, a fast CQT)
    /// gets its cue when the onset does.</summary>
    [Fact]
    public void AProvisionalOnsetAfterTheCredit_CuesTheAwaitingHit()
    {
        var matcher = CqtMatcher(Chart((2, 3)));
        CreditedAt(matcher, 1000 + ConfirmedAt);
        Assert.Empty(matcher.ProvisionalGrades);

        var cue = Assert.Single(Provisional(matcher, key: 4, 1050));
        Assert.Equal(LivePlayGradeStage.Provisional, cue.Stage);
        Assert.Equal(LiveTimingTier.Great, cue.Tier);
        Assert.Equal(LivePlayStats.CreditFloor(LivePlayVerdict.Hit), matcher.Stats.Score);

        // ONE cue per hit: a second provisional onset (a double trigger) times nothing.
        Assert.Empty(Provisional(matcher, key: 5, 1060));

        Assert.Equal(LivePlayGradeStage.Confirmed, Assert.Single(Finalized(matcher, Midi(3), key: 4, 1052)).Stage);
    }

    /// <summary>A withdrawn provisional onset (the analyzer rejected it) times nothing.</summary>
    [Fact]
    public void AWithdrawnProvisionalOnset_TimesNothing()
    {
        var matcher = CqtMatcher(Chart((2, 3)));
        Provisional(matcher, key: 9, 1020);
        matcher.WithdrawProvisionalOnset(9);

        CreditedAt(matcher, 1000 + ConfirmedAt);
        Assert.Empty(matcher.ProvisionalGrades);
    }

    /// <summary>
    ///     The finalized event retires ITS provisional onset by key. Here the finalized onset of
    ///     the note at 1000 ms arrives before the CQT credits it (it times the credit directly),
    ///     and a leftover provisional onset at 1000 ms would sit inside the early window of the
    ///     NEXT note, at 1125 ms, and cue it "early" from another note's attack.
    /// </summary>
    [Fact]
    public void AFinalizedEvent_RetiresItsProvisionalOnset()
    {
        var matcher = CqtMatcher(Chart((2, 3), (2.25, 5)));             // 1000 ms, 1125 ms
        Provisional(matcher, key: 1, 1000);
        Assert.Empty(Finalized(matcher, Midi(3), key: 1, 1001));        // nothing awaiting yet

        var credits = matcher.AdvanceTo(1000 + ConfirmedAt);
        Assert.Equal(LiveTimingTier.Perfect, Assert.Single(credits).Tier); // timed by the finalized onset
        Assert.Empty(matcher.ProvisionalGrades);

        CreditedAt(matcher, 1125 + ConfirmedAt);
        Assert.Empty(matcher.ProvisionalGrades);                          // no stale provisional onset cues it
    }

    /// <summary>A seek forgets every provisional onset and every cue: a kept hit resolves untimed,
    /// as before.</summary>
    [Fact]
    public void ASeek_ForgetsProvisionalOnsets()
    {
        var matcher = CqtMatcher(Chart((2, 3), (6, 5)));                  // 1000 ms, 3000 ms
        Provisional(matcher, key: 1, 3010);
        matcher.Rewind(2900);
        CreditedAt(matcher, 3000 + ConfirmedAt);
        Assert.Empty(matcher.ProvisionalGrades);
    }

    /// <summary>The classic matcher (no CQT) never awaits a grade, so it ignores provisional onsets.</summary>
    [Fact]
    public void TheClassicMatcher_IgnoresProvisionalOnsets()
    {
        var arr = Chart((2, 3));
        var matcher = LiveHitWindows.CreateMatcher(LiveHitWindowPreset.Normal);
        matcher.Load(arr, arr.Levels[0], Beats());
        Assert.False(matcher.AwaitAttackOnsets);

        Assert.Empty(Provisional(matcher, key: 1, 1010));
        Assert.Empty(matcher.AdvanceTo(1000 + ConfirmedAt));
        Assert.Empty(matcher.ProvisionalGrades);
    }

    /// <summary>
    ///     The guarantee that keeps every hit rate where it was: the same onset stream with and
    ///     without its provisional onsets ends on the SAME stats — score, hits, every tier count,
    ///     every sound's tier — confirmed, corrected, withdrawn and unconfirmed cues included.
    /// </summary>
    [Fact]
    public void NoStatMoves_WithOrWithoutProvisionalOnsets()
    {
        var chart = Chart((2, 3), (3, 5), (4, 3), (5, 7), (6, 3));       // 1000 … 3000 ms

        static LiveNoteMatcher Play(Arrangement arr, bool provisional)
        {
            var matcher = CqtMatcher(arr);

            void P(long key, double ms)
            {
                if (provisional) matcher.OfferProvisionalOnset(key, ms);
            }

            void F(int fret, long key, double ms)
            {
                Span<LivePitch> one = [new LivePitch(Midi(fret), 0f, 1f)];
                matcher.OfferPitches(one, ms, onsetKey: key);
            }

            P(1, 1010);
            matcher.AdvanceTo(1100);
            F(3, 1, 1012);                                                // confirmed
            P(2, 1510);
            matcher.AdvanceTo(1600);
            F(5, 2, 1580);                                                // corrected
            P(3, 2005);
            matcher.AdvanceTo(2100);                                      // never finalized: untimed
            if (provisional) matcher.WithdrawProvisionalOnset(3);
            P(4, 2520);
            matcher.AdvanceTo(2600);
            F(7, 4, 2522);
            matcher.AdvanceTo(3100);
            F(3, 5, 3030);                                                // no provisional at all
            matcher.AdvanceTo(5000);
            return matcher;
        }

        var with = Play(chart, provisional: true);
        var without = Play(chart, provisional: false);

        Assert.Equal(without.Stats.Score, with.Stats.Score);
        Assert.Equal(without.Stats.Hits, with.Stats.Hits);
        Assert.Equal(without.Stats.PerfectCount, with.Stats.PerfectCount);
        Assert.Equal(without.Stats.GreatCount, with.Stats.GreatCount);
        Assert.Equal(without.Stats.GoodCount, with.Stats.GoodCount);
        Assert.Equal(without.Expected.Select(s => s.TimingTier), with.Expected.Select(s => s.TimingTier));
        Assert.Equal(5, with.Stats.Hits);
        Assert.Equal(LiveTimingTier.None, with.Expected[2].TimingTier); // a cue no finalized onset confirmed is never paid
    }
}
