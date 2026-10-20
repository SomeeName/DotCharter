using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     Early/late feedback: the SIGN of the timing delta, which
///     <see cref="LiveTimingTier"/> discards (it grades |delta|, so a rush and
///     a drag of equal size are the same tier). The direction is purely
///     informational — it must never move the score.
///     The load-bearing cases are the two where NO honest attack time exists —
///     a miss, and a CQT pull-verified hit — both of which are judged at the
///     playhead when the window closes and would otherwise report a large,
///     fictional "late".
/// </summary>
public sealed class LiveTimingDirectionTests
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

    private static GuitarNote Note(int beat, int fret) => new()
    {
        Position = new FractionalPosition(beat),
        Fret = fret,
        StringIndex = 0
    };

    // Low E fret 3 => MIDI 43; chart note at beat 2 = 1000 ms.
    private static Arrangement OneNoteArr()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 3));
        return arr;
    }

    private static LiveNoteMatcher Loaded(Arrangement arr)
    {
        var matcher = new LiveNoteMatcher();
        matcher.Load(arr, arr.Levels[0], Beats());
        return matcher;
    }

    // The on-time band is ±LiveTimingReport.OnTimeWindowMs (15 ms) — deliberately
    // NOT the tier windows, which are preset-scaled and asymmetric.
    [Theory]
    [InlineData(1000, LiveTimingDirection.OnTime)] // dead on
    [InlineData(1015, LiveTimingDirection.OnTime)] // +15, the inclusive edge
    [InlineData(985, LiveTimingDirection.OnTime)]  // −15, symmetric edge
    [InlineData(1016, LiveTimingDirection.Late)]   // +16, just past the edge
    [InlineData(984, LiveTimingDirection.Early)]   // −16, just past the edge
    [InlineData(1120, LiveTimingDirection.Late)]   // a real drag
    [InlineData(880, LiveTimingDirection.Early)]   // a real rush
    public void HitDelta_IsSignedEarlyOrLate(double offerAtMs, LiveTimingDirection expected)
    {
        var matcher = Loaded(OneNoteArr());

        var result = matcher.OfferNote(midi: 43, timeMs: offerAtMs);

        Assert.NotNull(result);
        Assert.Equal(LivePlayVerdict.Hit, result!.Value.Verdict);
        Assert.Equal(expected, result.Value.Direction);
    }

    /// <summary>A rush and a drag of the same size grade the same TIER but
    /// opposite DIRECTIONS — the whole point of the feature.</summary>
    [Fact]
    public void SameTier_OppositeDirections()
    {
        var early = Loaded(OneNoteArr()).OfferNote(midi: 43, timeMs: 940); // −60
        var late = Loaded(OneNoteArr()).OfferNote(midi: 43, timeMs: 1060); // +60

        Assert.Equal(LiveTimingTier.Great, early!.Value.Tier);
        Assert.Equal(LiveTimingTier.Great, late!.Value.Tier);
        Assert.Equal(LiveTimingDirection.Early, early.Value.Direction);
        Assert.Equal(LiveTimingDirection.Late, late.Value.Direction);
    }

    /// <summary>A miss is judged when its window EXPIRES, so its DetectedAtMs is
    /// the playhead, not a detection. Signing that delta would label every miss
    /// a huge "late".</summary>
    [Fact]
    public void Miss_HasNoDirection()
    {
        var arr = OneNoteArr();
        var matcher = Loaded(arr);

        var events = matcher.AdvanceTo(playheadMs: 2000); // window closed, nothing played

        Assert.Single(events);
        Assert.Equal(LivePlayVerdict.Miss, events[0].Verdict);
        Assert.Equal(LiveTimingDirection.None, events[0].Direction);
    }

    /// <summary>The CQT chart-verifier confirms WHAT was played, never WHEN the
    /// attack landed: it pull-verifies at the playhead as the window closes. Its
    /// tier is already None; its direction must be too, or every CQT hit would
    /// read as ~250 ms late.</summary>
    [Fact]
    public void CqtPullVerifiedHit_HasNoDirection()
    {
        var arr = OneNoteArr();
        var matcher = new LiveNoteMatcher { ChartVerify = _ => new ChartVerifyOutcome(true, 0.9f) };
        matcher.Load(arr, arr.Levels[0], Beats());

        var events = matcher.AdvanceTo(playheadMs: 2000); // 1000 ms past the chart time

        Assert.Single(events);
        Assert.Equal(LivePlayVerdict.Hit, events[0].Verdict);
        Assert.Equal(LiveTimingTier.None, events[0].Tier);
        Assert.Equal(LiveTimingDirection.None, events[0].Direction);
    }

    /// <summary>Direction is informational only — it must not perturb scoring,
    /// which stays magnitude-only (a 60 ms rush and a 60 ms drag both pay Great).</summary>
    [Fact]
    public void Direction_DoesNotAffectScore()
    {
        var early = Loaded(OneNoteArr());
        var late = Loaded(OneNoteArr());

        early.OfferNote(midi: 43, timeMs: 940);  // −60, Early
        late.OfferNote(midi: 43, timeMs: 1060);  // +60, Late

        Assert.Equal(80, early.Stats.Score); // Great base
        Assert.Equal(early.Stats.Score, late.Stats.Score);
    }

    /// <summary>The chord chroma fallback is the matcher's second honest judge
    /// path and signs its hits identically.</summary>
    [Fact]
    public void ChordChromaPath_IsSignedToo()
    {
        var arr = NewArr();
        var chord = new Chord { Position = new FractionalPosition(2) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 }); // G2 -> pc 7
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 }); // D3 -> pc 2
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 5 }); // G3 -> pc 7
        arr.Levels[0].Chords.Add(chord);

        var matcher = Loaded(arr);
        var chroma = new float[12];
        chroma[7] = 0.5f; // G
        chroma[2] = 0.3f; // D

        var result = matcher.OfferChroma(chroma, timeMs: 940); // −60, early

        Assert.NotNull(result);
        Assert.Equal(LiveTimingDirection.Early, result!.Value.Direction);
    }

    [Theory]
    [InlineData(LivePlayVerdict.Miss, LiveTimingTier.Perfect, -100, LiveTimingDirection.None)]
    [InlineData(LivePlayVerdict.Hit, LiveTimingTier.None, -100, LiveTimingDirection.None)]
    [InlineData(LivePlayVerdict.HitWrongTechnique, LiveTimingTier.Good, -100, LiveTimingDirection.Early)]
    [InlineData(LivePlayVerdict.Hit, LiveTimingTier.Good, 100, LiveTimingDirection.Late)]
    [InlineData(LivePlayVerdict.Hit, LiveTimingTier.Perfect, 0, LiveTimingDirection.OnTime)]
    public void Classify_IsPureAndGatesOnEvidence(
        LivePlayVerdict verdict, LiveTimingTier tier, double deltaMs, LiveTimingDirection expected)
        => Assert.Equal(expected, LiveTiming.Classify(verdict, tier, deltaMs));
}
