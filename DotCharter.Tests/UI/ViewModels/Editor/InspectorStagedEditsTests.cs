using DotCharter.Tests.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     #751 P3: the staged-edit engine behind the Note pages' Apply. Driven on the chord-ladder fixture (three
///     strings per chord, frets root / root+2 / root+2, two levels): nothing is written until Apply, Apply is ONE
///     undo node on every level the scope covers, Discard writes nothing, a target that cannot take a value is
///     passed by and NAMED, and a history change under a draft re-reads its baseline.
/// </summary>
public sealed class InspectorStagedEditsTests
{
    private static readonly StagedKey Vibrato1 = new(TechniqueField.Vibrato, 1);

    /// <summary>The engine over the fixture: 16 s of audio (beat 32) unless the test set its own length,
    /// and the timeline's grid at <paramref name="gridDivisor" /> steps a beat. Settings ▸ Editor's Apply step is
    /// ON (these tests pin the staged behaviour; <c>InspectorImmediateApplyTests</c> pins the default, off).</summary>
    internal static InspectorStagedEdits Engine(ChordLadderFixture f, int gridDivisor = 4, bool applyStep = true)
    {
        f.State.InspectorApplyStep = applyStep;
        if (f.State.TotalAudioLengthMs <= 0) f.State.TotalAudioLengthMs = 16_000;
        return new InspectorStagedEdits(f.Editor, f.History, f.Keys, FakeLocalization.English(), f.State, () => gridDivisor,
            () => f.Panel.TechniqueTargets()?.Chords ?? f.Selection.SelectedChords);
    }

    public static TheoryData<ChordApplyRung, int> Rungs() => ChordLadderFixture.Rungs();

    [Theory]
    [MemberData(nameof(Rungs))]
    public void ATechnique_WritesNothingUntilApply_ThenExactlyTheRung_InOneUndoNode(ChordApplyRung rung, int expected)
    {
        var f = new ChordLadderFixture();
        var before = ChartProjectCloner.CloneArrangement(f.Arrangement);
        using var staged = Engine(f);
        f.Choose(rung);
        var fingerprints = f.Fingerprints();

        staged.StageTechnique(Vibrato1, 1);

        Assert.Empty(f.ChangedSince(fingerprints));
        Assert.False(staged.CanLeave(StagedPage.Technique));
        Assert.Equal(expected, staged.Plan(StagedPage.Technique).Count);
        Assert.Equal(expected > 1, staged.Review(StagedPage.Technique).NeedsConfirm);

        Assert.True(staged.Apply(StagedPage.Technique));

        var changed = f.ChangedSince(fingerprints);
        Assert.Equal(expected, changed.Count);
        Assert.True(changed.SetEquals(f.Panel.TechniqueTargets()!.Chords));
        Assert.All(changed, c => Assert.True(c.ChordNotes.Single(n => n.StringIndex == 1).Vibrato));
        Assert.All(changed, c => Assert.False(c.ChordNotes.Single(n => n.StringIndex == 0).Vibrato));
        Assert.Single(f.History.GetUndoSteps());
        Assert.True(staged.CanLeave(StagedPage.Technique));

        f.History.Undo();
        Assert.Empty(DeepCompare.Diff("arr", before, ChartProjectCloner.CloneArrangement(f.Arrangement), new HashSet<string>()));
    }

    [Fact]
    public void Discard_WritesNothing_AndFreesTheGuard()
    {
        var f = new ChordLadderFixture();
        using var staged = Engine(f);
        var fingerprints = f.Fingerprints();
        staged.StageTechnique(Vibrato1, 1);
        staged.StageBulk(BulkDraft.None with { Strum = PickDirectionType.Down });

        staged.Discard(StagedPage.Technique);
        staged.Discard(StagedPage.Selection);

        Assert.True(staged.CanLeave(StagedPage.Technique));
        Assert.True(staged.CanLeave(StagedPage.Selection));
        Assert.False(staged.Apply(StagedPage.Technique));
        Assert.Empty(f.ChangedSince(fingerprints));
        Assert.Empty(f.History.GetUndoSteps());
    }

    [Fact]
    public void AStringATargetDoesNotPlay_IsPassedBy_AndNamedInTheReview()
    {
        var f = new ChordLadderFixture();
        var twoStrings = AddChord(f, 6, (0, 5), (1, 7));
        f.Selection.SelectedChords.Add(twoStrings);
        using var staged = Engine(f);
        f.Choose(ChordApplyRung.Selected);

        staged.StageTechnique(new StagedKey(TechniqueField.Vibrato, 2), 1);
        var plan = staged.Plan(StagedPage.Technique);
        var review = staged.Review(StagedPage.Technique);

        Assert.Equal(2, plan.Count);
        Assert.DoesNotContain(twoStrings, plan.Writes);
        var skip = Assert.Single(plan.Skips);
        Assert.Equal((StagedSkipReason.StringSilent, 2), (skip.Reason, skip.StringIndex));
        Assert.Contains("0:03.000", review.Text, StringComparison.Ordinal);
        Assert.Contains("not played", review.Text, StringComparison.Ordinal);
        Assert.True(review.NeedsConfirm);
    }

    /// <summary>Decision 2: a slide that would leave the neck on one target skips THAT target's slide and
    /// names it; the rest of the edit still lands there, and every other target slides the same interval.</summary>
    [Fact]
    public void ASlideLandsAsAnInterval_AndAnOffNeckTarget_KeepsItsSlideButTakesTheRest()
    {
        var f = new ChordLadderFixture();
        var high = AddChord(f, 6, (0, 23), (1, 24));
        f.Selection.SelectedChords.Add(high);
        using var staged = Engine(f);
        f.Choose(ChordApplyRung.Selected);

        staged.StageTechnique(new StagedKey(TechniqueField.SlideTo, 0), 7);
        staged.StageTechnique(new StagedKey(TechniqueField.Accent), 1);
        var plan = staged.Plan(StagedPage.Technique);

        Assert.Equal(3, plan.Count);
        Assert.Empty(plan.Skips);
        var part = Assert.Single(plan.Partly);
        Assert.Same(high, part.Target);
        Assert.Equal(StagedSkipReason.SlideOffNeck, part.Reason);
        Assert.Contains("Only in part: 0:03.000 (the slide would leave the neck)", staged.Review(StagedPage.Technique).Text,
            StringComparison.Ordinal);

        Assert.True(staged.Apply(StagedPage.Technique));

        Assert.Equal(7, f.First.ChordNotes[0].SlideTo);
        Assert.Equal(7, f.Second.ChordNotes[0].SlideTo);
        Assert.Null(high.ChordNotes[0].SlideTo);
        Assert.True(high.Accent);
        Assert.Single(f.History.GetUndoSteps());
    }

    [Fact]
    public void ASlideCopiedOntoAnotherShape_KeepsTheSourcesInterval()
    {
        var f = new ChordLadderFixture();
        var other = AddChord(f, 6, (0, 9), (1, 11));
        f.Selection.SelectedChords.Add(other);
        using var staged = Engine(f);
        f.Choose(ChordApplyRung.Selected);

        staged.StageTechnique(new StagedKey(TechniqueField.SlideTo, 0), 3);
        staged.Apply(StagedPage.Technique);

        Assert.Equal(3, f.First.ChordNotes[0].SlideTo);
        Assert.Equal(7, other.ChordNotes[0].SlideTo);
    }

    [Fact]
    public void Reset_ClearsEveryTechniqueOnEachTarget_BeforeTheStagedValuesLand()
    {
        var f = new ChordLadderFixture();
        f.First.Accent = true;
        f.First.ChordNotes[0].Mute = MuteType.Palm;
        f.First.ChordNotes[2].Vibrato = true;
        f.First.ChordNotes[2].VibratoStrength = 80;
        using var staged = Engine(f);
        f.Choose(ChordApplyRung.ThisChord);

        staged.ResetTechnique();
        staged.StageTechnique(new StagedKey(TechniqueField.Tremolo, 1), 1);
        Assert.True(staged.Apply(StagedPage.Technique));

        Assert.False(f.First.Accent);
        Assert.Equal(MuteType.None, f.First.ChordNotes[0].Mute);
        Assert.False(f.First.ChordNotes[2].Vibrato);
        Assert.Equal(0, f.First.ChordNotes[2].VibratoStrength);
        Assert.True(f.First.ChordNotes[1].Tremolo);
    }

    [Fact]
    public void BulkValues_ReadAPartlyLinkedChordAsMixed_AndPassByWhatATargetCannotTake()
    {
        var f = new ChordLadderFixture();
        f.First.ChordNotes[0].LinkNext = true;
        var note = AddNote(f, 20, 5);
        f.Selection.SelectedNotes.Add(note);
        f.Selection.SelectedChords.Remove(f.Second);
        using var staged = Engine(f);
        var targets = staged.Targets(StagedPage.Selection);

        Assert.Null(StagedBulk.Read(f.First, BulkFlag.LinkNext));
        Assert.Null(StagedBulk.Read(targets, BulkFlag.LinkNext));
        Assert.False(StagedBulk.Read(note, BulkFlag.LinkNext));

        staged.StageBulk(BulkDraft.None with { Strum = PickDirectionType.Down });
        var plan = staged.Plan(StagedPage.Selection);
        Assert.Equal([f.First], plan.Writes);
        Assert.Equal(StagedSkipReason.NothingLands, Assert.Single(plan.Skips).Reason);
        Assert.Contains("no staged value lands there", staged.Review(StagedPage.Selection).Text, StringComparison.Ordinal);

        staged.StageBulk(BulkDraft.None with { Flags = new Dictionary<BulkFlag, bool> { [BulkFlag.LinkNext] = true } });
        Assert.True(staged.Apply(StagedPage.Selection));
        Assert.All(f.First.ChordNotes, n => Assert.True(n.LinkNext));
        Assert.True(note.LinkNext);
        Assert.True(StagedBulk.Read(staged.Targets(StagedPage.Selection), BulkFlag.LinkNext));
        Assert.Single(f.History.GetUndoSteps());
    }

    [Fact]
    public void ABulkSlideOffTheNeck_IsNamed_AndNoSlideOnASoundThatNeverSlid_CountsNothing()
    {
        var f = new ChordLadderFixture();
        var high = AddChord(f, 6, (0, 23));
        f.Selection.SelectedChords.Add(high);
        using var staged = Engine(f);

        staged.StageBulk(BulkDraft.None with { Slide = 2 });
        var plan = staged.Plan(StagedPage.Selection);
        Assert.Equal(2, plan.Count);
        Assert.Equal((high, StagedSkipReason.SlideOffNeck), (Assert.Single(plan.Skips).Target, plan.Skips[0].Reason));

        staged.StageBulk(BulkDraft.None with { Slide = 0 });
        Assert.Equal(0, staged.Plan(StagedPage.Selection).Count);
        Assert.False(staged.Apply(StagedPage.Selection));
    }

    [Fact]
    public void ABend_LandsOnTheSameStringOfTheRung_AndNamesTheAuthoredBendItReplaces()
    {
        var f = new ChordLadderFixture();
        f.Second.ChordNotes[2].BendValues.Add(new BendValue(f.Second.Position, 1));
        using var staged = Engine(f);
        f.Choose(ChordApplyRung.Selected);

        staged.StageBend(2, [.. f.First.ChordNotes[2].BendValues]);
        Assert.Equal(0, staged.Plan(StagedPage.Bends).Count);
        Assert.True(staged.CanLeave(StagedPage.Bends));

        staged.StageBend(2, [new BendValue(f.First.Position, 0), new BendValue(f.First.Position, 2)]);
        var plan = staged.Plan(StagedPage.Bends);
        Assert.Equal(2, plan.Count);
        Assert.Equal([f.Second], plan.Replacements);
        Assert.Contains("Replaces the authored bend at 0:00.500", staged.Review(StagedPage.Bends).Text, StringComparison.Ordinal);

        Assert.True(staged.Apply(StagedPage.Bends));
        Assert.Equal(2, f.Second.ChordNotes[2].BendValues.Max(v => v.Step));
        Assert.Empty(f.Second.ChordNotes[1].BendValues);
        Assert.Single(f.History.GetUndoSteps());
    }

    /// <summary>#751 P5 review nit: a chord that one staged string lands on is WRITTEN (the string it lacks is a part it
    /// keeps, <see cref="StagedEditPlan.Partly" />), never also a skip; only a chord no staged string lands on is skipped.</summary>
    [Fact]
    public void ABendOnTwoStrings_WritesAChordThatPlaysOne_AndSkipsOnlyAChordThatPlaysNeither()
    {
        static Chord Chord(int beat, params int[] strings)
        {
            var chord = new Chord { Position = new FractionalPosition(beat) };
            foreach (var s in strings) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = 5, Sustain = new FractionalPosition(1) });
            return chord;
        }

        var source = Chord(0, 1, 2);
        var both = Chord(2, 1, 2);
        var one = Chord(4, 1);
        var neither = Chord(6, 0);
        BendDraft Draft(int strand) =>
            new(strand, [new BendValue(source.Position, 1)], [], source.Position, new FractionalPosition(1));

        var plan = StagedBends.Plan(source, [Draft(1), Draft(2)], [source, both, one, neither]);

        Assert.Equal([source, both, one], plan.Writes);
        Assert.DoesNotContain(plan.Skips, s => ReferenceEquals(s.Target, one));
        Assert.Equal((2, StagedSkipReason.StringSilent), (Assert.Single(plan.Partly, p => ReferenceEquals(p.Target, one)).StringIndex,
            plan.Partly.Single(p => ReferenceEquals(p.Target, one)).Reason));
        Assert.All(plan.Skips, s => Assert.Same(neither, s.Target));
        Assert.Equal([1, 2], plan.Skips.Select(s => s.StringIndex ?? -1).Order());
    }

    /// <summary>#751 P5 review nit: an Apply whose write drops another sound's bend draft keeps the "draft dropped" line
    /// on the status rail, with the Apply's own message, instead of overwriting it.</summary>
    [Fact]
    public void AnApplyThatDropsAnotherDraft_SaysBoth()
    {
        var f = new ChordLadderFixture();
        using var staged = Engine(f);
        f.Selection.SelectedChords.Clear();
        f.Selection.SelectedChords.Add(f.Second);
        staged.StageBend(2, [new BendValue(f.Second.Position, 0), new BendValue(f.Second.Position, 1)]);

        f.Selection.SelectedChords.Clear();
        f.Selection.SelectedChords.Add(f.First);
        f.Selection.SelectedChords.Add(f.Second);
        f.Choose(ChordApplyRung.Selected);
        staged.StageBend(2, [new BendValue(f.First.Position, 0), new BendValue(f.First.Position, 2)]);
        Assert.Same(f.First, staged.Source);
        Assert.True(staged.Apply(StagedPage.Bends));

        var message = f.State.MainDisplayMessage;
        Assert.Contains("was dropped", message, StringComparison.Ordinal);
        Assert.Contains("applied", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AHistoryChangeUnderABendDraft_RereadsItsBaseline()
    {
        var f = new ChordLadderFixture();
        using var staged = Engine(f);
        IReadOnlyList<BendValue> curve = [new BendValue(f.First.Position, 1)];
        staged.StageBend(2, curve);
        Assert.False(staged.CanLeave(StagedPage.Bends));

        // A keyboard or timeline edit writes the very curve the draft holds: nothing is left to apply.
        var clone = f.First.Clone();
        clone.ChordNotes[2].BendValues.Add(new BendValue(f.First.Position, 1));
        f.History.Do(new MutateChordsAction(f.Arrangement.Levels[0].Chords, [f.First], [f.First.Clone()], [clone]));

        Assert.True(staged.CanLeave(StagedPage.Bends));
    }

    [Fact]
    public void Placement_CarriesTheSlideWithAFretMove_AndRefusesOneThatWouldLeaveTheNeck()
    {
        var f = new ChordLadderFixture();
        f.Selection.SelectedChords.Clear();
        var note = AddNote(f, 20, 5, slideTo: 7);
        f.Selection.SelectedNotes.Add(note);
        using var staged = Engine(f);

        staged.StagePlacement(new PlacementDraft { Fret = 5 });
        Assert.True(staged.CanLeave(StagedPage.Placement));

        staged.StagePlacement(new PlacementDraft { Fret = 23 });
        var refused = staged.Plan(StagedPage.Placement);
        Assert.Equal(0, refused.Count);
        Assert.Equal(StagedSkipReason.SlideOffNeck, Assert.Single(refused.Skips).Reason);

        staged.StagePlacement(new PlacementDraft { Fret = 8, StringIndex = 3 });
        Assert.True(staged.Apply(StagedPage.Placement));
        Assert.Equal((8, 3, 10), (note.Fret, note.StringIndex, note.SlideTo));
        Assert.Single(f.History.GetUndoSteps());
    }

    [Fact]
    public void ANoteTechnique_ReachesTheSelectedNotes_OnlyWhenItsScopeSaysSo()
    {
        var f = new ChordLadderFixture();
        f.Selection.SelectedChords.Clear();
        var a = AddNote(f, 20, 5);
        var b = AddNote(f, 21, 7);
        f.Selection.SelectedNotes.Add(a);
        f.Selection.SelectedNotes.Add(b);
        using var staged = Engine(f);

        staged.StageTechnique(new StagedKey(TechniqueField.Hopo), (int)HopoType.HammerOn);
        Assert.Equal([a], staged.Plan(StagedPage.Technique).Writes);
        staged.TechniqueOnSelectedNotes = true;
        Assert.True(staged.Apply(StagedPage.Technique));

        Assert.Equal((HopoType.HammerOn, HopoType.HammerOn), (a.Hopo, b.Hopo));
    }

    [Fact]
    public void AFretMove_TakesEachSoundWhole_WithItsSlides_AndNamesTheOneThatStays()
    {
        var f = new ChordLadderFixture();
        f.Selection.SelectedChords.Clear();
        var open = AddChord(f, 6, (0, 0), (1, 5));
        open.ChordNotes[1].SlideTo = 7;
        var high = AddChord(f, 7, (0, 22), (1, 24));
        f.Selection.SelectedChords.Add(open);
        f.Selection.SelectedChords.Add(high);
        using var staged = Engine(f);

        var plan = staged.PlanMove(BulkMove.Fret, 2);
        Assert.Equal([open], plan.Writes);
        Assert.Equal((high, StagedSkipReason.LeavesNeck), (Assert.Single(plan.Skips).Target, plan.Skips[0].Reason));
        Assert.Contains("would leave the neck", staged.ReviewMove(BulkMove.Fret, 2).Text, StringComparison.Ordinal);

        Assert.True(staged.ApplyMove(BulkMove.Fret, 2));

        Assert.Equal((0, 7, 9), (open.ChordNotes[0].Fret, open.ChordNotes[1].Fret, open.ChordNotes[1].SlideTo));
        Assert.Equal((22, 24), (high.ChordNotes[0].Fret, high.ChordNotes[1].Fret));
        Assert.Single(f.History.GetUndoSteps());
    }

    [Fact]
    public void ASustainStep_StopsAtZero_AndSaysSo()
    {
        var f = new ChordLadderFixture();
        f.Selection.SelectedChords.Clear();
        var note = AddNote(f, 20, 5);
        note.Sustain = new FractionalPosition(new Fraction(1, 8));
        f.Selection.SelectedNotes.Add(note);
        using var staged = Engine(f);

        Assert.Equal(StagedSkipReason.SustainFloor, Assert.Single(staged.PlanMove(BulkMove.Sustain, -1).Partly).Reason);
        Assert.True(staged.ApplyMove(BulkMove.Sustain, -1));
        Assert.Equal(FractionalPosition.Zero, note.Sustain);
    }

    /// <summary>#751 P4 review: another arrangement or level no longer drops the drafts silently: each is on its own
    /// sound, which is still in the project, and the editor counts it as unsaved work.</summary>
    [Fact]
    public void AnotherArrangementOrLevel_KeepsTheDrafts_AsUnsavedWork()
    {
        var f = new ChordLadderFixture();
        using var staged = Engine(f);
        staged.StageTechnique(Vibrato1, 1);

        f.State.ActiveArrangement = new Arrangement { Name = "Rhythm" };
        Assert.True(staged.HasAnyDraft);
        Assert.True(f.Editor.HasPendingDrafts);

        f.State.ActiveArrangement = f.Arrangement;
        f.State.ActiveLevel = f.Arrangement.Levels[0];
        if (!f.Selection.SelectedChords.Contains(f.First)) f.Selection.SelectedChords.Add(f.First);
        Assert.Same(f.First, staged.Source);
        Assert.Equal(1, staged.Technique[Vibrato1]);
    }

    [Fact]
    public void AnotherProject_DropsEveryDraft()
    {
        var f = new ChordLadderFixture();
        using var staged = Engine(f);
        staged.StageTechnique(Vibrato1, 1);
        staged.StageBulk(BulkDraft.None with { Legato = HopoType.Tap });

        f.State.ActiveProject = new ChartProject();

        Assert.False(staged.HasAnyDraft);
        Assert.False(f.Editor.HasPendingDrafts);
    }

    /// <summary>#751 P4 review: a keyboard edit that gives the sound the staged value drops it from the draft, so
    /// a later keyboard edit is not reverted by Apply. A value the sound already held when it was staged stays (the
    /// scope may reach sounds that do not hold it).</summary>
    [Fact]
    public void AKeyboardEditUnderATechniqueDraft_DropsTheValueItGave_SoApplyNeverRevertsIt()
    {
        var f = new ChordLadderFixture();
        using var staged = Engine(f);
        f.Choose(ChordApplyRung.ThisChord);
        staged.StageTechnique(Vibrato1, 1);
        staged.StageTechnique(new StagedKey(TechniqueField.Tremolo, 2), 0);

        SetVibrato(f, true);
        Assert.False(staged.Technique.ContainsKey(Vibrato1));
        Assert.True(staged.Technique.ContainsKey(new StagedKey(TechniqueField.Tremolo, 2)));

        SetVibrato(f, false);
        staged.Discard(StagedPage.Technique);
        staged.StageTechnique(Vibrato1, 1);
        SetVibrato(f, true);
        SetVibrato(f, false);

        Assert.False(staged.HasDraft(StagedPage.Technique));
        Assert.False(staged.Apply(StagedPage.Technique));
        Assert.False(f.First.ChordNotes[1].Vibrato);
    }

    /// <summary>#751 P4 review: a staged value a keyboard edit matched is parked, not lost: undoing that edit brings it
    /// back and Apply writes it.</summary>
    [Fact]
    public void UndoingTheKeyboardEditThatMatchedAStagedValue_BringsTheValueBack()
    {
        var f = new ChordLadderFixture();
        using var staged = Engine(f);
        f.Choose(ChordApplyRung.ThisChord);
        staged.StageTechnique(Vibrato1, 1);

        SetVibrato(f, true);
        Assert.False(staged.HasDraft(StagedPage.Technique));
        Assert.False(staged.HasAnyDraft);

        f.History.Undo();

        Assert.Equal(1, staged.Technique[Vibrato1]);
        Assert.True(staged.HasDraft(StagedPage.Technique));
        Assert.True(staged.Apply(StagedPage.Technique));
        Assert.True(f.First.ChordNotes[1].Vibrato);
    }

    /// <summary>#751 P4 review: the "still in the project" index behind the history re-read is built once and kept
    /// through in-place edits; a sound leaving a level rebuilds it, and the draft on that sound goes.</summary>
    [Fact]
    public void TheProjectIndex_IsKeptThroughInPlaceEdits_AndRebuiltWhenASoundLeaves()
    {
        var f = new ChordLadderFixture();
        using var staged = Engine(f);
        staged.StageTechnique(Vibrato1, 1);

        SetVibrato(f, true, f.Second);
        SetVibrato(f, false, f.Second);
        f.History.Undo();
        Assert.Equal(1, staged.SoundIndexBuilds);
        Assert.True(staged.HasAnyDraft);

        f.Arrangement.Levels[0].Chords.Remove(f.First);
        SetVibrato(f, false, f.Second); // the undo left it on

        Assert.Equal(2, staged.SoundIndexBuilds);
        Assert.False(staged.HasAnyDraft);
    }

    /// <summary>#751 P4 review: the close prompt's Save applies every held draft through the editor, the one on screen
    /// with its scope and one left on another level on its own sound, as ONE undo step, and drops them.</summary>
    [Fact]
    public void ApplyingThePendingDrafts_WritesEveryDraft_InOneUndoStep()
    {
        var f = new ChordLadderFixture();
        using var staged = Engine(f);
        var elsewhere = new GuitarNote { Position = new FractionalPosition(20), StringIndex = 1, Fret = 5 };
        f.Arrangement.Levels[1].Notes.Add(elsewhere);
        f.Selection.SelectedChords.Clear();
        f.Selection.SelectedNotes.Add(elsewhere);
        staged.StageTechnique(new StagedKey(TechniqueField.Hopo), (int)HopoType.HammerOn);
        f.Selection.SelectedNotes.Clear();
        f.Selection.SelectedChords.Add(f.First);
        f.Selection.SelectedChords.Add(f.Second);
        f.Choose(ChordApplyRung.Selected);
        staged.StageTechnique(Vibrato1, 1);
        Assert.True(f.Editor.HasPendingDrafts);

        f.Editor.ApplyPendingDrafts();

        Assert.Equal(HopoType.HammerOn, elsewhere.Hopo);
        Assert.True(f.First.ChordNotes[1].Vibrato);
        Assert.True(f.Second.ChordNotes[1].Vibrato);
        Assert.Single(f.History.GetUndoSteps());
        Assert.False(f.Editor.HasPendingDrafts);

        f.History.Undo();
        Assert.NotEqual(HopoType.HammerOn, elsewhere.Hopo);
        Assert.False(f.First.ChordNotes[1].Vibrato);
    }

    private static void SetVibrato(ChordLadderFixture f, bool on, Chord? chord = null)
    {
        chord ??= f.First;
        var clone = chord.Clone();
        clone.ChordNotes[1].Vibrato = on;
        f.History.Do(new MutateChordsAction(f.Arrangement.Levels[0].Chords, [chord], [chord.Clone()], [clone]));
    }

    internal static Chord AddChord(ChordLadderFixture f, int beat, params (int String, int Fret)[] strings)
    {
        var chord = new Chord { Position = new FractionalPosition(beat) };
        foreach (var (s, fret) in strings) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = fret });
        f.Arrangement.Levels[0].Chords.Add(chord);
        return chord;
    }

    internal static GuitarNote AddNote(ChordLadderFixture f, int beat, int fret, int? slideTo = null)
    {
        var note = new GuitarNote { Position = new FractionalPosition(beat), StringIndex = 1, Fret = fret, SlideTo = slideTo };
        f.Arrangement.Levels[0].Notes.Add(note);
        return note;
    }
}
