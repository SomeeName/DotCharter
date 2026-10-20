using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     A chord and the hand shape drawn over it are ONE authored decision, so
///     re-fingering the chord has to take the bar with it.
///     <para>
///         The failure this pins is silent and only visible in the game. A
///         fingering edit moves no fret, so it resolves through
///         <c>FindIdenticalTemplate</c> and MINTS a private template — and the
///         covering hand shape was only carried along when it happened to be in
///         the selection. Selecting a chord in the highway does NOT select the
///         bar above it (the owner's 2026-07-28 session logged
///         "chords=1, handshapes=0" over and over), so the ordinary gesture left
///         that bar pointing at the shape that used to be there, which is what
///         the 3D highway draws its fingers from.
///     </para>
/// </summary>
public sealed class HandShapeFollowsChordTemplateTests
{
    [Fact]
    public void ReFingeringAChord_TakesItsCoveringHandShapeWithIt()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Shape(0, "A5", [(1, 7), (2, 7)], [(1, 1), (2, 3)]));
        var chord = AddBoundChord(arr, 0, (1, 7), (2, 7));
        AddBoundChord(arr, 0, (1, 7), (2, 7)); // a sibling nobody selected
        var bar = AddHandShape(arr, chord.Position, chord.Position + new FractionalPosition(2), 0);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chord); // chord ONLY — the real gesture

        vm.StringInputs[2].FingerText = "4";
        vm.SaveFingerEditsCommand.Execute(null);

        Assert.NotEqual(0, chord.TemplateId); // the edit really did mint/rebind
        Assert.Equal(chord.TemplateId, bar.TemplateId);
        Assert.Equal(4, arr.ChordTemplates.Single(t => t.Id == chord.TemplateId).Fingers[2]);
    }

    [Fact]
    public void WithTheSettingOff_TheBarIsLeftOnTheOldShape()
    {
        var h = new Harness();
        h.Config.AppConfig.SyncHandShapeToChordTemplate = false;
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Shape(0, "A5", [(1, 7), (2, 7)], [(1, 1), (2, 3)]));
        var chord = AddBoundChord(arr, 0, (1, 7), (2, 7));
        AddBoundChord(arr, 0, (1, 7), (2, 7)); // a sibling nobody selected
        var bar = AddHandShape(arr, chord.Position, chord.Position + new FractionalPosition(2), 0);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chord);

        vm.StringInputs[2].FingerText = "4";
        vm.SaveFingerEditsCommand.Execute(null);

        Assert.NotEqual(0, chord.TemplateId);
        Assert.Equal(0, bar.TemplateId);
    }

    /// <summary>
    ///     A bar sharing the template but sitting ELSEWHERE on the timeline is a
    ///     different authored decision. Dragging it along would be the "one edit
    ///     flattened the whole chart" failure the panel's narrowing already exists
    ///     to prevent.
    /// </summary>
    [Fact]
    public void ABarElsewhereOnTheTimelineIsNotDraggedAlong()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Shape(0, "A5", [(1, 7), (2, 7)], [(1, 1), (2, 3)]));
        var chord = AddBoundChord(arr, 0, (1, 7), (2, 7));
        AddBoundChord(arr, 0, (1, 7), (2, 7)); // a sibling nobody selected
        var faraway = AddHandShape(arr, new FractionalPosition(80), new FractionalPosition(84), 0);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chord);

        vm.StringInputs[2].FingerText = "4";
        vm.SaveFingerEditsCommand.Execute(null);

        Assert.Equal(0, faraway.TemplateId);
    }

    /// <summary>The sync rides the SAME history node — one Ctrl+Z, not two.</summary>
    [Fact]
    public void TheChordAndItsBarMoveBackTogetherOnOneUndo()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Shape(0, "A5", [(1, 7), (2, 7)], [(1, 1), (2, 3)]));
        var chord = AddBoundChord(arr, 0, (1, 7), (2, 7));
        AddBoundChord(arr, 0, (1, 7), (2, 7)); // a sibling nobody selected
        var bar = AddHandShape(arr, chord.Position, chord.Position + new FractionalPosition(2), 0);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chord);

        vm.StringInputs[2].FingerText = "4";
        vm.SaveFingerEditsCommand.Execute(null);
        Assert.Equal(chord.TemplateId, bar.TemplateId);

        h.History.Undo();

        Assert.Equal(0, chord.TemplateId);
        Assert.Equal(0, bar.TemplateId);
    }

    // ------------------------------------------------------------- helpers

    private static ChordTemplate Shape(int id, string name,
        (int stringIndex, int fret)[] frets, (int stringIndex, int finger)[]? fingers = null)
    {
        var template = new ChordTemplate { Id = id, Name = name };
        foreach (var (stringIndex, fret) in frets) template.Frets[stringIndex] = fret;
        foreach (var (stringIndex, finger) in fingers ?? []) template.Fingers[stringIndex] = finger;
        return template;
    }

    private static Chord AddBoundChord(Arrangement arr, int templateId,
        params (int stringIndex, int fret)[] notes)
    {
        var chord = new Chord
        {
            TemplateId = templateId,
            Position = new FractionalPosition(arr.Levels[0].Chords.Count * 4)
        };
        foreach (var (stringIndex, fret) in notes)
            chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
        arr.Levels[0].Chords.Add(chord);
        return chord;
    }

    private static HandShape AddHandShape(Arrangement arr,
        FractionalPosition start, FractionalPosition end, int templateId)
    {
        var shape = new HandShape { Position = start, EndPosition = end, TemplateId = templateId };
        arr.Levels[0].HandShapes.Add(shape);
        return shape;
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public void PostNormal(Action action) => action();
        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
    }

    private sealed class Harness
    {
        public Harness()
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var project = new ChartProject();
            var arr = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            arr.Levels.Add(new ChartLevel());
            project.Arrangements.Add(arr);

            State.ActiveProject = project;
            State.ActiveArrangement = arr;
            State.ActiveLevel = arr.Levels[0];

            var transport = new TransportViewModel(Audio, State, Config, FakeLocalization.KeysOnly());
            Editor = new ChartEditorViewModel(State, transport, History, Selection,
                new TempoService(logger), new TrackContextRegistry(State, Selection), FakeLocalization.KeysOnly());
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public FakeAudioPlaybackService Audio { get; } = new();
        public FakeConfigurationService Config { get; } = new();
        public HistoryService History { get; }
        public ChartEditorViewModel Editor { get; }

        public ChordTemplateEditorViewModel CreateChordEditor() =>
            new(Editor, History, new FingeringGenerationService(), FakeLocalization.English(), new InlineDispatcher(), Config);
    }
}

/// <summary>
///     The pure companion resolver behind the sync. Span semantics get their own
///     tests because an off-by-one at the boundary makes abutting per-chord
///     shapes both claim the sound between them.
/// </summary>
public sealed class SoundCompanionsTests
{
    private static HandShape Shape(int start, int end) => new()
    {
        Position = new FractionalPosition(start),
        EndPosition = new FractionalPosition(end)
    };

    [Fact]
    public void AShapeCoveringTheSoundIsFound()
    {
        var shape = Shape(4, 8);
        Assert.Same(shape, Assert.Single(
            SoundCompanions.CoveringHandShapes([new FractionalPosition(5)], [shape])));
    }

    /// <summary>Start inclusive, end EXCLUSIVE — the rule the pattern matcher and
    /// the anchor generator already use.</summary>
    [Fact]
    public void TheEndIsExclusiveSoAbuttingShapesDoNotBothClaimTheBoundary()
    {
        var first = Shape(4, 8);
        var second = Shape(8, 12);

        Assert.Same(second, Assert.Single(
            SoundCompanions.CoveringHandShapes([new FractionalPosition(8)], [first, second])));
    }

    [Fact]
    public void AShapeAlreadyInTheSelectionIsNotReturnedTwice()
    {
        var shape = Shape(4, 8);
        Assert.Empty(SoundCompanions.CoveringHandShapes(
            [new FractionalPosition(5)], [shape], new List<HandShape> { shape }));
    }

    [Fact]
    public void OneShapeCoveringSeveralSoundsIsReturnedOnce()
    {
        var shape = Shape(4, 12);
        Assert.Same(shape, Assert.Single(SoundCompanions.CoveringHandShapes(
            [new FractionalPosition(5), new FractionalPosition(6), new FractionalPosition(7)], [shape])));
    }

    [Fact]
    public void NoPositionsMeansNoCompanions() => Assert.Empty(SoundCompanions.CoveringHandShapes([], [Shape(4, 8)]));

    [Fact]
    public void TheGoverningAnchorIsTheLastOneAtOrBeforeTheSound()
    {
        var early = new FretAnchor { Position = new FractionalPosition(0), Fret = 1 };
        var governing = new FretAnchor { Position = new FractionalPosition(4), Fret = 5 };
        var later = new FretAnchor { Position = new FractionalPosition(9), Fret = 7 };

        Assert.Same(governing,
            SoundCompanions.GoverningAnchor(new FractionalPosition(6), [early, governing, later]));
    }

    [Fact]
    public void AnAnchorExactlyOnTheSoundGovernsIt()
    {
        var onIt = new FretAnchor { Position = new FractionalPosition(4), Fret = 5 };
        Assert.Same(onIt, SoundCompanions.GoverningAnchor(new FractionalPosition(4), [onIt]));
    }

    [Fact]
    public void ASoundBeforeEveryAnchorHasNoGoverningAnchor()
    {
        var later = new FretAnchor { Position = new FractionalPosition(9), Fret = 7 };
        Assert.Null(SoundCompanions.GoverningAnchor(new FractionalPosition(4), [later]));
    }
}

/// <summary>
///     The FHP half of the same idea, behind
///     <see cref="AppConfig.SyncAnchorToChordFingering" /> — OFF by default.
///     <para>
///         Rocksmith derives the playing finger as
///         <c>1 + (fret − anchor.fret) − (anchor.width − 4)</c>, so an anchor that
///         disagrees with the authored index finger makes the game show a
///         different fingering than the chart states. That is inspector rule E04.
///     </para>
///     <para>
///         It is opt-in while the hand-shape sync is not, deliberately: a hand
///         shape describes the same decision as its chord, whereas an anchor says
///         where the HAND goes and is usually shared with neighbouring sounds the
///         edit never touched.
///     </para>
/// </summary>
public sealed class AnchorFollowsFingeringTests
{
    private static (Harness h, Chord chord, FretAnchor anchor, ChordTemplateEditorViewModel vm) Setup(
        bool enabled, int anchorFret = 12, int capo = 0)
    {
        var h = new Harness();
        h.Config.AppConfig.SyncAnchorToChordFingering = enabled;
        var arr = h.Editor.ActiveArrangement!;
        arr.Capo = capo;

        // Index finger authored on fret 7; the anchor says 12 — E04's exact case.
        arr.ChordTemplates.Add(ShapeOf(0, "A5", [(1, 7), (2, 9)], [(1, 1), (2, 3)]));
        var chord = AddChord(arr, 0, (1, 7), (2, 9));
        AddChord(arr, 0, (1, 7), (2, 9)); // unselected sibling forces the mint path

        var anchor = new FretAnchor { Position = new FractionalPosition(0), Fret = anchorFret, Width = 4 };
        arr.Levels[0].Anchors.Add(anchor);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chord);
        return (h, chord, anchor, vm);
    }

    private static ChordTemplate ShapeOf(int id, string name,
        (int stringIndex, int fret)[] frets, (int stringIndex, int finger)[] fingers)
    {
        var t = new ChordTemplate { Id = id, Name = name };
        foreach (var (s, f) in frets) t.Frets[s] = f;
        foreach (var (s, f) in fingers) t.Fingers[s] = f;
        return t;
    }

    private static Chord AddChord(Arrangement arr, int templateId, params (int stringIndex, int fret)[] notes)
    {
        var chord = new Chord
        {
            TemplateId = templateId,
            Position = new FractionalPosition(arr.Levels[0].Chords.Count * 4)
        };
        foreach (var (s, f) in notes) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f });
        arr.Levels[0].Chords.Add(chord);
        return chord;
    }

    [Fact]
    public void WithTheSettingOn_TheAnchorMovesToTheIndexFingersFret()
    {
        var (_, _, anchor, vm) = Setup(enabled: true);

        vm.StringInputs[2].FingerText = "1";
        vm.SaveFingerEditsCommand.Execute(null);

        Assert.Equal(7, anchor.Fret);
    }

    [Fact]
    public void OffByDefault_TheAnchorIsLeftAlone()
    {
        var (_, _, anchor, vm) = Setup(enabled: false);

        vm.StringInputs[2].FingerText = "1";
        vm.SaveFingerEditsCommand.Execute(null);

        Assert.Equal(12, anchor.Fret);
    }

    /// <summary>
    ///     One undo node. Two would let a Ctrl+Z revert the fingering and strand
    ///     the anchor — the exact disagreement this is meant to remove.
    /// </summary>
    [Fact]
    public void TheAnchorMoveRidesTheSameUndoNode()
    {
        var (h, _, anchor, vm) = Setup(enabled: true);

        vm.StringInputs[2].FingerText = "1";
        vm.SaveFingerEditsCommand.Execute(null);
        Assert.Equal(7, anchor.Fret);

        h.History.Undo();
        Assert.Equal(12, anchor.Fret);
    }

    /// <summary>
    ///     #452: with no finger 1 the corrector reads the INFERRED index, the same
    ///     fret rule E04 reports — finger 2 on fret 7 puts the index on 6.
    /// </summary>
    [Fact]
    public void WithTheSettingOn_AnInferredIndexMovesTheAnchorToo()
    {
        var (_, _, anchor, vm) = Setup(enabled: true);

        vm.StringInputs[1].FingerText = "2";
        vm.SaveFingerEditsCommand.Execute(null);

        Assert.Equal(6, anchor.Fret);
    }

    /// <summary>An anchor that already agrees is not rewritten.</summary>
    [Fact]
    public void AnAgreeingAnchorIsNotTouched()
    {
        var (h, _, anchor, vm) = Setup(enabled: true, anchorFret: 7);

        vm.StringInputs[2].FingerText = "1";
        vm.SaveFingerEditsCommand.Execute(null);

        Assert.Equal(7, anchor.Fret);
        // Undo returns the fingering; the anchor never moved, so it stays put.
        h.History.Undo();
        Assert.Equal(7, anchor.Fret);
    }

    /// <summary>
    ///     #618: a RESHAPE takes the fingering of whichever template already has the new frets. Two
    ///     do here — one fingered 2-4 (index on 6) and one 1-4 that the arrangement plays under this
    ///     FHP 7. With the sync on the reshape lands on the grip the anchor agrees with, and the
    ///     anchor stays: the arrangement says it is right.
    /// </summary>
    [Fact]
    public void WithTheSettingOn_AReshapePrefersTheGripTheAnchorAgreesWith()
    {
        var (h, chord, anchor, _) = SetupReshape(enabled: true);

        h.Vm.StringInputs[2].FretText = "10";

        Assert.Equal(6, chord.TemplateId);
        Assert.Equal(7, anchor.Fret);
    }

    /// <summary>No agreeing grip played under this anchor: the first one with the frets, and the
    ///     corrector moves the anchor to it as before.</summary>
    [Fact]
    public void WithNoAgreeingGripPlayedHere_AReshapeKeepsTheFirstGrip_AndTheAnchorMoves()
    {
        var (h, chord, anchor, evidence) = SetupReshape(enabled: true);
        h.Harness.Editor.ActiveArrangement!.Levels[0].Chords.Remove(evidence);

        h.Vm.StringInputs[2].FretText = "10";

        Assert.Equal(5, chord.TemplateId);
        Assert.Equal(6, anchor.Fret);
    }

    [Fact]
    public void OffByDefault_AReshapeKeepsTheFirstGrip()
    {
        var (h, chord, anchor, _) = SetupReshape(enabled: false);

        h.Vm.StringInputs[2].FretText = "10";

        Assert.Equal(5, chord.TemplateId);
        Assert.Equal(7, anchor.Fret);
    }

    private static ((Harness Harness, ChordTemplateEditorViewModel Vm) h, Chord chord, FretAnchor anchor, Chord evidence)
        SetupReshape(bool enabled)
    {
        var (h, chord, anchor, vm) = Setup(enabled, anchorFret: 7);
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(ShapeOf(5, "", [(1, 7), (2, 10)], [(1, 2), (2, 4)]));
        arr.ChordTemplates.Add(ShapeOf(6, "", [(1, 7), (2, 10)], [(1, 1), (2, 4)]));
        var evidence = AddChord(arr, 6, (1, 7), (2, 10));
        return ((h, vm), chord, anchor, evidence);
    }

    private sealed class Harness
    {
        public Harness()
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var project = new ChartProject();
            var arr = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            arr.Levels.Add(new ChartLevel());
            project.Arrangements.Add(arr);

            State.ActiveProject = project;
            State.ActiveArrangement = arr;
            State.ActiveLevel = arr.Levels[0];

            var transport = new TransportViewModel(Audio, State, Config, FakeLocalization.KeysOnly());
            Editor = new ChartEditorViewModel(State, transport, History, Selection,
                new TempoService(logger), new TrackContextRegistry(State, Selection), FakeLocalization.KeysOnly());
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public FakeAudioPlaybackService Audio { get; } = new();
        public FakeConfigurationService Config { get; } = new();
        public HistoryService History { get; }
        public ChartEditorViewModel Editor { get; }

        public ChordTemplateEditorViewModel CreateChordEditor() =>
            new(Editor, History, new FingeringGenerationService(), FakeLocalization.English(), new Dispatcher(), Config);
    }

    private sealed class Dispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public void PostNormal(Action action) => action();
        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
    }
}

/// <summary>
///     The owner's request, verbatim: "some of the edited chords are also very
///     similar, only all moved 1 fret up or down, so there must be also some easy
///     way to apply the same fingering to those". It was the "Finger N alike
///     shapes" button; since #484 it is Save on the "+ the same grip elsewhere"
///     rung, which works with nothing typed when a grip is fingered differently.
/// </summary>
public sealed class SameGripFingeringTests
{
    private static void FingerAlike(ChordTemplateEditorViewModel vm)
    {
        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.SameGrip);
        vm.SaveFingerEditsCommand.Execute(null);
    }

    private static ChordTemplate Shape(int id, (int s, int f)[] frets, (int s, int f)[]? fingers = null)
    {
        var t = new ChordTemplate { Id = id };
        foreach (var (s, f) in frets) t.Frets[s] = f;
        foreach (var (s, f) in fingers ?? []) t.Fingers[s] = f;
        return t;
    }

    private static (H h, ChordTemplateEditorViewModel vm, ChordTemplate src, ChordTemplate moved) Setup()
    {
        var h = new H();
        var arr = h.Editor.ActiveArrangement!;
        var src = Shape(0, [(1, 5), (2, 7)], [(1, 1), (2, 3)]);
        var moved = Shape(1, [(1, 8), (2, 10)], [(1, 2), (2, 4)]); // guessed wrong
        arr.ChordTemplates.Add(src);
        arr.ChordTemplates.Add(moved);

        var vm = h.CreateChordEditor();
        vm.LoadTemplate(src, 6);
        return (h, vm, src, moved);
    }

    [Fact]
    public void TheFingeringLandsOnTheSameGripElsewhereOnTheNeck()
    {
        var (_, vm, _, moved) = Setup();

        FingerAlike(vm);

        Assert.Equal(1, moved.Fingers[1]);
        Assert.Equal(3, moved.Fingers[2]);
    }

    /// <summary>It OVERWRITES a guessed fingering — that is the whole point.</summary>
    [Fact]
    public void AnExistingFingeringIsReplaced()
    {
        var (_, vm, _, moved) = Setup();
        Assert.Equal(2, moved.Fingers[1]); // the suggester's guess, before

        FingerAlike(vm);

        Assert.NotEqual(2, moved.Fingers[1]);
    }

    [Fact]
    public void TheWholePropagationIsOneUndoStep()
    {
        var h = new H();
        var arr = h.Editor.ActiveArrangement!;
        var src = Shape(0, [(1, 5), (2, 7)], [(1, 1), (2, 3)]);
        arr.ChordTemplates.Add(src);
        var moved = new List<ChordTemplate>();
        for (var i = 1; i <= 4; i++)
        {
            var t = Shape(i, [(1, 5 + i), (2, 7 + i)], [(1, 4), (2, 4)]);
            arr.ChordTemplates.Add(t);
            moved.Add(t);
        }

        var vm = h.CreateChordEditor();
        vm.LoadTemplate(src, 6);
        FingerAlike(vm);

        Assert.All(moved, t => Assert.Equal(1, t.Fingers[1]));
        Assert.Single(h.History.GetUndoSteps());

        h.History.Undo();
        Assert.All(moved, t => Assert.Equal(4, t.Fingers[1]));
    }

    /// <summary>A shape that is not the same grip is left alone.</summary>
    [Fact]
    public void AnUnrelatedShapeIsNotRefingered()
    {
        var h = new H();
        var arr = h.Editor.ActiveArrangement!;
        var src = Shape(0, [(1, 5), (2, 7)], [(1, 1), (2, 3)]);
        var unrelated = Shape(1, [(1, 6), (2, 10)], [(1, 2), (2, 4)]);
        arr.ChordTemplates.Add(src);
        arr.ChordTemplates.Add(unrelated);

        var vm = h.CreateChordEditor();
        vm.LoadTemplate(src, 6);
        FingerAlike(vm);

        Assert.Equal(2, unrelated.Fingers[1]);
    }

    /// <summary>The rung counts the alike shapes, and is disabled when the shape stands alone.</summary>
    [Fact]
    public void TheRungCountsTheAlikeShapesAndIsDisabledWhenAlone()
    {
        var (_, vm, _, _) = Setup();
        Assert.Equal(1, vm.Ladder.SameGrip.Reach.GripTemplates);
        Assert.True(vm.Ladder.SameGrip.IsAvailable);

        var h2 = new H();
        var lone = Shape(0, [(1, 5), (2, 7)], [(1, 1)]);
        h2.Editor.ActiveArrangement!.ChordTemplates.Add(lone);
        var vm2 = h2.CreateChordEditor();
        vm2.LoadTemplate(lone, 6);
        Assert.Equal(0, vm2.Ladder.SameGrip.Reach.GripTemplates);
        Assert.False(vm2.Ladder.SameGrip.IsAvailable);
    }

    /// <summary>Nothing to copy: an unfingered source is a no-op, not a wipe.</summary>
    [Fact]
    public void AnUnfingeredSourceDoesNothing()
    {
        var h = new H();
        var arr = h.Editor.ActiveArrangement!;
        var src = Shape(0, [(1, 5), (2, 7)]);
        var moved = Shape(1, [(1, 8), (2, 10)], [(1, 2), (2, 4)]);
        arr.ChordTemplates.Add(src);
        arr.ChordTemplates.Add(moved);

        var vm = h.CreateChordEditor();
        vm.LoadTemplate(src, 6);
        FingerAlike(vm);

        Assert.Equal(2, moved.Fingers[1]);
        Assert.False(h.History.CanUndo);
    }

    private sealed class H
    {
        public H()
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var project = new ChartProject();
            var arr = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            arr.Levels.Add(new ChartLevel());
            project.Arrangements.Add(arr);
            State.ActiveProject = project;
            State.ActiveArrangement = arr;
            State.ActiveLevel = arr.Levels[0];

            var transport = new TransportViewModel(Audio, State, Config, FakeLocalization.KeysOnly());
            Editor = new ChartEditorViewModel(State, transport, History, Selection,
                new TempoService(logger), new TrackContextRegistry(State, Selection), FakeLocalization.KeysOnly());
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public FakeAudioPlaybackService Audio { get; } = new();
        public FakeConfigurationService Config { get; } = new();
        public HistoryService History { get; }
        public ChartEditorViewModel Editor { get; }

        public ChordTemplateEditorViewModel CreateChordEditor() =>
            new(Editor, History, new FingeringGenerationService(), FakeLocalization.English(), new D(), Config);
    }

    private sealed class D : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public void PostNormal(Action action) => action();
        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
    }
}
