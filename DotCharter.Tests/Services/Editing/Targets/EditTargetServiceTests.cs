using System.Collections.ObjectModel;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;

namespace DotCharter.Tests.Services.Editing.Targets;

/// <summary>
///     The ladder wired to real state, and — the point of Stage 3 — every
///     keyboard verb in <see cref="HighwayEditingService" /> reaching the sound
///     it resolves. Before this, the pen resolved an implied target while every
///     other verb was selection-only, so the same keystroke reached a different
///     sound depending on which verb it was.
/// </summary>
public sealed class EditTargetServiceTests
{
    // === The gate ============================================================

    [Fact]
    public void WithFastChartingInputOffOnlyTheSelectionRungExists()
    {
        // Byte-for-byte the pre-v0.35 "act on the selection, else do nothing":
        // Stage 3's implied rungs ride the same one setting as the pen.
        var h = new Harness(fastInput: false);
        var note = h.AddNote(2, 1, 3);
        h.Pen(new FractionalPosition(2));

        Assert.Equal(EditTargetSource.None, h.Targets.Resolve().Source);

        h.Selection.SelectedNotes.Add(note);
        Assert.Equal(EditTargetSource.Selection, h.Targets.Resolve().Source);
    }

    [Fact]
    public void WithFastChartingInputOffAHandShapeSelectionResolvesToNothing()
    {
        var h = new Harness(fastInput: false);
        h.AddNote(2, 1, 3);
        h.Selection.SelectedHandShapes.Add(h.AddHandShape(0, 8));

        Assert.Equal(EditTargetSource.None, h.Targets.Resolve().Source);
    }

    [Fact]
    public void TheGateIsReReadPerCallSoTheSettingNeedsNoRestart()
    {
        var h = new Harness(fastInput: false);
        h.AddNote(2, 1, 3);
        h.Pen(new FractionalPosition(2));

        Assert.Equal(EditTargetSource.None, h.Targets.Resolve().Source);

        h.Config.AppConfig.UseFastChartingInput = true;

        Assert.Equal(EditTargetSource.Pen, h.Targets.Resolve().Source);
    }

    // === The pen-position provider ===========================================

    [Fact]
    public void WithNoProviderInstalledThereIsNoPenRung()
    {
        var h = new Harness();
        h.AddNote(2, 1, 3);

        Assert.Equal(EditTargetSource.None, h.Targets.Resolve().Source);
    }

    // === Promotion ===========================================================

    [Fact]
    public void AnImpliedPenTargetIsPromotedToARealSelection()
    {
        // EOF's implied selection made DRAWABLE — the user must be able to see
        // what the next keystroke is about to rewrite.
        var h = new Harness();
        var note = h.AddNote(2, 1, 3);
        h.Pen(new FractionalPosition(2));

        h.Targets.ResolveAndPromote();

        Assert.Same(note, Assert.Single(h.Selection.SelectedNotes));
    }

    [Fact]
    public void PromotingAHandShapeTargetKeepsTheShapeSelected()
    {
        // Losing the bar you deliberately clicked would be a surprise, and
        // keeping it makes promotion idempotent: the next Resolve() takes the
        // SELECTION rung and returns exactly this set.
        var h = new Harness();
        var note = h.AddNote(2, 1, 3);
        var shape = h.AddHandShape(0, 8);
        h.Selection.SelectedHandShapes.Add(shape);

        h.Targets.ResolveAndPromote();

        Assert.Same(note, Assert.Single(h.Selection.SelectedNotes));
        Assert.Same(shape, Assert.Single(h.Selection.SelectedHandShapes));
        Assert.Equal(EditTargetSource.Selection, h.Targets.Resolve().Source);
    }

    [Fact]
    public void PromotionIsANoOpWhenNothingResolved()
    {
        var h = new Harness();
        h.Pen(new FractionalPosition(2));

        var target = h.Targets.ResolveAndPromote();

        Assert.True(target.IsNone);
        Assert.Empty(h.Selection.SelectedNotes);
        Assert.Empty(h.Selection.SelectedChords);
    }

    [Fact]
    public void AnExplicitPenPositionBeatsTheInstalledProvider()
    {
        // The pen is handed a grid divisor per keystroke and snaps its own
        // position; it passes that straight in rather than round-tripping
        // through a global the composition root has to have wired.
        var h = new Harness();
        var atTwo = h.AddNote(2, 1, 3);
        var atFour = h.AddNote(4, 1, 3);
        h.Pen(new FractionalPosition(2));

        var target = h.Targets.ResolveAndPromoteAt(new FractionalPosition(4));

        Assert.Same(atFour, Assert.Single(target.Notes));
        Assert.NotSame(atTwo, target.Notes[0]);
    }

    // === Every keyboard verb now reaches the resolved sound ==================

    [Fact]
    public void ATechniqueKeybindReachesTheSoundUnderThePlayhead()
    {
        var h = new Harness();
        var note = h.AddNote(2, 1, 3);
        h.Pen(new FractionalPosition(2));
        Assert.Empty(h.Selection.SelectedNotes);

        h.Highway.ToggleSelectedHopo();

        Assert.True(note.Hopo != HopoType.None);
        Assert.Single(h.Selection.SelectedNotes);
    }

    [Fact]
    public void AFretKeybindReachesTheSoundUnderThePlayhead()
    {
        var h = new Harness();
        var note = h.AddNote(2, 1, 3);
        h.Pen(new FractionalPosition(2));

        h.Highway.MoveSelectedFretUp();

        Assert.Equal(4, note.Fret);
    }

    [Fact]
    public void ASustainKeybindReachesTheSoundUnderThePlayhead()
    {
        var h = new Harness();
        var note = h.AddNote(2, 1, 3);
        h.Pen(new FractionalPosition(2));

        Assert.True(h.Highway.AdjustSelectedNotesSustain(1, 8000, 4, h.State.Beats));
        Assert.True(note.Sustain > FractionalPosition.Zero);
    }

    [Fact]
    public void AKeyboardVerbReachesTheSoundsUnderASelectedHandShape()
    {
        // Selecting a hand-shape bar and pressing a technique key used to do
        // NOTHING AT ALL — the verbs read SelectedNotes/SelectedChords only.
        var h = new Harness();
        var inside = h.AddNote(2, 1, 3);
        var outside = h.AddNote(9, 1, 3);
        h.Selection.SelectedHandShapes.Add(h.AddHandShape(0, 8));

        h.Highway.MoveSelectedFretUp();

        Assert.Equal(4, inside.Fret);
        Assert.Equal(3, outside.Fret);
    }

    [Fact]
    public void AVerbWithNothingResolvedRecordsNoHistoryNode()
    {
        var h = new Harness();
        h.AddNote(2, 1, 3);
        h.Pen(new FractionalPosition(6)); // empty space

        h.Highway.MoveSelectedFretUp();

        Assert.False(h.History.CanUndo);
    }

    private sealed class Harness
    {
        public Harness(bool fastInput = true)
        {
            Config.AppConfig.UseFastChartingInput = fastInput;

            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var scope = new EditingScope(State);
            Targets = new EditTargetService(Selection, scope, Config);
            Highway = new HighwayEditingService(State, Selection, History, new StrictCollisionService(),
                Config, scope, Targets, logger);

            var project = new ChartProject();
            for (var i = 0; i < 16; i++)
                project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
            Arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            Arrangement.Levels.Add(new ChartLevel());
            project.Arrangements.Add(Arrangement);

            State.ActiveProject = project;
            State.ActiveArrangement = Arrangement;
            State.ActiveLevel = Arrangement.Levels[0];
            State.TotalAudioLengthMs = 8000;
        }

        public FakeConfigurationService Config { get; } = new();
        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public HistoryService History { get; }
        public EditTargetService Targets { get; }
        public HighwayEditingService Highway { get; }
        public Arrangement Arrangement { get; }

        public ObservableCollection<GuitarNote> Notes => State.PlacedNotes;

        /// <summary>Installs the pen-position provider the composition root
        /// installs, at a fixed position.</summary>
        public void Pen(FractionalPosition position) => Targets.SetPenPositionProvider(() => position);

        public GuitarNote AddNote(int beat, int stringIdx, int fret)
        {
            var n = new GuitarNote
            {
                Position = new FractionalPosition(beat), StringIndex = stringIdx, Fret = fret
            };
            Notes.Add(n);
            return n;
        }

        public HandShape AddHandShape(int startBeat, int endBeat)
        {
            var hs = new HandShape
            {
                Position = new FractionalPosition(startBeat),
                EndPosition = new FractionalPosition(endBeat),
                TemplateId = -1
            };
            State.HandShapes.Add(hs);
            return hs;
        }
    }
}
