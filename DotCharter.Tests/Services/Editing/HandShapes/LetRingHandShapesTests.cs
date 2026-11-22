using DotCharter.Services.Editing.HandShapes;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tests.Core.Linking;
using DotCharter.Tests.Services.State.Linking;

namespace DotCharter.Tests.Services.Editing.HandShapes;

public sealed class LetRingHandShapesTests
{
    [Fact]
    public void OwnerRunCreatesSixShapesWithThreeSharedGrips()
    {
        var chart = OwnerRun();
        var fingered = new ChordTemplate
        {
            Id = 9,
            Frets = { [1] = 0, [2] = 7, [3] = 5, [4] = 6 },
            Fingers = { [2] = 4, [3] = 1, [4] = 2 }
        };
        chart.Arrangement.ChordTemplates.Add(fingered);

        var result = Apply(chart);
        var shapes = chart.Level.HandShapes.ToArray();
        Assert.Equal(["x0755x", "x0756x", "x0755x", "x0756x", "x0776x", "x0756x"], shapes.Select(chart.Frets));
        Assert.Equal((6, 0), result);
        Assert.Equal(new[] { 0, 6, 10, 12, 14, 16 }.Select(At), shapes.Select(s => s.Position));
        Assert.Equal(new[] { 6, 10, 12, 14, 16, 18 }.Select(At), shapes.Select(s => s.EndPosition));
        Assert.Equal([0b11000, 0b01110, 0b01110, 0b01110, 0b10110, 0b10110], shapes.Select(s => s.GhostMask));
        Assert.Same(chart.Grip(shapes[0]), chart.Grip(shapes[2]));
        Assert.All(new[] { 1, 3, 5 }, i => Assert.Same(fingered, chart.Grip(shapes[i])));
        Assert.Equal(3, chart.Arrangement.ChordTemplates.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void RepickingAndAddingStringsKeepOneShape(int firstBeat)
    {
        var chart = new HeldNoteGripsTests.Fixture();
        Ring(chart, 0, 1, 0).Position += firstBeat;
        Ring(chart, 2, 1, 0).Position += firstBeat;
        Ring(chart, 4, 3, 5).Position += firstBeat;

        Assert.Equal((1, 0), Apply(chart));
        var shape = Assert.Single(chart.Level.HandShapes);
        Assert.Equal("x0x5xx", chart.Frets(shape));
        Assert.Equal(new FractionalPosition(firstBeat), shape.Position);
        Assert.Equal(At(6) + firstBeat, shape.EndPosition);
    }

    [Theory]
    [InlineData("unselected")]
    [InlineData("unsustained")]
    [InlineData("silence")]
    public void AGapEndsTheRunAndClearsHeldFrets(string gap)
    {
        var chart = new HeldNoteGripsTests.Fixture();
        Ring(chart, 0, 2, 7).Sustain = new(2);
        if (gap == "unselected") chart.Note(At(2), 3, 5);
        if (gap == "unsustained") chart.Selection.SelectedNotes.Add(chart.Note(At(2), 3, 5));
        if (gap == "silence") chart.Level.Notes[0].Sustain = new(0, new Fraction(1, 4));
        Ring(chart, 4, 4, 6);

        Assert.Equal((2, 0), Apply(chart));
        Assert.Equal(["xx7xxx", "xxxx6x"], chart.Level.HandShapes.Select(chart.Frets));
        Assert.Equal(gap == "silence" ? At(1) : At(2), chart.Level.HandShapes[0].EndPosition);
    }

    [Fact]
    public void ExistingShapeIsCountedAndPreservedWhileFreeNotesGetShapes()
    {
        var chart = new HeldNoteGripsTests.Fixture();
        var existing = chart.Shape(1, 2);
        existing.TemplateId = 7;
        existing.GhostMask = 4;
        var original = existing.Clone();
        Ring(chart, 0, 2, 7).Sustain = new(2);
        Ring(chart, 4, 3, 5);
        Ring(chart, 6, 3, 6);
        Ring(chart, 8, 4, 6);

        Assert.Equal((2, 1), Apply(chart));
        Assert.Same(existing, chart.Level.HandShapes[1]);
        Assert.Equal(original.Position, existing.Position);
        Assert.Equal(original.EndPosition, existing.EndPosition);
        Assert.Equal(original.TemplateId, existing.TemplateId);
        Assert.Equal(original.GhostMask, existing.GhostMask);
        Assert.Equal(existing.Position, chart.Level.HandShapes[0].EndPosition);
        Assert.Equal("xxxx6x", chart.Frets(chart.Level.HandShapes[2]));
    }

    [Fact]
    public void OneUndoRemovesAllNewShapesAndTemplates()
    {
        var chart = OwnerRun();
        Assert.Equal((6, 0), Apply(chart));
        Assert.Equal((0, 6), Apply(chart));

        chart.History.Undo();

        Assert.Empty(chart.Level.HandShapes);
        Assert.Empty(chart.Arrangement.ChordTemplates);
        Assert.Equal(8, chart.Level.Notes.Count);
        Assert.Single(chart.Level.Chords);
        Assert.False(chart.History.CanUndo);
        chart.History.Redo();
        Assert.Equal(6, chart.Level.HandShapes.Count);
        Assert.Equal(3, chart.Arrangement.ChordTemplates.Count);
    }

    [Fact]
    public void GenerationCopiesShapesToEveryLinkedPlacementInOneUndoStep()
    {
        var chart = LinkHarness.WithRhythm();
        var lead = LinkHarness.Hardest(chart.Lead);
        var rhythm = LinkHarness.Hardest(chart.Rhythm);
        lead.HandShapes.Clear();
        rhythm.HandShapes.Clear();
        var placements = new[] { (chart.Lead, 1), (chart.Lead, 4), (chart.Rhythm, 4) };
        var group = chart.Link(placements);
        var note = LinkHarness.NoteAt(chart.Lead, LinkFixture.At(1, 1), 1);
        var selection = new SelectionService();
        selection.SelectedNotes.Add(note);
        var track = new TrackEditorContext(chart.Lead, selection) { Level = lead, Project = chart.Project };

        Assert.Equal((1, 0), LetRingHandShapes.Apply(track, new AppConfig(), chart.History));

        Assert.All(placements, placement =>
        {
            var (arrangement, measure) = placement;
            var start = LinkFixture.At(measure, 1);
            var shape = Assert.Single(LinkHarness.Hardest(arrangement).HandShapes, s => s.Position == start);
            Assert.Equal(start + note.Sustain, shape.EndPosition);
            Assert.Equal(note.Fret, arrangement.FindChordTemplate(shape.TemplateId)!.GetFret(note.StringIndex));
        });
        Assert.Equal(placements.Length, lead.HandShapes.Count + rhythm.HandShapes.Count);
        LinkHarness.AssertLinkedAndEqual(chart, group, placements.Length);

        chart.History.Undo();

        Assert.Empty(lead.HandShapes);
        Assert.Empty(rhythm.HandShapes);
        LinkHarness.AssertLinkedAndEqual(chart, group, placements.Length);
    }

    internal static HeldNoteGripsTests.Fixture OwnerRun()
    {
        var chart = new HeldNoteGripsTests.Fixture();
        var chord = chart.Chord(At(0), (1, 0), (2, 7));
        foreach (var note in chord.ChordNotes) note.Sustain = At(2);
        chart.Selection.SelectedChords.Add(chord);
        Ring(chart, 2, 3, 5);
        Ring(chart, 4, 4, 5);
        Ring(chart, 6, 4, 6);
        Ring(chart, 8, 3, 5);
        Ring(chart, 10, 4, 5);
        Ring(chart, 12, 4, 6);
        Ring(chart, 14, 3, 7);
        Ring(chart, 16, 3, 5);
        return chart;
    }

    internal static (int Created, int Existing) Apply(HeldNoteGripsTests.Fixture chart) =>
        LetRingHandShapes.Apply(chart.Track, new AppConfig(), chart.History);

    private static GuitarNote Ring(HeldNoteGripsTests.Fixture chart, int quarterBeats, int guitarString, int fret)
    {
        var note = chart.Note(At(quarterBeats), guitarString, fret);
        note.Sustain = At(2);
        chart.Selection.SelectedNotes.Add(note);
        return note;
    }

    private static FractionalPosition At(int quarterBeats) => new(quarterBeats / 4, new Fraction(quarterBeats % 4, 4));
}
