using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing.Targets;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
/// The sheet pane's EDITING glue: double-click insert (grid-quantized, default
/// fret from context, committed through the same highway pipeline/undo history
/// the timeline uses), exact click-select, and GP-style fret typing.
/// </summary>
public sealed class SheetMusicPaneViewModelTests
{
    [Fact]
    public void AddNoteAt_InsertsGridSnappedNote_SelectsIt_AndUndoRemovesIt()
    {
        var h = new Harness();

        // Click slightly after beat 2 (1000 ms at 120 BPM / 500 ms beats).
        h.Pane.AddNoteAt(1040, stringIndex: 2);

        var note = Assert.Single(h.State.PlacedNotes);
        Assert.Equal(new FractionalPosition(2), note.Position); // snapped to the grid
        Assert.Equal(2, note.StringIndex);
        Assert.Equal(0, note.Fret); // untouched string defaults to 0
        Assert.Same(note, Assert.Single(h.Selection.SelectedNotes)); // selected for refinement

        h.History.Undo(); // ONE undo step reverts the whole gesture
        Assert.Empty(h.State.PlacedNotes);
    }

    [Fact]
    public void AddNoteAt_DefaultsFret_FromPreviousNoteOnSameString()
    {
        var h = new Harness();
        h.State.PlacedNotes.Add(new GuitarNote
        {
            Position = new FractionalPosition(1), StringIndex = 2, Fret = 7
        });

        h.Pane.AddNoteAt(2010, stringIndex: 2); // beat 4

        var added = h.State.PlacedNotes.First(n => n.Position == new FractionalPosition(4));
        Assert.Equal(7, added.Fret);
    }

    [Fact]
    public void AddNoteAt_OnExistingNote_SelectsInsteadOfDuplicating()
    {
        var h = new Harness();
        var existing = new GuitarNote { Position = new FractionalPosition(2), StringIndex = 3, Fret = 5 };
        h.State.PlacedNotes.Add(existing);

        h.Pane.AddNoteAt(1000, stringIndex: 3);

        Assert.Single(h.State.PlacedNotes); // no duplicate
        Assert.Same(existing, Assert.Single(h.Selection.SelectedNotes));
    }

    [Fact]
    public void SelectSounds_ReplacesSelection_AndEmptyClears()
    {
        var h = new Harness();
        var a = new GuitarNote { Position = new FractionalPosition(1), StringIndex = 0, Fret = 3 };
        var b = new GuitarNote { Position = new FractionalPosition(2), StringIndex = 1, Fret = 5 };
        h.State.PlacedNotes.Add(a);
        h.State.PlacedNotes.Add(b);
        h.Selection.SelectedNotes.Add(a);

        h.Pane.SelectSounds(new[] { b }, Array.Empty<Chord>());
        Assert.Same(b, Assert.Single(h.Selection.SelectedNotes));

        // A rest click clears (and only clears).
        h.Pane.SelectSounds(Array.Empty<GuitarNote>(), Array.Empty<Chord>());
        Assert.Empty(h.Selection.SelectedNotes);
        Assert.Empty(h.Selection.SelectedChords);
    }

    [Fact]
    public void SelectSounds_Additive_TogglesIntoTheExistingSelection()
    {
        var h = new Harness();
        var a = new GuitarNote { Position = new FractionalPosition(1), StringIndex = 0, Fret = 3 };
        var b = new GuitarNote { Position = new FractionalPosition(2), StringIndex = 1, Fret = 5 };
        h.State.PlacedNotes.Add(a);
        h.State.PlacedNotes.Add(b);

        h.Pane.SelectSounds(new[] { a }, Array.Empty<Chord>());                  // {a}
        h.Pane.SelectSounds(new[] { b }, Array.Empty<Chord>(), additive: true);  // {a, b}
        Assert.Equal(2, h.Selection.SelectedNotes.Count);
        Assert.Contains(a, h.Selection.SelectedNotes);
        Assert.Contains(b, h.Selection.SelectedNotes);

        // Ctrl+click an already-selected note toggles it back OUT.
        h.Pane.SelectSounds(new[] { a }, Array.Empty<Chord>(), additive: true);  // {b}
        Assert.Same(b, Assert.Single(h.Selection.SelectedNotes));

        // Ctrl+click on empty space (rest) is a no-op — the multi-selection stays.
        h.Pane.SelectSounds(Array.Empty<GuitarNote>(), Array.Empty<Chord>(), additive: true);
        Assert.Same(b, Assert.Single(h.Selection.SelectedNotes));
    }

    [Fact]
    public void SetFret_AppliesToSelectedNotes_ThroughHistory()
    {
        var h = new Harness();
        var note = new GuitarNote { Position = new FractionalPosition(1), StringIndex = 0, Fret = 3 };
        h.State.PlacedNotes.Add(note);
        h.Selection.SelectedNotes.Add(note);

        h.Pane.SetFret(12);
        Assert.Equal(12, note.Fret);

        h.History.Undo();
        Assert.Equal(3, note.Fret);
    }

    [Fact]
    public void ApplyDuration_SetsNotationDuration_OnNotesAndChords_OneUndoStep()
    {
        var h = new Harness();
        var note = new GuitarNote { Position = new FractionalPosition(1), StringIndex = 0, Fret = 3 };
        var chord = new Chord { Position = new FractionalPosition(2) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 2 });
        h.State.PlacedNotes.Add(note);
        h.State.PlacedChords.Add(chord);
        h.Selection.SelectedNotes.Add(note);
        h.Selection.SelectedChords.Add(chord);

        h.Pane.ApplyDuration(8); // eighth = 1/2 quarter units
        Assert.Equal(new Fraction(1, 2), note.NotationDuration);
        Assert.Equal(new Fraction(1, 2), chord.NotationDuration);

        // A note AND a chord changed, but it is ONE user gesture = ONE undo step.
        h.History.Undo();
        Assert.Null(note.NotationDuration);
        Assert.Null(chord.NotationDuration);
    }

    [Fact]
    public void ApplyDuration_FoldsDottedAndTripletModifiers()
    {
        var h = new Harness();
        var note = new GuitarNote { Position = new FractionalPosition(0), StringIndex = 0, Fret = 3 };
        h.State.PlacedNotes.Add(note);
        h.Selection.SelectedNotes.Add(note);

        h.Pane.DurationDotted = true;
        h.Pane.ApplyDuration(4); // dotted quarter = 1/1 × 3/2 = 3/2
        Assert.Equal(new Fraction(3, 2), note.NotationDuration);

        // Turning on triplet turns dotted OFF (mutually exclusive).
        h.Pane.DurationTriplet = true;
        Assert.False(h.Pane.DurationDotted);
        h.Pane.ApplyDuration(8); // triplet eighth = 1/2 × 2/3 = 1/3
        Assert.Equal(new Fraction(1, 3), note.NotationDuration);
    }

    [Fact]
    public void LengthenAndShortenDuration_DoubleAndHalve_ClampedToTheLadder()
    {
        var h = new Harness();
        var note = new GuitarNote
        {
            Position = new FractionalPosition(0), StringIndex = 0, Fret = 3,
            NotationDuration = new Fraction(1, 2) // eighth
        };
        h.State.PlacedNotes.Add(note);
        h.Selection.SelectedNotes.Add(note);

        h.Pane.LengthenDuration(); // eighth → quarter
        Assert.Equal(new Fraction(1, 1), note.NotationDuration);
        h.Pane.ShortenDuration(); // quarter → eighth
        Assert.Equal(new Fraction(1, 2), note.NotationDuration);

        // Doubling never exceeds a whole (4/1).
        for (var i = 0; i < 8; i++) h.Pane.LengthenDuration();
        Assert.Equal(new Fraction(4, 1), note.NotationDuration);
        // Halving never drops below a 64th (1/16).
        for (var i = 0; i < 12; i++) h.Pane.ShortenDuration();
        Assert.Equal(new Fraction(1, 16), note.NotationDuration);
    }

    [Fact]
    public void LengthenDuration_OnUnauthoredNote_TreatsNullAsQuarter()
    {
        var h = new Harness();
        var note = new GuitarNote { Position = new FractionalPosition(0), StringIndex = 0, Fret = 3 };
        h.State.PlacedNotes.Add(note);
        h.Selection.SelectedNotes.Add(note);

        h.Pane.LengthenDuration(); // null → quarter (1/1) → half (2/1)
        Assert.Equal(new Fraction(2, 1), note.NotationDuration);
    }

    [Fact]
    public void ClearDuration_RevertsToAuto_ThroughHistory()
    {
        var h = new Harness();
        var note = new GuitarNote
        {
            Position = new FractionalPosition(0), StringIndex = 0, Fret = 3,
            NotationDuration = new Fraction(1, 4)
        };
        h.State.PlacedNotes.Add(note);
        h.Selection.SelectedNotes.Add(note);

        h.Pane.ClearDurationCommand.Execute(null);
        Assert.Null(note.NotationDuration);

        h.History.Undo();
        Assert.Equal(new Fraction(1, 4), note.NotationDuration);
    }

    [Fact]
    public void ApplyDuration_WithNoSelection_IsANoOp_NoHistoryNode()
    {
        var h = new Harness();
        var before = h.History.CurrentNodeId;

        h.Pane.ApplyDuration(4);

        Assert.Equal(before, h.History.CurrentNodeId); // nothing recorded
    }

    [Fact]
    public void ToggleLayout_FlipsPageMode_AndPersists()
    {
        var h = new Harness();
        Assert.False(h.Pane.SheetPageMode); // strip is the default

        h.Pane.SheetPageMode = !h.Pane.SheetPageMode; // what the stave cluster's ToggleButton writes
        Assert.True(h.Pane.SheetPageMode);
        Assert.True(h.Config.AppConfig.SheetMusicPageMode); // persisted

        h.Pane.SheetPageMode = !h.Pane.SheetPageMode; // what the stave cluster's ToggleButton writes
        Assert.False(h.Pane.SheetPageMode);
        Assert.False(h.Config.AppConfig.SheetMusicPageMode);
    }

    private sealed class Harness
    {
        public Harness()
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var collisions = new StrictCollisionService();
            var config = Config;
            var scope = new EditingScope(State);
            var highway = new HighwayEditingService(State, Selection, History, collisions, config, scope,
                new EditTargetService(Selection, scope, config), logger);
            var tempo = new TempoService(logger);

            var project = new ChartProject();
            for (var i = 0; i < 16; i++)
                project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
            var arr = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            arr.Levels.Add(new ChartLevel());
            project.Arrangements.Add(arr);

            State.ActiveProject = project;
            State.ActiveArrangement = arr;
            State.ActiveLevel = arr.Levels[0];
            State.TotalAudioLengthMs = 8000;

            var transport = new TransportViewModel(Audio, State, config, FakeLocalization.KeysOnly());
            var trackContexts = new TrackContextRegistry(State, Selection);
            var editor = new ChartEditorViewModel(State, transport, History, Selection, tempo, trackContexts, FakeLocalization.KeysOnly());
            Pane = new SheetMusicPaneViewModel(State, editor, highway, History, config);
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public HistoryService History { get; }
        public FakeAudioPlaybackService Audio { get; } = new();
        public FakeConfigurationService Config { get; } = new();
        public SheetMusicPaneViewModel Pane { get; }
    }
}
