using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Catalog;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Core.Domain.Vocals;

namespace DotCharter.Tests.Services.Editing;

/// <summary>
///     "Delete + related" (Edit → Delete and related) removes the hand shapes a
///     delete just emptied. It open-coded a CLOSED containment span
///     (<c>note.Position &lt;= hs.EndPosition</c>) while every other editing site
///     uses <see cref="HandShape.Covers" /> — start inclusive, end EXCLUSIVE.
///     <para>
///         Hand shapes ABUT: the per-chord shapes every tab importer emits park
///         one shape's end exactly on the next one's start (A=[0,4) B=[4,8)). So
///         under the closed reading B's opening sound counted as content of A,
///         and emptying A left it looking occupied — the orphan was never
///         collected, and the chart kept a hand-shape bar over nothing.
///     </para>
/// </summary>
public sealed class DeleteSelectedRelatedTests
{
    /// <summary>
    ///     Delete removes a selected LYRIC when it is the whole selection (2026-09-25). It had no
    ///     lyric branch: with a lyric selected Delete did nothing — or, before a lyric click
    ///     cleared the rest of the selection, deleted the NOTE left selected a moment earlier. The
    ///     lyric lane's focus area is the event rows, but a lone lyric is deleted from either.
    /// </summary>
    [Theory]
    [InlineData(EditorFocusArea.TimelineEvents)]
    [InlineData(EditorFocusArea.NoteHighway)]
    public void Delete_RemovesALyricThatIsTheWholeSelection_AsOneUndoStep(EditorFocusArea focus)
    {
        var h = new Harness();
        var (phrase, kept, doomed) = h.AddTwoLyrics();
        h.Selection.SelectedLyric = doomed;

        h.Coordinator.DeleteSelected(focus);

        Assert.Equal([kept], phrase.Lyrics);
        Assert.Null(h.Selection.SelectedLyric);

        h.History.Undo();
        Assert.Equal([kept, doomed], phrase.Lyrics);
    }

    /// <summary>With other content selected too, Delete keeps its focus-area rule and leaves the lyric.</summary>
    [Fact]
    public void Delete_WithANoteSelectedToo_LeavesTheLyric()
    {
        var h = new Harness();
        var (phrase, kept, other) = h.AddTwoLyrics();
        var note = Note(0);
        h.Level.Notes.Add(note);
        h.Selection.SelectedNotes.Add(note);
        h.Selection.SelectedLyric = other;

        h.Coordinator.DeleteSelected(EditorFocusArea.NoteHighway);

        Assert.Empty(h.Level.Notes);
        Assert.Equal([kept, other], phrase.Lyrics);
    }

    [Fact]
    public void EmptyingTheFirstOfTwoAbuttingShapes_RemovesIt()
    {
        var h = new Harness();
        var first = Shape(0, 4);
        var second = Shape(4, 8);
        h.Level.HandShapes.Add(first);
        h.Level.HandShapes.Add(second);

        var inFirst = Note(0);
        var inSecond = Note(4);
        h.Level.Notes.Add(inFirst);
        h.Level.Notes.Add(inSecond);

        h.Selection.SelectedNotes.Add(inFirst);
        h.Coordinator.DeleteSelectedRelated(EditorFocusArea.NoteHighway);

        Assert.Equal([second], h.Level.HandShapes);
    }

    /// <summary>
    ///     …and the shape that still holds a sound of its own survives. Without
    ///     this the fix could be "delete every shape", which is not the fix.
    /// </summary>
    [Fact]
    public void AShapeThatStillHoldsASound_Survives()
    {
        var h = new Harness();
        var first = Shape(0, 4);
        h.Level.HandShapes.Add(first);

        var deleted = Note(0);
        var kept = Note(2);
        h.Level.Notes.Add(deleted);
        h.Level.Notes.Add(kept);

        h.Selection.SelectedNotes.Add(deleted);
        h.Coordinator.DeleteSelectedRelated(EditorFocusArea.NoteHighway);

        Assert.Equal([first], h.Level.HandShapes);
    }

    /// <summary>
    ///     A shape that was already empty and had nothing to do with the delete
    ///     is left alone — the "only shapes that overlapped a deleted item" guard.
    ///     It used the same closed span, so deleting the first sound of an
    ///     abutting NEXT shape used to make its empty predecessor a candidate.
    /// </summary>
    [Fact]
    public void AnUnrelatedEmptyShape_IsLeftAlone()
    {
        var h = new Harness();
        var empty = Shape(0, 4);
        var busy = Shape(4, 8);
        h.Level.HandShapes.Add(empty);
        h.Level.HandShapes.Add(busy);

        var onlySoundOfBusy = Note(4);
        h.Level.Notes.Add(onlySoundOfBusy);

        h.Selection.SelectedNotes.Add(onlySoundOfBusy);
        h.Coordinator.DeleteSelectedRelated(EditorFocusArea.NoteHighway);

        // `busy` is genuinely orphaned and goes; `empty` never held that note.
        Assert.Equal([empty], h.Level.HandShapes);
    }

    [Fact]
    public void ChordsCountAsContentToo()
    {
        var h = new Harness();
        var first = Shape(0, 4);
        var second = Shape(4, 8);
        h.Level.HandShapes.Add(first);
        h.Level.HandShapes.Add(second);

        var inFirst = Chord(0);
        h.Level.Chords.Add(inFirst);
        h.Level.Chords.Add(Chord(4));

        h.Selection.SelectedChords.Add(inFirst);
        h.Coordinator.DeleteSelectedRelated(EditorFocusArea.NoteHighway);

        Assert.Equal([second], h.Level.HandShapes);
    }

    /// <summary>
    ///     One keybind, ONE undo node. "Delete and related" is a single verb
    ///     (<c>k.DeleteRelated</c>, and the <c>edit.deleteRelated</c> palette
    ///     command), but it used to call <c>DeleteSelected</c> — which commits its
    ///     own composite — and then push the orphaned hand shapes as a SECOND
    ///     node. So one Ctrl+Z restored the hand-shape bars over sounds that were
    ///     still deleted: a chart state that never existed, with a second press
    ///     needed to finish.
    ///     <para>
    ///         The observable assertion is the user's: after ONE Undo the sounds
    ///         and the bars are both back, and there is nothing left to undo.
    ///         <c>CanUndo</c> is what makes it sharp — the
    ///         two-node version restores the bars on the first press and leaves
    ///         the sound removal sitting on the stack. Nothing merges these away:
    ///         a remove action is not <c>IMergeableAction</c>.
    ///     </para>
    /// </summary>
    [Fact]
    public void DeleteAndRelated_IsOneUndoNode_NotTwo()
    {
        var h = new Harness();
        var first = Shape(0, 4);
        var second = Shape(4, 8);
        h.Level.HandShapes.Add(first);
        h.Level.HandShapes.Add(second);

        var inFirst = Note(0);
        var inSecond = Note(4);
        h.Level.Notes.Add(inFirst);
        h.Level.Notes.Add(inSecond);

        h.Selection.SelectedNotes.Add(inFirst);
        h.Coordinator.DeleteSelectedRelated(EditorFocusArea.NoteHighway);

        // The gesture did both halves: the sound went, and the shape it emptied.
        Assert.Equal([inSecond], h.Level.Notes);
        Assert.Equal([second], h.Level.HandShapes);

        h.History.Undo();

        Assert.Equal([inFirst, inSecond], h.Level.Notes);
        Assert.Equal([first, second], h.Level.HandShapes);
        Assert.False(h.History.CanUndo);
    }

    /// <summary>
    ///     The same one-node rule when the delete removes a hand shape the user
    ///     selected AND orphans another one: the selected shape must not be
    ///     collected twice (the orphan scan now runs before the removals execute,
    ///     so it has to discount them itself).
    /// </summary>
    [Fact]
    public void ASelectedShapeAndAnOrphanedOne_AreOneUndoNodeAndRemovedOnce()
    {
        var h = new Harness();
        var selected = Shape(0, 4);
        var orphaned = Shape(4, 8);
        h.Level.HandShapes.Add(selected);
        h.Level.HandShapes.Add(orphaned);

        var inOrphaned = Note(4);
        h.Level.Notes.Add(inOrphaned);

        h.Selection.SelectedHandShapes.Add(selected);
        h.Selection.SelectedNotes.Add(inOrphaned);
        h.Coordinator.DeleteSelectedRelated(EditorFocusArea.NoteHighway);

        Assert.Empty(h.Level.HandShapes);
        Assert.Empty(h.Level.Notes);

        h.History.Undo();

        Assert.Equal([selected, orphaned], h.Level.HandShapes);
        Assert.Equal([inOrphaned], h.Level.Notes);
        Assert.False(h.History.CanUndo);
    }

    private static HandShape Shape(int startBeat, int endBeat) => new()
    {
        TemplateId = 0,
        Position = new FractionalPosition(startBeat),
        EndPosition = new FractionalPosition(endBeat)
    };

    private static GuitarNote Note(int beat) =>
        new() { Position = new FractionalPosition(beat), StringIndex = 0, Fret = 3 };

    private static Chord Chord(int beat)
    {
        var chord = new Chord { TemplateId = 0, Position = new FractionalPosition(beat) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
        return chord;
    }

    private sealed class Harness
    {
        public Harness()
        {
            var logger = new FakeAppLogger();
            var history = new HistoryService(logger);
            History = history;
            var config = new FakeConfigurationService();
            // Min note distance OFF, which is what leaves the abutting shapes
            // abutting. With it on (the default is a 64th of a measure) every
            // collection action's post-pass runs ChartSpacingEnforcer
            // .EnforceHandShapeSpacing, which pulls a shape's end back off the
            // next shape's start before this code ever looks — which is exactly
            // why the closed-span reading survived unnoticed. It is a supported
            // setting, and with it the defect is live.
            config.AppConfig.MinNoteDistanceMode = MinNoteDistanceMode.Milliseconds;
            config.AppConfig.MinNoteDistanceMs = 0;
            var scope = new EditingScope(State);
            var highway = new HighwayEditingService(State, Selection, history, new StrictCollisionService(),
                config, scope, new EditTargetService(Selection, scope, config), logger);

            var project = new ChartProject();
            for (var i = 0; i < 64; i++) project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
            Arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            Arrangement.Levels.Add(new ChartLevel());
            Arrangement.ChordTemplates.Add(new ChordTemplate
            {
                Id = 0, Name = "G5", Frets = new Dictionary<int, int> { { 0, 3 }, { 1, 5 } }
            });
            project.Arrangements.Add(Arrangement);

            State.ActiveProject = project;
            State.ActiveArrangement = Arrangement;
            State.ActiveLevel = Arrangement.Levels[0];

            var tracks = new TrackContextRegistry(State, Selection);
            var catalogDir = Path.Combine(Path.GetTempPath(), "dotcharter-delete-related-tests-no-such-dir");

            Coordinator = new GlobalEditingCoordinator(State, Selection, history,
                new ClipboardService(logger), new TempoService(logger), new BeatScaleService(logger),
                highway, config, new SelectLikeService(logger), new FingeringGenerationService(),
                tracks, new PhraseCatalogService(catalogDir, new RscpChartSerializer(), logger), logger,
                FakeLocalization.English());
        }

        public (LyricPhrase Phrase, LyricEvent First, LyricEvent Second) AddTwoLyrics()
        {
            var beats = State.ActiveProject!.Beats;
            LyricEvent Ly(double ms, string text) => new()
            {
                Position = TimelineMath.PositionFromMs(ms, beats),
                EndPosition = TimelineMath.PositionFromMs(ms + 200, beats),
                Text = text
            };

            var first = Ly(1000, "la");
            var second = Ly(1500, "da");
            var phrase = new LyricPhrase { Lyrics = { first, second } };
            State.ActiveProject.VocalTracks.Add(new VocalTrack { Phrases = { phrase } });
            return (phrase, first, second);
        }

        public HistoryService History { get; }
        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public Arrangement Arrangement { get; }
        public ChartLevel Level => Arrangement.Levels[0];
        public GlobalEditingCoordinator Coordinator { get; }
    }
}
