namespace DotCharter.Tests.Services.Tempo;

/// <summary>
///     The chart #403 was found on, synthesized: a TRUE grid the notes were written
///     against, and the same grid moved by a planted offset, which is the grid the
///     chart actually ships with. Several tempos and triplets on purpose — a
///     quarter-beat shift lines straight sixteenths up just as well as the true one,
///     and only the mix tells the two apart.
/// </summary>
internal static class BeatGridFixtures
{
    /// <summary>4/4 beats from <paramref name="startMs" />, one tempo per section.</summary>
    public static List<BeatMarker> Grid(double startMs, params (int Beats, double Bpm)[] sections)
    {
        var beats = new List<BeatMarker>();
        var time = startMs;
        foreach (var (count, bpm) in sections)
        {
            for (var k = 0; k < count; k++)
            {
                beats.Add(new BeatMarker
                {
                    Time = time,
                    Bpm = bpm,
                    BeatsInMeasure = 4,
                    NoteDenominator = 4,
                    IsMeasure = beats.Count % 4 == 0
                });
                time += 60000.0 / bpm;
            }
        }

        return beats;
    }

    /// <summary>120 → 150 → 96 → 120 BPM, 60 beats from 2 s.</summary>
    public static List<BeatMarker> TrueGrid() => Grid(2000, (16, 120), (16, 150), (12, 96), (16, 120));

    /// <summary>
    ///     Where every note sits on the TRUE grid: each beat, sixteenths on every
    ///     other beat, an eighth on every fourth, triplets on every third.
    /// </summary>
    public static List<FractionalPosition> NotePositions()
    {
        var positions = new List<FractionalPosition>();
        for (var beat = 1; beat < 56; beat++)
        {
            positions.Add(new FractionalPosition(beat));
            if (beat % 2 == 0)
            {
                positions.Add(new FractionalPosition(beat, new Fraction(1, 4)));
                positions.Add(new FractionalPosition(beat, new Fraction(3, 4)));
            }

            if (beat % 4 == 1) positions.Add(new FractionalPosition(beat, new Fraction(1, 2)));
            if (beat % 3 == 0)
            {
                positions.Add(new FractionalPosition(beat, new Fraction(1, 3)));
                positions.Add(new FractionalPosition(beat, new Fraction(2, 3)));
            }
        }

        return positions;
    }

    /// <summary>
    ///     A chart whose shipped grid is the true grid moved by <paramref name="gridErrorMs" />
    ///     (negative = early, the #403 case) while every sound keeps the time it was
    ///     written at — placed the way an importer places a millisecond.
    /// </summary>
    public static ChartProject Chart(double gridErrorMs)
    {
        var truth = TrueGrid();
        var shipped = truth.Select(b =>
        {
            var moved = b.Clone();
            moved.Time += gridErrorMs;
            return moved;
        }).ToList();

        var project = new ChartProject();
        foreach (var beat in shipped) project.Beats.Add(beat);
        var level = new ChartLevel();
        foreach (var position in NotePositions())
        {
            level.Notes.Add(new GuitarNote
            {
                Position = TimelineMath.PositionFromImportedMs(position.GetPositionMs(truth), shipped),
                StringIndex = 0,
                Fret = 3
            });
        }

        var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        return project;
    }
}
