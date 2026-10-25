using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;

namespace DotCharter.Tests.Services.Editing.Highway;

/// <summary>
///     The fret and string nudge keys had NO merge verb, so a held key spent one
///     of the 500 history nodes per auto-repeat — while the anchor nudge and the
///     sustain nudge, two verbs over the same concept, both coalesced. And
///     <c>Shift+Alt+Up</c> (octave shift) was a twelve-iteration loop in
///     <c>AppKeybindRegistrar</c>: one keystroke, twelve <c>history.Do</c> calls,
///     twelve <c>ChartSpacingEnforcer</c> passes and up to eleven intermediate
///     <c>ChordTemplate</c>s that nothing garbage-collects.
///     <para>
///         Everything here drives an INJECTED clock and asserts node COUNTS, never
///         elapsed time — the repo rule about wall-clock assertions in the default
///         gate.
///     </para>
/// </summary>
public sealed class FretStringGestureMergeTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(900);

    [Fact]
    public void AHeldFretRunCollapsesToOneUndoNode()
    {
        var h = new Harness();
        var note = h.AddNote(2, 2, 5);
        h.Selection.SelectedNotes.Add(note);

        for (var i = 0; i < 8; i++)
        {
            h.Service.MoveSelectedFretUp();
            h.Clock.Advance(TimeSpan.FromMilliseconds(50));
        }

        Assert.Equal(13, note.Fret);
        Assert.Single(h.History.GetUndoSteps());

        // ONE Ctrl+Z takes the whole run back — before the verb this needed eight.
        h.History.Undo();
        Assert.Equal(5, note.Fret);
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void AHeldStringRunCollapsesToOneUndoNode()
    {
        var h = new Harness();
        var note = h.AddNote(2, 0, 5);
        h.Selection.SelectedNotes.Add(note);

        for (var i = 0; i < 4; i++)
        {
            h.Service.MoveSelectedStringUp(6);
            h.Clock.Advance(TimeSpan.FromMilliseconds(50));
        }

        Assert.Equal(4, note.StringIndex);
        Assert.Single(h.History.GetUndoSteps());

        h.History.Undo();
        Assert.Equal(0, note.StringIndex);
    }

    /// <summary>
    ///     Rule (a) of the merge contract: fret and string are the SAME action
    ///     type over the SAME sound, so one shared verb would let a string move
    ///     fold into the preceding fret run and make one Ctrl+Z revert both. Same
    ///     reason <c>AnchorFret</c> and <c>AnchorWidth</c> are two verbs.
    /// </summary>
    [Fact]
    public void AFretRunAndAStringRunDoNotFoldTogether()
    {
        var h = new Harness();
        var note = h.AddNote(2, 1, 5);
        h.Selection.SelectedNotes.Add(note);

        h.Service.MoveSelectedFretUp();
        h.Service.MoveSelectedFretUp();
        // No clock advance at all: only the VERB may separate these.
        h.Service.MoveSelectedStringUp(6);

        Assert.Equal(7, note.Fret);
        Assert.Equal(2, note.StringIndex);
        Assert.Equal(2, h.History.GetUndoSteps().Count);

        // Undoing the string move leaves the fret run standing.
        h.History.Undo();
        Assert.Equal(1, note.StringIndex);
        Assert.Equal(7, note.Fret);
    }

    [Fact]
    public void APauseLongerThanTheWindowStartsANewFretNode()
    {
        var h = new Harness();
        var note = h.AddNote(2, 2, 5);
        h.Selection.SelectedNotes.Add(note);

        h.Service.MoveSelectedFretUp();
        h.Clock.Advance(TimeSpan.FromMilliseconds(100));
        h.Service.MoveSelectedFretUp();

        h.Clock.Advance(Window + TimeSpan.FromMilliseconds(1));
        h.Service.MoveSelectedFretUp();

        Assert.Equal(8, note.Fret);
        Assert.Equal(2, h.History.GetUndoSteps().Count);

        h.History.Undo();
        Assert.Equal(7, note.Fret);
    }

    // === The octave shift =====================================================

    [Fact]
    public void TheOctaveShiftIsOneUndoNode()
    {
        var h = new Harness();
        var note = h.AddNote(2, 2, 5);
        h.Selection.SelectedNotes.Add(note);

        h.Service.MoveSelectedFretByOctave(1);

        Assert.Equal(17, note.Fret);
        Assert.Single(h.History.GetUndoSteps());

        h.History.Undo();
        Assert.Equal(5, note.Fret);
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void TheOctaveShiftDownIsOneUndoNode()
    {
        var h = new Harness();
        var note = h.AddNote(2, 2, 20);
        h.Selection.SelectedNotes.Add(note);

        h.Service.MoveSelectedFretByOctave(-1);

        Assert.Equal(8, note.Fret);
        Assert.Single(h.History.GetUndoSteps());
    }

    /// <summary>
    ///     The single ±12 pass has to be byte-identical to the twelve ±1 steps it
    ///     replaces, INCLUDING at the clamps — that is what makes this a pure
    ///     granularity fix rather than a behaviour change. A shape running into
    ///     the ceiling still squashes, exactly as the fret keys always have.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(12)]
    [InlineData(15)]
    [InlineData(20)]
    [InlineData(24)]
    public void TheOctaveShiftLandsWhereTwelveSingleStepsWould(int startFret)
    {
        foreach (var direction in new[] { 1, -1 })
        {
            var stepwise = new Harness();
            var a = stepwise.AddNote(2, 2, startFret);
            stepwise.Selection.SelectedNotes.Add(a);
            for (var i = 0; i < 12; i++)
            {
                if (direction > 0) stepwise.Service.MoveSelectedFretUp();
                else stepwise.Service.MoveSelectedFretDown();
            }

            var oneShot = new Harness();
            var b = oneShot.AddNote(2, 2, startFret);
            oneShot.Selection.SelectedNotes.Add(b);
            oneShot.Service.MoveSelectedFretByOctave(direction);

            Assert.Equal(a.Fret, b.Fret);
        }
    }

    /// <summary>
    ///     The expensive half of the old loop: each of the twelve steps resolved a
    ///     chord template for an intermediate shape, and each intermediate shape
    ///     stays in the arrangement for the life of the file unless something
    ///     removes it. One pass mints exactly one.
    /// </summary>
    [Fact]
    public void TheOctaveShiftOnAChordMintsExactlyOneTemplate()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 3), (1, 5));
        var chord = h.AddChord(2, 0, (0, 3), (1, 5));
        h.KeepInUse(0);
        h.Selection.SelectedChords.Add(chord);

        h.Service.MoveSelectedFretByOctave(1);

        Assert.Equal(new[] { 15, 17 }, chord.ChordNotes.Select(n => n.Fret));
        Assert.Equal(2, h.Arrangement.ChordTemplates.Count);
        Assert.Single(h.History.GetUndoSteps());

        // …and the one node takes the mint back out with it.
        h.History.Undo();
        Assert.Equal(new[] { 3, 5 }, chord.ChordNotes.Select(n => n.Fret));
        Assert.Equal(0, chord.TemplateId);
        Assert.Single(h.Arrangement.ChordTemplates);
    }

    /// <summary>
    ///     What the loop actually cost, spelled out so the regression is visible:
    ///     twelve nodes for ONE keystroke. Each step takes the shape it leaves behind
    ///     out with it, so the arrangement never collects surplus templates.
    /// </summary>
    [Fact]
    public void TheOldTwelveStepLoopIsWhatTheOneShotReplaces()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 3), (1, 5));
        var chord = h.AddChord(2, 0, (0, 3), (1, 5));
        h.Selection.SelectedChords.Add(chord);

        // Exactly what AppKeybindRegistrar used to do.
        for (var i = 0; i < 12; i++) h.Service.MoveSelectedFretUp();

        Assert.Equal(new[] { 15, 17 }, chord.ChordNotes.Select(n => n.Fret));
        Assert.Equal(12, h.History.GetUndoSteps().Count);
        Assert.Single(h.Arrangement.ChordTemplates);
    }

    /// <summary>
    ///     A held fret run over an UNTEMPLATED chord still coalesces — nothing is
    ///     minted, so the composite that would block the merge is never built.
    /// </summary>
    [Fact]
    public void AHeldFretRunOnAnUntemplatedChordCoalesces()
    {
        var h = new Harness();
        var chord = h.AddChord(2, templateId: -1, (0, 3), (1, 5));
        h.Selection.SelectedChords.Add(chord);

        for (var i = 0; i < 5; i++)
        {
            h.Service.MoveSelectedFretUp();
            h.Clock.Advance(TimeSpan.FromMilliseconds(40));
        }

        Assert.Equal(new[] { 8, 10 }, chord.ChordNotes.Select(n => n.Fret));
        Assert.Single(h.History.GetUndoSteps());

        h.History.Undo();
        Assert.Equal(new[] { 3, 5 }, chord.ChordNotes.Select(n => n.Fret));
    }

    /// <summary>
    ///     Reference identity (rule (c)): the same verb over a DIFFERENT sound is a
    ///     different gesture, even inside the window.
    /// </summary>
    [Fact]
    public void AFretRunDoesNotFoldAcrossAChangeOfSelection()
    {
        var h = new Harness();
        var first = h.AddNote(2, 2, 5);
        var second = h.AddNote(6, 2, 5);

        h.Selection.SelectedNotes.Add(first);
        h.Service.MoveSelectedFretUp();

        h.Selection.SelectedNotes.Clear();
        h.Selection.SelectedNotes.Add(second);
        h.Service.MoveSelectedFretUp();

        Assert.Equal(2, h.History.GetUndoSteps().Count);
        Assert.Equal(6, first.Fret);
        Assert.Equal(6, second.Fret);
    }

    /// <summary>
    ///     The pen's typed fret deliberately carries NO merge verb — it owns its
    ///     own run granularity (a continuation digit undoes the run's node before
    ///     committing the new value), and a second coalescing mechanism on top of
    ///     that is the "two verbs over one concept" confusion the verb table exists
    ///     to prevent.
    /// </summary>
    [Fact]
    public void SettingAnAbsoluteFretIsNotAMergeableGesture()
    {
        var h = new Harness();
        var note = h.AddNote(2, 2, 5);
        h.Selection.SelectedNotes.Add(note);

        h.Service.SetSelectedFret(7);
        h.Service.SetSelectedFret(9);
        h.Service.SetSelectedFret(11);

        Assert.Equal(11, note.Fret);
        Assert.Equal(3, h.History.GetUndoSteps().Count);
    }

    internal sealed class Harness
    {
        public Harness()
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger, clock: Clock.Read, mergeWindow: Window);
            var collisions = new StrictCollisionService();
            var config = new FakeConfigurationService();
            Scope = new EditingScope(State);
            Service = new HighwayEditingService(State, Selection, History, collisions, config, Scope,
                new EditTargetService(Selection, Scope, config), logger);

            var project = new ChartProject();
            for (var i = 0; i < 32; i++)
                project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
            Arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            Arrangement.Levels.Add(new ChartLevel());
            project.Arrangements.Add(Arrangement);

            State.ActiveProject = project;
            State.ActiveArrangement = Arrangement;
            State.ActiveLevel = Arrangement.Levels[0];
        }

        public TestClock Clock { get; } = new();
        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public HistoryService History { get; }
        public EditingScope Scope { get; }
        public HighwayEditingService Service { get; }
        public Arrangement Arrangement { get; }

        public GuitarNote AddNote(int beat, int stringIdx, int fret)
        {
            var n = new GuitarNote
            {
                Position = new FractionalPosition(beat),
                StringIndex = stringIdx,
                Fret = fret
            };
            State.PlacedNotes.Add(n);
            return n;
        }

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

        /// <summary>A bar far from every edit that keeps <paramref name="templateId" /> in use, so the edit does not strand it.</summary>
        public void KeepInUse(int templateId) => Arrangement.Levels[0].HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(12),
            EndPosition = new FractionalPosition(13),
            TemplateId = templateId
        });
    }

    internal sealed class TestClock
    {
        private DateTime _nowUtc = new(2026, 7, 28, 15, 43, 0, DateTimeKind.Utc);
        public DateTime Read() => _nowUtc;
        public void Advance(TimeSpan by) => _nowUtc += by;
    }
}
