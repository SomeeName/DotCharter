using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     Rocksmith-style pause rewind: unpausing backs the song up by a lead-in so the
///     player re-orients before notes arrive again. The replayed stretch was ALREADY
///     judged — it must not be re-scored, must not re-miss, must not break the streak
///     and must not demote the dynamic-difficulty ladder. The session arms the seek as
///     its OWN, which is the whole trick: <see cref="LiveNoteMatcher.Rewind"/> — which
///     DELETES verdicts (and therefore score) past the playhead — must never fire.
///     <para>
///     Sounds whose hit window was still OPEN at the pause point stay judgeable, or a
///     full combo would become impossible after any pause.
///     </para>
/// </summary>
public sealed class LivePlayResumeRewindTests
{
    private const double Lead = 1500;

    // 120 BPM => 500 ms / beat.
    private static List<BeatMarker> Beats(int count = 64, double bpm = 120)
    {
        var ms = 60000.0 / bpm;
        return [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * ms, Bpm = bpm })];
    }

    private static GuitarNote Note(int beat, int fret, bool halfBeatLater = false) => new()
    {
        Position = new FractionalPosition(beat, halfBeatLater ? new Fraction(1, 2) : Fraction.Zero),
        Fret = fret,
        StringIndex = 0
    };

    /// <summary>One level, notes on beats 2..9 => 1000, 1500 … 4500 ms.</summary>
    private static Arrangement SimpleArr()
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        var level = new ChartLevel();
        for (var beat = 2; beat < 10; beat++) level.Notes.Add(Note(beat, fret: 3));
        arr.Levels.Add(level);
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Section = "intro" });
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(8), Section = "verse" });
        return arr;
    }

    private static readonly double[] NoteTimes = [1000, 1500, 2000, 2500, 3000, 3500, 4000, 4500];

    private static LivePlaySessionService StartSession(Arrangement arr, LivePlaySessionOptions? options = null,
        ChartLevel? level = null)
    {
        var session = new LivePlaySessionService();
        session.Start(arr, level ?? arr.Levels[0], Beats(), options ?? new LivePlaySessionOptions());
        session.SyncTo(0);
        return session;
    }

    private static void OfferHit(LivePlaySessionService session, double timeMs, int midi = 43)
    {
        Span<LivePitch> one = [new LivePitch(midi, 0f, 1f)];
        session.Offer(one, [], timeMs, LiveTechniqueFlags.None, 0f);
    }

    /// <summary>Drives HandleTick along song time in fixed steps, hitting every note
    /// time in <paramref name="hitTimes"/> exactly as the playhead reaches it.</summary>
    private static List<LivePlayTickResult> Drive(LivePlaySessionService session, double fromMs, double toMs,
        double stepMs = 50, IEnumerable<double>? hitTimes = null)
    {
        var pending = new List<double>(hitTimes ?? Array.Empty<double>());
        var results = new List<LivePlayTickResult>();
        for (var t = fromMs; t <= toMs; t += stepMs)
        {
            for (var i = pending.Count - 1; i >= 0; i--)
            {
                if (pending[i] <= t)
                {
                    OfferHit(session, pending[i]);
                    pending.RemoveAt(i);
                }
            }

            results.Add(session.HandleTick(t, isPlaying: true));
        }

        return results;
    }

    /// <summary>Plays to a pause at 3000 ms with the first four notes hit. The note at
    /// 3000 ms is deliberately left unjudged: its hit window (2850..3250) was still OPEN
    /// at the pause.</summary>
    private static LivePlaySessionService PlayedToPause(out double pausedAtMs)
    {
        var session = StartSession(SimpleArr());
        pausedAtMs = 3000;
        Drive(session, 0, pausedAtMs, hitTimes: new[] { 1000.0, 1500, 2000, 2500 });
        Assert.Equal(4, session.Stats!.Hits);
        Assert.Equal(0, session.Stats!.Misses);
        return session;
    }

    private static (long Score, int Hits, int Misses, int Streak, int BestStreak) Snapshot(
        LivePlaySessionService session)
    {
        var s = session.Stats!;
        return (s.Score, s.Hits, s.Misses, s.Streak, s.BestStreak);
    }

    // ---- the core guarantee --------------------------------------------------

    [Fact]
    public void ResumeRewind_PreservesScoreStreakAndVerdicts()
    {
        var session = PlayedToPause(out var pausedAt);
        var before = Snapshot(session);
        var verdictsBefore = session.Matcher!.Expected.Select(s => s.Verdict).ToArray();

        var target = session.BeginResumeRewind(pausedAt, Lead);

        Assert.Equal(1500, target!.Value, 3);

        // The transport lands on our target: HandleTick must NOT see a seek.
        var landing = session.HandleTick(target.Value, isPlaying: true);
        Assert.False(landing.StatsChanged);
        Assert.Empty(landing.Events);

        Assert.Equal(before, Snapshot(session));
        Assert.Equal(verdictsBefore, session.Matcher!.Expected.Select(s => s.Verdict).ToArray());
    }

    [Fact]
    public void ResumeRewind_ReplayedNotes_AreNotReJudged()
    {
        var session = PlayedToPause(out var pausedAt);
        var before = Snapshot(session);
        session.BeginResumeRewind(pausedAt, Lead);
        session.HandleTick(1500, isPlaying: true);

        // Playing along with the replayed (already-hit) notes must produce nothing.
        Assert.Null(OfferAt(session, 1500));
        Assert.Null(OfferAt(session, 2000));
        Assert.Null(OfferAt(session, 2500));

        Assert.Equal(before, Snapshot(session));
    }

    [Fact]
    public void ResumeRewind_ReplayedNotes_DoNotMissAgain()
    {
        var session = PlayedToPause(out var pausedAt);
        session.BeginResumeRewind(pausedAt, Lead);

        // Replay the whole rewound stretch WITHOUT playing a note: not one miss,
        // because every sound in it is already resolved.
        var events = Drive(session, 1500, 2900).SelectMany(r => r.Events).ToList();

        Assert.Empty(events);
        Assert.Equal(0, session.Stats!.Misses);
        Assert.Equal(4, session.Stats!.Hits);
    }

    [Fact]
    public void ResumeRewind_DoesNotBreakStreak()
    {
        var session = PlayedToPause(out var pausedAt);
        Assert.Equal(4, session.Stats!.Streak);

        session.BeginResumeRewind(pausedAt, Lead);
        Drive(session, 1500, 2900);

        Assert.Equal(4, session.Stats!.Streak);
        Assert.Equal(4, session.Stats!.BestStreak);
        Assert.Equal(1, session.Stats!.Multiplier);
    }

    /// <summary>THE FULL-COMBO GUARD. A sound whose window was still open at the pause
    /// (here 3000 ms, window 2850..3250) must remain judgeable — locking out everything
    /// up to the raw pause point would leave it forever unjudged, and an FC (Hits ==
    /// TotalSounds) impossible after any pause.</summary>
    [Fact]
    public void ResumeRewind_NotesInFlightAtPause_StayJudgeable()
    {
        var session = PlayedToPause(out var pausedAt);
        Assert.Null(session.Matcher!.Expected.First(s => s.TimeMs == 3000).Verdict);

        session.BeginResumeRewind(pausedAt, Lead);
        Drive(session, 1500, 5200, hitTimes: new[] { 3000.0, 3500, 4000, 4500 });

        var result = session.Stop(1.0);
        Assert.Equal(8, result.Hits);
        Assert.Equal(0, result.Misses);
        Assert.Equal(8, result.TotalSounds);
        Assert.True(result.IsFullCombo);
        Assert.Equal(1.0, result.SongProgress, 3);
    }

    // ---- clamping -------------------------------------------------------------

    [Fact]
    public void ResumeRewind_ClampsToZeroAtSongStart()
    {
        var session = StartSession(SimpleArr());
        Drive(session, 0, 1000, hitTimes: new[] { 1000.0 });

        Assert.Equal(0, session.BeginResumeRewind(1000, Lead)!.Value, 3);
    }

    [Fact]
    public void ResumeRewind_AtSongStart_IsNoOp()
    {
        var session = StartSession(SimpleArr());

        Assert.Null(session.BeginResumeRewind(0, Lead));
        Assert.Null(session.BeginResumeRewind(3000, 0));   // lead-in off
        Assert.Null(session.BeginResumeRewind(3000, -100));
    }

    [Fact]
    public void ResumeRewind_ClampsToLoopStart()
    {
        var session = StartSession(SimpleArr());
        session.SetLoop(2000, 4000);
        Drive(session, 0, 3000, hitTimes: new[] { 2000.0, 2500 });

        // A 1.5 s rewind from 3000 would land at 1500 — outside the loop. Clamped.
        var target = session.BeginResumeRewind(3000, Lead);

        Assert.Equal(2000, target!.Value, 3);
    }

    // ---- what must NOT change --------------------------------------------------

    /// <summary>Phrase mastery persists DOWNWARD (it is ladder state, not a best), so a
    /// spurious phrase re-completion at replay accuracy would DEMOTE the player.</summary>
    [Fact]
    public void ResumeRewind_DoesNotReCompletePhrases()
    {
        var session = new LivePlaySessionService();
        session.Start(DdArr(), null, Beats(), new LivePlaySessionOptions { DynamicDifficulty = true });
        session.SyncTo(0);

        // Clean pass over phrase 0 (2000..4000) levels it up in free play.
        Drive(session, 0, 4300, hitTimes: new[] { 2000.0, 2500, 3000, 3500 });
        var span0 = session.PhraseSpans[0];
        Assert.Equal(1, session.Difficulty!.LevelFor(span0));

        session.BeginResumeRewind(4300, Lead); // back to 2800 — inside the completed phrase
        var replay = Drive(session, 2800, 4400);

        Assert.All(replay, r => Assert.False(r.DifficultyChanged));
        Assert.Equal(1, session.Difficulty!.LevelFor(span0)); // never demoted
    }

    /// <summary>The manual scrub reset must NOT regress: a backwards seek the session did
    /// not request still rewinds the matcher (Rocksmith semantics — seeking back replays
    /// the region instead of banking its verdicts twice).</summary>
    [Fact]
    public void ExternalBackwardsSeek_StillRewinds()
    {
        var session = PlayedToPause(out _);
        Assert.Equal(4, session.Stats!.Hits);

        var afterSeek = session.HandleTick(500, isPlaying: true);

        Assert.True(afterSeek.StatsChanged);
        Assert.Equal(0, session.Stats!.Hits);
        Assert.All(session.Matcher!.Expected, s => Assert.Null(s.Verdict));
    }

    // ---- the money test ---------------------------------------------------------

    /// <summary>
    ///     The same performance, once uninterrupted and once with a pause + resume rewind
    ///     in the middle, must produce an IDENTICAL result. If a pause can change the
    ///     score, the feature is broken however good the rest of it looks.
    ///     <para>
    ///     The rewound stretch is deliberately NOT played again — that is the point of the
    ///     rewind (the player is re-orienting, and detection there is inert). So if the
    ///     resume ever rewound the MATCHER, those four hits would turn into misses and
    ///     this assertion would blow up.
    ///     </para>
    /// </summary>
    [Fact]
    public void PauseResume_MidRun_FinalResult_MatchesUninterruptedRun()
    {
        var clean = StartSession(SimpleArr());
        Drive(clean, 0, 5200, hitTimes: NoteTimes);
        var expected = clean.Stop(1.0);

        var paused = StartSession(SimpleArr());
        Drive(paused, 0, 2900, hitTimes: new[] { 1000.0, 1500, 2000, 2500 });
        Assert.Equal(1400, paused.BeginResumeRewind(2900, Lead)!.Value, 3);
        // Re-orient over the replayed stretch (no playing), then play the rest of the
        // song exactly as the clean run did.
        Drive(paused, 1400, 5200, hitTimes: NoteTimes.Where(t => t >= 3000));
        var actual = paused.Stop(1.0);

        Assert.Equal(expected.Score, actual.Score);
        Assert.Equal(expected.Hits, actual.Hits);
        Assert.Equal(expected.Misses, actual.Misses);
        Assert.Equal(expected.TotalSounds, actual.TotalSounds);
        Assert.Equal(expected.BestStreak, actual.BestStreak);
        Assert.Equal(expected.Accuracy, actual.Accuracy, 6);
        Assert.Equal(expected.SongProgress, actual.SongProgress, 6);
        Assert.Equal(expected.PerfectCount, actual.PerfectCount);
        Assert.Equal(expected.GreatCount, actual.GreatCount);
        Assert.Equal(expected.GoodCount, actual.GoodCount);
        Assert.Equal(expected.IsFullCombo, actual.IsFullCombo);
        Assert.True(actual.IsFullCombo);
    }

    // ---- the renderer's horizon ---------------------------------------------------

    [Fact]
    public void ResolvedThroughMs_CoversTheReplayedStretch()
    {
        var session = StartSession(SimpleArr());
        Assert.Equal(double.NegativeInfinity, session.Matcher!.ResolvedThroughMs);

        var pausedAt = 3000.0;
        Drive(session, 0, pausedAt, hitTimes: new[] { 1000.0, 1500, 2000, 2500 });

        // Everything through 2500 is resolved; the note at 3000 (window still open) is not.
        Assert.Equal(2500, session.Matcher!.ResolvedThroughMs, 3);

        // Outside a replay the horizon always trails the playhead, so no note can be
        // "ahead of the playhead AND resolved" — the dead-note predicate is false.
        Assert.True(session.Matcher!.ResolvedThroughMs < pausedAt);

        // After the rewind the playhead sits BEHIND the horizon: the notes in between
        // are the dead ones (1500, 2000, 2500).
        var target = session.BeginResumeRewind(pausedAt, Lead)!.Value;
        var dead = session.Matcher!.Expected
            .Where(s => s.TimeMs >= target && s.TimeMs <= session.Matcher!.ResolvedThroughMs)
            .Select(s => s.TimeMs)
            .ToArray();
        Assert.Equal(new[] { 1500.0, 2000, 2500 }, dead);
        Assert.All(dead, t => Assert.NotNull(session.Matcher!.Expected.First(s => s.TimeMs == t).Verdict));
    }

    // ---- helpers ------------------------------------------------------------------

    private static LivePlayEvent? OfferAt(LivePlaySessionService session, double timeMs, int midi = 43)
    {
        Span<LivePitch> one = [new LivePitch(midi, 0f, 1f)];
        return session.Offer(one, [], timeMs, LiveTechniqueFlags.None, 0f);
    }

    /// <summary>Two levels and two phrase iterations: "riff" (beats 4..8 = 2000..4000 ms)
    /// and "verse" (beat 8 on). Level 1 doubles the note density, so a level-up is
    /// visible in the totals.</summary>
    private static Arrangement DdArr()
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        var level0 = new ChartLevel { DifficultyIndex = 0 };
        var level1 = new ChartLevel { DifficultyIndex = 1 };
        for (var beat = 4; beat < 12; beat++)
        {
            level0.Notes.Add(Note(beat, fret: 3));
            level1.Notes.Add(Note(beat, fret: 3));
            level1.Notes.Add(Note(beat, fret: 5, halfBeatLater: true));
        }

        arr.Levels.Add(level0);
        arr.Levels.Add(level1);
        arr.Phrases.Add("riff");
        arr.Phrases.Add("verse");
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(4), 0));
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(8), 1));
        return arr;
    }
}
