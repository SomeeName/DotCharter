using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     #751 bug (b): a fret or finger edit to a grip whose hand shapes hold ghost strings reached only the FIRST
///     selected bar, whatever the Apply-to rung said. The ladder must mean the same with or without ghosts: This
///     chord = the bar, Selected = the selected bars, Every use = every bar AND chord on the grip, Same grip = that plus
///     the grip moved along the neck (fingering only). Each bar keeps its OWN opening ghost mask, nothing else moves,
///     and one undo puts everything back.
/// </summary>
public sealed class GhostedGripApplyToTests
{
    private static readonly int Two = ChordTemplate.GhostBit(2);
    private static readonly int Four = ChordTemplate.GhostBit(4);

    [Theory]
    [InlineData(ChordApplyRung.ThisChord, false)]
    [InlineData(ChordApplyRung.Selected, false)]
    [InlineData(ChordApplyRung.EveryUse, false)]
    [InlineData(ChordApplyRung.SameGrip, false)]
    [InlineData(ChordApplyRung.ThisChord, true)]
    [InlineData(ChordApplyRung.Selected, true)]
    [InlineData(ChordApplyRung.EveryUse, true)]
    [InlineData(ChordApplyRung.SameGrip, true)]
    public void AGhostedEdit_ReachesTheRung_EachBarKeepsItsMask_InOneUndoStep(ChordApplyRung rung, bool fret)
    {
        var h = new Harness();
        var w = h.World();
        var vm = h.CreateChordEditor();
        h.Selection.SelectedHandShapes.Add(w.First);
        h.Selection.SelectedHandShapes.Add(w.Second);
        vm.ApplyRung = rung;
        var before = h.Snapshot();
        var node = h.History.CurrentNodeId;

        if (fret)
        {
            vm.StringInputs.Single(i => i.StringIndex == 4).FretText = "6";
        }
        else
        {
            vm.StringInputs.Single(i => i.StringIndex == 3).FingerText = "2";
            vm.SaveFingerEditsCommand.Execute(null);
        }

        var arr = h.Editor.ActiveArrangement!;
        var reached = rung switch
        {
            ChordApplyRung.ThisChord => new[] { w.First },
            ChordApplyRung.Selected => [w.First, w.Second],
            _ => [w.First, w.Second, w.Far]
        };
        foreach (var (bar, mask) in new[] { (w.First, Two), (w.Second, Four), (w.Far, 0) })
        {
            Assert.Equal(mask, bar.GhostMask);
            var now = arr.FindChordTemplate(bar.TemplateId)!;
            Assert.Equal(reached.Contains(bar), fret ? now.GetFret(4) == 6 : now.GetFinger(3) == 2);
        }

        // Every use and Same grip move the chord on the grip with its bars (it holds no ghosts).
        var chordGrip = arr.FindChordTemplate(w.Chord.TemplateId)!;
        var chordFollows = rung is ChordApplyRung.EveryUse or ChordApplyRung.SameGrip;
        Assert.Equal(chordFollows, fret ? w.Chord.ChordNotes.Single(n => n.StringIndex == 4).Fret == 6 : chordGrip.GetFinger(3) == 2);
        Assert.Equal(0, chordGrip.GhostMask);
        // Same grip carries a fingering to the moved grip in place; its bar keeps its mask.
        var moved = arr.FindChordTemplate(w.Moved.TemplateId)!;
        Assert.Equal(rung == ChordApplyRung.SameGrip && !fret, moved.GetFinger(3) == 2);
        Assert.Equal(Two | Four, w.Moved.GhostMask);

        Assert.NotEqual(node, h.History.CurrentNodeId);
        h.History.Undo();
        Assert.Equal(node, h.History.CurrentNodeId);
        Assert.Equal(before, h.Snapshot());
    }

    /// <summary>
    ///     #751 carry-over: a grip edit that frets a string the grip did not hold reaches the chords on the grip
    ///     (Every use, Same grip) but never adds a note to them: each keeps the strings it sounded, on a grip that
    ///     describes exactly those strings, and one undo puts it back.
    /// </summary>
    [Theory]
    [InlineData(ChordApplyRung.EveryUse)]
    [InlineData(ChordApplyRung.SameGrip)]
    public void AGripEditThatFretsANewString_NeverAddsANoteToTheChordsOnTheGrip(ChordApplyRung rung)
    {
        var h = new Harness();
        var w = h.World();
        var vm = h.CreateChordEditor();
        h.Selection.SelectedHandShapes.Add(w.First);
        vm.ApplyRung = rung;
        var before = h.Snapshot();

        vm.StringInputs.Single(i => i.StringIndex == 1).FretText = "5";

        var arr = h.Editor.ActiveArrangement!;
        Assert.Equal(5, arr.FindChordTemplate(w.First.TemplateId)!.GetFret(1));
        Assert.Equal([2, 3, 4], w.Chord.ChordNotes.Select(n => n.StringIndex).Order());
        var chordGrip = arr.FindChordTemplate(w.Chord.TemplateId)!;
        Assert.True(chordGrip.DescribesChordNotes(w.Chord.ChordNotes), "the chord's grip names a string it does not sound");
        h.History.Undo();
        Assert.Equal(before, h.Snapshot());
    }

    private sealed class Harness
    {
        public Harness()
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var tempo = new TempoService(logger);
            var config = new FakeConfigurationService();
            var project = new ChartProject();
            var arr = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            arr.Levels.Add(new ChartLevel());
            project.Arrangements.Add(arr);
            State.ActiveProject = project;
            State.ActiveArrangement = arr;
            State.ActiveLevel = arr.Levels[0];
            var transport = new TransportViewModel(Audio, State, config, FakeLocalization.KeysOnly());
            var tracks = new TrackContextRegistry(State, Selection);
            Editor = new ChartEditorViewModel(State, transport, History, Selection, tempo, tracks, FakeLocalization.KeysOnly());
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public FakeAudioPlaybackService Audio { get; } = new();
        public HistoryService History { get; }
        public ChartEditorViewModel Editor { get; }

        public sealed record Uses(HandShape First, HandShape Second, HandShape Far, HandShape Moved, Chord Chord);

        // One grip (x x 5 5 7 x) used by three bars, each with its own opening ghosts (string 2, string 4, none),
        // and by a chord; the same grip two frets up (x x 7 7 9 x) under a fourth bar ghosting strings 2 and 4.
        public Uses World()
        {
            var arr = Editor.ActiveArrangement!;
            var grip = new ChordTemplate
            {
                Id = 0, Name = "grip",
                Frets = new Dictionary<int, int> { [2] = 5, [3] = 5, [4] = 7 },
                Fingers = new Dictionary<int, int> { [2] = 1, [3] = 1, [4] = 3 }
            };
            var moved = new ChordTemplate
            {
                Id = 1, Name = "grip",
                Frets = new Dictionary<int, int> { [2] = 7, [3] = 7, [4] = 9 },
                Fingers = new Dictionary<int, int> { [2] = 1, [3] = 1, [4] = 3 }
            };
            arr.ChordTemplates.Add(grip);
            arr.ChordTemplates.Add(moved);
            var level = arr.Levels[0];
            var first = new HandShape { TemplateId = 0, GhostMask = Two, Position = new(0), EndPosition = new(2) };
            var second = new HandShape { TemplateId = 0, GhostMask = Four, Position = new(4), EndPosition = new(6) };
            var far = new HandShape { TemplateId = 0, GhostMask = 0, Position = new(8), EndPosition = new(10) };
            var up = new HandShape { TemplateId = 1, GhostMask = Two | Four, Position = new(12), EndPosition = new(14) };
            foreach (var bar in new[] { first, second, far, up }) level.HandShapes.Add(bar);
            var chord = new Chord { TemplateId = 0, Position = new(16) };
            foreach (var (s, f) in grip.Frets) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f });
            level.Chords.Add(chord);
            return new Uses(first, second, far, up, chord);
        }

        public string Snapshot()
        {
            var arr = Editor.ActiveArrangement!;
            var level = arr.Levels[0];
            return string.Join(";", level.HandShapes.Select(b => $"{b.TemplateId}/{b.GhostMask}")
                .Concat(level.Chords.Select(c => $"{c.TemplateId}:{string.Join(',', c.ChordNotes.Select(n => $"{n.StringIndex}={n.Fret}"))}"))
                .Concat(arr.ChordTemplates.Select(t => $"{t.Id}:{string.Join(',', t.Frets.OrderBy(f => f.Key))}|{string.Join(',', t.Fingers.OrderBy(f => f.Key))}|{t.GhostMask}")));
        }

        public ChordTemplateEditorViewModel CreateChordEditor() =>
            new(Editor, History, new FingeringGenerationService(), FakeLocalization.English(), new InlineDispatcher());

        private sealed class InlineDispatcher : IUiDispatcher
        {
            public bool CheckAccess() => true;
            public void Post(Action action) => action();
            public void PostNormal(Action action) => action();
            public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
            public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
            public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
        }
    }
}
