
namespace DotCharter.Tests.Services.Editing.Validation;

public sealed class TechniqueCombinationRuleTests
{
    [Fact]
    public void AHarmonicNoteWithVibrato_WarnsAsD70()
    {
        var note = Note(h: HarmonicType.Normal, vibrato: true);

        var finding = Assert.Single(Findings(Project(note), ArrangementIssueType.HarmonicVibrato));

        Assert.Equal("D70", finding.Type.GetCode());
        Assert.Equal(ArrangementIssueSeverity.Warning, finding.Severity);
        Assert.Same(note, finding.Note);
    }

    [Theory]
    [InlineData(HarmonicType.None, true)]
    [InlineData(HarmonicType.Normal, false)]
    [InlineData(HarmonicType.Pinch, false)]
    public void AHarmonicWithoutVibrato_OrVibratoWithoutAHarmonic_IsClean(HarmonicType harmonic, bool vibrato) =>
        Assert.Empty(Findings(Project(Note(h: harmonic, vibrato: vibrato)), ArrangementIssueType.HarmonicVibrato));

    [Fact]
    public void AChordWithTwoVibratoHarmonics_WarnsOnceAndNamesTheFirstString()
    {
        var chord = new Chord { Position = new FractionalPosition(1) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 12, Harmonic = HarmonicType.Normal, Vibrato = true });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 12, Harmonic = HarmonicType.Normal, Vibrato = true });
        var project = Project(null, chord);

        var finding = Assert.Single(Findings(project, ArrangementIssueType.HarmonicVibrato));

        Assert.Same(chord, finding.Chord);
        Assert.Equal(2, finding.StringIndex);
    }

    [Fact]
    public void TheFix_ClearsTheVibratoOnly_AsOneUndoStep()
    {
        var note = Note(h: HarmonicType.Pinch, vibrato: true);
        note.VibratoStrength = 3;
        var project = Project(note);
        var level = project.Arrangements[0].Levels[0];
        var issue = Assert.Single(Findings(project, ArrangementIssueType.HarmonicVibrato));
        var history = new HistoryService(new FakeAppLogger());

        history.Do(Assert.IsAssignableFrom<IChartAction>(HarmonicVibratoFix.For(issue, level)));

        Assert.False(note.Vibrato);
        Assert.Equal(0, note.VibratoStrength);
        Assert.Equal(HarmonicType.Pinch, note.Harmonic);
        Assert.Empty(Findings(project, ArrangementIssueType.HarmonicVibrato));
        history.Undo();
        Assert.True(note.Vibrato);
        Assert.Equal(3, note.VibratoStrength);
    }

    [Fact]
    public void TheChordFix_ClearsOnlyTheHarmonicNotes()
    {
        var chord = new Chord { Position = new FractionalPosition(1) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3, Vibrato = true });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 12, Harmonic = HarmonicType.Normal, Vibrato = true });
        var project = Project(null, chord);
        var issue = Assert.Single(Findings(project, ArrangementIssueType.HarmonicVibrato));
        var history = new HistoryService(new FakeAppLogger());

        history.Do(Assert.IsAssignableFrom<IChartAction>(HarmonicVibratoFix.For(issue, project.Arrangements[0].Levels[0])));

        Assert.True(chord.ChordNotes[0].Vibrato);
        Assert.False(chord.ChordNotes[1].Vibrato);
        history.Undo();
        Assert.True(chord.ChordNotes[1].Vibrato);
    }

    [Theory]
    [InlineData(5, 6, true)]
    [InlineData(5, 4, true)]
    [InlineData(5, 5, false)]
    [InlineData(5, 7, false)]
    public void AnUnpitchedSlideOfOneFret_WarnsAsD71(int fret, int to, bool flagged)
    {
        var note = Note(fret: fret);
        note.SlideTo = to;
        note.UnpitchedSlide = true;

        var found = Findings(Project(note), ArrangementIssueType.UnpitchedSlideOneFret);

        Assert.Equal(flagged, found.Count == 1);
        if (flagged) Assert.Equal("D71", found[0].Type.GetCode());
    }

    [Fact]
    public void AMutedOneFretUnpitchedSlide_IsAPickScrapeAndNotThisFinding()
    {
        var note = Note(fret: 21);
        note.SlideTo = 22;
        note.UnpitchedSlide = true;
        note.Mute = MuteType.Full;

        Assert.Empty(Findings(Project(note), ArrangementIssueType.UnpitchedSlideOneFret));
    }

    [Fact]
    public void APitchedOneFretSlide_IsNotThisFinding()
    {
        var note = Note(fret: 5);
        note.SlideTo = 6;

        Assert.Empty(Findings(Project(note), ArrangementIssueType.UnpitchedSlideOneFret));
    }

    [Fact]
    public void AChordNoteUnpitchedSlideOfOneFret_NamesItsString()
    {
        var chord = new Chord { Position = new FractionalPosition(1) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 4, Fret = 7, SlideTo = 8, UnpitchedSlide = true });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 7, SlideTo = 12, UnpitchedSlide = true });

        var finding = Assert.Single(Findings(Project(null, chord), ArrangementIssueType.UnpitchedSlideOneFret));

        Assert.Equal(4, finding.StringIndex);
    }

    private static List<ArrangementIssue> Findings(ChartProject project, ArrangementIssueType type) =>
        [.. new ArrangementValidationService(new FakeAppLogger())
            .Validate(project.Arrangements[0], project.Beats, null, project)
            .Where(issue => issue.Type == type)];

    private static GuitarNote Note(HarmonicType h = HarmonicType.None, bool vibrato = false, int fret = 7) =>
        new() { Position = new FractionalPosition(1), StringIndex = 1, Fret = fret, Harmonic = h, Vibrato = vibrato };

    private static ChartProject Project(GuitarNote? note, Chord? chord = null)
    {
        var project = new ChartProject();
        for (var i = 0; i < 24; i++) project.Beats.Add(new BeatMarker { Time = i * 500 });
        var level = new ChartLevel();
        if (note != null) level.Notes.Add(note);
        if (chord != null) level.Chords.Add(chord);
        var arrangement = new Arrangement();
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        return project;
    }
}
