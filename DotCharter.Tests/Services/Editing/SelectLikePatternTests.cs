using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Catalog;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.Services.Editing;

/// <summary>
///     The select-like ENTRY POINT (Ctrl+L / Edit → Select like), end to end
///     through the real coordinator. The matching rule itself is pinned in
///     <c>SelectLikeShapeTests</c>; what these add is that the coordinator
///     seeds from — and writes back to — the hand-shape selection as well as
///     the chord selection, which it simply never did.
/// </summary>
public sealed class SelectLikePatternTests
{
    [Fact]
    public void AfterOneChordIsRefingered_SelectLikeStillFindsItsSiblings()
    {
        // The owner's log, verbatim: eight identical freshly imported chords,
        // re-finger ONE (it gets a private template) and select-like from it
        // reported "added 0 chord(s)" until the fingering edit was undone.
        var h = new Harness();
        h.Arrangement.ChordTemplates.Add(NewTemplate(0, "A5", [(1, 7), (2, 7)], [(1, 1), (2, 4)]));

        var refingered = NewChord(0, 0, (1, 7), (2, 7));
        h.Level.Chords.Add(refingered);
        var siblings = new List<Chord>();
        for (var i = 1; i < 8; i++)
        {
            var sibling = NewChord(-1, i * 4, (1, 7), (2, 7));
            h.Level.Chords.Add(sibling);
            siblings.Add(sibling);
        }

        h.Selection.SelectedChords.Add(refingered);
        h.Coordinator.SelectLikePattern();

        Assert.Equal(8, h.Selection.SelectedChords.Count);
        Assert.All(siblings, c => Assert.Contains(c, h.Selection.SelectedChords));
    }

    [Fact]
    public void SelectLike_SelectsLikeHandShapes()
    {
        var h = new Harness();
        h.Arrangement.ChordTemplates.Add(NewTemplate(0, "A5", [(1, 7), (2, 7)], [(1, 1), (2, 3)]));
        h.Arrangement.ChordTemplates.Add(NewTemplate(1, "C5", [(1, 3), (2, 5)]));

        var seed = NewShape(0, 0);
        var alike = NewShape(0, 8);
        var different = NewShape(1, 16);
        h.Level.HandShapes.Add(seed);
        h.Level.HandShapes.Add(alike);
        h.Level.HandShapes.Add(different);

        h.Selection.SelectedHandShapes.Add(seed);
        h.Coordinator.SelectLikePattern();

        Assert.Equal([seed, alike], h.Selection.SelectedHandShapes);
    }

    [Fact]
    public void SelectLike_WithNothingSelected_SelectsNothing()
    {
        var h = new Harness();
        h.Arrangement.ChordTemplates.Add(NewTemplate(0, "A5", [(1, 7), (2, 7)]));
        h.Level.Chords.Add(NewChord(0, 0, (1, 7), (2, 7)));

        h.Coordinator.SelectLikePattern();

        Assert.Empty(h.Selection.SelectedChords);
        Assert.Empty(h.Selection.SelectedHandShapes);
    }

    private static ChordTemplate NewTemplate(int id, string name,
        (int stringIndex, int fret)[] frets, (int stringIndex, int finger)[]? fingers = null)
    {
        var template = new ChordTemplate { Id = id, Name = name };
        foreach (var (stringIndex, fret) in frets) template.Frets[stringIndex] = fret;
        foreach (var (stringIndex, finger) in fingers ?? [])
            template.Fingers[stringIndex] = finger;
        return template;
    }

    private static Chord NewChord(int templateId, int beat, params (int stringIndex, int fret)[] notes)
    {
        var chord = new Chord { TemplateId = templateId, Position = new FractionalPosition(beat) };
        foreach (var (stringIndex, fret) in notes)
            chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
        return chord;
    }

    private static HandShape NewShape(int templateId, int beat) => new()
    {
        TemplateId = templateId,
        Position = new FractionalPosition(beat),
        EndPosition = new FractionalPosition(beat + 2)
    };

    private sealed class Harness
    {
        public Harness()
        {
            var logger = new FakeAppLogger();
            var history = new HistoryService(logger);
            var config = new FakeConfigurationService();
            var scope = new EditingScope(State);
            var highway = new HighwayEditingService(State, Selection, history, new StrictCollisionService(),
                config, scope, new EditTargetService(Selection, scope, config), logger);

            var project = new ChartProject();
            for (var i = 0; i < 64; i++) project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
            Arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            Arrangement.Levels.Add(new ChartLevel());
            project.Arrangements.Add(Arrangement);

            State.ActiveProject = project;
            State.ActiveArrangement = Arrangement;
            State.ActiveLevel = Arrangement.Levels[0];

            var tracks = new TrackContextRegistry(State, Selection);
            var catalogDir = Path.Combine(Path.GetTempPath(), "dotcharter-selectlike-tests-no-such-dir");

            Coordinator = new GlobalEditingCoordinator(State, Selection, history,
                new ClipboardService(logger), new TempoService(logger), new BeatScaleService(logger),
                highway, config, new SelectLikeService(logger), new FingeringGenerationService(),
                tracks, new PhraseCatalogService(catalogDir, new RscpChartSerializer(), logger), logger,
                FakeLocalization.English());
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public Arrangement Arrangement { get; }
        public ChartLevel Level => Arrangement.Levels[0];
        public GlobalEditingCoordinator Coordinator { get; }
    }
}
