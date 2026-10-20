using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     Pins the EXACT external-seek detection contract in <c>HandleTick</c> so an
///     in-flight refactor of the detection pump has a regression net. Two
///     constants govern whether a playhead delta is treated as a seek (and the
///     matcher rewound, deleting verdicts/score past the landing):
///
///     <list type="bullet">
///     <item><c>SeekJumpThresholdMs = 750</c> — a jump fires only when
///       <c>|playhead - lastPlayhead| &gt; 750</c> (strictly greater; 750 is
///       NOT a jump).</item>
///     <item>own-seek landing tolerance <c>&lt; 500</c> — a jump that lands within
///       500 ms of the session's own registered seek target is recognised as ours
///       and does NOT rewind (500 exactly is NOT within tolerance).</item>
///     </list>
///
///     The existing session/resume-rewind suites use far seeks; none of them pins
///     the 749/750/751 or the 499/500/501 edge. If a refactor moves the compare
///     to <c>&gt;=</c>, drops the <c>Abs</c> (forward seeks), or shifts the
///     tolerance, one of these fails loudly.
/// </summary>
public sealed class LivePlaySeekThresholdTests
{
    private static List<BeatMarker> Beats(int count = 64, double bpm = 120)
    {
        var ms = 60000.0 / bpm;
        return [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * ms, Bpm = bpm })];
    }

    private static GuitarNote Note(int beat, int fret) => new()
    {
        Position = new FractionalPosition(beat, Fraction.Zero),
        Fret = fret,
        StringIndex = 0
    };

    /// <summary>Notes on beats 2..9 => 1000, 1500 … 4500 ms.</summary>
    private static Arrangement SimpleArr()
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        var level = new ChartLevel();
        for (var beat = 2; beat < 10; beat++) level.Notes.Add(Note(beat, fret: 3));
        arr.Levels.Add(level);
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Section = "intro" });
        return arr;
    }

    private static LivePlaySessionService StartSession(Arrangement arr)
    {
        var session = new LivePlaySessionService();
        session.Start(arr, arr.Levels[0], Beats(), new LivePlaySessionOptions());
        session.SyncTo(0);
        return session;
    }

    private static void OfferHit(LivePlaySessionService session, double timeMs, int midi = 43)
    {
        Span<LivePitch> one = [new LivePitch(midi, 0f, 1f)];
        session.Offer(one, [], timeMs, LiveTechniqueFlags.None, 0f);
    }

    /// <summary>Drives HandleTick forward in fixed sub-threshold steps (no jumps),
    /// leaving <c>_lastPlayheadMs</c> at <paramref name="toMs"/>.</summary>
    private static void DriveTo(LivePlaySessionService session, double toMs, double stepMs = 100)
    {
        for (var t = 0.0; t <= toMs; t += stepMs) session.HandleTick(t, isPlaying: true);
    }

    // ---- backward seek: the 750 ms threshold edge ----------------------------

    [Theory]
    [InlineData(749, false)] // just under -> no seek
    [InlineData(750, false)] // exactly at the threshold -> still no seek (strictly >)
    [InlineData(751, true)]  // just over -> seek, matcher rewinds
    public void BackwardSeek_AtThreshold_RewindsOnlyAbove750(double delta, bool expectRewind)
    {
        var session = StartSession(SimpleArr());
        // Advance to 3000 ms without hits: notes at 1000/1500/2000/2500 have
        // expired into misses; _lastPlayheadMs == 3000.
        DriveTo(session, 3000);
        var baselineMisses = session.Stats!.Misses;
        Assert.True(baselineMisses >= 4, $"expected >=4 misses by 3000 ms, saw {baselineMisses}");

        var result = session.HandleTick(3000 - delta, isPlaying: true);

        Assert.Equal(expectRewind, result.StatsChanged);
        if (expectRewind)
        {
            // Rewinding to ~2249 (horizon 1999) clears the misses at/after 1999,
            // so the recomputed total drops below the baseline.
            Assert.True(session.Stats!.Misses < baselineMisses,
                $"rewind should have cleared misses (was {baselineMisses}, now {session.Stats!.Misses})");
        }
        else
        {
            // A sub-threshold backward nudge must leave every verdict intact.
            Assert.Equal(baselineMisses, session.Stats!.Misses);
        }
    }

    // ---- forward seek: |Δ| uses Abs, and skipped notes are not missed --------

    [Fact]
    public void ForwardSeek_AboveThreshold_RewindsAndDoesNotMissSkippedNotes()
    {
        var session = StartSession(SimpleArr());
        // Reach 1000 ms and hit that note; nothing has expired yet.
        DriveTo(session, 1000);
        OfferHit(session, 1000);
        session.HandleTick(1000, isPlaying: true);
        Assert.Equal(1, session.Stats!.Hits);
        Assert.Equal(0, session.Stats!.Misses);

        // Seek forward by 751 ms (> threshold). Abs() makes this a jump too, so
        // the matcher repositions PAST the skipped note at 1500 — which must NOT
        // become a miss (a forward seek skips content, it does not fail it).
        var result = session.HandleTick(1000 + 751, isPlaying: true);

        Assert.True(result.StatsChanged);
        Assert.Equal(1, session.Stats!.Hits);   // the earlier hit survives
        Assert.Equal(0, session.Stats!.Misses); // the skipped 1500 ms note is not a miss
    }

    // ---- own-seek landing tolerance: the 500 ms edge --------------------------

    [Theory]
    [InlineData(499, false)] // within tolerance -> recognised as our seek, no rewind
    [InlineData(500, true)]  // exactly 500 -> NOT within tolerance (strictly <) -> rewind
    [InlineData(501, true)]  // outside -> rewind
    public void OwnLoopSeekLanding_WithinToleranceSuppressesRewind(double offset, bool expectRewind)
    {
        var session = StartSession(SimpleArr());
        session.SetLoop(2000, 4000);

        // Wrap the loop once: the session registers its own seek back to the
        // count-in pre-roll (_pendingSeekTargetMs) while _lastPlayheadMs sits at
        // the loop end (~4000). The transport now seeks toward that target.
        LivePlayTickResult? pass = null;
        for (var t = 0.0; t <= 4000 && pass == null; t += 50)
        {
            var r = session.HandleTick(t, isPlaying: true);
            if (r.PassCompleted) pass = r;
        }

        Assert.NotNull(pass);
        var target = pass!.SeekToMs!.Value;

        // Land `offset` ms from the registered target. Every landing is a >750 ms
        // jump from the loop-end last-playhead, so the ONLY thing that decides a
        // rewind is the own-seek landing tolerance.
        var landing = session.HandleTick(target + offset, isPlaying: true);

        Assert.Equal(expectRewind, landing.StatsChanged);
    }
}
