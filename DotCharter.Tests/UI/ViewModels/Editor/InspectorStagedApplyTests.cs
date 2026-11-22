using DotCharter.Services.Editing;
using DotCharter.Tests.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.ViewModels.Panels;
using static DotCharter.Tests.UI.ViewModels.Editor.InspectorStagedEditsTests;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     #751 P3 review: the staged Apply writes exactly what its review says. Every Apply is one node that undoes
///     and redoes whole; the review's count, its skipped and its "only in part" sounds are the sounds Apply
///     changes (a sound that already holds the staged value is not counted); the sustain step is the timeline
///     grid's with the keyboard's clamps; a chord slide never moves an open string; and a draft dies with its
///     level, its selection (Selection page) or the engine.
/// </summary>
public sealed class InspectorStagedApplyTests
{
    // ---- one node: undo and redo ---------------------------------------------

    [Fact]
    public void ABulkApply_UndoesAndRedoesWhole()
    {
        var f = new ChordLadderFixture();
        using var staged = Engine(f);
        staged.StageBulk(BulkDraft.None with { Legato = HopoType.HammerOn });

        AssertOneNodeUndoesAndRedoes(f, () => staged.Apply(StagedPage.Selection));
    }

    [Fact]
    public void ABendsApply_UndoesAndRedoesWhole()
    {
        var f = new ChordLadderFixture();
        using var staged = Engine(f);
        f.Choose(ChordApplyRung.Selected);
        staged.StageBend(2, [new BendValue(f.First.Position, 0), new BendValue(f.First.Position, 2)]);

        AssertOneNodeUndoesAndRedoes(f, () => staged.Apply(StagedPage.Bends));
    }

    [Fact]
    public void APlacementApply_UndoesAndRedoesWhole()
    {
        var (f, note) = OneNote(5, slideTo: 7);
        using var staged = Engine(f);
        staged.StagePlacement(new PlacementDraft { Fret = 8, StringIndex = 3 });

        AssertOneNodeUndoesAndRedoes(f, () => staged.Apply(StagedPage.Placement));
        Assert.Equal((8, 3, 10), (note.Fret, note.StringIndex, note.SlideTo));
    }

    [Fact]
    public void ASustainStep_UndoesAndRedoesWhole()
    {
        var (f, note) = OneNote(5);
        note.Sustain = new FractionalPosition(1);
        using var staged = Engine(f);

        AssertOneNodeUndoesAndRedoes(f, () => staged.ApplyMove(BulkMove.Sustain, 1));
    }

    // ---- discard and drop ------------------------------------------------------

    [Fact]
    public void DiscardingABendOrAPlacementDraft_WritesNothing()
    {
        var (f, _) = OneNote(5);
        using var staged = Engine(f);
        var before = Snapshot(f);

        staged.StagePlacement(new PlacementDraft { Fret = 9 });
        staged.StageBend(null, [new BendValue(new FractionalPosition(20), 1)]);
        Assert.False(staged.CanLeave(StagedPage.Placement));
        Assert.False(staged.CanLeave(StagedPage.Bends));
        staged.Discard(StagedPage.Placement);
        staged.Discard(StagedPage.Bends);

        Assert.True(staged.CanLeave(StagedPage.Placement));
        Assert.True(staged.CanLeave(StagedPage.Bends));
        Assert.False(staged.Apply(StagedPage.Placement));
        Assert.False(staged.Apply(StagedPage.Bends));
        Assert.Empty(Diff(before, Snapshot(f)));
        Assert.Empty(f.History.GetUndoSteps());
    }

    /// <summary>#751 P4 review: a level switch no longer drops the drafts silently; each is kept on its own sound
    /// (the footer names it and jumps back), and is there again on the way back.</summary>
    [Fact]
    public void AnotherLevel_KeepsEachSoundsDraft()
    {
        var f = new ChordLadderFixture();
        using var staged = Engine(f);
        staged.StageTechnique(new StagedKey(TechniqueField.Vibrato, 1), 1);
        staged.StageBend(2, [new BendValue(f.First.Position, 1)]);
        staged.StageBulk(BulkDraft.None with { Legato = HopoType.Tap });

        f.State.ActiveLevel = f.Arrangement.Levels[1];
        f.State.ActiveLevel = f.Arrangement.Levels[0];
        if (!f.Selection.SelectedChords.Contains(f.First)) f.Selection.SelectedChords.Add(f.First);

        Assert.Same(f.First, staged.Source);
        Assert.Equal(1, staged.Technique[new StagedKey(TechniqueField.Vibrato, 1)]);
        Assert.NotNull(staged.Bend);
    }

    [Fact]
    public void ASelectionChange_DropsTheSelectionDraft_ButAMoveOfTheSameSelectionKeepsIt()
    {
        var f = new ChordLadderFixture();
        using var staged = Engine(f);
        staged.StageBulk(BulkDraft.None with { Legato = HopoType.Tap });

        Assert.True(staged.ApplyMove(BulkMove.Fret, 1));
        Assert.False(staged.CanLeave(StagedPage.Selection));

        f.Selection.SelectedChords.Remove(f.Second);

        Assert.True(staged.Bulk.IsEmpty);
        Assert.True(staged.CanLeave(StagedPage.Selection));
    }

    [Fact]
    public void Dispose_StopsListeningToTheSelectionAndTheEditor()
    {
        var f = new ChordLadderFixture();
        var staged = Engine(f);
        var draft = BulkDraft.None with { Legato = HopoType.Tap };
        staged.StageBulk(draft);

        staged.Dispose();
        f.Selection.SelectedChords.Remove(f.Second);
        Assert.Same(draft, staged.Bulk);
        f.State.ActiveArrangement = new Arrangement { Name = "Rhythm" };
        Assert.Same(draft, staged.Bulk);
    }

    [Fact]
    public void Dispose_StopsListeningToTheHistory()
    {
        var f = new ChordLadderFixture();
        var staged = Engine(f);
        staged.StageBend(2, [new BendValue(f.First.Position, 1)]);

        staged.Dispose();
        var clone = f.First.Clone();
        clone.ChordNotes[2].BendValues.Add(new BendValue(f.First.Position, 1));
        f.History.Do(new MutateChordsAction(f.Arrangement.Levels[0].Chords, [f.First], [f.First.Clone()], [clone]));

        Assert.False(staged.CanLeave(StagedPage.Bends));
    }

    // ---- the review is what Apply writes ---------------------------------------

    public static TheoryData<string> ReviewCases() =>
    [
        "StringSilent", "StringSilentPartly", "TechniqueSlideOffNeckPartly", "BulkSlideOffNeck", "BulkSlideOffNeckPartly",
        "NothingLands", "PlacementSlideOffNeck", "LeavesNeck", "SustainFloor", "SustainFloorPartly", "SustainNextSoundPartly",
        "SustainAudioEndPartly", "SustainAudioEnd", "SustainLinked"
    ];

    [Theory]
    [MemberData(nameof(ReviewCases))]
    public void TheReview_NamesExactlyWhatApplyWrites(string name)
    {
        var c = Case(name);
        var before = Sounds(c.F);
        var review = c.Review();
        var plan = c.Plan();

        Assert.Equal(plan.Count > 0, c.Apply());
        var changed = Changed(before);

        Assert.Equal(plan.Count, review.Count);
        Assert.True(changed.SetEquals(plan.Writes), $"{name}: Apply changed {changed.Count}, the plan counted {plan.Count}.");
        var note = Assert.Single(c.Partly ? plan.Partly : plan.Skips);
        Assert.Empty(c.Partly ? plan.Skips : plan.Partly);
        Assert.Same(c.Named, note.Target);
        Assert.Equal(c.Partly, changed.Contains(note.Target));
        Assert.Equal(c.Reason, note.Reason);
        Assert.Contains(c.Partly ? "Only in part: 0:" : "Skips 0:", review.Text, StringComparison.Ordinal);
        Assert.Contains(c.Words, review.Text, StringComparison.Ordinal);
        Assert.True(review.NeedsConfirm);
    }

    // ---- counts ------------------------------------------------------------------

    [Fact]
    public void ASoundThatAlreadyHoldsTheStagedValue_IsNotCounted()
    {
        var f = new ChordLadderFixture();
        f.First.Accent = true;
        using var staged = Engine(f);
        f.Choose(ChordApplyRung.Selected);

        staged.StageTechnique(new StagedKey(TechniqueField.Accent), 1);
        Assert.Equal([f.Second], staged.Plan(StagedPage.Technique).Writes);
        Assert.Equal(1, staged.Review(StagedPage.Technique).Count);
        Assert.False(staged.Review(StagedPage.Technique).NeedsConfirm);
        staged.Discard(StagedPage.Technique);

        staged.StageBulk(BulkDraft.None with { Flags = new Dictionary<BulkFlag, bool> { [BulkFlag.Accent] = true } });
        Assert.Equal([f.Second], staged.Plan(StagedPage.Selection).Writes);
    }

    [Fact]
    public void Reset_KeepsTheSlideOfATargetItsStagedSlideWouldPushOffTheNeck()
    {
        var f = new ChordLadderFixture();
        var high = AddChord(f, 6, (0, 23), (1, 22));
        high.ChordNotes[0].SlideTo = 24;
        high.Accent = true;
        f.Selection.SelectedChords.Add(high);
        using var staged = Engine(f);
        f.Choose(ChordApplyRung.Selected);

        staged.ResetTechnique();
        staged.StageTechnique(new StagedKey(TechniqueField.SlideTo, 0), 7);
        Assert.Equal(StagedSkipReason.SlideOffNeck, Assert.Single(staged.Plan(StagedPage.Technique).Partly).Reason);
        Assert.True(staged.Apply(StagedPage.Technique));

        Assert.Equal(24, high.ChordNotes[0].SlideTo);
        Assert.False(high.Accent);
        Assert.Equal(7, f.First.ChordNotes[0].SlideTo);
    }

    [Fact]
    public void AStringStepOfMoreThanOne_ReachesNothing_AndWritesNothing()
    {
        var f = new ChordLadderFixture();
        using var staged = Engine(f);
        var before = Snapshot(f);

        Assert.Equal(0, staged.PlanMove(BulkMove.String, 2).Count);
        Assert.False(staged.ApplyMove(BulkMove.String, 2));
        Assert.Empty(Diff(before, Snapshot(f)));
    }

    // ---- sustain step = the timeline grid -----------------------------------------

    [Theory]
    [InlineData(12)]
    [InlineData(32)]
    public void ASustainStep_IsOneStepOfTheTimelineGrid(int gridDivisor)
    {
        var (f, note) = OneNote(5);
        note.Sustain = new FractionalPosition(1);
        using var staged = Engine(f, gridDivisor);

        var plan = staged.PlanMove(BulkMove.Sustain, 1);
        Assert.Equal([note], plan.Writes);
        Assert.Empty(plan.Partly);
        Assert.True(staged.ApplyMove(BulkMove.Sustain, 1));
        Assert.Equal(new FractionalPosition(new Fraction(gridDivisor + 1, gridDivisor)), note.Sustain);

        Assert.True(staged.ApplyMove(BulkMove.Sustain, -2));
        Assert.Equal(new FractionalPosition(new Fraction(gridDivisor - 1, gridDivisor)), note.Sustain);
    }

    [Fact]
    public void ASustainStep_StopsAtTheEndOfTheAudio_AndSaysSo()
    {
        var (f, note) = OneNote(5);
        f.State.TotalAudioLengthMs = 10_600; // beat 21.2 on the 1/192 grid: about 1.2 beats after the note at beat 20
        note.Sustain = new FractionalPosition(1);
        using var staged = Engine(f);
        var end = AudioEnd(f, note);

        var plan = staged.PlanMove(BulkMove.Sustain, 1);
        Assert.Equal(StagedSkipReason.SustainAudioEnd, Assert.Single(plan.Partly).Reason);
        Assert.True(staged.ApplyMove(BulkMove.Sustain, 1));
        Assert.Equal(end, note.Sustain);

        Assert.Equal(0, staged.PlanMove(BulkMove.Sustain, 1).Count);
        Assert.False(staged.ApplyMove(BulkMove.Sustain, 1));
        Assert.Equal(end, note.Sustain);
    }

    // ---- chord slides: fretted strings only, inside the mask -------------------------

    [Fact]
    public void ABulkSlide_NeverMovesAnOpenString_AndKeepsToTheStringMask()
    {
        var f = new ChordLadderFixture();
        f.Selection.SelectedChords.Clear();
        var open = AddChord(f, 6, (0, 0), (1, 22), (2, 5));
        f.Selection.SelectedChords.Add(open);
        f.State.StringEditMask = StringEditMask.FromStrings(0, 1);
        using var staged = Engine(f);

        staged.StageBulk(BulkDraft.None with { Slide = 2 });
        Assert.Equal([open], staged.Plan(StagedPage.Selection).Writes);
        Assert.True(staged.Apply(StagedPage.Selection));

        Assert.Equal((null, 24, null), (open.ChordNotes[0].SlideTo, open.ChordNotes[1].SlideTo, open.ChordNotes[2].SlideTo));
    }

    // ---- helpers -------------------------------------------------------------------

    private sealed record ReviewCase(
        ChordLadderFixture F,
        Func<StagedEditPlan> Plan,
        Func<StagedEditReview> Review,
        Func<bool> Apply,
        ITimedElement Named,
        StagedSkipReason Reason,
        bool Partly,
        string Words);

    private static ReviewCase Case(string name)
    {
        var f = new ChordLadderFixture();
        var staged = Engine(f);
        ReviewCase Page(StagedPage page, ITimedElement named, StagedSkipReason reason, bool partly, string words) =>
            new(f, () => staged.Plan(page), () => staged.Review(page), () => staged.Apply(page), named, reason, partly, words);
        ReviewCase Move(BulkMove move, int delta, ITimedElement named, StagedSkipReason reason, bool partly, string words) =>
            new(f, () => staged.PlanMove(move, delta), () => staged.ReviewMove(move, delta), () => staged.ApplyMove(move, delta),
                named, reason, partly, words);

        switch (name)
        {
            case "StringSilent" or "StringSilentPartly":
            {
                var two = AddChord(f, 6, (0, 5), (1, 7));
                f.Selection.SelectedChords.Add(two);
                f.Choose(ChordApplyRung.Selected);
                staged.StageTechnique(new StagedKey(TechniqueField.Vibrato, 2), 1);
                if (name == "StringSilentPartly") staged.StageTechnique(new StagedKey(TechniqueField.Accent), 1);
                return Page(StagedPage.Technique, two, StagedSkipReason.StringSilent, name == "StringSilentPartly", "not played");
            }
            case "TechniqueSlideOffNeckPartly":
            {
                var high = AddChord(f, 6, (0, 23), (1, 24));
                f.Selection.SelectedChords.Add(high);
                f.Choose(ChordApplyRung.Selected);
                staged.StageTechnique(new StagedKey(TechniqueField.SlideTo, 0), 7);
                staged.StageTechnique(new StagedKey(TechniqueField.Accent), 1);
                return Page(StagedPage.Technique, high, StagedSkipReason.SlideOffNeck, true, "the slide would leave the neck");
            }
            case "BulkSlideOffNeck" or "BulkSlideOffNeckPartly":
            {
                var high = AddChord(f, 6, (0, 23));
                f.Selection.SelectedChords.Add(high);
                var partly = name == "BulkSlideOffNeckPartly";
                staged.StageBulk(BulkDraft.None with { Slide = 2, Legato = partly ? HopoType.HammerOn : null });
                return Page(StagedPage.Selection, high, StagedSkipReason.SlideOffNeck, partly, "the slide would leave the neck");
            }
            case "NothingLands":
            {
                var note = InspectorStagedEditsTests.AddNote(f, 20, 5);
                f.Selection.SelectedNotes.Add(note);
                staged.StageBulk(BulkDraft.None with { Strum = PickDirectionType.Down });
                return Page(StagedPage.Selection, note, StagedSkipReason.NothingLands, false, "no staged value lands there");
            }
            case "PlacementSlideOffNeck":
            {
                var note = Only(f, 5, slideTo: 7);
                staged.StagePlacement(new PlacementDraft { Fret = 23 });
                return Page(StagedPage.Placement, note, StagedSkipReason.SlideOffNeck, false, "the slide would leave the neck");
            }
            case "LeavesNeck":
            {
                var high = AddChord(f, 7, (0, 22), (1, 24));
                f.Selection.SelectedChords.Add(high);
                return Move(BulkMove.Fret, 2, high, StagedSkipReason.LeavesNeck, false, "would leave the neck");
            }
            case "SustainFloor" or "SustainFloorPartly":
            {
                var note = Only(f, 5);
                note.Sustain = name == "SustainFloor" ? FractionalPosition.Zero : new FractionalPosition(new Fraction(1, 8));
                return Move(BulkMove.Sustain, -1, note, StagedSkipReason.SustainFloor, name == "SustainFloorPartly", "stops at 0");
            }
            case "SustainNextSoundPartly":
            {
                var note = Only(f, 5);
                note.Sustain = new FractionalPosition(new Fraction(3, 4));
                InspectorStagedEditsTests.AddNote(f, 21, 7);
                return Move(BulkMove.Sustain, 1, note, StagedSkipReason.SustainNextSound, true, "stops before the next sound");
            }
            case "SustainAudioEnd" or "SustainAudioEndPartly":
            {
                var note = Only(f, 5);
                f.State.TotalAudioLengthMs = 10_600;
                note.Sustain = name == "SustainAudioEnd" ? AudioEnd(f, note) : new FractionalPosition(1);
                return Move(BulkMove.Sustain, 1, note, StagedSkipReason.SustainAudioEnd, name == "SustainAudioEndPartly",
                    "stops at the end of the audio");
            }
            case "SustainLinked":
            {
                var note = Only(f, 5);
                note.LinkNext = true;
                return Move(BulkMove.Sustain, 1, note, StagedSkipReason.SustainLinked, false, "linked to the next note");
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, null);
        }
    }

    /// <summary>The longest sustain <paramref name="note" /> can have before the audio ends.</summary>
    private static FractionalPosition AudioEnd(ChordLadderFixture f, GuitarNote note) =>
        TimelineMath.PositionFromMs(f.State.TotalAudioLengthMs, f.Project.Beats) - note.Position;

    private static (ChordLadderFixture F, GuitarNote Note) OneNote(int fret, int? slideTo = null)
    {
        var f = new ChordLadderFixture();
        return (f, Only(f, fret, slideTo));
    }

    /// <summary>A note at beat 20 (10 s), the only selected sound.</summary>
    private static GuitarNote Only(ChordLadderFixture f, int fret, int? slideTo = null)
    {
        f.Selection.SelectedChords.Clear();
        var note = InspectorStagedEditsTests.AddNote(f, 20, fret, slideTo);
        f.Selection.SelectedNotes.Add(note);
        return note;
    }

    private static void AssertOneNodeUndoesAndRedoes(ChordLadderFixture f, Func<bool> apply)
    {
        var before = Snapshot(f);
        Assert.True(apply());
        var after = Snapshot(f);
        Assert.NotEmpty(Diff(before, after));
        Assert.Single(f.History.GetUndoSteps());

        f.History.Undo();
        Assert.Empty(Diff(before, Snapshot(f)));
        f.History.Redo();
        Assert.Empty(Diff(after, Snapshot(f)));
    }

    private static Arrangement Snapshot(ChordLadderFixture f) => ChartProjectCloner.CloneArrangement(f.Arrangement);

    private static List<string> Diff(Arrangement a, Arrangement b) => [.. DeepCompare.Diff("arr", a, b, new HashSet<string>())];

    /// <summary>Every sound on every level with a copy of how it stands now.</summary>
    private static List<(ITimedElement Sound, ITimedElement Copy)> Sounds(ChordLadderFixture f) =>
    [
        .. f.Arrangement.Levels.SelectMany(l => l.Notes).Select(n => ((ITimedElement)n, (ITimedElement)n.Clone())),
        .. f.Arrangement.Levels.SelectMany(l => l.Chords).Select(c => ((ITimedElement)c, (ITimedElement)c.Clone()))
    ];

    private static HashSet<ITimedElement> Changed(List<(ITimedElement Sound, ITimedElement Copy)> before) => new(
        before.Where(p => p switch
        {
            (GuitarNote n, GuitarNote copy) => !ChartActionStateComparer.GuitarNotesEqual(n, copy),
            (Chord c, Chord copy) => !ChartActionStateComparer.ChordsEqual(c, copy),
            _ => false
        }).Select(p => p.Sound), ReferenceEqualityComparer.Instance);
}
