using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     0.16 timing-graded judgment: Perfect/Great/Good tiers from the
///     per-hit |detected − expected| delta (event times are already
///     latency-offset-compensated before they reach the matcher), tier
///     weighting of the score, tier counts in the stats/session result, and
///     the tier lifecycle across rewind/reload. CQT pull-verified hits carry
///     no timing evidence — tier None, full base score (pre-tier behavior).
/// </summary>
public sealed class LiveTimingTierTests
{
    // 120 BPM => 500 ms / beat.
    private static List<BeatMarker> Beats(int count = 64, double bpm = 120)
    {
        var ms = 60000.0 / bpm;
        return [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * ms, Bpm = bpm })];
    }

    private static Arrangement NewArr()
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        arr.Levels.Add(new ChartLevel());
        return arr;
    }

    private static GuitarNote Note(int beat, int fret, MuteType mute = MuteType.None) => new()
    {
        Position = new FractionalPosition(beat),
        Fret = fret,
        StringIndex = 0,
        Mute = mute
    };

    private static LiveNoteMatcher Loaded(Arrangement arr)
    {
        var matcher = new LiveNoteMatcher(); // defaults: perfect 45 ms, great 95 ms
        matcher.Load(arr, arr.Levels[0], Beats());
        return matcher;
    }

    // Low E string fret 3 => MIDI 43; chart note at beat 2 = 1000 ms.
    private static Arrangement OneNoteArr()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 3));
        return arr;
    }

    [Theory]
    [InlineData(1000, LiveTimingTier.Perfect)]  // dead on
    [InlineData(1044, LiveTimingTier.Perfect)]  // +44 ≤ 45
    [InlineData(956, LiveTimingTier.Perfect)]   // −44, symmetric
    [InlineData(1060, LiveTimingTier.Great)]    // +60 ≤ 95
    [InlineData(910, LiveTimingTier.Great)]     // −90, symmetric
    [InlineData(1120, LiveTimingTier.Good)]     // +120 > 95
    [InlineData(880, LiveTimingTier.Good)]      // −120, early Good
    public void HitDelta_MapsToTier(double offerAtMs, LiveTimingTier expected)
    {
        var matcher = Loaded(OneNoteArr());

        var result = matcher.OfferNote(midi: 43, timeMs: offerAtMs);

        Assert.NotNull(result);
        Assert.Equal(LivePlayVerdict.Hit, result!.Value.Verdict);
        Assert.Equal(expected, result.Value.Tier);
    }

    [Theory]
    [InlineData(1000, 100)] // Perfect
    [InlineData(1060, 80)]  // Great
    [InlineData(1120, 50)]  // Good
    public void Score_IsTierWeighted(double offerAtMs, long expectedScore)
    {
        var matcher = Loaded(OneNoteArr());
        Assert.NotNull(matcher.OfferNote(midi: 43, timeMs: offerAtMs));
        Assert.Equal(expectedScore, matcher.Stats.Score);
    }

    [Fact]
    public void WrongTechnique_HalvesTheTierScore()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 3, mute: MuteType.Palm));
        var matcher = Loaded(arr);

        // Bright attack where a mute belongs, 60 ms late => Great (80) / 2.
        var result = matcher.OfferNote(midi: 43, timeMs: 1060, centroidHz: 5200f);

        Assert.Equal(LivePlayVerdict.HitWrongTechnique, result!.Value.Verdict);
        Assert.Equal(LiveTimingTier.Great, result.Value.Tier);
        Assert.Equal(40, matcher.Stats.Score);
    }

    [Fact]
    public void TierCounts_TrackEachGradedHit_MissesUntouched()
    {
        var arr = NewArr();
        for (var beat = 2; beat <= 5; beat++) arr.Levels[0].Notes.Add(Note(beat, fret: 3));
        var matcher = Loaded(arr);

        Assert.NotNull(matcher.OfferNote(midi: 43, timeMs: 1000)); // Perfect
        Assert.NotNull(matcher.OfferNote(midi: 43, timeMs: 1560)); // Great
        Assert.NotNull(matcher.OfferNote(midi: 43, timeMs: 2120)); // Good
        matcher.AdvanceTo(playheadMs: 4000);                       // last one misses

        Assert.Equal(1, matcher.Stats.PerfectCount);
        Assert.Equal(1, matcher.Stats.GreatCount);
        Assert.Equal(1, matcher.Stats.GoodCount);
        Assert.Equal(3, matcher.Stats.Hits);
        Assert.Equal(1, matcher.Stats.Misses);
    }

    [Fact]
    public void ChordChromaPath_GradesTimingToo()
    {
        var arr = NewArr();
        var chord = new Chord { Position = new FractionalPosition(2) }; // 1000 ms
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
        arr.Levels[0].Chords.Add(chord);
        var matcher = Loaded(arr);

        var chroma = new float[12];
        chroma[7] = 0.5f;
        chroma[2] = 0.3f;
        var result = matcher.OfferChroma(chroma, timeMs: 1070); // +70 => Great

        Assert.NotNull(result);
        Assert.Equal(LiveTimingTier.Great, result!.Value.Tier);
        Assert.Equal(1, matcher.Stats.GreatCount);
    }

    [Fact]
    public void Rewind_ClearsTiersWithVerdicts_AndRecomputesCounts()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 1, fret: 3)); // 500 ms
        arr.Levels[0].Notes.Add(Note(beat: 6, fret: 3)); // 3000 ms
        var matcher = Loaded(arr);

        Assert.NotNull(matcher.OfferNote(midi: 43, timeMs: 560));  // Great, survives
        Assert.NotNull(matcher.OfferNote(midi: 43, timeMs: 3120)); // Good, rewound away
        Assert.Equal(1, matcher.Stats.GreatCount);
        Assert.Equal(1, matcher.Stats.GoodCount);

        matcher.Rewind(playheadMs: 2000);

        Assert.Equal(1, matcher.Stats.GreatCount); // replayed from surviving verdict
        Assert.Equal(0, matcher.Stats.GoodCount);
        Assert.Equal(80, matcher.Stats.Score); // Great(80) × 1
        Assert.Equal(LiveTimingTier.None, matcher.Expected[1].TimingTier);
    }

    [Fact]
    public void CqtPullVerifiedHit_HasNoTier_AndPaysFullBase()
    {
        var arr = OneNoteArr();
        var matcher = new LiveNoteMatcher { ChartVerify = _ => new ChartVerifyOutcome(true, 0.9f) };
        matcher.Load(arr, arr.Levels[0], Beats());

        var events = matcher.AdvanceTo(playheadMs: 2000); // window closes → pull-verify

        Assert.Single(events);
        Assert.Equal(LivePlayVerdict.Hit, events[0].Verdict);
        Assert.Equal(LiveTimingTier.None, events[0].Tier);
        Assert.Equal(100, matcher.Stats.Score); // pre-tier behavior preserved
        Assert.Equal(0, matcher.Stats.PerfectCount + matcher.Stats.GreatCount + matcher.Stats.GoodCount);
    }

    [Theory]
    [InlineData(LiveHitWindowPreset.Strict, 1040, LiveTimingTier.Great)]   // 40 > 25
    [InlineData(LiveHitWindowPreset.Lenient, 1040, LiveTimingTier.Perfect)] // 40 ≤ 65
    [InlineData(LiveHitWindowPreset.Normal, 1040, LiveTimingTier.Perfect)]  // 40 ≤ 45
    public void PresetScalesTierWindows(LiveHitWindowPreset preset, double offerAtMs, LiveTimingTier expected)
    {
        var arr = OneNoteArr();
        var matcher = LiveHitWindows.CreateMatcher(preset);
        matcher.Load(arr, arr.Levels[0], Beats());

        var result = matcher.OfferNote(midi: 43, timeMs: offerAtMs);

        Assert.NotNull(result);
        Assert.Equal(expected, result!.Value.Tier);
    }

    [Fact]
    public void SessionStop_CarriesTierCountsIntoTheResult()
    {
        var arr = NewArr();
        for (var beat = 2; beat <= 4; beat++) arr.Levels[0].Notes.Add(Note(beat, fret: 3));
        var session = new LivePlaySessionService();
        session.Start(arr, arr.Levels[0], Beats(), new LivePlaySessionOptions());
        session.SyncTo(0);

        Span<LivePitch> one = [new LivePitch(43, 0f, 1f)];
        session.HandleTick(900, isPlaying: true);
        session.Offer(one, [], 1000, LiveTechniqueFlags.None, 0f); // Perfect
        session.Offer(one, [], 1560, LiveTechniqueFlags.None, 0f); // Great
        session.Offer(one, [], 2120, LiveTechniqueFlags.None, 0f); // Good

        var result = session.Stop(1.0);

        Assert.Equal(1, result.PerfectCount);
        Assert.Equal(1, result.GreatCount);
        Assert.Equal(1, result.GoodCount);
        Assert.Equal(100 + 80 + 50, result.Score);
    }

    [Fact]
    public void DifficultyReload_CarriesTiersAcross()
    {
        // Mirrors LiveNoteMatcher.Reload: judged sounds before the horizon
        // keep verdict AND tier, so the replayed score stays tier-weighted.
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        var level0 = new ChartLevel { DifficultyIndex = 0 };
        level0.Notes.Add(Note(beat: 2, fret: 3));
        level0.Notes.Add(Note(beat: 8, fret: 3));
        arr.Levels.Add(level0);
        arr.Phrases.Add("riff");
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(0), 0));

        var matcher = new LiveNoteMatcher();
        matcher.Load(arr, Beats(), _ => 0);
        Assert.NotNull(matcher.OfferNote(midi: 43, timeMs: 1060)); // Great at 1000 ms
        Assert.Equal(80, matcher.Stats.Score);

        matcher.Reload(arr, Beats(), _ => 0, playheadMs: 2000);

        Assert.Equal(1, matcher.Stats.GreatCount);
        Assert.Equal(80, matcher.Stats.Score);
    }
}
