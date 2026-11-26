using DotCharter.Services.Editing.Validation.Rules;
using DotCharter.Tests.Services.Editing.HandShapes;

namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     D17/D18 (<see cref="BoundaryCrossingRule" />, #970): a sustain, a Link-Next chain or a hand shape
///     across a phrase start, a section start or past END, and the trim to the boundary
///     (<see cref="BoundaryCrossing.Trim" />). The grid is 120 BPM, one beat 500 ms; a phrase starts at
///     beat 4, a section at beat 8 and END at beat 12.
/// </summary>
public sealed class BoundaryCrossingRuleTests
{
    private static List<BeatMarker> Beats() =>
        [.. Enumerable.Range(0, 32).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    private static FractionalPosition Pos(double beats) => ChordHandShapePlannerTests.Pos(beats);

    private static Arrangement Arr()
    {
        var arr = ChordHandShapePlannerTests.Arr();
        arr.EventPoints.Add(new EventPoint { Position = Pos(0), Phrase = "intro" });
        arr.EventPoints.Add(new EventPoint { Position = Pos(4), Phrase = "verse" });
        arr.EventPoints.Add(new EventPoint { Position = Pos(8), Section = "chorus" });
        arr.EventPoints.Add(new EventPoint { Position = Pos(12), Phrase = "END" });
        return arr;
    }

    private static GuitarNote Note(double beat, double sustain, bool linkNext = false) => new()
    {
        Position = Pos(beat), StringIndex = 1, Fret = 5, Sustain = Pos(sustain), LinkNext = linkNext
    };

    private static List<ArrangementIssue> Run(Arrangement arr) =>
        new ArrangementValidationService(new FakeAppLogger(), [new BoundaryCrossingRule()]).Validate(arr, Beats(), null)
            .Where(i => i.Type is ArrangementIssueType.SustainCrossesBoundary or ArrangementIssueType.HandShapeCrossesBoundary)
            .ToList();

    [Fact]
    public void TheCodesAreD17AndD18_Warnings()
    {
        Assert.Equal("D17", ArrangementIssueType.SustainCrossesBoundary.GetCode());
        Assert.Equal("D18", ArrangementIssueType.HandShapeCrossesBoundary.GetCode());
        Assert.Equal(ArrangementIssueSeverity.Warning, ArrangementIssueType.SustainCrossesBoundary.GetSeverity());
        Assert.Equal(ArrangementIssueSeverity.Warning, ArrangementIssueType.HandShapeCrossesBoundary.GetSeverity());
    }

    [Fact]
    public void TheRuleRunsByDefault()
    {
        var arr = Arr();
        arr.Levels[0].Notes.Add(Note(3, 2));

        var issues = new ArrangementValidationService(new FakeAppLogger()).Validate(arr, Beats(), null);

        Assert.Contains(issues, i => i.Type == ArrangementIssueType.SustainCrossesBoundary);
    }

    [Fact]
    public void ASustainAcrossAPhraseStart_IsReportedOnTheNote()
    {
        var arr = Arr();
        var note = Note(3, 2);
        arr.Levels[0].Notes.Add(note);

        var finding = Assert.Single(Run(arr));

        Assert.Equal(ArrangementIssueType.SustainCrossesBoundary, finding.Type);
        Assert.Equal("Validation_SustainCrossesPhrase", finding.MessageKey);
        Assert.Same(note, finding.Note);
        Assert.Equal(1500, finding.TimeMs);
        Assert.Contains("verse", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASustainEndingExactlyOnTheBoundary_IsClean()
    {
        var arr = Arr();
        arr.Levels[0].Notes.Add(Note(3, 1));
        arr.Levels[0].Chords.Add(ChordHandShapePlannerTests.Chord(7, 1, (0, 3), (1, 5)));
        arr.Levels[0].HandShapes.Add(new HandShape { Position = Pos(10), EndPosition = Pos(12) });

        Assert.Empty(Run(arr));
    }

    [Fact]
    public void AHandShapeAcrossASection_IsReportedOnTheShape()
    {
        var arr = Arr();
        var shape = new HandShape { Position = Pos(7), EndPosition = Pos(9) };
        arr.Levels[0].HandShapes.Add(shape);

        var finding = Assert.Single(Run(arr));

        Assert.Equal(ArrangementIssueType.HandShapeCrossesBoundary, finding.Type);
        Assert.Equal("Validation_HandShapeCrossesSection", finding.MessageKey);
        Assert.Same(shape, finding.HandShape);
        Assert.Contains("chorus", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASustainPastEnd_SaysEnd()
    {
        var arr = Arr();
        var chord = ChordHandShapePlannerTests.Chord(11, 2, (0, 3), (1, 5));
        arr.Levels[0].Chords.Add(chord);

        var finding = Assert.Single(Run(arr));

        Assert.Equal("Validation_SustainPastEnd", finding.MessageKey);
        Assert.Same(chord, finding.Chord);
    }

    [Fact]
    public void ALinkNextNoteAcrossAPhrase_StaysI10sFinding_ButAcrossASectionItIsReported()
    {
        var arr = Arr();
        arr.Levels[0].Notes.Add(Note(3, 1, linkNext: true));
        arr.Levels[0].Notes.Add(Note(4, 1));
        var section = Note(7, 1, linkNext: true);
        arr.Levels[0].Notes.Add(section);
        arr.Levels[0].Notes.Add(Note(8, 1));

        var all = new ArrangementValidationService(new FakeAppLogger()).Validate(arr, Beats(), null);

        Assert.Single(all, i => i.Type == ArrangementIssueType.PhraseChangeOnLinkNextNote);
        var finding = Assert.Single(all, i => i.Type == ArrangementIssueType.SustainCrossesBoundary);
        Assert.Same(section, finding.Note);
        Assert.Equal("Validation_LinkNextCrossesSection", finding.MessageKey);
        Assert.Null(BoundaryCrossing.Trim(arr, arr.Levels[0], finding));
    }

    [Fact]
    public void TheTrim_EndsTheSustainOnTheBoundary_AndUndoes()
    {
        var arr = Arr();
        var note = Note(3, 2);
        arr.Levels[0].Notes.Add(note);

        var trim = BoundaryCrossing.Trim(arr, arr.Levels[0], Assert.Single(Run(arr)));
        Assert.NotNull(trim);
        trim.Execute();

        Assert.Equal(Pos(1), note.Sustain);
        Assert.Empty(Run(arr));

        trim.Undo();
        Assert.Equal(Pos(2), note.Sustain);
        Assert.Single(Run(arr));
    }

    [Fact]
    public void TheTrim_EndsAChordAndAHandShapeOnTheBoundary()
    {
        var arr = Arr();
        arr.Levels[0].Chords.Add(ChordHandShapePlannerTests.Chord(11, 2, (0, 3), (1, 5)));
        var shape = new HandShape { Position = Pos(7), EndPosition = Pos(9) };
        arr.Levels[0].HandShapes.Add(shape);

        foreach (var finding in Run(arr)) BoundaryCrossing.Trim(arr, arr.Levels[0], finding)!.Execute();

        Assert.Equal(Pos(1), arr.Levels[0].Chords[0].ChordNotes[0].Sustain);
        Assert.Equal(Pos(8), shape.EndPosition);
        Assert.Empty(Run(arr));
    }

    [Fact]
    public void AShapeWhoseContentGoesOnPastTheBoundary_GetsNoTrim()
    {
        var arr = Arr();
        arr.Levels[0].HandShapes.Add(new HandShape { Position = Pos(7), EndPosition = Pos(9) });
        arr.Levels[0].Chords.Add(ChordHandShapePlannerTests.Chord(8.5, 0, (0, 3), (1, 5)));

        Assert.Null(BoundaryCrossing.Trim(arr, arr.Levels[0], Assert.Single(Run(arr))));
    }

    [Fact]
    public void ASlideOrABendPastTheBoundary_GetsNoTrim()
    {
        var arr = Arr();
        var slide = Note(3, 2);
        slide.SlideTo = 7;
        var bend = Note(7, 2);
        bend.StringIndex = 2;
        bend.BendValues.Add(new BendValue(Pos(8.5), 1));
        arr.Levels[0].Notes.Add(slide);
        arr.Levels[0].Notes.Add(bend);

        var findings = Run(arr);

        Assert.Equal(2, findings.Count);
        Assert.All(findings, f => Assert.Null(BoundaryCrossing.Trim(arr, arr.Levels[0], f)));
    }
}
