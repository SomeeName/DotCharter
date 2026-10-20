namespace DotCharter.Tests.Services.Tempo;

public class TempoServiceTests
{
    private static List<BeatMarker> BuildBeats(int count, double bpm = 120.0, double startMs = 0.0)
    {
        var beats = new List<BeatMarker>();
        var msPerBeat = 60000.0 / bpm;
        for (var i = 0; i < count; i++)
            beats.Add(new BeatMarker { Time = startMs + i * msPerBeat, Bpm = bpm });
        return beats;
    }

    [Fact]
    public void GenerateInitialBeats_FillsExpectedCountAndLogs()
    {
        var logger = new FakeAppLogger();
        var svc = new TempoService(logger);
        var beats = new List<BeatMarker>();

        svc.GenerateInitialBeats(beats, 2000, 120.0);

        // 120 BPM → 500ms per beat → 5 beats at 0, 500, 1000, 1500, 2000.
        Assert.Equal(5, beats.Count);
        Assert.Equal(0.0, beats[0].Time);
        Assert.Equal(500.0, beats[1].Time, 3);
        Assert.True(logger.HasEntryContaining("INFO", "Generated 5 initial beats"));
    }

    [Fact]
    public void GenerateInitialBeats_SkipsWhenBeatsAlreadyExist()
    {
        var logger = new FakeAppLogger();
        var svc = new TempoService(logger);
        var beats = BuildBeats(3);
        var originalCount = beats.Count;

        svc.GenerateInitialBeats(beats, 5000, 120.0);

        Assert.Equal(originalCount, beats.Count);
        Assert.True(logger.HasEntryContaining("TRACE", "skipped"));
    }

    [Fact]
    public void UpdateBpm_RejectsOutOfRangeIndexAndWarns()
    {
        var logger = new FakeAppLogger();
        var svc = new TempoService(logger);
        var beats = BuildBeats(3);

        svc.UpdateBpm(beats, 99, 90.0);

        Assert.True(logger.HasEntryContaining("WARN", "out of range"));
        Assert.All(beats, b => Assert.Equal(120.0, b.Bpm));
    }

    [Fact]
    public void UpdateBpm_ShiftsDownstreamUnanchoredBeats()
    {
        var logger = new FakeAppLogger();
        var svc = new TempoService(logger);
        var beats = BuildBeats(4);
        var originalBeat3Time = beats[3].Time;

        // Halve the tempo at beat 1 → downstream spacing doubles.
        svc.UpdateBpm(beats, 1, 60.0);

        Assert.Equal(60.0, beats[1].Bpm);
        Assert.True(beats[3].Time > originalBeat3Time,
            $"Expected downstream beat to shift later; was {originalBeat3Time}, now {beats[3].Time}.");
    }

    /// <summary>
    ///     A tempo change applies FORWARD from the beat it is written on — it
    ///     says how long the beats AFTER this one last. The beat itself must not
    ///     move: the charter clicked it where it is.
    ///     <para>
    ///         <c>RecalculateBeatTimes</c> started its walk at <c>startIndex</c>
    ///         rather than <c>startIndex + 1</c>, so it re-derived the edited
    ///         beat's own time from its PREDECESSOR's tempo. On a grid whose
    ///         spacing matches its printed BPM that is a no-op, which is why it
    ///         survived — but every imported chart (RS-XML, EOF, psarc) and every
    ///         hand-dragged beat has spacing the BPM field only approximates, and
    ///         there opening the beat dialog and touching the BPM yanked the beat
    ///         back onto the previous beat's grid before applying the change.
    ///     </para>
    /// </summary>
    [Fact]
    public void UpdateBpm_DoesNotMoveTheBeatItIsWrittenOn()
    {
        var svc = new TempoService(new FakeAppLogger());

        // An imported grid: the times are what the file said, the Bpm fields are
        // a rounded derivation of them. Beat 2 sits 40 ms later than 120 BPM
        // from beat 1 would put it.
        var beats = new List<BeatMarker>
        {
            new() { Time = 0, Bpm = 120 },
            new() { Time = 500, Bpm = 120 },
            new() { Time = 1040, Bpm = 120 },
            new() { Time = 1540, Bpm = 120 }
        };

        svc.UpdateBpm(beats, 2, 60.0);

        Assert.Equal(1040.0, beats[2].Time, 6);
        // …and the new tempo governs what follows it.
        Assert.Equal(2040.0, beats[3].Time, 6);
    }

    [Fact]
    public void UpdateBpm_DoesNotShiftAnchoredBeats()
    {
        var logger = new FakeAppLogger();
        var svc = new TempoService(logger);
        var beats = BuildBeats(4);
        beats[2].IsAnchor = true;
        var anchoredTime = beats[2].Time;

        svc.UpdateBpm(beats, 1, 60.0);

        Assert.Equal(anchoredTime, beats[2].Time);
    }

    [Fact]
    public void SetUniformBpm_RewritesAllBeatsAndClearsAnchors()
    {
        var logger = new FakeAppLogger();
        var svc = new TempoService(logger);
        var beats = BuildBeats(5);
        beats[0].Time = 100.0;
        beats[2].IsAnchor = true;

        svc.SetUniformBpm(beats, 60.0);

        Assert.All(beats, b => Assert.Equal(60.0, b.Bpm));
        Assert.All(beats, b => Assert.False(b.IsAnchor));
        Assert.Equal(100.0, beats[0].Time);
        Assert.Equal(1100.0, beats[1].Time, 3);
        Assert.True(logger.HasEntryContaining("INFO", "Set uniform BPM"));
    }

    [Fact]
    public void SnapToGrid_ReturnsZeroForEmptyBeatList()
    {
        var svc = new TempoService(new FakeAppLogger());
        Assert.Equal(FractionalPosition.Zero, svc.SnapToGrid(1234, Array.Empty<BeatMarker>(), 16));
    }

    [Fact]
    public void SnapToGrid_SnapsToNearestSubdivision()
    {
        var svc = new TempoService(new FakeAppLogger());
        var beats = BuildBeats(3); // 500ms per beat

        // 125 ms into a 500 ms beat = quarter of the way.
        var pos = svc.SnapToGrid(125, beats, 16);

        Assert.Equal(0, pos.BeatId);
        Assert.Equal(0.25, pos.Fraction.DoubleValue, 6);
    }

    /// <summary>
    ///     Past the end of the beat map the snap has to extrapolate, and it must
    ///     extrapolate off the LAST interval — the same rule
    ///     <c>TimelineMath.CalculateTimeMs</c> renders by. Off the FIRST one the
    ///     two disagree on any chart with a tempo change: the note lands at a
    ///     different instant than the click that placed it, with no error anywhere.
    ///     <para>
    ///         This is the third copy of a bug already fixed in
    ///         <c>TimelineMath.PositionFromMs</c> and <c>AudioToChartAnalyzer</c>,
    ///         and it is reachable after any RS-XML/EOF import (whose beat map ends
    ///         with the chart, not with the audio) or a Remove Beat.
    ///     </para>
    /// </summary>
    [Fact]
    public void SnapToGrid_PastTheLastBeat_ExtrapolatesOffTheLastInterval()
    {
        var svc = new TempoService(new FakeAppLogger());

        // 60 BPM (1000 ms) for two beats, then 240 BPM (250 ms) for two more —
        // the beat map ends at 2500 ms while the audio runs on.
        var beats = new List<BeatMarker>
        {
            new() { Time = 0, Bpm = 60 },
            new() { Time = 1000, Bpm = 60 },
            new() { Time = 2000, Bpm = 240 },
            new() { Time = 2250, Bpm = 240 },
            new() { Time = 2500, Bpm = 240 }
        };

        // 200 ms past the last beat. The last interval is 250 ms, so that is
        // 12.8/16 of a beat and snaps to 13/16; off the FIRST interval (1000 ms)
        // it would be 3.2/16 and snap to 3/16.
        var pos = svc.SnapToGrid(2700, beats, 16);

        Assert.Equal(4, pos.BeatId);
        Assert.Equal(new Fraction(13, 16), pos.Fraction);

        // …and the whole point: what the snap chose renders where the user
        // clicked, to within the grid step it snapped to (250/16 = 15.6 ms).
        // Off the first interval it renders at 2546.9 — 153 ms BEFORE the click.
        Assert.InRange(pos.GetPositionMs(beats), 2700.0 - 15.7, 2700.0 + 15.7);
    }

    /// <summary>A one-beat map has no interval at all and must still answer
    /// something finite rather than dividing by nothing.</summary>
    [Fact]
    public void SnapToGrid_PastASingleBeat_UsesTheFallbackInterval()
    {
        var svc = new TempoService(new FakeAppLogger());
        var beats = new List<BeatMarker> { new() { Time = 0, Bpm = 120 } };

        var pos = svc.SnapToGrid(250, beats, 16);

        Assert.Equal(0, pos.BeatId);
        Assert.Equal(new Fraction(8, 16), pos.Fraction);
    }
}
