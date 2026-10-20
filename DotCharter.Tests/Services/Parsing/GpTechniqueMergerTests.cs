using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing;

public class GpTechniqueMergerTests
{
    private static FractionalPosition Pos(int beat, int subdiv = 0)
        => new(beat, new Fraction(subdiv, 192));

    private static Arrangement BuildArrangement(params GuitarNote[] notes)
    {
        var arr = new Arrangement { Name = "Lead" };
        var lvl = new ChartLevel();
        foreach (var n in notes) lvl.Notes.Add(n);
        arr.Levels.Add(lvl);
        return arr;
    }

    [Fact]
    public void ApplyTechniquesOnly_CopiesTechniques_KeepsPositionAndFret()
    {
        var dst = BuildArrangement(new GuitarNote
        {
            Position = Pos(4),
            StringIndex = 2,
            Fret = 5,
            Sustain = Pos(2),
            Mute = MuteType.None,
            Hopo = HopoType.None
        });
        var src = BuildArrangement(new GuitarNote
        {
            Position = Pos(4),
            StringIndex = 2,
            Fret = 99, // fret should be ignored
            Sustain = Pos(8),                              // sustain should be ignored
            Mute = MuteType.Palm,
            Hopo = HopoType.HammerOn,
            Vibrato = true
        });

        var stats = GpTechniqueMerger.ApplyTechniquesOnly(dst, src);

        var note = dst.Levels[0].Notes[0];
        Assert.Equal(MuteType.Palm, note.Mute);
        Assert.Equal(HopoType.HammerOn, note.Hopo);
        Assert.True(note.Vibrato);
        Assert.Equal(5, note.Fret);
        Assert.Equal(Pos(2), note.Sustain);
        Assert.Equal(1, stats.NotesUpdated);
        Assert.Equal(1, stats.NotesScanned);
        Assert.Equal(0, stats.NotesUnmatched);
    }

    [Fact]
    public void ApplyTechniquesOnly_UnmatchedSourceNotes_AreCounted_NotInserted()
    {
        var dst = BuildArrangement(new GuitarNote { Position = Pos(4), StringIndex = 2, Fret = 5 });
        var src = BuildArrangement(
            new GuitarNote { Position = Pos(4), StringIndex = 2, Mute = MuteType.Palm },
            new GuitarNote { Position = Pos(8), StringIndex = 0, Mute = MuteType.Palm });

        var stats = GpTechniqueMerger.ApplyTechniquesOnly(dst, src);

        Assert.Single(dst.Levels[0].Notes);
        Assert.Equal(2, stats.NotesScanned);
        Assert.Equal(1, stats.NotesUnmatched);
        Assert.Equal(1, stats.NotesUpdated);
    }

    [Fact]
    public void ApplyDiff_OnlyAppliesChangedFields_PreservingUserEdits()
    {
        // User added Vibrato locally; old GP didn't have it. New GP changes Mute
        // but keeps Vibrato false. ApplyDiff must update Mute (changed) and
        // leave Vibrato alone (unchanged in GP, user-edited locally).
        var dst = BuildArrangement(new GuitarNote
        {
            Position = Pos(4),
            StringIndex = 2,
            Mute = MuteType.None,
            Vibrato = true
        });
        var oldSrc = BuildArrangement(new GuitarNote
        {
            Position = Pos(4),
            StringIndex = 2,
            Mute = MuteType.None,
            Vibrato = false
        });
        var newSrc = BuildArrangement(new GuitarNote
        {
            Position = Pos(4),
            StringIndex = 2,
            Mute = MuteType.Palm,
            Vibrato = false
        });

        var stats = GpTechniqueMerger.ApplyDiff(dst, oldSrc, newSrc);

        var note = dst.Levels[0].Notes[0];
        Assert.Equal(MuteType.Palm, note.Mute);
        Assert.True(note.Vibrato); // preserved
        Assert.Equal(1, stats.FieldsChanged);
    }

    // ---- Chords -----------------------------------------------------------
    //
    // The merger indexed lvl.Notes only, so "re-import a newer GP file and
    // update techniques without losing my charting" was a silent NO-OP for
    // every strum — and the unmatched counter did not even report it.

    private static Arrangement BuildChordArrangement(params Chord[] chords)
    {
        var arr = new Arrangement { Name = "Rhythm" };
        var lvl = new ChartLevel();
        foreach (var c in chords) lvl.Chords.Add(c);
        arr.Levels.Add(lvl);
        return arr;
    }

    private static Chord Strum(FractionalPosition position, params ChordNote[] notes)
    {
        var chord = new Chord { Position = position };
        foreach (var n in notes) chord.ChordNotes.Add(n);
        return chord;
    }

    [Fact]
    public void ApplyTechniquesOnly_CopiesChordNoteTechniques_KeepsPositionAndFret()
    {
        var dst = BuildChordArrangement(Strum(Pos(4),
            new ChordNote { StringIndex = 0, Fret = 3, Sustain = Pos(2) },
            new ChordNote { StringIndex = 1, Fret = 5, Sustain = Pos(2) }));
        var src = BuildChordArrangement(Strum(Pos(4),
            new ChordNote
            {
                StringIndex = 0, Fret = 99, Sustain = Pos(8),
                Mute = MuteType.Palm, Vibrato = true, VibratoStrength = 80
            },
            new ChordNote { StringIndex = 1, Fret = 99, SlideTo = 7, LinkNext = true }));
        src.Levels[0].Chords[0].Accent = true;

        var stats = GpTechniqueMerger.ApplyTechniquesOnly(dst, src);

        var chord = dst.Levels[0].Chords[0];
        Assert.True(chord.Accent);
        Assert.Equal(MuteType.Palm, chord.ChordNotes[0].Mute);
        Assert.True(chord.ChordNotes[0].Vibrato);
        Assert.Equal(80, chord.ChordNotes[0].VibratoStrength);
        Assert.Equal(7, chord.ChordNotes[1].SlideTo);
        Assert.True(chord.ChordNotes[1].LinkNext);
        // Position, fret and sustain are never touched, exactly as for notes.
        Assert.Equal(3, chord.ChordNotes[0].Fret);
        Assert.Equal(5, chord.ChordNotes[1].Fret);
        Assert.Equal(Pos(2), chord.ChordNotes[0].Sustain);

        Assert.Equal(1, stats.ChordsScanned);
        Assert.Equal(1, stats.ChordsUpdated);
        Assert.Equal(0, stats.ChordsUnmatched);
        Assert.True(stats.FieldsChanged >= 5);
    }

    [Fact]
    public void ApplyTechniquesOnly_UnmatchedSourceChords_AreCounted_NotInserted()
    {
        var dst = BuildChordArrangement(Strum(Pos(4), new ChordNote { StringIndex = 0, Fret = 3 }));
        var src = BuildChordArrangement(
            Strum(Pos(4), new ChordNote { StringIndex = 0, Fret = 3, Mute = MuteType.Palm }),
            Strum(Pos(8), new ChordNote { StringIndex = 0, Fret = 5, Mute = MuteType.Palm }));

        var stats = GpTechniqueMerger.ApplyTechniquesOnly(dst, src);

        Assert.Single(dst.Levels[0].Chords);
        Assert.Equal(2, stats.ChordsScanned);
        Assert.Equal(1, stats.ChordsUnmatched);
        Assert.Equal(1, stats.ChordsUpdated);
    }

    [Fact]
    public void ApplyDiff_OnlyAppliesChangedChordFields_PreservingUserEdits()
    {
        // User added vibrato to the top string locally; the new GP file only
        // changed the mute. The vibrato must survive.
        var dst = BuildChordArrangement(Strum(Pos(4),
            new ChordNote { StringIndex = 0, Fret = 3, Mute = MuteType.None, Vibrato = true }));
        var oldSrc = BuildChordArrangement(Strum(Pos(4),
            new ChordNote { StringIndex = 0, Fret = 3, Mute = MuteType.None, Vibrato = false }));
        var newSrc = BuildChordArrangement(Strum(Pos(4),
            new ChordNote { StringIndex = 0, Fret = 3, Mute = MuteType.Palm, Vibrato = false }));

        var stats = GpTechniqueMerger.ApplyDiff(dst, oldSrc, newSrc);

        var chordNote = dst.Levels[0].Chords[0].ChordNotes[0];
        Assert.Equal(MuteType.Palm, chordNote.Mute);
        Assert.True(chordNote.Vibrato); // preserved
        Assert.Equal(1, stats.FieldsChanged);
        Assert.Equal(1, stats.ChordsUpdated);
    }

    [Fact]
    public void ApplyDiff_NoChangesBetweenOldAndNew_LeavesDestinationUntouched()
    {
        var dst = BuildArrangement(new GuitarNote
        {
            Position = Pos(4),
            StringIndex = 2,
            Mute = MuteType.Palm,
            Vibrato = true
        });
        var oldSrc = BuildArrangement(new GuitarNote { Position = Pos(4), StringIndex = 2, Mute = MuteType.None });
        var newSrc = BuildArrangement(new GuitarNote { Position = Pos(4), StringIndex = 2, Mute = MuteType.None });

        var stats = GpTechniqueMerger.ApplyDiff(dst, oldSrc, newSrc);

        var note = dst.Levels[0].Notes[0];
        Assert.Equal(MuteType.Palm, note.Mute);
        Assert.True(note.Vibrato);
        Assert.Equal(0, stats.FieldsChanged);
    }
}
