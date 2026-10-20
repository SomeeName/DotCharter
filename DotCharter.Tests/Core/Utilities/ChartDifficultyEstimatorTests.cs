namespace DotCharter.Tests.Core.Utilities;

/// <summary>
///     0.20 deterministic chart difficulty estimator: a pure pass over an
///     arrangement (note density, chord-change rate, technique mix, tempo)
///     yielding a 1–10 double. Tests pin monotonicity across hand-built
///     charts of obviously increasing difficulty, factor-level monotonicity
///     and bitwise determinism.
/// </summary>
public class ChartDifficultyEstimatorTests
{
    private static List<BeatMarker> Beats(double bpm, int count = 64)
    {
        var msPerBeat = 60000.0 / bpm;
        var beats = new List<BeatMarker>();
        for (var i = 0; i < count; i++) beats.Add(new BeatMarker { Time = i * msPerBeat, Bpm = bpm });
        return beats;
    }

    private static GuitarNote Note(int beat, int num = 0, int den = 1, int fret = 3) => new()
    {
        Position = new FractionalPosition(beat, den > 1 ? new Fraction(num, den) : Fraction.Zero),
        Fret = fret,
        StringIndex = 0
    };

    private static Chord PowerChord(int beat, int rootFret, int templateId) => new()
    {
        Position = new FractionalPosition(beat),
        TemplateId = templateId,
        ChordNotes =
        {
            new ChordNote { StringIndex = 0, Fret = rootFret },
            new ChordNote { StringIndex = 1, Fret = rootFret + 2 }
        }
    };

    private static Arrangement Wrap(ChartLevel level)
    {
        var arr = new Arrangement();
        arr.Levels.Add(level);
        return arr;
    }

    /// <summary>Sparse quarter notes, slow, no techniques, no chords.</summary>
    private static (Arrangement Arr, List<BeatMarker> Beats) EasyChart()
    {
        var level = new ChartLevel();
        for (var beat = 0; beat < 16; beat += 2) level.Notes.Add(Note(beat));
        return (Wrap(level), Beats(90));
    }

    /// <summary>Eighth notes at a moderate tempo.</summary>
    private static (Arrangement Arr, List<BeatMarker> Beats) MediumChart()
    {
        var level = new ChartLevel();
        for (var beat = 0; beat < 16; beat++)
        {
            level.Notes.Add(Note(beat));
            level.Notes.Add(Note(beat, 1, 2));
        }

        return (Wrap(level), Beats(120));
    }

    /// <summary>Sixteenths, changing chords and some techniques, faster.</summary>
    private static (Arrangement Arr, List<BeatMarker> Beats) HardChart()
    {
        var level = new ChartLevel();
        for (var beat = 0; beat < 16; beat++)
        {
            for (var sub = 0; sub < 4; sub++)
            {
                var note = Note(beat, sub, 4, fret: 3 + sub);
                if (sub == 1) note.Hopo = HopoType.HammerOn;
                if (sub == 3) note.SlideTo = 7;
                level.Notes.Add(note);
            }
        }

        for (var beat = 16; beat < 24; beat++) level.Chords.Add(PowerChord(beat, 3 + beat % 4, beat % 4));
        return (Wrap(level), Beats(150));
    }

    /// <summary>Dense, technique-saturated, fast — obviously the hardest.</summary>
    private static (Arrangement Arr, List<BeatMarker> Beats) ExtremeChart()
    {
        var level = new ChartLevel();
        for (var beat = 0; beat < 16; beat++)
        {
            for (var sub = 0; sub < 6; sub++)
            {
                var note = Note(beat, sub, 6, fret: 3 + sub * 2);
                switch (sub % 6)
                {
                    case 0: note.BendValues.Add(new BendValue(note.Position, 2.0)); break;
                    case 1: note.Hopo = HopoType.PullOff; break;
                    case 2: note.SlideTo = 12; break;
                    case 3: note.Harmonic = HarmonicType.Pinch; break;
                    case 4: note.Mute = MuteType.Palm; break;
                    case 5: note.Tremolo = true; note.Vibrato = true; break;
                }

                level.Notes.Add(note);
            }
        }

        for (var beat = 16; beat < 32; beat++) level.Chords.Add(PowerChord(beat, 3 + beat % 8, beat % 8));
        return (Wrap(level), Beats(200));
    }

    [Fact]
    public void HandBuiltCharts_RateInStrictlyIncreasingDifficulty()
    {
        var (easyArr, easyBeats) = EasyChart();
        var (medArr, medBeats) = MediumChart();
        var (hardArr, hardBeats) = HardChart();
        var (extremeArr, extremeBeats) = ExtremeChart();

        var easy = ChartDifficultyEstimator.Estimate(easyArr, easyBeats);
        var medium = ChartDifficultyEstimator.Estimate(medArr, medBeats);
        var hard = ChartDifficultyEstimator.Estimate(hardArr, hardBeats);
        var extreme = ChartDifficultyEstimator.Estimate(extremeArr, extremeBeats);

        Assert.True(easy < medium, $"easy {easy} !< medium {medium}");
        Assert.True(medium < hard, $"medium {medium} !< hard {hard}");
        Assert.True(hard < extreme, $"hard {hard} !< extreme {extreme}");

        // Everything stays on the 1..10 scale, with sane anchors.
        Assert.InRange(easy, 1.0, 4.0);
        Assert.InRange(extreme, 5.0, 10.0);
    }

    [Fact]
    public void Estimate_IsDeterministic()
    {
        var (arr, beats) = HardChart();
        var first = ChartDifficultyEstimator.Estimate(arr, beats);
        var second = ChartDifficultyEstimator.Estimate(arr, beats);
        Assert.Equal(first, second); // exact double equality — no drift
    }

    [Fact]
    public void ChordOrderInTheCollection_DoesNotChangeTheRating()
    {
        var (arr, beats) = HardChart();
        var baseline = ChartDifficultyEstimator.Estimate(arr, beats);

        // Same chords, reversed collection order — signature transitions are
        // counted on the time-sorted sequence, so the rating is identical.
        var reversed = new ChartLevel();
        foreach (var n in arr.Levels[0].Notes) reversed.Notes.Add(n);
        for (var i = arr.Levels[0].Chords.Count - 1; i >= 0; i--)
            reversed.Chords.Add(arr.Levels[0].Chords[i]);

        Assert.Equal(baseline, ChartDifficultyEstimator.Estimate(Wrap(reversed), beats));
    }

    [Fact]
    public void EmptyArrangement_RatesMinimum()
    {
        Assert.Equal(1.0, ChartDifficultyEstimator.Estimate(new Arrangement(), Beats(120)));
        Assert.Equal(1.0, ChartDifficultyEstimator.Estimate(Wrap(new ChartLevel()), Beats(120)));
    }

    [Fact]
    public void HigherDensity_AloneRaisesTheRating()
    {
        var sparse = new ChartLevel();
        for (var beat = 0; beat < 16; beat += 2) sparse.Notes.Add(Note(beat));
        var dense = new ChartLevel();
        for (var beat = 0; beat < 16; beat++)
        {
            dense.Notes.Add(Note(beat));
            dense.Notes.Add(Note(beat, 1, 2));
        }

        var beats = Beats(120);
        Assert.True(ChartDifficultyEstimator.Estimate(Wrap(sparse), beats)
                    < ChartDifficultyEstimator.Estimate(Wrap(dense), beats));
    }

    [Fact]
    public void FasterTempo_AloneRaisesTheRating()
    {
        static ChartLevel Level()
        {
            var level = new ChartLevel();
            for (var beat = 0; beat < 16; beat++) level.Notes.Add(Note(beat));
            return level;
        }

        Assert.True(ChartDifficultyEstimator.Estimate(Wrap(Level()), Beats(90))
                    < ChartDifficultyEstimator.Estimate(Wrap(Level()), Beats(180)));
    }

    [Fact]
    public void TechniqueMix_AloneRaisesTheRating()
    {
        static ChartLevel Level(bool withTechniques)
        {
            var level = new ChartLevel();
            for (var beat = 0; beat < 16; beat++)
            {
                var note = Note(beat);
                if (withTechniques && beat % 2 == 0) note.Hopo = HopoType.HammerOn;
                if (withTechniques && beat % 3 == 0) note.BendValues.Add(new BendValue(note.Position, 1.0));
                level.Notes.Add(note);
            }

            return level;
        }

        var beats = Beats(120);
        Assert.True(ChartDifficultyEstimator.Estimate(Wrap(Level(false)), beats)
                    < ChartDifficultyEstimator.Estimate(Wrap(Level(true)), beats));
    }

    [Fact]
    public void ChangingChordShapes_RateHarderThanOneRepeatedShape()
    {
        static ChartLevel Level(bool changing)
        {
            var level = new ChartLevel();
            for (var beat = 0; beat < 16; beat++)
                level.Chords.Add(PowerChord(beat, changing ? 3 + beat % 4 : 3, changing ? beat % 4 : 0));
            return level;
        }

        var beats = Beats(120);
        Assert.True(ChartDifficultyEstimator.Estimate(Wrap(Level(false)), beats)
                    < ChartDifficultyEstimator.Estimate(Wrap(Level(true)), beats));
    }

    [Fact]
    public void HardestLevel_IsTheOneRated()
    {
        var easyLevel = new ChartLevel { DifficultyIndex = 0 };
        for (var beat = 0; beat < 16; beat += 4) easyLevel.Notes.Add(Note(beat));
        var hardLevel = new ChartLevel { DifficultyIndex = 1 };
        for (var beat = 0; beat < 16; beat++)
        {
            hardLevel.Notes.Add(Note(beat));
            hardLevel.Notes.Add(Note(beat, 1, 2));
        }

        var arr = new Arrangement();
        arr.Levels.Add(easyLevel);
        arr.Levels.Add(hardLevel);

        var beats = Beats(120);
        Assert.Equal(ChartDifficultyEstimator.Estimate(hardLevel, beats),
            ChartDifficultyEstimator.Estimate(arr, beats));
    }
}
