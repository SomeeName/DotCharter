using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;

namespace DotCharter.Tests.Services.Editing.Highway;

/// <summary>
///     Every keyboard technique / fret / string verb used to be a silent no-op on
///     a CHORD: <c>SelectedNoteMutationCommands</c> was
///     <c>ObservableCollection&lt;GuitarNote&gt;</c> end to end, so
///     <c>HighwayEditingService</c> passed only <c>SelectedNotes</c> and the
///     inspector was the only route to a chord's technique state (editing-rework
///     audit 03 §0(c), §6.4 — "the single highest value-per-line change").
///     <para>
///         Widening it is not just "pass the chords too". A chord is not a note:
///         each verb had to be assigned a meaning (per-string via the string
///         mask, whole-chord, or whole-shape), and any verb that moves a fret or
///         a string is a SHAPE change that must repoint <c>TemplateId</c> through
///         <c>ChordTemplateResolver</c> — the rscp <c>&lt;chordNote&gt;</c>
///         carries no <c>fret</c> attribute, so a fret written without repointing
///         is discarded on the next save (format audit 02 §6.5).
///     </para>
/// </summary>
public sealed class ChordKeyboardMutationTests
{
    // === Per-string verbs =====================================================

    [Fact]
    public void ToggleHopo_OnASelectedChord_CyclesEveryStringsHopo()
    {
        var h = new Harness();
        var chord = h.AddChord(2, templateId: -1, (0, 3), (1, 5), (2, 5));
        h.Selection.SelectedChords.Add(chord);

        h.Service.ToggleSelectedHopo();

        Assert.All(chord.ChordNotes, n => Assert.Equal(HopoType.HammerOn, n.Hopo));
    }

    [Fact]
    public void ToggleMute_OnASelectedChord_CyclesEveryStringsMute()
    {
        var h = new Harness();
        var chord = h.AddChord(2, templateId: -1, (0, 3), (1, 5));
        h.Selection.SelectedChords.Add(chord);

        h.Service.ToggleSelectedMute();

        Assert.All(chord.ChordNotes, n => Assert.Equal(MuteType.Full, n.Mute));
    }

    [Fact]
    public void ToggleVibrato_OnASelectedChord_ClearsTheStrengthWhenTurnedOff()
    {
        var h = new Harness();
        var chord = h.AddChord(2, templateId: -1, (0, 3), (1, 5));
        foreach (var n in chord.ChordNotes)
        {
            n.Vibrato = true;
            n.VibratoStrength = 120;
        }

        h.Selection.SelectedChords.Add(chord);

        h.Service.ToggleSelectedVibrato();

        Assert.All(chord.ChordNotes, n =>
        {
            Assert.False(n.Vibrato);
            Assert.Equal(0, n.VibratoStrength);
        });
    }

    [Fact]
    public void ChordAndNoteInOneSelection_AreOneUndoStep()
    {
        var h = new Harness();
        var note = h.AddNote(1, 0, 7);
        var chord = h.AddChord(2, templateId: -1, (0, 3), (1, 5));
        h.Selection.SelectedNotes.Add(note);
        h.Selection.SelectedChords.Add(chord);

        h.Service.ToggleSelectedTremolo();

        Assert.True(note.Tremolo);
        Assert.All(chord.ChordNotes, n => Assert.True(n.Tremolo));

        h.History.Undo();

        Assert.False(note.Tremolo);
        Assert.All(chord.ChordNotes, n => Assert.False(n.Tremolo));
    }

    // === Whole-chord verbs ====================================================

    [Fact]
    public void ToggleIgnore_OnAChord_WritesTheCHORD_LEVELFlag()
    {
        // Chord.Ignore is what RS-XML/SNG read and what the inspector's "Whole
        // chord" row writes; ChordNote.Ignore is rscp-only and has no visible
        // control, so writing it from a keybind would create unreachable state.
        var h = new Harness();
        var chord = h.AddChord(2, templateId: -1, (0, 3), (1, 5));
        h.Selection.SelectedChords.Add(chord);

        h.Service.ToggleSelectedIgnore();

        Assert.True(chord.Ignore);
        Assert.All(chord.ChordNotes, n => Assert.False(n.Ignore));
    }

    [Fact]
    public void ToggleAccent_OnAChord_WritesTheCHORD_LEVELFlag()
    {
        var h = new Harness();
        var chord = h.AddChord(2, templateId: -1, (0, 3), (1, 5));
        h.Selection.SelectedChords.Add(chord);

        h.Service.ToggleSelectedAccent();

        Assert.True(chord.Accent);
        Assert.All(chord.ChordNotes, n => Assert.False(n.Accent));
    }

    [Fact]
    public void TogglePassOtherNotes_OnAChord_WritesTheCHORD_LEVELFlag()
    {
        // ChordNote has no PassOtherNotes at all — only Chord does.
        var h = new Harness();
        var chord = h.AddChord(2, templateId: -1, (0, 3), (1, 5));
        h.Selection.SelectedChords.Add(chord);

        h.Service.ToggleSelectedPassOtherNotes();

        Assert.True(chord.PassOtherNotes);
    }

    [Fact]
    public void GroupToggle_TakesItsValueFromTheFirstSelectedSound_AndAppliesItToTheChordToo()
    {
        var h = new Harness();
        var note = h.AddNote(1, 0, 7);
        note.Ignore = true;
        var chord = h.AddChord(2, templateId: -1, (0, 3), (1, 5));
        h.Selection.SelectedNotes.Add(note);
        h.Selection.SelectedChords.Add(chord);

        h.Service.ToggleSelectedIgnoreGroup();

        // First sound was Ignore=true ⇒ everything goes to false.
        Assert.False(note.Ignore);
        Assert.False(chord.Ignore);
    }

    // === Shape verbs: the ChordTemplate trap ==================================

    [Fact]
    public void FretUp_OnAChord_RepointsTemplateInsteadOfDivergingFromTheSharedOne()
    {
        var h = new Harness();
        var shared = h.AddTemplate(0, (0, 3), (1, 5));
        var chord = h.AddChord(2, 0, (0, 3), (1, 5));
        var sibling = h.AddChord(6, 0, (0, 3), (1, 5));
        h.Selection.SelectedChords.Add(chord);

        h.Service.MoveSelectedFretUp();

        Assert.Equal(new[] { 4, 6 }, chord.ChordNotes.Select(n => n.Fret));
        // The chord now points at a template describing what it plays…
        var resolved = h.Arrangement.ChordTemplates.Single(t => t.Id == chord.TemplateId);
        Assert.Equal(ChordTemplateResolver.FretsOf(chord), resolved.Frets);
        Assert.NotEqual(shared.Id, chord.TemplateId);
        // …and the shared template is untouched, so the sibling still means what it did.
        Assert.Equal(3, shared.Frets[0]);
        Assert.Equal(5, shared.Frets[1]);
        Assert.Equal(0, sibling.TemplateId);
        Assert.Equal(new[] { 3, 5 }, sibling.ChordNotes.Select(n => n.Fret));
    }

    [Fact]
    public void FretUp_OnAChord_ReusesAnExistingTemplateWithTheSameShape()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 3), (1, 5));
        var destination = h.AddTemplate(1, (0, 4), (1, 6));
        var chord = h.AddChord(2, 0, (0, 3), (1, 5));
        h.KeepInUse(0);
        h.Selection.SelectedChords.Add(chord);

        h.Service.MoveSelectedFretUp();

        Assert.Equal(destination.Id, chord.TemplateId);
        Assert.Equal(2, h.Arrangement.ChordTemplates.Count);
    }

    [Fact]
    public void FretUp_OnAChord_IsOneUndoStepThatRestoresTheTemplateIdToo()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 3), (1, 5));
        var chord = h.AddChord(2, 0, (0, 3), (1, 5));
        h.KeepInUse(0);
        h.Selection.SelectedChords.Add(chord);

        h.Service.MoveSelectedFretUp();
        Assert.NotEqual(0, chord.TemplateId);

        h.History.Undo();

        Assert.Equal(0, chord.TemplateId);
        Assert.Equal(new[] { 3, 5 }, chord.ChordNotes.Select(n => n.Fret));
        // The mint is part of the same node, so it comes back out.
        Assert.Single(h.Arrangement.ChordTemplates);
    }

    [Fact]
    public void FretUp_OnAnUNTEMPLATEDChord_LeavesTheSentinelAlone()
    {
        // TemplateId -1 has nothing to disagree with; minting for it would
        // bind a freshly imported strum before the user asked.
        var h = new Harness();
        var chord = h.AddChord(2, templateId: -1, (0, 3), (1, 5));
        h.Selection.SelectedChords.Add(chord);

        h.Service.MoveSelectedFretUp();

        Assert.Equal(-1, chord.TemplateId);
        Assert.Empty(h.Arrangement.ChordTemplates);
    }

    [Fact]
    public void FretDown_OnAChord_ClampsAtZeroPerString()
    {
        var h = new Harness();
        var chord = h.AddChord(2, templateId: -1, (0, 0), (1, 2));
        h.Selection.SelectedChords.Add(chord);

        h.Service.MoveSelectedFretDown();

        Assert.Equal(new[] { 0, 1 }, chord.ChordNotes.Select(n => n.Fret));
    }

    [Fact]
    public void SetFret_OnAChord_WithNoMask_SetsEveryString()
    {
        var h = new Harness();
        var chord = h.AddChord(2, templateId: -1, (0, 3), (1, 5));
        h.Selection.SelectedChords.Add(chord);

        h.Service.SetSelectedFret(7);

        Assert.All(chord.ChordNotes, n => Assert.Equal(7, n.Fret));
    }

    [Fact]
    public void StringUp_OnAChord_MovesTheWholeShape()
    {
        var h = new Harness();
        var chord = h.AddChord(2, templateId: -1, (0, 3), (1, 5));
        h.Selection.SelectedChords.Add(chord);

        h.Service.MoveSelectedStringUp(6);

        Assert.Equal(new[] { 1, 2 }, chord.ChordNotes.Select(n => n.StringIndex));
    }

    [Fact]
    public void StringUp_OnAChordAtTheNeckEdge_MovesNothing()
    {
        // Clamping per string (what the note path does) would stack two chord
        // notes on lane 5 — a state the SNG per-string arrays cannot express.
        var h = new Harness();
        var chord = h.AddChord(2, templateId: -1, (4, 3), (5, 5));
        h.Selection.SelectedChords.Add(chord);

        h.Service.MoveSelectedStringUp(6);

        Assert.Equal(new[] { 4, 5 }, chord.ChordNotes.Select(n => n.StringIndex));
    }

    [Fact]
    public void StringDown_OnAChordAtTheNeckEdge_MovesNothing()
    {
        var h = new Harness();
        var chord = h.AddChord(2, templateId: -1, (0, 3), (1, 5));
        h.Selection.SelectedChords.Add(chord);

        h.Service.MoveSelectedStringDown();

        Assert.Equal(new[] { 0, 1 }, chord.ChordNotes.Select(n => n.StringIndex));
    }

    [Fact]
    public void StringUp_OnAChord_AlsoRepointsTheTemplate()
    {
        var h = new Harness();
        var shared = h.AddTemplate(0, (0, 3), (1, 5));
        var chord = h.AddChord(2, 0, (0, 3), (1, 5));
        h.Selection.SelectedChords.Add(chord);

        h.Service.MoveSelectedStringUp(6);

        var resolved = h.Arrangement.ChordTemplates.Single(t => t.Id == chord.TemplateId);
        Assert.Equal(ChordTemplateResolver.FretsOf(chord), resolved.Frets);
        Assert.Equal(2, shared.Frets.Count);
        Assert.True(shared.Frets.ContainsKey(0));
    }

    // === Nothing selected =====================================================

    [Fact]
    public void NothingSelected_RecordsNoHistoryNode()
    {
        var h = new Harness();
        h.AddChord(2, templateId: -1, (0, 3), (1, 5));

        h.Service.ToggleSelectedHopo();

        Assert.False(h.History.CanUndo);
    }

    // === Ignore provenance ====================================================
    // AUTHORED WINS (§6.5). These pair with Editing/IgnoreProvenanceTests, which
    // owns the enforcer half; this file owns the half that was missing — that the
    // HIGHWAY toggle authors, and so must drop the derived mark the way the
    // inspector chip does (InspectorViewModel.OnNoteIgnoreChanged).

    [Fact]
    public void ToggleIgnore_OnANote_ClearsTheDerivedMark()
    {
        var h = new Harness();
        var note = h.AddNote(2, 0, 5);
        note.Ignore = true;
        note.IgnoreDerived = true; // the enforcer had flagged it
        h.Selection.SelectedNotes.Add(note);

        h.Service.ToggleSelectedIgnore();

        Assert.False(note.Ignore);
        Assert.False(note.IgnoreDerived);
    }

    [Fact]
    public void ToggleIgnore_OnAChord_ClearsTheDerivedMark()
    {
        var h = new Harness();
        var chord = h.AddChord(2, templateId: -1, (0, 3), (1, 5));
        chord.Ignore = true;
        chord.IgnoreDerived = true;
        h.Selection.SelectedChords.Add(chord);

        h.Service.ToggleSelectedIgnore();

        Assert.False(chord.Ignore);
        Assert.False(chord.IgnoreDerived);
    }

    [Fact]
    public void ToggleIgnoreGroup_ClearsTheDerivedMarkToo()
    {
        var h = new Harness();
        var first = h.AddNote(2, 0, 5);
        var second = h.AddNote(3, 1, 7);
        second.IgnoreDerived = true;
        h.Selection.SelectedNotes.Add(first);
        h.Selection.SelectedNotes.Add(second);

        h.Service.ToggleSelectedIgnoreGroup();

        Assert.True(first.Ignore);
        Assert.True(second.Ignore);
        Assert.False(second.IgnoreDerived);
    }

    /// <summary>
    ///     The whole point, end to end — and the reason this was invisible.
    ///     <para>
    ///         The keypress and the loss are separated by an unrelated edit. A
    ///         note the enforcer flagged (fret 24) carries <c>IgnoreDerived</c>.
    ///         The charter authors <c>Ignore</c> on it from the highway. Later the
    ///         note is dragged back into range, so the structural reason is gone —
    ///         and the enforcer retracts "only what this pass is responsible for".
    ///         While the derived mark still stood, the charter's flag WAS that.
    ///     </para>
    /// </summary>
    [Fact]
    public void AnAuthoredIgnore_SurvivesTheStructuralReasonGoingAway()
    {
        var h = new Harness();
        var note = h.AddNote(2, 0, 24);
        // Same instance in the level the enforcer walks.
        h.Arrangement.Levels[0].Notes.Add(note);

        var beats = new List<BeatMarker>();
        for (var i = 0; i < 16; i++) beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
        var config = new AppConfig
        {
            MinNoteDistanceMode = MinNoteDistanceMode.BeatFraction,
            MinNoteDistanceDivisor = 64
        };

        // 1. The enforcer flags the unplayable fret as ITS OWN.
        ChartSpacingEnforcer.EnforceArrangement(h.Arrangement, beats, config);
        Assert.True(note.Ignore);
        Assert.True(note.IgnoreDerived);

        // 2. The charter authors the flag from the highway (off, then on).
        h.Selection.SelectedNotes.Add(note);
        h.Service.ToggleSelectedIgnore();
        h.Service.ToggleSelectedIgnore();
        Assert.True(note.Ignore);

        // 3. The note is dragged into range, so the enforcer's reason is gone.
        note.Fret = 5;
        ChartSpacingEnforcer.EnforceArrangement(h.Arrangement, beats, config);

        Assert.True(note.Ignore); // the charter's flag, not the enforcer's
    }

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
}
