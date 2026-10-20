using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Tests.Services.Parsing;

/// <summary>
/// The per-string before-beat grace hint on <c>chordNote</c> (<c>gf</c>): a
/// backward-tolerant v3 addition with the SAME contract as the single note's
/// <c>gf</c> — written only when set, parsed if present, never a version bump.
/// A chord grace must survive save/load, and every pre-hint project (no
/// attribute anywhere) must still load with null hints.
/// </summary>
public sealed class RscpChordGraceHintTests
{
    private static ChartProject NewProject()
    {
        var project = new ChartProject();
        for (var i = 0; i < 8; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
        var arr = new Arrangement { Name = "Lead" };
        arr.Levels.Add(new ChartLevel());
        project.Arrangements.Add(arr);
        return project;
    }

    private static ChartProject RoundTrip(ChartProject project)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-cngf-{Guid.NewGuid():N}.rscp");
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

    private static Chord GracedChord()
    {
        var chord = new Chord { Position = new FractionalPosition(1) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5, GraceFret = 3 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 7, GraceFret = 0 }); // open-string grace
        chord.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 7 });                // no grace
        return chord;
    }

    [Fact]
    public void ChordGraceHints_SurviveSaveLoad()
    {
        var project = NewProject();
        project.Arrangements[0].Levels[0].Chords.Add(GracedChord());

        var loaded = RoundTrip(project);

        var notes = Assert.Single(loaded.Arrangements[0].Levels[0].Chords)
            .ChordNotes.OrderBy(cn => cn.StringIndex).ToList();
        Assert.Equal(3, notes[0].GraceFret);
        Assert.Equal(0, notes[1].GraceFret); // fret 0 is a real hint, not "absent"
        Assert.Null(notes[2].GraceFret);
    }

    [Fact]
    public void FilesWithoutTheAttribute_LoadWithNullHints()
    {
        // A pre-hint project: the writer emits no `gf` on any chordNote, which is
        // byte-identical to every older save. Loading must not synthesize hints.
        var project = NewProject();
        var chord = new Chord { Position = new FractionalPosition(1) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
        project.Arrangements[0].Levels[0].Chords.Add(chord);

        var loaded = RoundTrip(project);

        Assert.All(Assert.Single(loaded.Arrangements[0].Levels[0].Chords).ChordNotes,
            cn => Assert.Null(cn.GraceFret));
    }

    [Fact]
    public void CloneAndCopyFrom_CarryTheHint()
    {
        // ChartProjectCloner snapshots go through Clone()/CopyFrom — a field
        // missed there silently vanishes from undo history and from saves.
        Assert.Equal(3, new ChordNote { GraceFret = 3 }.Clone().GraceFret);
        Assert.Null(new ChordNote().Clone().GraceFret);

        var clonedChord = GracedChord().Clone();
        Assert.Equal(new int?[] { 3, 0, null },
            clonedChord.ChordNotes.OrderBy(cn => cn.StringIndex).Select(cn => cn.GraceFret));

        // CopyFrom must also CLEAR a stale hint when the source has none.
        var target = new ChordNote { GraceFret = 9 };
        target.CopyFrom(new ChordNote());
        Assert.Null(target.GraceFret);
    }
}
