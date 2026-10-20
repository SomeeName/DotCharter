using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     0.21 technique report card, capture half: per-technique tallies over
///     a run's judged sounds, snapshotted by the session service at Stop.
/// </summary>
public sealed class LiveTechniqueTallyTests
{
    private static ExpectedSound Sound(LivePlayVerdict? verdict = LivePlayVerdict.Hit,
        bool palmMute = false, bool harmonic = false, bool pinch = false, bool legato = false,
        bool slapOrPop = false, int? slideTo = null, bool unpitchedSlide = false, bool fullMute = false)
    {
        var sound = new ExpectedSound
        {
            TimeMs = 1000, MidiNotes = [40],
            PalmMute = palmMute, Harmonic = harmonic, PinchHarmonic = pinch, Legato = legato,
            SlapOrPop = slapOrPop, SlideToMidi = slideTo, UnpitchedSlide = unpitchedSlide,
            FullMute = fullMute
        };
        if (verdict is { } v) sound.Verdict = v;
        return sound;
    }

    [Fact]
    public void EachFlag_TalliesItsTechnique_ByVerdict()
    {
        var tallies = LiveTechniqueTally.Compute(
        [
            Sound(LivePlayVerdict.Hit, palmMute: true),
            Sound(LivePlayVerdict.HitWrongTechnique, palmMute: true),
            Sound(LivePlayVerdict.Miss, palmMute: true),
            Sound(LivePlayVerdict.Hit, legato: true)
        ]);

        Assert.Equal(2, tallies.Count);
        var palm = Assert.Single(tallies, t => t.Technique == PlayedTechnique.PalmMute);
        Assert.Equal(1, palm.Hits);
        Assert.Equal(1, palm.WrongTechnique);
        Assert.Equal(1, palm.Misses);
        Assert.Equal(3, palm.Judged);
        var legato = Assert.Single(tallies, t => t.Technique == PlayedTechnique.Legato);
        Assert.Equal(1, legato.Hits);
    }

    [Fact]
    public void MultiTechniqueSound_CountsInEveryBucket()
    {
        // A palm-muted pull-off that was missed is a miss for BOTH techniques.
        var tallies = LiveTechniqueTally.Compute(
            [Sound(LivePlayVerdict.Miss, palmMute: true, legato: true)]);

        Assert.Equal(2, tallies.Count);
        Assert.All(tallies, t => Assert.Equal(1, t.Misses));
    }

    [Fact]
    public void PinchHarmonic_CountsAsHarmonicToo_MirroringTheMatcherFlags()
    {
        // The matcher sets Harmonic for every harmonic kind and PinchHarmonic
        // on top for pinches — the tally follows the flags as given.
        var tallies = LiveTechniqueTally.Compute(
            [Sound(LivePlayVerdict.Hit, harmonic: true, pinch: true)]);

        Assert.Contains(tallies, t => t.Technique == PlayedTechnique.Harmonic);
        Assert.Contains(tallies, t => t.Technique == PlayedTechnique.PinchHarmonic);
    }

    [Theory]
    [InlineData(45, false)] // pitched slide (SlideToMidi set)
    [InlineData(null, true)] // unpitched slide
    public void EitherSlideKind_TalliesSlide(int? slideTo, bool unpitched)
    {
        var tallies = LiveTechniqueTally.Compute(
            [Sound(LivePlayVerdict.Hit, slideTo: slideTo, unpitchedSlide: unpitched)]);

        var slide = Assert.Single(tallies);
        Assert.Equal(PlayedTechnique.Slide, slide.Technique);
        Assert.Equal(1, slide.Hits);
    }

    [Fact]
    public void RemainingFlags_MapToTheirTechniques()
    {
        var tallies = LiveTechniqueTally.Compute(
        [
            Sound(slapOrPop: true),
            Sound(fullMute: true)
        ]);

        Assert.Contains(tallies, t => t.Technique == PlayedTechnique.SlapOrPop);
        Assert.Contains(tallies, t => t.Technique == PlayedTechnique.FullMute);
    }

    [Fact]
    public void UnjudgedSounds_AreSkipped() => Assert.Empty(LiveTechniqueTally.Compute([Sound(verdict: null, palmMute: true)]));

    [Fact]
    public void PlainSounds_ProduceNoTallies() =>
        // A run over a technique-free chart persists nothing (store stays lean).
        Assert.Empty(LiveTechniqueTally.Compute([Sound(), Sound(LivePlayVerdict.Miss)]));

    // ---- session-service capture at Stop -------------------------------------

    private static List<BeatMarker> Beats(int count = 32, double bpm = 120)
    {
        var ms = 60000.0 / bpm;
        return [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * ms, Bpm = bpm })];
    }

    private static Arrangement PalmMuteArr(int notes = 4)
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        var level = new ChartLevel();
        for (var beat = 2; beat < 2 + notes; beat++)
        {
            level.Notes.Add(new GuitarNote
            {
                Position = new FractionalPosition(beat),
                Fret = 3, StringIndex = 0, Mute = MuteType.Palm
            });
        }

        arr.Levels.Add(level);
        return arr;
    }

    [Fact]
    public void Stop_CapturesTheRunsTechniqueTallies()
    {
        var session = new LivePlaySessionService();
        session.Start(PalmMuteArr(notes: 4), null, Beats(), new LivePlaySessionOptions());
        session.SyncTo(0);

        // Play through without a single attack: every palm mute is a miss.
        for (var t = 0.0; t <= 8000; t += 100) session.HandleTick(t, isPlaying: true);
        session.Stop(1.0);

        var palm = Assert.Single(session.LastTechniqueTallies);
        Assert.Equal(PlayedTechnique.PalmMute, palm.Technique);
        Assert.Equal(4, palm.Misses);
        Assert.Equal(0, palm.Hits);
    }

    [Fact]
    public void Start_ResetsTheLastTallies()
    {
        var session = new LivePlaySessionService();
        session.Start(PalmMuteArr(), null, Beats(), new LivePlaySessionOptions());
        session.SyncTo(0);
        for (var t = 0.0; t <= 8000; t += 100) session.HandleTick(t, isPlaying: true);
        session.Stop(1.0);
        Assert.NotEmpty(session.LastTechniqueTallies);

        session.Start(PalmMuteArr(), null, Beats(), new LivePlaySessionOptions());

        Assert.Empty(session.LastTechniqueTallies);
    }
}
