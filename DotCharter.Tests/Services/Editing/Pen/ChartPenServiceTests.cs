using System.Collections.ObjectModel;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Pen;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;

namespace DotCharter.Tests.Services.Editing.Pen;

/// <summary>
///     The pen driven against a real chart: the verbs, the implied target, the
///     undo granularity and the chord-template contract.
/// </summary>
public sealed class ChartPenServiceTests
{
    private const int Divisor = 4;

    // === The setting ==========================================================

    [Fact]
    public void EveryPenVerbIsInertWhileTheSettingIsOff()
    {
        var h = new Harness(penEnabled: false);
        h.SeekToBeat(2);

        h.Pen.ToggleString(1, Divisor);

        Assert.Empty(h.Notes);
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void WithThePenOffADigitIsStillThePreV035SingleDigitSetFret()
    {
        var h = new Harness(penEnabled: false);
        var note = h.AddNote(2, 0, 3);
        h.Selection.SelectedNotes.Add(note);

        h.Pen.TypeFretDigit(1, Divisor);
        h.Pen.TypeFretDigit(2, Divisor);

        // No accumulator: the second digit is fret 2, not fret 12.
        Assert.Equal(2, note.Fret);
    }

    // === 1..9 — the note-entry verb ==========================================

    [Fact]
    public void AStringKeyOnEmptySpaceCreatesANoteAtThePlayhead()
    {
        var h = new Harness();
        h.SeekToBeat(2);
        h.State.PreviewNoteFret = 7;

        h.Pen.ToggleString(1, Divisor);

        var note = Assert.Single(h.Notes);
        Assert.Equal(new FractionalPosition(2), note.Position);
        Assert.Equal(1, note.StringIndex);
        Assert.Equal(7, note.Fret);
    }

    [Fact]
    public void ASecondStringKeyAtTheSamePositionBuildsAChord()
    {
        var h = new Harness();
        h.SeekToBeat(2);

        h.Pen.ToggleString(1, Divisor);
        h.Pen.ToggleString(3, Divisor);

        Assert.Empty(h.Notes);
        var chord = Assert.Single(h.Chords);
        Assert.Equal(new[] { 1, 3 }, chord.ChordNotes.Select(n => n.StringIndex).OrderBy(i => i));
    }

    [Fact]
    public void PressingTheSameStringAgainRemovesItAndDeletesTheLastSound()
    {
        var h = new Harness();
        h.SeekToBeat(2);

        h.Pen.ToggleString(1, Divisor);
        Assert.Single(h.Notes);

        h.Pen.ToggleString(1, Divisor);
        Assert.Empty(h.Notes);
    }

    [Fact]
    public void AStringKeyPointsTheEditMaskAtTheStringItJustPlayed()
    {
        var h = new Harness();
        h.SeekToBeat(2);

        h.Pen.ToggleString(4, Divisor);

        Assert.True(h.State.StringEditMask.IsExplicit(4));
        Assert.Equal(1, h.State.StringEditMask.Count);
    }

    [Fact]
    public void RemovingAStringLeavesTheMaskAlone()
    {
        // Aiming the mask at a string the same keystroke just removed would
        // point every following per-string verb at nothing.
        var h = new Harness();
        h.SeekToBeat(2);
        h.Pen.ToggleString(4, Divisor);
        h.Pen.ToggleString(2, Divisor);

        h.Pen.ToggleString(2, Divisor);

        Assert.True(h.State.StringEditMask.IsExplicit(2));
    }

    [Fact]
    public void TheJustPlacedSoundIsSelectedSoTheTargetIsVisible()
    {
        var h = new Harness();
        h.SeekToBeat(2);

        h.Pen.ToggleString(1, Divisor);

        Assert.Single(h.Selection.SelectedNotes);
    }

    [Fact]
    public void EachStringToggleIsItsOwnUndoStep()
    {
        var h = new Harness();
        h.SeekToBeat(2);

        h.Pen.ToggleString(1, Divisor);
        h.Pen.ToggleString(3, Divisor);

        h.History.Undo();
        Assert.Single(h.Notes);
        Assert.Empty(h.Chords);

        h.History.Undo();
        Assert.Empty(h.Notes);
    }

    [Fact]
    public void ThePenSnapsOntoAnOffGridSoundRatherThanTheNearestGridLine()
    {
        // Seek-to-next-sound parks the playhead ON an imported tuplet; a plain
        // grid snap would start a SECOND sound a few ms away instead of adding
        // a string to the one that is there.
        var h = new Harness();
        var offGrid = new FractionalPosition(2, new Fraction(1, 3));
        h.AddNoteAt(offGrid, 0, 5);
        h.State.CurrentTimeMs = offGrid.GetPositionMs(h.State.Beats);

        h.Pen.ToggleString(1, Divisor);

        Assert.Empty(h.Notes);
        var chord = Assert.Single(h.Chords);
        Assert.Equal(offGrid, chord.Position);
    }

    // === Typed frets ==========================================================

    [Fact]
    public void TwoDigitsInsideTheWindowTypeATwoDigitFret()
    {
        var h = new Harness();
        h.SeekToBeat(2);
        h.Pen.ToggleString(1, Divisor);

        h.Pen.TypeFretDigit(1, Divisor);
        h.Pen.TypeFretDigit(2, Divisor);

        Assert.Equal(12, Assert.Single(h.Notes).Fret);
    }

    [Fact]
    public void AWholeTypingRunIsOneUndoStep()
    {
        var h = new Harness();
        var note = h.AddNote(2, 1, 3);
        h.Selection.SelectedNotes.Add(note);

        h.Pen.TypeFretDigit(1, Divisor);
        h.Pen.TypeFretDigit(2, Divisor);
        Assert.Equal(12, note.Fret);

        h.History.Undo();

        // Straight back to 3 — never to the intermediate "fret 1".
        Assert.Equal(3, note.Fret);
    }

    [Fact]
    public void TypingAFretAlsoArmsTheFretTheNextCreatedNoteInherits()
    {
        // The ambient placement fret used to be mouse-wheel-only, so there was
        // no keyboard route to frets 10..24 at all.
        var h = new Harness();
        h.SeekToBeat(2);
        h.Pen.ToggleString(1, Divisor);
        h.Pen.TypeFretDigit(1, Divisor);
        h.Pen.TypeFretDigit(7, Divisor);

        Assert.Equal(17, h.State.PreviewNoteFret);

        h.SeekToBeat(4);
        h.Pen.ToggleString(1, Divisor);

        Assert.Equal(17, h.Notes.Single(n => n.Position == new FractionalPosition(4)).Fret);
    }

    [Fact]
    public void TypingAFretWithNothingUnderThePenArmsItForTheNextStringKey()
    {
        // The other half of the loop: type the fret first, then place. The
        // accumulator still runs, so "1","2" arms 12 rather than 2.
        var h = new Harness();
        h.SeekToBeat(2);

        h.Pen.TypeFretDigit(1, Divisor);
        h.Pen.TypeFretDigit(2, Divisor);
        Assert.Empty(h.Notes);
        Assert.False(h.History.CanUndo);

        h.Pen.ToggleString(3, Divisor);

        Assert.Equal(12, Assert.Single(h.Notes).Fret);
    }

    [Fact]
    public void APenCreatedNoteNeverLandsBehindTheCapo()
    {
        // Same loop, on a capo'd chart. The armed fret is ProjectState's
        // PreviewNoteFret, which snaps to the frets the neck HAS ({0} ∪ [capo,
        // 24]) — otherwise typing 3 under a capo at 5 CREATED an L01
        // (NoteFretBelowCapo), the malformed sound the app's own validator flags
        // and FixCapoFrets silently raises on the next auto-fix.
        var h = new Harness();
        h.Arrangement.Capo = 5;
        h.SeekToBeat(2);

        h.Pen.TypeFretDigit(3, Divisor);
        h.Pen.ToggleString(3, Divisor);

        Assert.Equal(5, Assert.Single(h.Notes).Fret);
    }

    [Fact]
    public void APenCreatedNoteCanStillBeAnOpenString()
    {
        var h = new Harness();
        h.Arrangement.Capo = 5;
        h.SeekToBeat(2);

        h.Pen.TypeFretDigit(0, Divisor);
        h.Pen.ToggleString(3, Divisor);

        Assert.Equal(0, Assert.Single(h.Notes).Fret);
    }

    [Fact]
    public void WithNothingSelectedTheDigitsTargetTheSoundAtThePlayhead()
    {
        // EOF's implied selection (menu/note.c:11065-11112) — no select-then-act
        // round trip.
        var h = new Harness();
        var note = h.AddNote(2, 1, 3);
        h.SeekToBeat(2);
        Assert.Empty(h.Selection.SelectedNotes);

        h.Pen.TypeFretDigit(9, Divisor);

        Assert.Equal(9, note.Fret);
        Assert.Single(h.Selection.SelectedNotes);
    }

    [Fact]
    public void AMaskedDigitOnAChordWritesOnlyTheMaskedString()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 5), (1, 7), (2, 7));
        var chord = h.AddChord(2, 0, (0, 5), (1, 7), (2, 7));
        h.Selection.SelectedChords.Add(chord);
        h.State.StringEditMask = StringEditMask.FromStrings(2);

        h.Pen.TypeFretDigit(9, Divisor);

        Assert.Equal(new[] { 5, 7, 9 }, chord.ChordNotes.OrderBy(n => n.StringIndex).Select(n => n.Fret));
    }

    [Fact]
    public void AnUnmaskedDigitTransposesTheWholeShape()
    {
        // rscharter GuitarSoundsHandler:379-400 — the typed number becomes the
        // shape's lowest fret and the barre moves with it.
        var h = new Harness();
        h.AddTemplate(0, (0, 5), (1, 7), (2, 7));
        var chord = h.AddChord(2, 0, (0, 5), (1, 7), (2, 7));
        h.Selection.SelectedChords.Add(chord);

        h.Pen.TypeFretDigit(8, Divisor);

        Assert.Equal(new[] { 8, 10, 10 }, chord.ChordNotes.OrderBy(n => n.StringIndex).Select(n => n.Fret));
    }

    [Fact]
    public void ATransposeThatWouldRunOffTheNeckIsRefusedNotSquashed()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 5), (1, 22));
        var chord = h.AddChord(2, 0, (0, 5), (1, 22));
        h.Selection.SelectedChords.Add(chord);

        h.Pen.TypeFretDigit(9, Divisor);

        Assert.Equal(new[] { 5, 22 }, chord.ChordNotes.OrderBy(n => n.StringIndex).Select(n => n.Fret));
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void ATypedFretOnAChordRepointsItsTemplate()
    {
        // An rscp <chordNote> has no fret attribute — an unrepointed fret change
        // is discarded on the next save.
        var h = new Harness();
        var shared = h.AddTemplate(0, (0, 5), (1, 7));
        var chord = h.AddChord(2, 0, (0, 5), (1, 7));
        h.Selection.SelectedChords.Add(chord);

        h.Pen.TypeFretDigit(9, Divisor);

        var resolved = h.Arrangement.ChordTemplates.Single(t => t.Id == chord.TemplateId);
        Assert.Equal(ChordTemplateResolver.FretsOf(chord), resolved.Frets);
        Assert.Equal(new Dictionary<int, int> { [0] = 5, [1] = 7 }, shared.Frets);
    }

    [Fact]
    public void ADigitAimedAtADifferentSoundStartsANewRun()
    {
        // Seeking between two digits must not undo the fret written on the first
        // sound and land the extended value on the second.
        var h = new Harness();
        var first = h.AddNote(2, 1, 3);
        var second = h.AddNote(4, 1, 3);
        h.SeekToBeat(2);
        h.Pen.TypeFretDigit(1, Divisor);

        h.Selection.ClearSelections();
        h.SeekToBeat(4);
        h.Pen.TypeFretDigit(2, Divisor);

        Assert.Equal(1, first.Fret);
        Assert.Equal(2, second.Fret);
    }

    // === Esc ==================================================================

    [Fact]
    public void EscapeRevertsTheInProgressEntry()
    {
        var h = new Harness();
        var note = h.AddNote(2, 1, 3);
        h.Selection.SelectedNotes.Add(note);
        h.Pen.TypeFretDigit(1, Divisor);
        h.Pen.TypeFretDigit(2, Divisor);
        Assert.Equal(12, note.Fret);

        Assert.True(h.Pen.RevertPendingEntry());

        Assert.Equal(3, note.Fret);
    }

    [Fact]
    public void EscapeWithNoEntryInProgressClaimsNothing()
    {
        var h = new Harness();
        h.AddNote(2, 1, 3);

        Assert.False(h.Pen.RevertPendingEntry());
    }

    [Fact]
    public void EscapeAfterAStringToggleDoesNotUndoTheNote()
    {
        // Only an in-progress FRET entry is revertable — a placed note is a
        // committed structural act, undone with Ctrl+Z like everything else.
        var h = new Harness();
        h.SeekToBeat(2);
        h.Pen.ToggleString(1, Divisor);

        Assert.False(h.Pen.RevertPendingEntry());
        Assert.Single(h.Notes);
    }

    private sealed class Harness
    {
        private readonly FakeConfigurationService _config = new();

        public Harness(bool penEnabled = true)
        {
            _config.AppConfig.UseFastChartingInput = penEnabled;

            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var collisions = new StrictCollisionService();
            var scope = new EditingScope(State);
            Targets = new EditTargetService(Selection, scope, _config);
            var highway = new HighwayEditingService(State, Selection, History, collisions, _config, scope,
                Targets, logger);
            Pen = new ChartPenService(State, scope, History, Selection, collisions, new TempoService(logger),
                highway, Targets, _config, logger);

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

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public HistoryService History { get; }
        public ChartPenService Pen { get; }
        public EditTargetService Targets { get; }
        public Arrangement Arrangement { get; }

        public ObservableCollection<GuitarNote> Notes => State.PlacedNotes;
        public ObservableCollection<Chord> Chords => State.PlacedChords;

        public void SeekToBeat(int beat) => State.CurrentTimeMs = new FractionalPosition(beat).GetPositionMs(State.Beats);

        public GuitarNote AddNote(int beat, int stringIdx, int fret) => AddNoteAt(new FractionalPosition(beat), stringIdx, fret);

        public GuitarNote AddNoteAt(FractionalPosition position, int stringIdx, int fret)
        {
            var n = new GuitarNote { Position = position, StringIndex = stringIdx, Fret = fret };
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
    }
}
