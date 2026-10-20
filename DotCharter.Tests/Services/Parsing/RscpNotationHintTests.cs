using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Tests.Services.Parsing;

/// <summary>
/// The optional authored-notation-duration hint (<c>nd</c>, quarter-note units)
/// on <c>soundNote</c>/<c>soundChord</c>: a backward-tolerant v3 addition. Hints
/// must survive save/load byte-exactly, and files without the attribute (every
/// pre-hint project) must load exactly as before — null hints everywhere.
/// </summary>
public sealed class RscpNotationHintTests
{
    private static ChartProject NewProject()
    {
        var project = new ChartProject();
        for (var i = 0; i < 8; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
        var arr = new Arrangement { Name = "Lead" };
        var level = new ChartLevel();
        arr.Levels.Add(level);
        project.Arrangements.Add(arr);
        return project;
    }

    private static ChartProject RoundTrip(ChartProject project)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-ndtest-{Guid.NewGuid():N}.rscp");
        try
        {
            var serializer = new RscpChartSerializer();
            serializer.Save(path, project);
            return serializer.Load(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void NotationHints_SurviveSaveLoad_OnNotesAndChords()
    {
        var project = NewProject();
        var level = project.Arrangements[0].Levels[0];
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(0), StringIndex = 2, Fret = 5,
            NotationDuration = new Fraction(1, 2) // authored eighth
        });
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(1), StringIndex = 2, Fret = 7,
            NotationDuration = new Fraction(1, 3) // authored eighth triplet
        });
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(2), StringIndex = 2, Fret = 8 // no hint
        });
        var chord = new Chord
        {
            Position = new FractionalPosition(3),
            NotationDuration = new Fraction(3, 2) // authored dotted quarter
        };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 2 });
        level.Chords.Add(chord);

        var loaded = RoundTrip(project);
        var loadedLevel = loaded.Arrangements[0].Levels[0];

        var notes = loadedLevel.Notes.OrderBy(n => n.Position).ToList();
        Assert.Equal(new Fraction(1, 2), notes[0].NotationDuration);
        Assert.Equal(new Fraction(1, 3), notes[1].NotationDuration);
        Assert.Null(notes[2].NotationDuration);
        Assert.Equal(new Fraction(3, 2), Assert.Single(loadedLevel.Chords).NotationDuration);
    }

    [Fact]
    public void FilesWithoutTheAttribute_LoadWithNullHints()
    {
        // A pre-hint project (no `nd` anywhere) — the exact shape every old
        // save has. Loading must not synthesize hints.
        var project = NewProject();
        var level = project.Arrangements[0].Levels[0];
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(0), StringIndex = 0, Fret = 3 });
        var chord = new Chord { Position = new FractionalPosition(1) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 2 });
        level.Chords.Add(chord);

        var loaded = RoundTrip(project); // writer omits nd when null

        var loadedLevel = loaded.Arrangements[0].Levels[0];
        Assert.Null(Assert.Single(loadedLevel.Notes).NotationDuration);
        Assert.Null(Assert.Single(loadedLevel.Chords).NotationDuration);
    }

    [Fact]
    public void CloneAndCopyFrom_CarryTheHint()
    {
        // ChartProjectCloner snapshots go through Clone()/CopyFrom — a field
        // missed there silently vanishes from undo history and saves.
        var note = new GuitarNote { NotationDuration = new Fraction(1, 4) };
        Assert.Equal(new Fraction(1, 4), note.Clone().NotationDuration);

        var chord = new Chord { NotationDuration = new Fraction(1, 2) };
        Assert.Equal(new Fraction(1, 2), chord.Clone().NotationDuration);
    }
}
