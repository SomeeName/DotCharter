namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     #876: the inspector reports a chord note whose link has no note at its sustain end (DLC Builder
///     I08), on any difficulty level, and judges the gap the way the export writes it: whole
///     milliseconds, 1 ms tolerance.
/// </summary>
public sealed class LinkNextChordTargetTests
{
    // 120 BPM: 500 ms per beat.
    private static List<BeatMarker> Beats() =>
        [.. Enumerable.Range(0, 20).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    private static IArrangementValidationService NewService() => new ArrangementValidationService(new FakeAppLogger());

    private static FractionalPosition Beat(int whole, int num = 0, int den = 0) =>
        den == 0 ? new FractionalPosition(whole) : new FractionalPosition(whole, new Fraction(num, den));

    /// <summary>A two-note chord at beat 4, both strings linked, one beat long. String 3 continues into a
    /// note at the sustain end (beat 5), string 4 only has a note at beat 9.</summary>
    private static Arrangement TwoNoteChord(int levelIndex, bool linkString4)
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        for (var i = 0; i <= levelIndex; i++) arr.Levels.Add(new ChartLevel { DifficultyIndex = i });
        var level = arr.Levels[levelIndex];
        var chord = new Chord { Position = Beat(4) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 12, Sustain = Beat(1), LinkNext = true });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 4, Fret = 13, Sustain = Beat(1), LinkNext = linkString4 });
        level.Chords.Add(chord);
        level.Notes.Add(new GuitarNote { Position = Beat(5), StringIndex = 3, Fret = 12 });
        level.Notes.Add(new GuitarNote { Position = Beat(9), StringIndex = 4, Fret = 13 });
        return arr;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    public void A_chord_note_linked_to_nothing_is_reported_and_the_one_that_continues_is_not(int levelIndex)
    {
        var issues = NewService().Validate(TwoNoteChord(levelIndex, linkString4: true), Beats());

        var link = Assert.Single(issues, i => i.Type is ArrangementIssueType.IncorrectLinkNext
            or ArrangementIssueType.LinkNextMissingTargetNote);
        Assert.Equal(4, link.StringIndex);
    }

    [Fact]
    public void A_chord_with_only_the_continuing_note_linked_is_clean()
    {
        var issues = NewService().Validate(TwoNoteChord(0, linkString4: false), Beats());

        Assert.DoesNotContain(issues, i => i.Type is ArrangementIssueType.IncorrectLinkNext
            or ArrangementIssueType.LinkNextMissingTargetNote);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(5, true)]
    [InlineData(2, true)]
    public void The_gap_is_judged_in_whole_milliseconds_with_DLC_Builders_1_ms_tolerance(
        int gapMs, bool reported)
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        arr.Levels.Add(new ChartLevel());
        var chord = new Chord { Position = Beat(4) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 12, Sustain = Beat(1), LinkNext = true });
        arr.Levels[0].Chords.Add(chord);
        // 1 ms = 1/500 of a beat.
        arr.Levels[0].Notes.Add(new GuitarNote { Position = Beat(5, gapMs, 500), StringIndex = 3, Fret = 12 });

        var issues = NewService().Validate(arr, Beats());

        Assert.Equal(reported, issues.Any(i => i.Type == ArrangementIssueType.IncorrectLinkNext));
    }

    [Fact]
    public void A_sub_millisecond_gap_cannot_make_a_false_positive()
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        arr.Levels.Add(new ChartLevel());
        var chord = new Chord { Position = Beat(4) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 12, Sustain = Beat(1, 1, 3), LinkNext = true });
        arr.Levels[0].Chords.Add(chord);
        // Sustain ends at 1000.0 + 166.67 ms; the note sits on the whole millisecond the export writes.
        arr.Levels[0].Notes.Add(new GuitarNote { Position = Beat(5, 1, 3), StringIndex = 3, Fret = 12 });

        var issues = NewService().Validate(arr, Beats());

        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.IncorrectLinkNext);
    }
}
