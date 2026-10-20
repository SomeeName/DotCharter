using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Catalog;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.Services.Editing;

/// <summary>
///     A copy carries exactly what is selected (#468). One chord inside a hand
///     shape, copied and pasted, used to bring the whole bar and the governing
///     FHP along, and the bar's earlier start became the payload origin, so the
///     chord landed late. Hand shapes and anchors still travel when they are
///     selected themselves. Copy → paste end to end, through the real
///     clipboard and the history-backed commit.
/// </summary>
public sealed class ChordCopyScopeTests
{
    // 120 BPM: 500 ms per beat, so 8000 ms is beat 16.
    private const double PasteAtMs = 8000;

    [Fact]
    public void ASingleChordInsideAHandShapePastesAsExactlyOneChordAtThePastePosition()
    {
        var h = new Harness();
        var (chord, _, _) = h.ChordInsideHandShapeUnderAnAnchor();

        h.Selection.SelectedChords.Add(chord);
        h.CopyAndPaste();

        Assert.Equal(2, h.Level.Chords.Count);
        Assert.Contains(h.Level.Chords, c => c.Position == new FractionalPosition(16));
        Assert.Single(h.Level.HandShapes);
        Assert.Single(h.Level.Anchors);
    }

    [Fact]
    public void AnExplicitlySelectedHandShapeStillCopiesWithItsChord()
    {
        var h = new Harness();
        var (chord, bar, _) = h.ChordInsideHandShapeUnderAnAnchor();

        h.Selection.SelectedChords.Add(chord);
        h.Selection.SelectedHandShapes.Add(bar);
        h.CopyAndPaste();

        // The bar starts at beat 8 and is the payload origin; the chord at 10
        // keeps its distance from it.
        Assert.Equal(2, h.Level.HandShapes.Count);
        Assert.Contains(h.Level.HandShapes,
            hs => hs.Position == new FractionalPosition(16) && hs.EndPosition == new FractionalPosition(20));
        Assert.Contains(h.Level.Chords, c => c.Position == new FractionalPosition(18));
        Assert.Single(h.Level.Anchors);
    }

    [Fact]
    public void AnExplicitlySelectedAnchorStillCopies()
    {
        var h = new Harness();
        var (chord, _, anchor) = h.ChordInsideHandShapeUnderAnAnchor();

        h.Selection.SelectedChords.Add(chord);
        h.Selection.SelectedAnchors.Add(anchor);
        h.CopyAndPaste();

        Assert.Equal(2, h.Level.Anchors.Count);
        Assert.Contains(h.Level.Anchors, a => a.Position == new FractionalPosition(16) && a.Fret == 5);
        Assert.Contains(h.Level.Chords, c => c.Position == new FractionalPosition(20));
        Assert.Single(h.Level.HandShapes);
    }

    private sealed class Harness
    {
        private readonly ChartProject _project = new();
        private readonly HistoryService _history;
        private readonly EditingClipboardCoordinator _clipboard;

        public Harness()
        {
            var logger = new FakeAppLogger();
            _history = new HistoryService(logger);
            var config = new FakeConfigurationService();
            var scope = new EditingScope(State);
            var service = new HighwayEditingService(State, Selection, _history, new StrictCollisionService(),
                config, scope, new EditTargetService(Selection, scope, config), logger);

            for (var i = 0; i < 64; i++)
                _project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });

            Arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            foreach (var step in TuningUtilities.GetOffsetsForTuningType("E_STANDARD", 6))
                Arrangement.TuningPitches.Add(new TuningOffset(step));
            Arrangement.Levels.Add(new ChartLevel());
            _project.Arrangements.Add(Arrangement);
            State.ActiveProject = _project;
            State.ActiveArrangement = Arrangement;
            State.ActiveLevel = Level;

            _clipboard = new EditingClipboardCoordinator(new ClipboardService(logger), config, service,
                Selection, new TempoService(logger), logger);
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public Arrangement Arrangement { get; }
        public ChartLevel Level => Arrangement.Levels[0];

        /// <summary>FHP at beat 6 (fret 5), hand shape over beats 8..12, and
        /// a zero-sustain chord at beat 10 inside it.</summary>
        public (Chord Chord, HandShape Bar, FretAnchor Anchor) ChordInsideHandShapeUnderAnAnchor()
        {
            var template = new ChordTemplate { Id = 7, Name = "Dmin" };
            template.Frets[0] = 5;
            template.Frets[1] = 7;
            Arrangement.ChordTemplates.Add(template);

            var anchor = new FretAnchor { Position = new FractionalPosition(6), Fret = 5, Width = 4 };
            Level.Anchors.Add(anchor);
            var bar = new HandShape
            {
                Position = new FractionalPosition(8), EndPosition = new FractionalPosition(12), TemplateId = 7
            };
            Level.HandShapes.Add(bar);
            var chord = new Chord { Position = new FractionalPosition(10), TemplateId = 7 };
            chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 5 });
            chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 7 });
            Level.Chords.Add(chord);
            return (chord, bar, anchor);
        }

        public void CopyAndPaste()
        {
            var track = new TrackEditorContext(Arrangement, Selection) { Project = _project, Level = Level };
            _clipboard.Copy(track, EditorFocusArea.NoteHighway);
            Selection.ClearAll();
            var outcome = _clipboard.Paste(track, PasteAtMs, 4);
            Assert.NotNull(outcome);
            Assert.Equal(PhrasePasteResult.Pasted, outcome!.Result);
        }
    }
}
