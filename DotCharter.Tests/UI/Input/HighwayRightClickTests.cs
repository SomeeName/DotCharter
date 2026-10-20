using Avalonia.Input;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;
using DotCharter.UI.Input;

namespace DotCharter.Tests.UI.Input;

/// <summary>
///     #467: a plain right-click on the note highway is the charting verb again —
///     place over an empty lane, remove a note, and remove ONE string's note from
///     a chord. 0.59 had moved all three behind Alt and given the bare click a
///     context menu, which blocked the workflow. The menu is now Shift+right-click.
///     Driven through the REAL <see cref="HighwayEditingService" />, so these pin
///     the chart that results, not just the decision.
/// </summary>
public sealed class HighwayRightClickTests
{
    [Theory]
    [InlineData(KeyModifiers.None)]
    [InlineData(KeyModifiers.Alt)]
    public void RightClickOnANote_RemovesIt(KeyModifiers modifiers)
    {
        var h = new Harness();
        var note = h.AddNote(2, stringIndex: 1, fret: 5);

        var verb = h.RightClick(null, note, 2, 1, modifiers);

        Assert.Equal(HighwayRightClickVerb.RemoveNote, verb);
        Assert.Empty(h.Level.Notes);
        Assert.Empty(h.Level.Chords);
    }

    [Fact]
    public void RightClickOnOneChordString_RemovesOnlyThatStringsNote()
    {
        var h = new Harness();
        var chord = h.AddChord(2, (0, 3), (1, 5), (2, 5));

        var verb = h.RightClick(chord, null, 2, 1, KeyModifiers.None);

        Assert.Equal(HighwayRightClickVerb.EditChordString, verb);
        var remaining = Assert.Single(h.Level.Chords);
        Assert.Equal([0, 2], remaining.ChordNotes.Select(n => n.StringIndex).Order());
        Assert.Equal([3, 5], remaining.ChordNotes.OrderBy(n => n.StringIndex).Select(n => n.Fret));
        Assert.Empty(h.Level.Notes);
    }

    [Fact]
    public void RightClickOnEmptySpace_PlacesANote()
    {
        var h = new Harness();

        var verb = h.RightClick(null, null, 4, 3, KeyModifiers.None);

        Assert.Equal(HighwayRightClickVerb.PlaceNote, verb);
        var placed = Assert.Single(h.Level.Notes);
        Assert.Equal(3, placed.StringIndex);
        Assert.Equal(new FractionalPosition(4), placed.Position);
    }

    [Fact]
    public void RightClickBesideANoteOnAnotherString_PlacesRatherThanRemoves()
    {
        var h = new Harness();
        var note = h.AddNote(2, stringIndex: 1, fret: 5);

        var verb = h.RightClick(null, note, 2, 4, KeyModifiers.None);

        Assert.Equal(HighwayRightClickVerb.PlaceNote, verb);
        // The note survives (alone, or merged into a chord with the new one).
        Assert.Equal(2, h.Level.Notes.Count + h.Level.Chords.Sum(c => c.ChordNotes.Count));
    }

    [Fact]
    public void ShiftRightClick_OpensTheMenu_AndEditsNothing()
    {
        var h = new Harness();
        var note = h.AddNote(2, stringIndex: 1, fret: 5);
        var chord = h.AddChord(6, (0, 3), (1, 5));

        Assert.Equal(HighwayRightClickVerb.OpenMenu, h.RightClick(null, note, 2, 1, KeyModifiers.Shift));
        Assert.Equal(HighwayRightClickVerb.OpenMenu, h.RightClick(chord, null, 6, 1, KeyModifiers.Shift));
        Assert.Equal(HighwayRightClickVerb.OpenMenu, h.RightClick(null, null, 9, 1, KeyModifiers.Shift));

        Assert.Same(note, Assert.Single(h.Level.Notes));
        Assert.Equal(2, Assert.Single(h.Level.Chords).ChordNotes.Count);
    }

    [Fact]
    public void APlainRightClick_NeverOpensTheMenu()
    {
        var note = new GuitarNote { StringIndex = 1 };
        var chord = new Chord();
        foreach (var mods in new[] { KeyModifiers.None, KeyModifiers.Alt, KeyModifiers.Control })
        {
            Assert.NotEqual(HighwayRightClickVerb.OpenMenu, HighwayRightClick.Resolve(null, null, 0, mods));
            Assert.NotEqual(HighwayRightClickVerb.OpenMenu, HighwayRightClick.Resolve(null, note, 1, mods));
            Assert.NotEqual(HighwayRightClickVerb.OpenMenu, HighwayRightClick.Resolve(chord, null, 1, mods));
        }
    }

    private sealed class Harness
    {
        private readonly ChartProject _project = new();
        private readonly HighwayEditingService _service;

        public Harness()
        {
            var logger = new FakeAppLogger();
            var history = new HistoryService(logger);
            var config = new FakeConfigurationService();
            var scope = new EditingScope(State);
            _service = new HighwayEditingService(State, Selection, history, new StrictCollisionService(),
                config, scope, new EditTargetService(Selection, scope, config), logger);

            for (var i = 0; i < 32; i++)
                _project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });

            var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            arrangement.Levels.Add(new ChartLevel());
            _project.Arrangements.Add(arrangement);
            State.ActiveProject = _project;
            State.ActiveArrangement = arrangement;
            State.ActiveLevel = arrangement.Levels[0];
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public ChartLevel Level => State.ActiveLevel!;

        public HighwayRightClickVerb RightClick(
            Chord? chord, GuitarNote? note, int beat, int stringIndex, KeyModifiers modifiers)
        {
            var verb = HighwayRightClick.Resolve(chord, note, stringIndex, modifiers);
            var applied = HighwayRightClick.Apply(_service, verb, chord, note, new FractionalPosition(beat), stringIndex);
            Assert.Equal(verb != HighwayRightClickVerb.OpenMenu, applied);
            return verb;
        }

        public GuitarNote AddNote(int beat, int stringIndex, int fret)
        {
            var note = new GuitarNote { Position = new FractionalPosition(beat), StringIndex = stringIndex, Fret = fret };
            Level.Notes.Add(note);
            return note;
        }

        public Chord AddChord(int beat, params (int stringIndex, int fret)[] frets)
        {
            var chord = new Chord { Position = new FractionalPosition(beat) };
            foreach (var (stringIndex, fret) in frets)
                chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
            Level.Chords.Add(chord);
            return chord;
        }
    }
}
