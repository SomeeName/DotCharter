using DotCharter.Services.Editing;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Services.Editing.Collisions;

namespace DotCharter.Tests.Services.Editing.ChordShapes;

/// <summary>
///     The pure chord-shape placement planner (#477): one shape at one position over nothing, a
///     note and a chord; the capo; the arpeggio flavour; "Repeat previous chord". Every plan is
///     committed through a real history so "one undo node" is measured, not assumed.
/// </summary>
public sealed class ChordShapePlacementTests
{
    private static readonly FractionalPosition At = new(20);

    private readonly ChordShapesFixture _f = new();
    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly ChordShapePlacementTarget _target;

    public ChordShapePlacementTests() =>
        _target = new ChordShapePlacementTarget(_f.Arrangement, _f.Level1.Notes, _f.Level1.Chords,
            new StrictCollisionService(), null);

    private Chord? ChordAt(FractionalPosition position) => _f.Level1.Chords.SingleOrDefault(c => c.Position == position);

    private ChordShapePlacementPlan Commit(ChordShapePlacementPlan plan)
    {
        Assert.False(plan.IsRefused, $"refused: {plan.Refusal}");
        Assert.True(_history.Do(plan.Action!));
        return plan;
    }

    private static Dictionary<int, int> Frets(Chord chord) => ChordTemplateResolver.FretsOf(chord);

    [Fact]
    public void OverNothing_CreatesTheWholeChord_PointingAtTheTemplate_InOneUndoNode()
    {
        var chordsBefore = _f.Level1.Chords.Count;

        var plan = Commit(ChordShapePlacement.Plan(_target, _f.C, [At]));

        var chord = ChordAt(At)!;
        Assert.Equal(_f.C.Id, chord.TemplateId);
        Assert.Equal(_f.C.Frets, Frets(chord));
        Assert.Equal(1, plan.Created);
        Assert.Single(_history.GetUndoSteps());
        Assert.Equal(ChordShapePlacement.PlaceLabelKey, ((CompositeAction)plan.Action!).LabelKey);

        _history.Undo();
        Assert.Null(ChordAt(At));
        Assert.Equal(chordsBefore, _f.Level1.Chords.Count);
    }

    [Fact]
    public void OverANote_PromotesItIntoTheChord_KeepingItsTechniques_AtTheShapesFret()
    {
        var note = new GuitarNote { Position = At, StringIndex = 2, Fret = 7, Vibrato = true, Sustain = new FractionalPosition(1) };
        var stray = new GuitarNote { Position = At, StringIndex = 0, Fret = 3 };
        _f.Level1.Notes.Add(note);
        _f.Level1.Notes.Add(stray);

        Commit(ChordShapePlacement.Plan(_target, _f.C, [At]));

        Assert.Empty(_f.Level1.Notes);
        var chord = ChordAt(At)!;
        Assert.Equal(_f.C.Frets, Frets(chord));
        Assert.Equal(_f.C.Id, chord.TemplateId);
        var carried = chord.ChordNotes.Single(n => n.StringIndex == 2);
        Assert.Equal(_f.C.Frets[2], carried.Fret);
        Assert.True(carried.Vibrato);
        // A string the shape adds rings as long as the sound it joined.
        Assert.Equal(new FractionalPosition(1), chord.ChordNotes.Single(n => n.StringIndex == 1).Sustain);
        Assert.Single(_history.GetUndoSteps());

        _history.Undo();
        Assert.Equal(2, _f.Level1.Notes.Count);
        Assert.Contains(note, _f.Level1.Notes);
        Assert.Contains(stray, _f.Level1.Notes);
        Assert.Null(ChordAt(At));
    }

    [Fact]
    public void OverAChord_ReshapesThatChordInPlace_DroppingStringsTheShapeDoesNotPlay()
    {
        // The open-string E5 at beat 14 plays strings 0 and 1; C plays 1..5.
        var chord = _f.Level1.Chords.Single(c => c.TemplateId == _f.Open.Id);
        chord.ChordNotes.Single(n => n.StringIndex == 1).Mute = MuteType.Palm;
        var before = chord.Clone();

        var plan = Commit(ChordShapePlacement.Plan(_target, _f.C, [chord.Position]));

        Assert.Equal(1, plan.Reshaped);
        Assert.Same(chord, ChordAt(chord.Position));
        Assert.Equal(_f.C.Id, chord.TemplateId);
        Assert.Equal(_f.C.Frets, Frets(chord));
        Assert.Equal(MuteType.Palm, chord.ChordNotes.Single(n => n.StringIndex == 1).Mute);
        Assert.Single(_history.GetUndoSteps());

        _history.Undo();
        Assert.Equal(before.TemplateId, chord.TemplateId);
        Assert.Equal(Frets(before), Frets(chord));
    }

    [Fact]
    public void OverTheSameShapeAlready_IsRefused_AndRecordsNothing()
    {
        var chord = _f.Level1.Chords.Single(c => c.TemplateId == _f.C.Id);

        var plan = ChordShapePlacement.Plan(_target, _f.C, [chord.Position]);

        Assert.Equal(ChordShapePlacementRefusal.SameShape, plan.Refusal);
        Assert.Null(plan.Action);
    }

    [Fact]
    public void ADuplicateTemplate_IsTheOneThePlacedChordPointsAt_NotTheFirstWithThoseFrets()
    {
        // Am and AmAlt share frets; a frets-only find-or-create would answer Am.
        Commit(ChordShapePlacement.Plan(_target, _f.AmAlt, [At]));

        Assert.Equal(_f.AmAlt.Id, ChordAt(At)!.TemplateId);
        Assert.Equal(6, _f.Arrangement.ChordTemplates.Count);
    }

    [Fact]
    public void AnArpeggioTemplate_PlacesAnArpeggioChord_AndAPlainOneOverItPlacesPlain()
    {
        Commit(ChordShapePlacement.Plan(_target, _f.AmArp, [At]));
        Assert.Equal(_f.AmArp.Id, ChordAt(At)!.TemplateId);

        // The same frets from the plain row: a different template, so the chord is repointed.
        var plan = Commit(ChordShapePlacement.Plan(_target, _f.Am, [At]));
        Assert.Equal(1, plan.Reshaped);
        Assert.Equal(_f.Am.Id, ChordAt(At)!.TemplateId);
    }

    [Fact]
    public void BehindTheCapo_TheShapeIsRefused_NeverWrittenAsAnL01Fret()
    {
        // C frets string 4 at 1: behind a capo on 2 that fret does not exist.
        _f.Arrangement.Capo = 2;

        var plan = ChordShapePlacement.Plan(_target, _f.C, [At]);

        Assert.Equal(ChordShapePlacementRefusal.BelowCapo, plan.Refusal);
        Assert.Null(plan.Action);
        Assert.Null(ChordAt(At));

        // A shape that is open or at/above the capo is placed as is.
        Commit(ChordShapePlacement.Plan(_target, _f.Open, [At]));
        Assert.Equal(_f.Open.Frets, Frets(ChordAt(At)!));
    }

    [Fact]
    public void AStringTheArrangementHasNot_IsRefused()
    {
        _f.Arrangement.StringCount = 4;

        Assert.Equal(ChordShapePlacementRefusal.StringOutOfRange, ChordShapePlacement.Plan(_target, _f.C, [At]).Refusal);
    }

    [Fact]
    public void ATemplateFromAnotherArrangement_IsRefused()
    {
        var foreign = _f.C.Clone();

        Assert.Equal(ChordShapePlacementRefusal.NotInArrangement, ChordShapePlacement.Plan(_target, foreign, [At]).Refusal);
    }

    [Fact]
    public void ManyPositions_AreOneUndoNode()
    {
        var a = new FractionalPosition(20);
        var b = new FractionalPosition(22);
        var reshape = _f.Level1.Chords.Single(c => c.TemplateId == _f.Open.Id);

        var plan = Commit(ChordShapePlacement.Plan(_target, _f.G, [a, b, reshape.Position]));

        Assert.Equal((2, 1), (plan.Created, plan.Reshaped));
        Assert.All(new[] { ChordAt(a)!, ChordAt(b)!, reshape }, c => Assert.Equal(_f.G.Id, c.TemplateId));
        Assert.Single(_history.GetUndoSteps());

        _history.Undo();
        Assert.Null(ChordAt(a));
        Assert.Equal(_f.Open.Id, reshape.TemplateId);
    }

    // === Repeat previous chord =================================================

    [Fact]
    public void RepeatPrevious_PlacesTheNearestEarlierChordsTemplate()
    {
        // Before beat 20 the latest chord is the open E5 at 14 (C is at 10, the arpeggio at 12).
        Commit(ChordShapePlacement.PlanRepeatPrevious(_target, At));

        var chord = ChordAt(At)!;
        Assert.Equal(_f.Open.Id, chord.TemplateId);
        Assert.Equal(_f.Open.Frets, Frets(chord));
    }

    [Fact]
    public void RepeatPrevious_LooksStrictlyBefore_NotAtThePen()
    {
        Commit(ChordShapePlacement.PlanRepeatPrevious(_target, new FractionalPosition(13)));
        Assert.Equal(_f.AmArp.Id, ChordAt(new FractionalPosition(13))!.TemplateId);

        // At the arpeggio's own position the previous chord is the C at 10: it reshapes it.
        Commit(ChordShapePlacement.PlanRepeatPrevious(_target, new FractionalPosition(12)));
        Assert.Equal(_f.C.Id, ChordAt(new FractionalPosition(12))!.TemplateId);
    }

    [Fact]
    public void RepeatPrevious_WithNoChordBefore_IsRefused()
    {
        var plan = ChordShapePlacement.PlanRepeatPrevious(_target, FractionalPosition.Zero);

        Assert.Equal(ChordShapePlacementRefusal.NoPreviousChord, plan.Refusal);
    }

    [Fact]
    public void RepeatPrevious_OfADriftedArpeggio_MintsInTheArpeggiosFlavour_InsideTheSameNode()
    {
        // The arpeggio chord at 12 was edited to G frets without a repoint: its template no longer
        // describes it. The repeat resolves G's frets in the ARPEGGIO flavour — never the plain G.
        var drifted = _f.Level1.Chords.Single(c => c.TemplateId == _f.AmArp.Id);
        drifted.ChordNotes.Clear();
        foreach (var (s, fr) in _f.G.Frets) drifted.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = fr });
        var position = new FractionalPosition(13);

        var plan = Commit(ChordShapePlacement.PlanRepeatPrevious(_target, position));

        var minted = plan.Shape!;
        Assert.NotSame(_f.G, minted);
        Assert.True(minted.IsArpeggio);
        Assert.True(ArpeggioFlavour.Of(_f.AmArp).Matches(minted));
        Assert.Contains(minted, _f.Arrangement.ChordTemplates);
        Assert.Equal(minted.Id, ChordAt(position)!.TemplateId);
        Assert.Single(_history.GetUndoSteps());

        _history.Undo();
        Assert.False(ChordTemplateCollectionOps.ContainsInstance(_f.Arrangement.ChordTemplates, minted));
        Assert.Null(ChordAt(position));
    }

    [Fact]
    public void RepeatPrevious_OfAnUntemplatedChord_FindsThePlainTemplateWithItsFrets()
    {
        var position = new FractionalPosition(30);
        var untemplated = ChordShapesFixture.ChordOn(_f.G, 28);
        untemplated.TemplateId = -1;
        _f.Level1.Chords.Add(untemplated);

        Commit(ChordShapePlacement.PlanRepeatPrevious(_target, position));

        Assert.Equal(_f.G.Id, ChordAt(position)!.TemplateId);
    }
}
