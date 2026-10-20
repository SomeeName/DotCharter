using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     Practice-suite session rules: A/B loop wrap with count-in, pass
///     accuracy driving the speed ladder and dynamic difficulty, external
///     seek (rewind) semantics DURING a loop, and speed-invariance of the
///     judging (the clock-sync regression from the 0.10 hardening list).
/// </summary>
public sealed class LivePlaySessionServiceTests
{
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

    /// <summary>One level, notes on beats 2..9 (1000..4500 ms), two sections
    /// ("intro" 0.., "verse" beat 8 = 4000 ms..).</summary>
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

    private static LivePlaySessionService StartSession(Arrangement arr, LivePlaySessionOptions? options = null)
    {
        var session = new LivePlaySessionService();
        session.Start(arr, arr.Levels[0], Beats(), options ?? new LivePlaySessionOptions());
        session.SyncTo(0);
        return session;
    }

    private static void OfferHit(LivePlaySessionService session, double timeMs, int midi = 43)
    {
        Span<LivePitch> one = [new LivePitch(midi, 0f, 1f)];
        session.Offer(one, [], timeMs, LiveTechniqueFlags.None, 0f);
    }

    /// <summary>Drives HandleTick along song time in fixed steps, collecting results.</summary>
    private static List<LivePlayTickResult> Drive(LivePlaySessionService session,
        double fromMs, double toMs, double stepMs)
    {
        var results = new List<LivePlayTickResult>();
        for (var t = fromMs; t <= toMs; t += stepMs)
            results.Add(session.HandleTick(t, isPlaying: true));
        return results;
    }

    [Fact]
    public void SetLoop_ReturnsCountInPreRollTarget()
    {
        var session = StartSession(SimpleArr());

        // Loop beats 4..8 (2000..4000 ms) with the default 4-beat count-in:
        // the pre-roll starts 4 beats before 2000 ms => 0 ms.
        var target = session.SetLoop(2000, 4000);

        Assert.Equal(0, target!.Value, 3);
        Assert.NotNull(session.Loop);
        Assert.Equal(2000, session.Loop!.Value.StartMs, 3);
    }

    // ---- stall-vs-seek discrimination (2026-07-17 judging-inertness fix) ----

    [Fact]
    public void StalledForwardGap_WithoutSeekEvidence_ResolvesMissesInsteadOfRewinding()
    {
        var session = StartSession(SimpleArr());
        var result = new LivePlayTickResult();

        // Healthy ticks up to 900 ms (first note at 1000 ms still pending).
        for (var t = 0.0; t <= 900; t += 33)
            session.HandleTick(t, isPlaying: true, result, externalSeek: false);

        // The tick loop stalls for 2.4 s (>> the 750 ms seek threshold) while
        // the transport keeps playing: notes at 1000..3000 ms pass unplayed.
        var tick = session.HandleTick(3300, isPlaying: true, result, externalSeek: false);

        // The gap resolves as misses — judging continues, nothing rewinds.
        Assert.True(tick.Events.Count >= 4,
            $"expected the gap's notes resolved as misses, got {tick.Events.Count} events");
        Assert.All(tick.Events, e => Assert.Equal(LivePlayVerdict.Miss, e.Verdict));

        // And the session keeps judging normally afterwards.
        OfferHit(session, 3500);
        session.HandleTick(3600, isPlaying: true, result, externalSeek: false);
        Assert.True(session.Stats!.Hits >= 1, "post-stall hit did not register");
    }

    [Fact]
    public void ForwardJump_WithSeekEvidence_KeepsRewindSemantics()
    {
        var session = StartSession(SimpleArr());
        var result = new LivePlayTickResult();

        for (var t = 0.0; t <= 900; t += 33)
            session.HandleTick(t, isPlaying: true, result, externalSeek: false);

        // A real user seek far forward: notes in the skipped span must NOT be
        // punished as misses (historical Rocksmith rewind semantics).
        var tick = session.HandleTick(3300, isPlaying: true, result, externalSeek: true);
        Assert.Empty(tick.Events);
    }

    [Fact]
    public void BackwardJump_WithoutSeekEvidence_StillRewinds()
    {
        var session = StartSession(SimpleArr());
        var result = new LivePlayTickResult();

        for (var t = 0.0; t <= 3300; t += 33)
            session.HandleTick(t, isPlaying: true, result, externalSeek: false);

        // Time cannot run backwards without a seek — treat as one regardless
        // of missing evidence (safe interpretation; matcher repositions).
        var tick = session.HandleTick(500, isPlaying: true, result, externalSeek: false);
        Assert.Empty(tick.Events);
        // Notes after the rewind point are pending again (rewind happened).
        OfferHit(session, 1000);
        session.HandleTick(1050, isPlaying: true, result, externalSeek: false);
        Assert.True(session.Stats!.Hits >= 1, "rewound note could not be re-judged");
    }

    [Fact]
    public void LegacyOverload_TreatsEveryJumpAsSeek()
    {
        var session = StartSession(SimpleArr());

        for (var t = 0.0; t <= 900; t += 33)
            session.HandleTick(t, isPlaying: true);

        // The evidence-less overload keeps the historical contract.
        var tick = session.HandleTick(3300, isPlaying: true);
        Assert.Empty(tick.Events);
    }

    [Fact]
    public void CountIn_EmitsOneTickPerBeat_AndSuppressesJudging()
    {
        var session = StartSession(SimpleArr());
        session.SetLoop(2000, 4000);

        var totalTicks = 0;
        var missed = 0;
        foreach (var r in Drive(session, 0, 1999, 100))
        {
            totalTicks += r.CountInTicks;
            missed += r.Events.Count;
            if (r.CountInTicks > 0) Assert.True(r.InCountIn);
        }

        // Beats at 500/1000/1500 are crossed during the pre-roll (beat 0 is
        // the start itself, beat 2000 is the loop start). Notes at 1000 and
        // 1500 sit in the pre-roll but must NOT be judged as misses.
        Assert.Equal(3, totalTicks);
        Assert.Equal(0, missed);
        Assert.Equal(0, session.Stats!.Misses);
    }

    [Fact]
    public void LoopWrap_CompletesPass_SeeksBack_AndResetsRegionVerdicts()
    {
        var session = StartSession(SimpleArr());
        session.SetLoop(2000, 4000);

        // Play through the pre-roll + the loop, hitting both loop notes
        // (beats 4,5,6,7 => 2000,2500,3000,3500; hit 2 of 4).
        double? seekTo = null;
        LivePlayTickResult? pass = null;
        for (var t = 0.0; t <= 4100 && pass == null; t += 50)
        {
            if (t is 2000 or 2500) OfferHit(session, t);
            var r = session.HandleTick(t, isPlaying: true);
            if (r.PassCompleted)
            {
                pass = r;
                seekTo = r.SeekToMs;
            }
        }

        Assert.NotNull(pass);
        Assert.Equal(0, seekTo!.Value, 3); // back to the count-in pre-roll
        Assert.Equal(4, pass!.PassJudged);
        Assert.Equal(0.5, pass.PassAccuracy, 3);
        Assert.Equal(2, session.LastPassMisses.Count);

        // The wrap rewound the loop region: verdicts there are cleared again.
        var expected = session.Matcher!.Expected;
        foreach (var sound in expected)
        {
            if (sound.TimeMs >= 2000)
                Assert.Null(sound.Verdict);
        }
    }

    [Fact]
    public void SeekDuringLoop_KeepsMatcherStateConsistent()
    {
        var session = StartSession(SimpleArr());
        session.SetLoop(2000, 4000);

        // Enter the loop and judge its first two notes.
        session.HandleTick(2000, true);
        OfferHit(session, 2000);
        OfferHit(session, 2500);
        session.HandleTick(2600, true);
        Assert.Equal(2, session.Stats!.Hits);

        // The user seeks back INTO the loop start (external jump > 750 ms
        // relative to the last playhead we saw at 2600? use a far seek to 100).
        var afterSeek = session.HandleTick(100, true);
        Assert.True(afterSeek.StatsChanged);

        // Everything from the seek target's horizon on is unjudged again and
        // the totals recomputed from the surviving verdicts (none here).
        Assert.Equal(0, session.Stats!.Hits);
        Assert.Equal(0, session.Stats!.Misses);
        foreach (var sound in session.Matcher!.Expected)
            Assert.Null(sound.Verdict);

        // The loop itself survives the seek and wraps again later.
        Assert.NotNull(session.Loop);
        var wrapped = false;
        for (var t = 100.0; t <= 4200; t += 50)
        {
            if (session.HandleTick(t, true).PassCompleted)
                wrapped = true;
        }

        Assert.True(wrapped);
    }

    [Fact]
    public void OwnLoopSeek_DoesNotDoubleRewind()
    {
        var session = StartSession(SimpleArr());
        session.SetLoop(2000, 4000);

        // Wrap the loop; the session requests a seek to the pre-roll (0 ms).
        LivePlayTickResult? pass = null;
        for (var t = 0.0; t <= 4100 && pass == null; t += 50)
        {
            var r = session.HandleTick(t, true);
            if (r.PassCompleted) pass = r;
        }

        Assert.NotNull(pass);
        var missesAfterWrap = session.Stats!.Misses;

        // The transport lands on the requested target: this jump must be
        // recognized as OUR seek (no extra rewind => stats untouched).
        var landing = session.HandleTick(pass!.SeekToMs!.Value, true);
        Assert.False(landing.StatsChanged);
        Assert.Equal(missesAfterWrap, session.Stats!.Misses);
    }

    [Fact]
    public void SpeedLadder_StepsUpAfterCleanPass_AndReportsSpeedChange()
    {
        var session = StartSession(SimpleArr(), new LivePlaySessionOptions
        {
            Ladder = new SpeedLadder { StartSpeed = 0.6, StepUp = 0.05 }
        });
        session.SetLoop(2000, 4000);

        double? newSpeed = null;
        for (var t = 0.0; t <= 4100 && newSpeed == null; t += 50)
        {
            if (t is 2000 or 2500 or 3000 or 3500) OfferHit(session, t);
            var r = session.HandleTick(t, true);
            if (r.SpeedChange is { } s) newSpeed = s;
        }

        Assert.Equal(0.65, newSpeed!.Value, 3);
    }

    [Fact]
    public void SpeedLadder_FailedPass_StepsDown()
    {
        var session = StartSession(SimpleArr(), new LivePlaySessionOptions
        {
            Ladder = new SpeedLadder { StartSpeed = 0.6, StepDown = 0.05 }
        });
        session.SetLoop(2000, 4000);

        double? newSpeed = null;
        for (var t = 0.0; t <= 4100 && newSpeed == null; t += 50)
        {
            var r = session.HandleTick(t, true); // no hits at all
            if (r.SpeedChange is { } s) newSpeed = s;
        }

        Assert.Equal(0.55, newSpeed!.Value, 3);
    }

    /// <summary>
    ///     0.10 hardening: the speed ladder must never drift the judging.
    ///     Judging runs on SONG time, so the same performance replayed at
    ///     0.5×/0.75×/1× (= the same song-time attacks sampled at different
    ///     tick densities) must produce identical verdicts and stats.
    /// </summary>
    [Theory]
    [InlineData(0.5)]
    [InlineData(0.75)]
    [InlineData(1.0)]
    public void Judging_IsInvariantAcrossPlaybackSpeeds(double speed)
    {
        var session = StartSession(SimpleArr());

        // Wall-clock ticks every 33 ms => song-time step shrinks with speed.
        var songStep = 33.0 * speed;
        var hitTimes = new[] { 1000.0, 1500.0, 2000.0 }; // first three notes
        var nextHit = 0;
        for (var t = 0.0; t <= 5000; t += songStep)
        {
            while (nextHit < hitTimes.Length && hitTimes[nextHit] <= t)
                OfferHit(session, hitTimes[nextHit++]);
            session.HandleTick(t, true);
        }

        Assert.Equal(3, session.Stats!.Hits);
        Assert.Equal(5, session.Stats!.Misses);
        Assert.Equal(8, session.Stats!.TotalSounds);
    }

    [Fact]
    public void SectionTallies_TrackHitsAndMisses()
    {
        var session = StartSession(SimpleArr());

        Drive(session, 0, 900, 100); // approach without seek-sized jumps
        OfferHit(session, 1000);
        session.HandleTick(1000, true);
        Assert.Equal(2, session.Sections.Count);
        Assert.Equal(1, session.Sections[0].Hits);

        // Let the rest of the intro notes expire (intro spans to 4000 ms).
        Drive(session, 1100, 4200, 100);
        Assert.True(session.Sections[0].Misses > 0);
        // Verse untouched: nothing judged is NOT 100% (AccuracyMath) — the heat
        // strip and the worst-section pick both gate on the tally, so an
        // unplayed section reads as unmeasured rather than as a clean one.
        Assert.False(session.Sections[1].IsMeasured);
        Assert.Equal(0.0, session.Sections[1].Accuracy, 3);
    }

    [Fact]
    public void SectionLoop_UsesSectionBounds()
    {
        var session = StartSession(SimpleArr());
        var verse = session.Sections[1];

        var target = session.SetLoop(verse);

        Assert.NotNull(session.Loop);
        Assert.Equal(verse.StartMs, session.Loop!.Value.StartMs, 3);
        Assert.Equal(verse.EndMs, session.Loop!.Value.EndMs, 3);
        Assert.True(target < verse.StartMs); // pre-roll before the section
    }

    [Fact]
    public void DegenerateLoop_IsRejected()
    {
        var session = StartSession(SimpleArr());
        Assert.Null(session.SetLoop(2000, 2100));
        Assert.Null(session.Loop);
    }

    [Fact]
    public void Stop_ReportsFinalNumbers()
    {
        var session = StartSession(SimpleArr(), new LivePlaySessionOptions
        {
            Ladder = new SpeedLadder { StartSpeed = 0.6 }
        });
        Drive(session, 0, 900, 100);
        OfferHit(session, 1000);
        session.HandleTick(1000, true);

        var result = session.Stop(1.0);

        Assert.Equal(1, result.Hits);
        Assert.Equal(8, result.TotalSounds);
        // The ladder OWNS the transport while it runs, so it wins over the slider.
        Assert.Equal(60, result.SpeedPercent, 3);
        Assert.False(session.IsActive);
    }

    [Fact]
    public void Stop_WithoutALadder_RecordsTheTransportSpeed_NotFullSpeed()
    {
        // LivePlaySessionOptions.Ladder is non-null ONLY for the riff-repeater
        // ladder or an armed smart-practice drill. The ordinary speed slider is
        // always available, and the result used to hardcode 100 % for it — so a
        // 50 %-speed run claimed full mastery, and the SM-2 practice queue's
        // "a clean run at 60 % is not mastery" rule was defeated.
        var session = StartSession(SimpleArr());
        Assert.Null(session.Ladder);
        Drive(session, 0, 900, 100);
        OfferHit(session, 1000);
        session.HandleTick(1000, true);

        var result = session.Stop(0.5);

        Assert.Equal(50, result.SpeedPercent, 3);
    }

    [Fact]
    public void DynamicDifficulty_LevelsUpPhraseAcrossLoopPasses()
    {
        // Two levels; phrase "riff" covers beats 4..8. Level 0: 1 note/beat,
        // level 1: adds an extra note. Clean pass at level 0 must reload the
        // loop at level 1 (more sounds in the region).
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        var level0 = new ChartLevel { DifficultyIndex = 0 };
        var level1 = new ChartLevel { DifficultyIndex = 1 };
        for (var beat = 4; beat < 8; beat++)
        {
            level0.Notes.Add(Note(beat, fret: 3));
            level1.Notes.Add(Note(beat, fret: 3));
            level1.Notes.Add(Note(beat, fret: 5, halfBeatLater: true));
        }

        arr.Levels.Add(level0);
        arr.Levels.Add(level1);
        arr.Phrases.Add("riff");
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(4), 0));

        var session = new LivePlaySessionService();
        session.Start(arr, null, Beats(), new LivePlaySessionOptions { DynamicDifficulty = true });
        session.SyncTo(0);

        // Dynamic difficulty starts at level 0: 4 expected sounds.
        Assert.Equal(4, session.Stats!.TotalSounds);

        session.SetLoop(2000, 4000);
        LivePlayTickResult? pass = null;
        for (var t = 0.0; t <= 4100 && pass == null; t += 50)
        {
            if (t is 2000 or 2500 or 3000 or 3500) OfferHit(session, t);
            var r = session.HandleTick(t, true);
            if (r.PassCompleted) pass = r;
        }

        Assert.NotNull(pass);
        Assert.True(pass!.DifficultyChanged);
        Assert.Equal(1, session.Difficulty!.LevelFor(session.PhraseSpans[0]));
        // The reloaded chart now carries level 1's denser phrase.
        Assert.Equal(8, session.Stats!.TotalSounds);
    }
}
