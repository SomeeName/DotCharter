namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     E07 — a bend authored on an open string. Ported from Editor on Fire
///     ("One or more open notes are defined as bends"), which blocks the save:
///     a Rocksmith bend is an offset from a FRETTED note, so an open string has
///     nothing to bend from and the game has no defined behaviour for it.
/// </summary>
public sealed class OpenNoteBendRuleTests
{
    private static List<BeatMarker> Beats(int count = 16) =>
        [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    private static IReadOnlyList<ArrangementIssue> Violations(Arrangement arr) =>
        new ArrangementValidationService(new FakeAppLogger())
            .Validate(arr, Beats())
            .Where(i => i.Type == ArrangementIssueType.OpenNoteWithBend)
            .ToList();

    private static Arrangement WithNote(int fret, double bendStep, int capo = 0)
    {
        var arr = new Arrangement { Capo = capo };
        var level = new ChartLevel();
        arr.Levels.Add(level);

        var note = new GuitarNote { Position = new FractionalPosition(1), StringIndex = 2, Fret = fret };
        note.BendValues.Add(new BendValue(new FractionalPosition(1), bendStep));
        level.Notes.Add(note);

        return arr;
    }

    [Fact]
    public void AnOpenStringCarryingABendIsReported()
    {
        var issue = Assert.Single(Violations(WithNote(fret: 0, bendStep: 1.0)));

        Assert.Equal("E07", issue.Code);
        Assert.Equal(2, issue.StringIndex);
        Assert.Contains("String 3", issue.Message);
    }

    [Fact]
    public void AFrettedNoteCarryingABendIsFine() => Assert.Empty(Violations(WithNote(fret: 5, bendStep: 1.0)));

    /// <summary>
    ///     Importers routinely emit a leading 0.0 point to anchor a bend curve's
    ///     start. Treating that as a bend would fire on ordinary charts.
    /// </summary>
    [Fact]
    public void AZeroStepBendPointIsNotABend() => Assert.Empty(Violations(WithNote(fret: 0, bendStep: 0.0)));

    /// <summary>
    ///     A capo stops the string, so fret 0 under a capo cannot be bent either
    ///     — and "open" in this codebase means "at or below the capo".
    /// </summary>
    [Fact]
    public void AStringStoppedByTheCapoCannotBeBentEither()
    {
        var issue = Assert.Single(Violations(WithNote(fret: 0, bendStep: 1.0, capo: 3)));

        Assert.Contains("capo", issue.Message);
    }

    /// <summary>A note above the capo is genuinely fretted by the hand.</summary>
    [Fact]
    public void ANoteAboveTheCapoIsStillBendable() => Assert.Empty(Violations(WithNote(fret: 5, bendStep: 1.0, capo: 3)));

    /// <summary>One finding per SOUND — a strum whose open strings all carry a
    ///     stray bend is one mistake, not six.</summary>
    [Fact]
    public void AChordReportsOnceRatherThanOncePerOpenString()
    {
        var arr = new Arrangement();
        var level = new ChartLevel();
        arr.Levels.Add(level);

        var chord = new Chord { Position = new FractionalPosition(1), TemplateId = -1 };
        foreach (var stringIndex in new[] { 0, 1, 2 })
        {
            var chordNote = new ChordNote { StringIndex = stringIndex, Fret = 0 };
            chordNote.BendValues.Add(new BendValue(new FractionalPosition(1), 1.0));
            chord.ChordNotes.Add(chordNote);
        }

        level.Chords.Add(chord);

        var issue = Assert.Single(Violations(arr));
        Assert.Same(chord, issue.Chord);
    }

    [Fact]
    public void AnOpenStringWithNoBendAtAllIsFine()
    {
        var arr = new Arrangement();
        var level = new ChartLevel();
        arr.Levels.Add(level);
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), StringIndex = 0, Fret = 0 });

        Assert.Empty(Violations(arr));
    }
}
