using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;

namespace DotCharter.Tests.Services.Editing.Highway;

/// <summary>
///     <c>ApplyNotePlacementCommand</c> — the one creation verb, shared by the
///     pointer's right-click and the keyboard pen — adds and removes a CHORD's
///     strings, which is a SHAPE change. It used to emit a bare
///     <c>MutateChordsAction</c> and leave <c>TemplateId</c> alone while its
///     sibling <c>EditChordStringCommand</c> repointed, so the same gesture was
///     correct through one route and lossy through the other: an rscp
///     <c>&lt;chordNote&gt;</c> carries no <c>fret</c> attribute, so the added
///     string was discarded on the next save, and <c>ChartSpacingEnforcer</c>
///     deliberately never repairs a drifted chord.
/// </summary>
public sealed class PlacementTemplateRepointTests
{
    [Fact]
    public void AddingAStringToAChordRepointsItAtATemplateDescribingTheNewShape()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 5), (1, 7));
        var chord = h.AddChord(2, 0, (0, 5), (1, 7));
        h.State.PreviewNoteFret = 7;

        h.Service.ApplyPlacement(new FractionalPosition(2), 2);

        var resolved = h.Arrangement.ChordTemplates.SingleOrDefault(t => t.Id == chord.TemplateId);
        Assert.NotNull(resolved);
        Assert.Equal(ChordTemplateResolver.FretsOf(chord), resolved.Frets);
        Assert.Equal(3, resolved.Frets.Count);
    }

    [Fact]
    public void TheSharedTemplateIsNeverMutatedInPlace()
    {
        var h = new Harness();
        var shared = h.AddTemplate(0, (0, 5), (1, 7));
        var untouched = h.AddChord(6, 0, (0, 5), (1, 7));
        h.AddChord(2, 0, (0, 5), (1, 7));
        h.State.PreviewNoteFret = 7;

        h.Service.ApplyPlacement(new FractionalPosition(2), 2);

        Assert.Equal(2, shared.Frets.Count);
        Assert.Equal(0, untouched.TemplateId);
    }

    [Fact]
    public void RemovingAStringFromAThreeNoteChordAlsoRepoints()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 5), (1, 7), (2, 7));
        var chord = h.AddChord(2, 0, (0, 5), (1, 7), (2, 7));

        h.Service.ApplyPlacement(new FractionalPosition(2), 2);

        Assert.Equal(2, chord.ChordNotes.Count);
        var resolved = h.Arrangement.ChordTemplates.SingleOrDefault(t => t.Id == chord.TemplateId);
        Assert.NotNull(resolved);
        Assert.Equal(ChordTemplateResolver.FretsOf(chord), resolved.Frets);
    }

    [Fact]
    public void AnUntemplatedChordStaysUntemplated()
    {
        var h = new Harness();
        var chord = h.AddChord(2, templateId: -1, (0, 5), (1, 7));
        h.State.PreviewNoteFret = 7;

        h.Service.ApplyPlacement(new FractionalPosition(2), 2);

        Assert.Equal(-1, chord.TemplateId);
        Assert.Empty(h.Arrangement.ChordTemplates);
    }

    [Fact]
    public void TheMintedTemplateAndTheReshapeAreOneUndoStep()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 5), (1, 7));
        var chord = h.AddChord(2, 0, (0, 5), (1, 7));
        h.State.PreviewNoteFret = 7;

        h.Service.ApplyPlacement(new FractionalPosition(2), 2);
        Assert.Equal(2, h.Arrangement.ChordTemplates.Count);

        h.History.Undo();

        Assert.Single(h.Arrangement.ChordTemplates);
        Assert.Equal(0, chord.TemplateId);
        Assert.Equal(2, chord.ChordNotes.Count);
    }

    /// <summary>Internal so the chord-collapse tests can reuse the same wiring.</summary>
    internal sealed class Harness
    {
        public Harness()
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var collisions = new StrictCollisionService();
            var config = new FakeConfigurationService();
            Scope = new EditingScope(State);
            Service = new HighwayEditingService(State, Selection, History, collisions, config, Scope,
                new EditTargetService(Selection, Scope, config), logger);

            var project = new ChartProject();
            for (var i = 0; i < 16; i++)
                project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
            Arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            Arrangement.Levels.Add(new ChartLevel());
            project.Arrangements.Add(Arrangement);

            State.ActiveProject = project;
            State.ActiveArrangement = Arrangement;
            State.ActiveLevel = Arrangement.Levels[0];
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public HistoryService History { get; }
        public EditingScope Scope { get; }
        public HighwayEditingService Service { get; }
        public Arrangement Arrangement { get; }

        public ChordTemplate AddTemplate(int id, params (int stringIdx, int fret)[] frets)
        {
            var t = new ChordTemplate { Id = id };
            foreach (var (stringIdx, fret) in frets) t.Frets[stringIdx] = fret;
            Arrangement.ChordTemplates.Add(t);
            return t;
        }

        public Chord AddChord(int beat, int templateId, params (int stringIdx, int fret)[] notes)
        {
            var c = new Chord { Position = new FractionalPosition(beat), TemplateId = templateId };
            foreach (var (stringIdx, fret) in notes)
                c.ChordNotes.Add(new ChordNote { StringIndex = stringIdx, Fret = fret });
            State.PlacedChords.Add(c);
            return c;
        }
    }
}
