using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.Services.Editing.Highway;

/// <summary>
///     The split verb cuts a hand shape at the selected sound and gives the tail a grip built from
///     what sounds inside it. The chords under the tail already name their own templates, so a tail
///     that looked its grip up by frets alone could land on an equal-fret DUPLICATE and leave the bar
///     and its chord on two templates. With the sync on the tail takes the template of the chord it
///     covers; with it off the old frets-key lookup stands.
/// </summary>
public sealed class HandShapeSplitSyncTests
{
    [Fact]
    public void TheTail_TakesTheTemplateOfTheChordUnderIt_NotAnEqualFretDuplicate()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 3), (1, 5));
        h.AddTemplate(1, (0, 3), (1, 5));
        h.AddChord(1, 0, (0, 3), (1, 5));
        var pivot = h.AddChord(4, 1, (0, 3), (1, 5));
        h.AddHandShape(0, 8, 0);
        h.Selection.SelectedChords.Add(pivot);

        h.Splitter.SplitAtSelection(h.Track);

        var bars = h.Level.HandShapes.OrderBy(b => b.Position).ToList();
        Assert.Equal(2, bars.Count);
        Assert.Equal(0, bars[0].TemplateId);
        Assert.Equal(pivot.TemplateId, bars[1].TemplateId);
        Assert.Equal(2, h.Arrangement.ChordTemplates.Count);
    }

    [Fact]
    public void TheSplit_AndTheTailFollowingItsChord_AreOneUndoStep()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 3), (1, 5));
        h.AddTemplate(1, (0, 3), (1, 5));
        h.AddChord(1, 0, (0, 3), (1, 5));
        var pivot = h.AddChord(4, 1, (0, 3), (1, 5));
        var original = h.AddHandShape(0, 8, 0);
        h.Selection.SelectedChords.Add(pivot);

        h.Splitter.SplitAtSelection(h.Track);
        Assert.Single(h.History.GetUndoSteps());

        h.History.Undo();

        Assert.Same(original, Assert.Single(h.Level.HandShapes));
        Assert.Equal(0, original.TemplateId);
    }

    [Fact]
    public void WithTheSyncOff_TheTailKeepsTheFirstEqualFretTemplate()
    {
        var h = new Harness();
        h.Config.AppConfig.SyncHandShapeToChordTemplate = false;
        h.AddTemplate(0, (0, 3), (1, 5));
        h.AddTemplate(1, (0, 3), (1, 5));
        h.AddChord(1, 0, (0, 3), (1, 5));
        var pivot = h.AddChord(4, 1, (0, 3), (1, 5));
        h.AddHandShape(0, 8, 0);
        h.Selection.SelectedChords.Add(pivot);

        h.Splitter.SplitAtSelection(h.Track);

        var tail = h.Level.HandShapes.OrderBy(b => b.Position).Last();
        Assert.Equal(0, tail.TemplateId);
    }

    [Fact]
    public void ATailWhoseNotesAreMoreThanTheChord_StillGetsAGripFromItsNotes()
    {
        // The chord's template is only part of what sounds in the tail, so it is not the bar's grip.
        var h = new Harness();
        h.AddTemplate(0, (0, 3), (1, 5));
        h.AddTemplate(1, (0, 3), (1, 5));
        h.AddChord(1, 0, (0, 3), (1, 5));
        var pivot = h.AddChord(4, 1, (0, 3), (1, 5));
        h.AddNote(6, 2, 7);
        h.AddHandShape(0, 8, 0);
        h.Selection.SelectedChords.Add(pivot);

        h.Splitter.SplitAtSelection(h.Track);

        var tail = h.Level.HandShapes.OrderBy(b => b.Position).Last();
        var grip = h.Arrangement.ChordTemplates.Single(t => t.Id == tail.TemplateId);
        Assert.Equal(3, grip.Frets.Count);
    }

    private sealed class Harness
    {
        private readonly ChartProject _project = new();

        public Harness()
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var scope = new EditingScope(State);
            Service = new HighwayEditingService(State, Selection, History, new StrictCollisionService(),
                Config, scope, new EditTargetService(Selection, scope, Config), logger);

            for (var i = 0; i < 32; i++)
                _project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });

            Arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            Arrangement.Levels.Add(new ChartLevel());
            _project.Arrangements.Add(Arrangement);
            State.ActiveProject = _project;
            State.ActiveArrangement = Arrangement;
            State.ActiveLevel = Arrangement.Levels[0];

            Splitter = new HandShapeSplitter(new FingeringGenerationService(), Service, Selection, Config, logger);
            Track = new TrackEditorContext(Arrangement, Selection)
            {
                Project = _project,
                Level = Arrangement.Levels[0]
            };
        }

        public FakeConfigurationService Config { get; } = new();
        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public HistoryService History { get; }
        public HighwayEditingService Service { get; }
        public HandShapeSplitter Splitter { get; }
        public Arrangement Arrangement { get; }
        public TrackEditorContext Track { get; }
        public ChartLevel Level => Arrangement.Levels[0];

        public ChordTemplate AddTemplate(int id, params (int stringIdx, int fret)[] frets)
        {
            var t = new ChordTemplate { Id = id };
            foreach (var (stringIdx, fret) in frets) t.Frets[stringIdx] = fret;
            Arrangement.ChordTemplates.Add(t);
            return t;
        }

        public HandShape AddHandShape(int start, int end, int templateId)
        {
            var shape = new HandShape
            {
                Position = new FractionalPosition(start),
                EndPosition = new FractionalPosition(end),
                TemplateId = templateId
            };
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

        public Chord AddChord(int beat, int templateId, params (int stringIndex, int fret)[] frets)
        {
            var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = templateId };
            foreach (var (stringIndex, fret) in frets)
                chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
            Level.Chords.Add(chord);
            return chord;
        }
    }
}
