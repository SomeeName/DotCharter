using DotCharter.Services.Editing.Fixing;

namespace DotCharter.Tests.Services.Editing.Fixing;

/// <summary>
///     <c>ArrangementAutoFixService.FixNoteLengths</c> against
///     <c>PassOtherNotes</c> — the flag that says "this sound is ALLOWED to ring
///     past the next sound on its string" (the inspector's ring-out / let-ring
///     toggle, <c>SelectedNoteMutationCommands.TogglePassOtherNotes</c>).
///     <para>
///         The single-note branch honoured it and the CHORD-NOTE branch did not,
///         so one auto-fix run silently truncated every authored ring-out on
///         every chord in the chart — a fixer changing far more than the finding
///         it names, which is the shape that cost 27 linked chords in v0.40.
///         <c>ChartSpacingEnforcer</c> is the reference implementation and reads
///         the flag off the PARENT CHORD for each of its chord notes
///         (<c>new SoundEntry(chordNote.StringIndex, chord.Position, chordNote,
///         chord.PassOtherNotes)</c>), because <c>PassOtherNotes</c> lives on
///         <c>Chord</c>, not on <c>ChordNote</c> — a whole strum rings out or it
///         does not.
///     </para>
/// </summary>
public sealed class AutoFixPassOtherNotesTests
{
    private static ArrangementFixResult Fix(Arrangement arr)
    {
        var result = new ArrangementFixResult();
        new ArrangementAutoFixService().Fix(arr, result);
        return result;
    }

    /// <summary>A sound on string 0 at beat 0 running four beats, with the next
    /// sound on the same string at beat 2 — an overlap of two beats that
    /// <c>FixNoteLengths</c> exists to cut back.</summary>
    private static Arrangement RingingChord(bool passOtherNotes)
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        var level = new ChartLevel();

        var chord = new Chord { Position = FractionalPosition.Zero, PassOtherNotes = passOtherNotes };
        chord.ChordNotes.Add(new ChordNote
        {
            StringIndex = 0, Fret = 5, Sustain = new FractionalPosition(4)
        });
        level.Chords.Add(chord);

        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(2), StringIndex = 0, Fret = 7
        });

        arr.Levels.Add(level);
        return arr;
    }

    private static Arrangement RingingNote(bool passOtherNotes)
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        var level = new ChartLevel();

        level.Notes.Add(new GuitarNote
        {
            Position = FractionalPosition.Zero, StringIndex = 0, Fret = 5,
            Sustain = new FractionalPosition(4), PassOtherNotes = passOtherNotes
        });
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(2), StringIndex = 0, Fret = 7
        });

        arr.Levels.Add(level);
        return arr;
    }

    [Fact]
    public void AChordSetToRingOutKeepsItsSustain()
    {
        var arr = RingingChord(passOtherNotes: true);

        var result = Fix(arr);

        Assert.Equal(new FractionalPosition(4), arr.Levels[0].Chords[0].ChordNotes[0].Sustain);
        Assert.Equal(0, result.Changes.GetValueOrDefault("noteLengthsFixed"));
    }

    /// <summary>The other half of the same rule: without the flag the overlap is
    /// still cut back, so the fix narrows the fixer rather than disabling it.</summary>
    [Fact]
    public void AnOrdinaryChordStillHasItsOverlapTrimmed()
    {
        var arr = RingingChord(passOtherNotes: false);

        Fix(arr);

        Assert.Equal(new FractionalPosition(2), arr.Levels[0].Chords[0].ChordNotes[0].Sustain);
    }

    /// <summary>The single-note branch already read the flag; pinned here so the
    /// two halves are stated together and cannot drift apart again.</summary>
    [Fact]
    public void ANoteSetToRingOutKeepsItsSustain()
    {
        var arr = RingingNote(passOtherNotes: true);

        Fix(arr);

        Assert.Equal(new FractionalPosition(4), arr.Levels[0].Notes[0].Sustain);
    }

    [Fact]
    public void AnOrdinaryNoteStillHasItsOverlapTrimmed()
    {
        var arr = RingingNote(passOtherNotes: false);

        Fix(arr);

        Assert.Equal(new FractionalPosition(2), arr.Levels[0].Notes[0].Sustain);
    }
}
