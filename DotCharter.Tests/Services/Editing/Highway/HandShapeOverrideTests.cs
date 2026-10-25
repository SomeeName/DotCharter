using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.Services.Editing.Highway;

/// <summary>
///     Dropping a hand shape destroys the sounds under where it LANDS — that is
///     the drag semantic, and the shape's own sounds are excluded because a drag
///     hands them back as target/new pairs.
///     <para>
///         A caller that replaces a span IN PLACE hands nothing back, so every
///         note under the result used to qualify. Splitting a shape is exactly
///         that shape: one span becomes two spans inside it. Both split entry
///         points — the <c>SplitHandShape</c> verb and Ctrl+right-click on the
///         highway — silently deleted every note and chord under the shape while
///         landing the two halves correctly, and the commit log said
///         "removed notes=0" because these removals were never counted.
///     </para>
/// </summary>
public sealed class HandShapeOverrideTests
{
    [Fact]
    public void SplittingAHandShapeInPlace_KeepsEveryNoteAndChordUnderIt()
    {
        var h = new Harness();
        var shape = h.AddHandShape(0, 8, templateId: 5);
        var before = h.AddNote(1, stringIndex: 0, fret: 3);
        var atSplit = h.AddNote(4, stringIndex: 1, fret: 5);
        var after = h.AddNote(6, stringIndex: 2, fret: 7);
        var chord = h.AddChord(2, (0, 3), (1, 5));

        var head = h.Shape(0, 4, 5);
        var tail = h.Shape(4, 8, 5);
        h.Service.CommitHandShapeDragAndDrop(
            [], [], [], [],
            [shape], [head, tail],
            [], []);

        Assert.Equal(2, h.Level.HandShapes.Count);
        Assert.Contains(before, h.Level.Notes);
        Assert.Contains(atSplit, h.Level.Notes);
        Assert.Contains(after, h.Level.Notes);
        Assert.Contains(chord, h.Level.Chords);
    }

    [Fact]
    public void TheSplitVerb_LeavesTheSoundsAloneEndToEnd()
    {
        var h = new Harness();
        h.AddHandShape(0, 8, templateId: 5);
        h.AddNote(1, stringIndex: 0, fret: 3);
        var pivot = h.AddNote(4, stringIndex: 1, fret: 5);
        h.AddNote(6, stringIndex: 2, fret: 7);

        h.Selection.SelectedNotes.Add(pivot);
        h.Splitter.SplitAtSelection(h.Track);

        Assert.Equal(2, h.Level.HandShapes.Count);
        Assert.Equal(3, h.Level.Notes.Count);
    }

    [Fact]
    public void MovingAHandShapeOntoOccupiedGround_StillDestroysWhatIsThere()
    {
        // The guard on the fix: newly covered ground keeps its old meaning.
        var h = new Harness();
        var shape = h.AddHandShape(0, 4, templateId: 5);
        var stranger = h.AddNote(9, stringIndex: 0, fret: 3);
        var strangerChord = h.AddChord(10, (0, 3), (1, 5));

        h.Service.CommitHandShapeDragAndDrop(
            [], [], [], [],
            [shape], [h.Shape(8, 12, 5)],
            [], []);

        Assert.DoesNotContain(stranger, h.Level.Notes);
        Assert.DoesNotContain(strangerChord, h.Level.Chords);
    }

    [Fact]
    public void MovingAHandShapeOntoGroundItAlreadyCovered_KeepsThatOverlapsSounds()
    {
        // A shape sliding forward over itself: the note in the overlap belonged
        // to the shape before the move and belongs to it after.
        var h = new Harness();
        var shape = h.AddHandShape(0, 8, templateId: 5);
        var inOverlap = h.AddNote(6, stringIndex: 0, fret: 3);
        var ahead = h.AddNote(10, stringIndex: 1, fret: 5);

        h.Service.CommitHandShapeDragAndDrop(
            [], [], [], [],
            [shape], [h.Shape(4, 12, 5)],
            [], []);

        Assert.Contains(inOverlap, h.Level.Notes);
        Assert.DoesNotContain(ahead, h.Level.Notes);
    }

    private sealed class Harness
    {
        private readonly ChartProject _project = new();

        public Harness()
        {
            var logger = new FakeAppLogger();
            var history = new HistoryService(logger);
            var config = new FakeConfigurationService();
            var scope = new EditingScope(State);
            Service = new HighwayEditingService(State, Selection, history, new StrictCollisionService(),
                config, scope, new EditTargetService(Selection, scope, config), logger);

            for (var i = 0; i < 32; i++)
                _project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });

            Arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            Arrangement.Levels.Add(new ChartLevel());
            _project.Arrangements.Add(Arrangement);
            State.ActiveProject = _project;
            State.ActiveArrangement = Arrangement;
            State.ActiveLevel = Arrangement.Levels[0];

            Splitter = new HandShapeSplitter(new FingeringGenerationService(), Service, Selection, config, logger);
            Track = new TrackEditorContext(Arrangement, Selection)
            {
                Project = _project,
                Level = Arrangement.Levels[0]
            };
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public HighwayEditingService Service { get; }
        public HandShapeSplitter Splitter { get; }
        public Arrangement Arrangement { get; }
        public TrackEditorContext Track { get; }
        public ChartLevel Level => Arrangement.Levels[0];

        public HandShape Shape(int start, int end, int templateId) => new()
        {
            Position = new FractionalPosition(start),
            EndPosition = new FractionalPosition(end),
            TemplateId = templateId
        };

        public HandShape AddHandShape(int start, int end, int templateId)
        {
            var shape = Shape(start, end, templateId);
            Level.HandShapes.Add(shape);
            return shape;
        }

        public GuitarNote AddNote(int beat, int stringIndex, int fret)
        {
            var note = new GuitarNote
            {
                Position = new FractionalPosition(beat), StringIndex = stringIndex, Fret = fret
            };
            Level.Notes.Add(note);
            return note;
        }

        public Chord AddChord(int beat, params (int stringIndex, int fret)[] frets)
        {
            var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = -1 };
            foreach (var (stringIndex, fret) in frets)
                chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
            Level.Chords.Add(chord);
            return chord;
        }
    }
}
