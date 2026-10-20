namespace DotCharter.Tests.Services.Export;

/// <summary>
///     The per-note left-hand finger and pick direction and the per-chord strum
///     direction (#798) go through every format, and a chart that states none
///     of them exports exactly what it always did.
/// </summary>
public sealed partial class NoteFingerPickStrumTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-798-" + Guid.NewGuid().ToString("N"));

    public NoteFingerPickStrumTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { /* a leftover temp dir is harmless */ }
    }

    private static ChartProject Project(Action<ChartLevel> fill)
    {
        var project = new ChartProject();
        for (var i = 0; i < 16; i++)
        {
            project.Beats.Add(new BeatMarker
            { Time = i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0, BeatsInMeasure = 4, NoteDenominator = 4 });
        }

        var arr = new Arrangement { ArrangementType = ArrangementType.Lead, Name = "Lead", StringCount = 6 };
        for (var s = 0; s < 6; s++) arr.TuningPitches.Add(new TuningOffset(0));
        arr.ChordTemplates.Add(new ChordTemplate { Id = 1, Name = "F", Frets = { [0] = 1, [1] = 3, [2] = 3 } });
        arr.Phrases.AddRange(["COUNT", "END"]);
        arr.PhraseIterations.Add(new PhraseIteration(FractionalPosition.Zero, 0));
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(8), 1));

        var level = new ChartLevel { DifficultyIndex = 0 };
        fill(level);
        level.Anchors.Add(new FretAnchor(FractionalPosition.Zero, 1));
        arr.Levels.Add(level);
        project.Arrangements.Add(arr);
        return project;
    }

    private static Chord Strum(PickDirectionType strum, int beat)
    {
        var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = 1, Strum = strum };
        foreach (var (s, f) in new[] { (0, 1), (1, 3), (2, 3) })
            chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f });
        return chord;
    }

    private static ChartProject Fingered() => Project(level =>
    {
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(1), StringIndex = 0, Fret = 5,
            LeftHandFinger = 0, PickDirection = PickDirectionType.Up
        });
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(2), StringIndex = 1, Fret = 7,
            LeftHandFinger = 3, PickDirection = PickDirectionType.Down
        });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(3), StringIndex = 2, Fret = 7 });
        level.Chords.Add(Strum(PickDirectionType.Up, 4));
        level.Chords.Add(Strum(PickDirectionType.Down, 5));
        level.Chords.Add(Strum(PickDirectionType.None, 6));
    });

    private static void AssertFingered(ChartLevel level)
    {
        var notes = level.Notes.OrderBy(n => n.Position).ToList();
        Assert.Equal(0, notes[0].LeftHandFinger);
        Assert.Equal(PickDirectionType.Up, notes[0].PickDirection);
        Assert.Equal(3, notes[1].LeftHandFinger);
        Assert.Equal(PickDirectionType.Down, notes[1].PickDirection);
        Assert.Equal(-1, notes[2].LeftHandFinger);
        Assert.Equal(PickDirectionType.None, notes[2].PickDirection);

        var chords = level.Chords.OrderBy(c => c.Position).ToList();
        Assert.Equal(PickDirectionType.Up, chords[0].Strum);
        Assert.Equal(PickDirectionType.Down, chords[1].Strum);
        Assert.Equal(PickDirectionType.None, chords[2].Strum);
    }
}
