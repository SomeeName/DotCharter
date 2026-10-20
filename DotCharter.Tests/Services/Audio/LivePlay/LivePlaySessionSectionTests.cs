using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     A section's bar range is captured ONCE, in <c>BuildSections</c>, while
///     the beat map is still open — the practice store persists no beat map, so
///     a recorded run can never be re-barred afterwards. Anything that rebuilds
///     the section list therefore has to carry those two numbers forward, and
///     they are <c>init</c>-only, so omitting them is silent.
/// </summary>
public sealed class LivePlaySessionSectionTests
{
    /// <summary>120 BPM ⇒ 500 ms/beat, a measure marker every 4 beats.</summary>
    private static List<BeatMarker> Beats(int count = 64, double bpm = 120)
    {
        var ms = 60000.0 / bpm;
        return
        [
            .. Enumerable.Range(0, count).Select(i => new BeatMarker
            {
                Time = i * ms, Bpm = bpm, IsMeasure = i % 4 == 0
            })
        ];
    }

    private static GuitarNote Note(int beat, int fret, bool halfBeatLater = false) => new()
    {
        Position = new FractionalPosition(beat, halfBeatLater ? new Fraction(1, 2) : Fraction.Zero),
        Fret = fret,
        StringIndex = 0
    };

    /// <summary>Two levels so dynamic difficulty has somewhere to go, one
    /// phrase over beats 4..8, and two barred sections.</summary>
    private static Arrangement LeveledArr()
    {
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
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Section = "intro" });
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(8), Section = "verse" });
        return arr;
    }

    private static void OfferHit(LivePlaySessionService session, double timeMs, int midi = 43)
    {
        Span<LivePitch> one = [new LivePitch(midi, 0f, 1f)];
        session.Offer(one, [], timeMs, LiveTechniqueFlags.None, 0f);
    }

    [Fact]
    public void ADifficultyReload_KeepsEverySectionsBarNumbers()
    {
        var session = new LivePlaySessionService();
        session.Start(LeveledArr(), null, Beats(), new LivePlaySessionOptions { DynamicDifficulty = true });
        session.SyncTo(0);

        var before = session.Sections
            .Select(s => (s.Name, s.StartBar, s.EndBar))
            .ToList();
        Assert.Equal(2, before.Count);
        Assert.All(before, b =>
        {
            Assert.NotNull(b.StartBar);
            Assert.NotNull(b.EndBar);
        });

        // One clean loop pass over the phrase levels it up, which reloads the
        // matcher and rebuilds the section list.
        session.SetLoop(2000, 4000);
        LivePlayTickResult? pass = null;
        for (var t = 0.0; t <= 4100 && pass == null; t += 50)
        {
            if (t is 2000 or 2500 or 3000 or 3500) OfferHit(session, t);

            var r = session.HandleTick(t, true);
            if (r.PassCompleted) pass = r;
        }

        Assert.NotNull(pass);
        Assert.True(pass!.DifficultyChanged, "the phrase never levelled up, so nothing rebuilt the sections");

        // The rebuild recounts SOUNDS. It must not move — or forget — a bar.
        var after = session.Sections
            .Select(s => (s.Name, s.StartBar, s.EndBar))
            .ToList();
        Assert.Equal(before, after);
    }

    /// <summary>Keeps the case above honest: with no measure markers the bars
    /// are legitimately null everywhere, so the assertion would hold vacuously
    /// on a chart that cannot be barred at all.</summary>
    [Fact]
    public void AChartWithMeasureMarkers_IsBarredAtAll()
    {
        var session = new LivePlaySessionService();
        session.Start(LeveledArr(), null, Beats(), new LivePlaySessionOptions());
        session.SyncTo(0);

        Assert.Equal(1, session.Sections[0].StartBar);
        // "verse" opens on beat 8 = bar 3 (measures on beats 0, 4, 8, …).
        Assert.Equal(3, session.Sections[1].StartBar);
    }
}
