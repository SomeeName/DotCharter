using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     The contour judge inside the matcher (#645): a credited bend, slide or vibrato waits for
///     its ruling WITHOUT delaying the hit, a NotMet only downgrades it (half score, never a
///     miss), Unknown is a full hit, the score never moves down while it waits, and with no
///     judge wired the matcher judges exactly as it did before.
/// </summary>
public sealed class LiveNoteMatcherTechniqueContourTests
{
    private static readonly ChartVerifyOutcome Confirmed = new(true, 0.9f);

    private static List<BeatMarker> Beats() =>
        [.. Enumerable.Range(0, 16).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    // A full-step bend at 1000 ms, peaking 250 ms in, held for a beat (500 ms).
    private static GuitarNote BentNote() => new()
    {
        Position = new FractionalPosition(2),
        Fret = 5,
        StringIndex = 3,
        Sustain = new FractionalPosition(1),
        BendValues = { new BendValue(new FractionalPosition(2, new Fraction(1, 2)), 2) }
    };

    private static Arrangement With(params GuitarNote[] notes)
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        arr.Levels.Add(new ChartLevel());
        foreach (var note in notes) arr.Levels[0].Notes.Add(note);
        return arr;
    }

    private static LiveNoteMatcher Matcher(Arrangement arr, Func<ExpectedSound, TechniqueContourVerdict?>? judge)
    {
        var m = LiveHitWindows.CreateMatcher(LiveHitWindowPreset.Normal,
            chartVerify: _ => Confirmed, chartVerifyEarly: _ => Confirmed, judgeContour: judge);
        m.Load(arr, arr.Levels[0], Beats());
        return m;
    }

    // Ticks the matcher at 30 Hz up to `untilMs`, asserting the score never goes down.
    private static List<LiveTechniqueJudgment> Run(LiveNoteMatcher m, double fromMs, double untilMs,
        List<LivePlayEvent>? judged = null)
    {
        var rulings = new List<LiveTechniqueJudgment>();
        var last = m.Stats.Score;
        for (var t = fromMs; t <= untilMs; t += 33)
        {
            // Not `judged?.AddRange(m.AdvanceTo(t))`: that skips the call when judged is null.
            var events = m.AdvanceTo(t);
            judged?.AddRange(events);
            rulings.AddRange(m.TechniqueJudgments);
            Assert.True(m.Stats.Score >= last, $"score moved down at {t} ms: {last} -> {m.Stats.Score}");
            last = m.Stats.Score;
        }

        return rulings;
    }

    [Fact]
    public void Off_by_default_the_matcher_judges_a_bend_exactly_as_before()
    {
        var off = Matcher(With(BentNote()), judge: null);
        var judged = new List<LivePlayEvent>();
        var rulings = Run(off, 900, 3000, judged);

        Assert.Null(off.JudgeContour);
        Assert.Empty(rulings);
        var hit = Assert.Single(judged);
        Assert.Equal(LivePlayVerdict.Hit, hit.Verdict);
        Assert.Equal(LivePlayVerdict.Hit, off.Expected[0].Verdict);
        Assert.Null(off.Expected[0].ContourVerdict);
        // An untimed CQT hit pays the full untimed base, as it always has.
        Assert.Equal(100, off.Stats.Score);
    }

    [Fact]
    public void Without_the_CQT_there_is_no_contour_and_nothing_is_judged()
    {
        var classic = LiveHitWindows.CreateMatcher(LiveHitWindowPreset.Normal,
            judgeContour: _ => TechniqueContourVerdict.NotMet);
        Assert.Null(classic.JudgeContour);
    }

    [Fact]
    public void A_bend_not_bent_is_downgraded_to_half_score_and_is_never_a_miss()
    {
        var m = Matcher(With(BentNote()), _ => TechniqueContourVerdict.NotMet);
        var judged = new List<LivePlayEvent>();
        var rulings = Run(m, 900, 3000, judged);

        // The hit is shown as a hit the moment it is confirmed...
        var credit = Assert.Single(judged);
        Assert.Equal(LivePlayVerdict.Hit, credit.Verdict);
        // ...and the ruling downgrades it afterwards.
        var ruling = Assert.Single(rulings);
        Assert.Equal(TechniqueContourVerdict.NotMet, ruling.Verdict);
        Assert.Equal(LivePlayVerdict.HitWrongTechnique, m.Expected[0].Verdict);
        Assert.Equal(TechniqueContourVerdict.NotMet, m.Expected[0].ContourVerdict);
        Assert.Equal(1, m.Stats.Hits);
        Assert.Equal(0, m.Stats.Misses);
        Assert.Equal(1, m.Stats.Streak);
        Assert.Equal(50, m.Stats.Score);
    }

    [Theory]
    [InlineData(TechniqueContourVerdict.Met)]
    [InlineData(TechniqueContourVerdict.Unknown)]
    public void A_bend_met_or_unknown_is_a_full_hit(TechniqueContourVerdict verdict)
    {
        var m = Matcher(With(BentNote()), _ => verdict);
        var rulings = Run(m, 900, 3000);

        Assert.Equal(verdict, Assert.Single(rulings).Verdict);
        Assert.Equal(LivePlayVerdict.Hit, m.Expected[0].Verdict);
        Assert.Equal(100, m.Stats.Score);
    }

    [Fact]
    public void The_ruling_waits_for_the_bend_peak_and_its_slack_and_a_late_audio_is_asked_again()
    {
        var asked = new List<double>();
        var playhead = 0.0;
        var m = Matcher(With(BentNote()), _ =>
        {
            asked.Add(playhead);
            // The CQT has not heard the end of the window for the first two asks.
            return asked.Count <= 2 ? null : TechniqueContourVerdict.Met;
        });

        for (playhead = 900; playhead <= 3000; playhead += 33)
        {
            m.AdvanceTo(playhead);
        }

        // Evidence ends at the 250 ms peak + 250 ms slack (the window's 500 ms): 1500 ms.
        Assert.Equal(3, asked.Count);
        Assert.True(asked[0] >= 1500, $"asked before the evidence ended: {asked[0]}");
        Assert.Equal(TechniqueContourVerdict.Met, m.Expected[0].ContourVerdict);
        Assert.Equal(100, m.Stats.Score);
    }

    [Fact]
    public void Audio_that_never_arrives_ends_unknown_a_full_hit()
    {
        var m = Matcher(With(BentNote()), _ => null);
        var rulings = Run(m, 900, 1500 + LiveTechniqueChecks.AudioWaitMs + 100);

        Assert.Equal(TechniqueContourVerdict.Unknown, Assert.Single(rulings).Verdict);
        Assert.Equal(100, m.Stats.Score);
    }

    [Theory]
    [InlineData(TechniqueContourVerdict.NotMet, 50)]
    [InlineData(TechniqueContourVerdict.Met, 100)]
    public void A_timed_hit_is_graded_then_ruled_and_pays_its_tier_once(TechniqueContourVerdict verdict, long score)
    {
        var m = Matcher(With(BentNote()), _ => verdict);
        Run(m, 900, 1100);
        // The analyzer reports the attack onset: a Perfect grade (base 100).
        var graded = new List<LivePlayGrade>();
        m.OfferPitches([new LivePitch(m.Expected[0].MidiNotes[0], 0f, 1f)], 1005, graded: graded);
        Assert.Equal(LiveTimingTier.Perfect, Assert.Single(graded).Tier);
        Run(m, 1100, 3000);

        Assert.Equal(score, m.Stats.Score);
        Assert.Equal(1, m.Stats.PerfectCount);
    }

    [Fact]
    public void A_ruling_before_the_window_closes_lifts_the_credit_and_the_close_pays_the_rest()
    {
        // A pre-bend: its evidence ends 250 ms in, before the CQT window closes.
        var note = BentNote();
        note.BendValues[0].Position = new FractionalPosition(2);
        var m = Matcher(With(note), _ => TechniqueContourVerdict.Met);
        Run(m, 900, 3000);

        Assert.Equal(100, m.Stats.Score);
    }

    [Fact]
    public void A_seek_keeps_a_hit_still_being_judged_as_a_full_hit_and_never_asks_again()
    {
        var asks = 0;
        var m = Matcher(With(BentNote()), _ =>
        {
            asks++;
            return TechniqueContourVerdict.NotMet;
        });
        Run(m, 900, 1200);
        Assert.True(m.Expected[0].Verdict is LivePlayVerdict.Hit);

        // Seek forward past the note: its hit is kept, its audio no longer lines up.
        m.Rewind(1800);
        var rulings = Run(m, 1800, 4000);

        Assert.Equal(0, asks);
        Assert.Empty(rulings);
        Assert.Equal(LivePlayVerdict.Hit, m.Expected[0].Verdict);
        Assert.Equal(TechniqueContourVerdict.Unknown, m.Expected[0].ContourVerdict);
        Assert.Equal(100, m.Stats.Score);
    }

    [Fact]
    public void A_seek_back_over_a_downgraded_hit_clears_it_with_its_ruling()
    {
        var m = Matcher(With(BentNote()), _ => TechniqueContourVerdict.NotMet);
        Run(m, 900, 3000);
        Assert.Equal(50, m.Stats.Score);

        m.Rewind(500);

        Assert.Null(m.Expected[0].Verdict);
        Assert.Null(m.Expected[0].ContourVerdict);
        Assert.Equal(0, m.Stats.Score);
    }

    [Fact]
    public void A_downgrade_survives_a_stats_replay()
    {
        var m = Matcher(With(BentNote(), new GuitarNote { Position = new FractionalPosition(8), Fret = 3 }),
            _ => TechniqueContourVerdict.NotMet);
        Run(m, 900, 3000);
        var before = m.Stats.Score;

        // A seek that keeps the bend replays the stats from the verdicts.
        m.Rewind(3600);

        Assert.Equal(LivePlayVerdict.HitWrongTechnique, m.Expected[0].Verdict);
        Assert.Equal(before, m.Stats.Score);
    }

    [Fact]
    public void Stopping_rules_on_what_was_heard_and_unheard_is_a_full_hit()
    {
        var m = Matcher(With(BentNote()), _ => null);
        Run(m, 900, 1200);

        m.ResolvePendingGrades();

        Assert.Equal(TechniqueContourVerdict.Unknown, m.Expected[0].ContourVerdict);
        Assert.Equal(LivePlayVerdict.Hit, m.Expected[0].Verdict);
        Assert.Equal(100, m.Stats.Score);
    }

    [Fact]
    public void A_bent_chord_and_a_plain_note_are_never_held_for_a_ruling()
    {
        var arr = With(new GuitarNote { Position = new FractionalPosition(2), Fret = 5 });
        arr.Levels[0].Chords.Add(new Chord
        {
            Position = new FractionalPosition(4),
            ChordNotes =
            {
                new ChordNote { StringIndex = 1, Fret = 5, BendValues = { new BendValue(new FractionalPosition(4), 2) } },
                new ChordNote { StringIndex = 2, Fret = 7 }
            }
        });
        var asks = 0;
        var m = Matcher(arr, _ =>
        {
            asks++;
            return TechniqueContourVerdict.NotMet;
        });
        Run(m, 900, 4000);

        Assert.Equal(0, asks);
        Assert.All(m.Expected, s => Assert.Equal(LivePlayVerdict.Hit, s.Verdict));
        Assert.Equal(200, m.Stats.Score);
    }

    [Fact]
    public void A_missed_bend_stays_a_miss_and_is_never_ruled_on()
    {
        var asks = 0;
        var m = LiveHitWindows.CreateMatcher(LiveHitWindowPreset.Normal,
            chartVerify: _ => default, chartVerifyEarly: _ => default, judgeContour: _ =>
            {
                asks++;
                return TechniqueContourVerdict.Met;
            });
        var arr = With(BentNote());
        m.Load(arr, arr.Levels[0], Beats());
        Run(m, 900, 4000);

        Assert.Equal(0, asks);
        Assert.Equal(LivePlayVerdict.Miss, m.Expected[0].Verdict);
    }

    [Fact]
    public void The_report_card_counts_a_downgraded_bend_as_played_without_its_technique()
    {
        var m = Matcher(With(BentNote()), _ => TechniqueContourVerdict.NotMet);
        Run(m, 900, 3000);

        var bend = Assert.Single(LiveTechniqueTally.Compute(m.Expected), t => t.Technique == PlayedTechnique.Bend);
        Assert.Equal(1, bend.WrongTechnique);
        Assert.Equal(0, bend.Hits);
        Assert.Equal(0, bend.Misses);
    }

    [Fact]
    public void The_session_hands_the_rulings_to_the_tick_and_never_tallies_them_as_judgments()
    {
        var session = new LivePlaySessionService();
        var arr = With(BentNote());
        session.Start(arr, arr.Levels[0], Beats(), new LivePlaySessionOptions
        {
            CountInBeats = 0,
            ChartVerify = _ => Confirmed,
            ChartVerifyEarly = _ => Confirmed,
            JudgeContour = _ => TechniqueContourVerdict.NotMet
        });
        session.SyncTo(900);

        var judgments = 0;
        var rulings = new List<LiveTechniqueJudgment>();
        for (var t = 900.0; t <= 3000; t += 33)
        {
            var tick = session.HandleTick(t, isPlaying: true);
            judgments += tick.Events.Count;
            rulings.AddRange(tick.TechniqueJudgments);
        }

        Assert.Equal(1, judgments);
        Assert.Equal(TechniqueContourVerdict.NotMet, Assert.Single(rulings).Verdict);
        var result = session.Stop(1.0);
        Assert.Equal(1, result.Hits);
        Assert.Equal(0, result.Misses);
        Assert.Contains(session.LastTechniqueTallies, t => t is { Technique: PlayedTechnique.Bend, WrongTechnique: 1 });
    }
}
