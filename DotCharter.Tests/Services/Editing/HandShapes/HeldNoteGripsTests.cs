using DotCharter.Services.Editing.HandShapes;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tests.Services.State.Linking;

namespace DotCharter.Tests.Services.Editing.HandShapes;

public sealed class HeldNoteGripsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AReusedFirstGripGetsAnExplicitOpeningMask(bool linked)
    {
        var chart = new Fixture();
        chart.Arrangement.Tuning = ["Eb", "Ab", "Db", "Gb", "Bb", "Eb"];
        var grip = new ChordTemplate
        {
            Id = 38, Frets = { [1] = 0, [2] = 7, [3] = 5, [4] = 5 },
            Fingers = { [2] = 3, [3] = 1, [4] = 1 }
        };
        chart.Arrangement.ChordTemplates.Add(grip);
        var runs = new[] { 0, 12, 24 }.Select(chart.AddRun).ToArray();
        foreach (var run in runs) run[0].TemplateId = grip.Id;
        var session = new LinkHarness(chart.Project, new AppConfig());
        var group = linked ? session.Link((chart.Arrangement, 0), (chart.Arrangement, 3), (chart.Arrangement, 6)) : null;
        var reports = new List<LinkPropagationReport>();
        session.History.LinksPropagated += reports.AddRange;
        foreach (var shape in runs[1]) chart.Selection.SelectedHandShapes.Add(shape);

        var result = HeldNoteGrips.Apply(chart.Track, session.Config, session.History);

        Assert.Equal((5, 5, 0), result);
        var firsts = chart.Level.HandShapes.Where(h => h.Position == new FractionalPosition(12) ||
            (linked && (h.Position == FractionalPosition.Zero || h.Position == new FractionalPosition(24))));
        Assert.Equal(linked ? 3 : 1, firsts.Count());
        Assert.All(firsts, h =>
        {
            Assert.Same(grip, chart.Grip(h));
            Assert.Equal(0b11000, h.GhostMask);
        });
        if (group != null)
        {
            LinkHarness.AssertLinkedAndEqual(session, group, 3);
            var report = Assert.Single(reports);
            Assert.Equal(new FractionalPosition(12), report.Source.Position);
            Assert.Equal(2, report.Targets.Count);
        }
        Assert.Equal((0, 5, 0), HeldNoteGrips.Apply(chart.Track, session.Config, session.History));
        session.History.Undo();
        Assert.All(firsts, h => Assert.Null(h.GhostMask));
    }

    [Fact]
    public void AReusedGripWithEveryStringPlayedAtTheStartGetsAnExplicitZero()
    {
        var chart = new Fixture();
        var shape = chart.Shape(0, 1);
        var grip = new ChordTemplate { Id = 0, Frets = { [1] = 0, [2] = 7 }, Fingers = { [2] = 3 } };
        chart.Arrangement.ChordTemplates.Add(grip);
        shape.TemplateId = grip.Id;
        chart.Chord(FractionalPosition.Zero, (1, 0), (2, 7));

        Assert.Equal((1, 1, 0), chart.Apply([shape]));
        Assert.Same(grip, chart.Grip(shape));
        Assert.Equal(0, shape.GhostMask);
        Assert.Equal((0, 1, 0), chart.Apply([shape]));
    }

    [Fact]
    public void FirstPitchedOnsetOpensAShapeThatStartsBeforeItsSounds()
    {
        var chart = new Fixture();
        var shape = chart.Shape(0, 4);
        chart.Level.FretHandMutes.Add(new FretHandMute(new(0)));
        chart.Note(new(1), 5, 3).Mute = MuteType.Full;
        chart.Chord(new(2), (1, 0), (2, 7));
        chart.Note(new(3), 3, 5);

        Assert.Equal((1, 1, 0), chart.Apply([shape]));
        Assert.Equal("x075xx", chart.Frets(shape));
        Assert.Equal(1 << 3, shape.GhostMask);
    }

    [Fact]
    public void SavedProjectKeepsEachGhostMaskOnTheSharedGrip()
    {
        var chart = new Fixture();
        chart.Arrangement.ChordTemplates.Add(new ChordTemplate { Id = 0, Frets = { [1] = 5, [2] = 7 } });
        var first = chart.Shape(0, 1);
        var second = chart.Shape(1, 2);
        first.TemplateId = second.TemplateId = 0;
        first.GhostMask = 0;
        second.GhostMask = 1 << 2;
        var serializer = new RscpChartSerializer();
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dcproj");
        try
        {
            serializer.Save(path, chart.Project);
            var shapes = serializer.Load(path).Arrangements[0].Levels[0].HandShapes;
            Assert.Equal(shapes[0].TemplateId, shapes[1].TemplateId);
            Assert.Equal(new int?[] { 0, 1 << 2 }, shapes.Select(h => h.GhostMask));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FiveShapesShareFingeredGripsAndKeepTheirOwnGhostStrings()
    {
        var chart = new Fixture();
        var run = chart.AddRun(0);
        var playedGrip = new ChordTemplate
        {
            Id = 0, Name = "Am", Frets = new() { [1] = 0, [2] = 7, [3] = 5, [4] = 5 },
            Fingers = new() { [2] = 4, [3] = 2, [4] = 2 }
        };
        chart.Arrangement.ChordTemplates.Add(playedGrip);

        Assert.Equal((5, 5, 0), chart.Apply(run.Reverse()));
        Assert.Equal(["x0755x", "x0756x", "x0755x", "x0776x", "x0756x"], run.Select(chart.Frets));
        Assert.Equal([0b11000, 0b01110, 0b01110, 0b01110, 0b10110], run.Select(chart.Mask));
        Assert.Same(playedGrip, chart.Grip(run[0]));
        Assert.Same(playedGrip, chart.Grip(run[2]));
        Assert.Equal([4, 2, 2], new[] { 2, 3, 4 }.Select(chart.Grip(run[2]).GetFinger));
        Assert.Same(chart.Grip(run[1]), chart.Grip(run[4]));
        Assert.All(run, shape => Assert.All(chart.Grip(shape).Frets.Where(f => f.Value > 0),
            fret => Assert.InRange(chart.Grip(shape).GetFinger(fret.Key), 1, 4)));
    }

    [Fact]
    public void RepeatedRunsAndRerunsKeepIdsAndDoNotAddHistory()
    {
        var chart = new Fixture();
        var first = chart.AddRun(0);
        var second = chart.AddRun(16);
        Assert.Equal((5, 5, 0), chart.Apply(first));
        var count = chart.Arrangement.ChordTemplates.Count;
        Assert.Equal((5, 5, 0), chart.Apply(second));
        Assert.Equal(count, chart.Arrangement.ChordTemplates.Count);
        Assert.Equal(first.Select(h => h.TemplateId), second.Select(h => h.TemplateId));
        Assert.Equal(first.Select(chart.Mask), second.Select(chart.Mask));
        Assert.Equal((0, 5, 0), chart.Apply(second));
        chart.History.Undo();
        Assert.All(second, shape => Assert.Equal(-1, shape.TemplateId));
        Assert.All(first, shape => Assert.True(shape.TemplateId >= 0));
        chart.History.Undo();
        Assert.False(chart.History.CanUndo);
        Assert.Empty(chart.Arrangement.ChordTemplates);
    }

    [Theory]
    [InlineData("gap", "xxxx6x")]
    [InlineData("minimum spacing", "xx7x6x")]
    [InlineData("overlap", "xxxx6x")]
    [InlineData("unselected", "xxxx6x")]
    [InlineData("open", "xxxx6x")]
    [InlineData("muted note", "xxxx6x")]
    [InlineData("muted chord", "xxxx6x")]
    [InlineData("mute marker", "xxxx6x")]
    public void HeldFretsCarryOnlyAcrossTouchingSelectedShapesWithoutAReset(string reset, string expected)
    {
        var chart = new Fixture();
        var first = chart.Shape(0, 1);
        var middle = chart.Shape(1, 2);
        var last = chart.Shape(2, 3);
        chart.Note(new(0), 2, 7);
        chart.Note(new(2), 4, 6);
        if (reset == "open") chart.Note(new(1), 1, 0);
        if (reset == "muted note") chart.Note(new(1), 3, 5).Mute = MuteType.Full;
        if (reset == "muted chord") chart.Chord(new(1), (3, 5)).ChordNotes[0].Mute = MuteType.Full;
        if (reset == "mute marker") chart.Level.FretHandMutes.Add(new FretHandMute(new(1)));
        if (reset == "minimum spacing") first.EndPosition -= new FractionalPosition(new Fraction(1, 16));
        if (reset == "gap") first.EndPosition -= new FractionalPosition(new Fraction(1, 4));
        if (reset == "overlap") first.EndPosition += new FractionalPosition(new Fraction(1, 4));
        chart.Apply(reset == "unselected" ? [first, last] : [first, middle, last]);
        Assert.Equal(expected, chart.Frets(last));
    }

    [Fact]
    public void ConflictingFretsLeaveTheShapeUnchangedAndAreCounted()
    {
        var chart = new Fixture();
        var first = chart.Shape(0, 1);
        var conflict = chart.Shape(1, 2);
        var last = chart.Shape(2, 3);
        chart.Note(new(0), 2, 7);
        chart.Note(new(1), 4, 5);
        chart.Chord(new(1), (4, 6));
        chart.Note(new(2), 3, 5);

        Assert.Equal((2, 3, 1), chart.Apply([first, conflict, last]));
        Assert.Equal(-1, conflict.TemplateId);
        Assert.Equal("xx7xxx", chart.Frets(first));
        Assert.Equal("xx756x", chart.Frets(last));
    }

    [Theory]
    [InlineData(false, "x075xx")]
    [InlineData(true, "xx75xx")]
    public void MidShapeResetKeepsPlayedFretsInTheGripButClearsCarry(bool muted, string expected)
    {
        var chart = new Fixture();
        var first = chart.Shape(0, 1);
        var middle = chart.Shape(1, 2);
        var last = chart.Shape(2, 3);
        chart.Note(new(0), 5, 8);
        chart.Note(new(1), 2, 7);
        var reset = new FractionalPosition(1, new Fraction(1, 3));
        if (muted) chart.Level.FretHandMutes.Add(new FretHandMute(reset));
        else chart.Note(reset, 1, 0);
        chart.Note(new(1, new Fraction(2, 3)), 3, 5);
        chart.Note(new(2), 4, 6);

        chart.Apply([first, middle, last]);

        Assert.Equal(expected, chart.Frets(middle));
        Assert.Equal(muted ? 0b01000 : 0b01010, chart.Mask(middle));
        Assert.Equal("xxx56x", chart.Frets(last));
        Assert.Equal(1 << 3, chart.Mask(last));
    }

    [Fact]
    public void ChangingOneShapeLeavesTheOtherUsesAndOneUndoRestoresTheOriginal()
    {
        var chart = new Fixture();
        var original = new ChordTemplate { Id = 0, Frets = new() { [1] = 5 }, Fingers = new() { [1] = 3 } };
        chart.Arrangement.ChordTemplates.Add(original);
        var selected = chart.Shape(0, 1);
        var other = chart.Shape(1, 2);
        selected.TemplateId = other.TemplateId = original.Id;
        chart.Note(new(0), 4, 6);

        Assert.Equal((1, 1, 0), chart.Apply([selected]));
        Assert.Equal("xxxx6x", chart.Frets(selected));
        Assert.Same(original, chart.Grip(other));
        Assert.Equal(5, original.GetFret(1));
        Assert.Equal(3, original.GetFinger(1));
        Assert.Equal(2, chart.Level.HandShapes.Count);
        chart.History.Undo();
        Assert.Same(original, chart.Grip(selected));
        Assert.Single(chart.Arrangement.ChordTemplates);
        Assert.False(chart.History.CanUndo);
        chart.History.Redo();
        Assert.Equal("xxxx6x", chart.Frets(selected));
    }

    internal sealed class Fixture
    {
        public Fixture()
        {
            Arrangement.Levels.Add(Level);
            Project.Arrangements.Add(Arrangement);
            for (var i = 0; i < 32; i++)
                Project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, IsMeasure = i % 4 == 0 });
            Track = new TrackEditorContext(Arrangement, Selection) { Project = Project, Level = Level };
        }

        public ChartProject Project { get; } = new();
        public Arrangement Arrangement { get; } = new() { Tuning = ["E", "A", "D", "G", "B", "E"] };
        public ChartLevel Level { get; } = new();
        public SelectionService Selection { get; } = new();
        public HistoryService History { get; } = new(new FakeAppLogger());
        public TrackEditorContext Track { get; }

        public ChordTemplate Grip(HandShape shape) => Arrangement.FindChordTemplate(shape.TemplateId)!;
        public string Frets(HandShape shape) => Grip(shape).GetTemplateFrets().Replace('-', 'x');
        public int Mask(HandShape shape) => shape.GhostMask ?? Grip(shape).GhostMask;

        public (int Changed, int Selected, int Conflicts) Apply(IEnumerable<HandShape> shapes)
        {
            Selection.SelectedHandShapes.Clear();
            foreach (var shape in shapes) Selection.SelectedHandShapes.Add(shape);
            return HeldNoteGrips.Apply(Track, new AppConfig(), History);
        }

        public HandShape[] AddRun(int at)
        {
            var shapes = Enumerable.Range(at, 5).Select(i => Shape(i, i + 1)).ToArray();
            Chord(new(at), (1, 0), (2, 7));
            Note(new(at, new Fraction(1, 3)), 3, 5);
            Note(new(at, new Fraction(2, 3)), 4, 5);
            Note(new(at + 1), 4, 6);
            Note(new(at + 1, new Fraction(1, 2)), 3, 5);
            Note(new(at + 2), 4, 5);
            Note(new(at + 3), 4, 6);
            Note(new(at + 3, new Fraction(1, 2)), 3, 7);
            Note(new(at + 4), 3, 5);
            return shapes;
        }

        public GuitarNote Note(FractionalPosition at, int guitarString, int fret)
        {
            var note = new GuitarNote { Position = at, StringIndex = guitarString, Fret = fret };
            Level.Notes.Add(note);
            return note;
        }

        public Chord Chord(FractionalPosition at, params (int String, int Fret)[] notes)
        {
            var chord = new Chord { Position = at };
            foreach (var (guitarString, fret) in notes)
                chord.ChordNotes.Add(new ChordNote { StringIndex = guitarString, Fret = fret });
            Level.Chords.Add(chord);
            return chord;
        }

        public HandShape Shape(int start, int end)
        {
            var shape = new HandShape { Position = new(start), EndPosition = new(end) };
            Level.HandShapes.Add(shape);
            return shape;
        }
    }
}
