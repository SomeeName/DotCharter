using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     An edit that lands a chord on a shape the arrangement already has sticks. A hover-typed
///     fret run (1, then 12) whose FIRST digit matches an existing template joins that template;
///     the continuation digit undoes the join and extends the shape the run started from, so the
///     typed value ends on the chord instead of being refused with a hint.
/// </summary>
public sealed class ChordTemplateDuplicateEditTests
{
    [Fact]
    public void ASingleFretEdit_ThatMatchesAnExistingShape_JoinsItAndSticks()
    {
        var h = new Harness();
        h.AddTemplate(0, "A5", [(0, 5), (1, 7), (2, 7)], [(0, 1), (1, 3), (2, 4)]);
        var other = h.AddTemplate(1, "Other", [(0, 5), (1, 7), (2, 9)], [(0, 1), (1, 2), (2, 4)]);
        var chords = h.AddChords(0, 3, (0, 5), (1, 7), (2, 7));
        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);
        vm.ApplyRung = ChordApplyRung.ThisChord;

        vm.StringInputs[2].FretText = "9";

        Assert.Equal(2, h.Arrangement.ChordTemplates.Count);
        Assert.Equal([1, 0, 0], chords.Select(c => c.TemplateId));
        Assert.Equal(9, chords[0].ChordNotes.Single(n => n.StringIndex == 2).Fret);
        Assert.Same(other, vm.Template);
    }

    [Fact]
    public void AnEveryUseEdit_ThatMakesTheShapeEqualAnother_JoinsIt_AndTheEmptiedShapeGoesInTheSameUndoStep()
    {
        var h = new Harness();
        h.AddTemplate(0, "A5", [(0, 5), (1, 7), (2, 7)], [(0, 1), (1, 3), (2, 4)]);
        var other = h.AddTemplate(1, "Other", [(0, 5), (1, 7), (2, 9)], [(0, 1), (1, 3), (2, 4)]);
        var chords = h.AddChords(0, 3, (0, 5), (1, 7), (2, 7));
        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);
        vm.ApplyRung = ChordApplyRung.EveryUse;

        vm.StringInputs[2].FretText = "9";

        // The emptied shape is gone and the ids were renumbered, so the kept shape is id 0 now.
        Assert.Same(other, Assert.Single(h.Arrangement.ChordTemplates));
        Assert.All(chords, c => Assert.Equal(other.Id, c.TemplateId));
        Assert.All(chords, c => Assert.Equal(9, c.ChordNotes.Single(n => n.StringIndex == 2).Fret));
        Assert.Equal("Other", other.Name);
        Assert.Same(other, vm.Template);
        Assert.Single(h.History.GetUndoSteps());
        h.History.Undo();
        Assert.Equal(2, h.Arrangement.ChordTemplates.Count);
        Assert.Equal([0, 0, 0], chords.Select(c => c.TemplateId));
    }

    [Fact]
    public void TheLadder_StatesWhatTheNextShapeEditDoes_BeforeItIsMade()
    {
        var h = new Harness();
        h.AddTemplate(0, "A5", [(0, 5), (1, 7), (2, 7)], [(0, 1), (1, 3), (2, 4)]);
        var chords = h.AddChords(0, 3, (0, 5), (1, 7), (2, 7));
        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);

        vm.ApplyRung = ChordApplyRung.ThisChord;
        Assert.Equal("A new shape is made for this chord; the other 2 keep “A5”.", vm.Ladder.ResultText);

        vm.ApplyRung = ChordApplyRung.EveryUse;
        Assert.Equal("“A5” itself changes, so every use plays the new grip.", vm.Ladder.ResultText);

        // "Selected" no longer silently means either: it says which, from the selection it has.
        vm.ApplyRung = ChordApplyRung.Selected;
        Assert.Equal("A new shape is made for this chord; the other 2 keep “A5”.", vm.Ladder.ResultText);
        h.Selection.SelectedChords.Add(chords[1]);
        h.Selection.SelectedChords.Add(chords[2]);
        Assert.Equal("“A5” itself changes, so every use plays the new grip.", vm.Ladder.ResultText);
    }

    [Fact]
    public void AHoverRun_WhoseFirstDigitMatchesAnExistingShape_StillEndsOnTheTypedFret()
    {
        var h = new Harness();
        h.AddTemplate(0, "A5", [(0, 5), (1, 7), (2, 7)], [(0, 1), (1, 3), (2, 4)]);
        var other = h.AddTemplate(1, "Other", [(0, 5), (1, 7), (2, 1)], [(0, 1), (1, 2), (2, 4)]);
        var chords = h.AddChords(0, 3, (0, 5), (1, 7), (2, 7));
        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);
        vm.ApplyRung = ChordApplyRung.ThisChord;

        vm.SetHoveredFretCommand.Execute(new ChordBoxFretEdit(2, 1, ContinuesRun: false));
        Assert.Equal(1, chords[0].TemplateId);
        vm.SetHoveredFretCommand.Execute(new ChordBoxFretEdit(2, 12, ContinuesRun: true));

        Assert.False(vm.Ladder.ShowsReshapeHint, "the second digit must not be refused");
        Assert.Equal(12, chords[0].ChordNotes.Single(n => n.StringIndex == 2).Fret);
        var shape = h.Arrangement.ChordTemplates.Single(t => t.Id == chords[0].TemplateId);
        Assert.Equal(12, shape.Frets[2]);
        // The shape the first digit joined is left exactly as it was, and the siblings stay put.
        Assert.Equal(1, other.Frets[2]);
        Assert.Equal(3, h.Arrangement.ChordTemplates.Count);
        Assert.Equal([0, 0], chords.Skip(1).Select(c => c.TemplateId));
        Assert.Same(shape, vm.Template);
    }

    [Fact]
    public void AHoverRun_WhoseFirstDigitMatchesAnExistingShape_IsOneUndoNode()
    {
        var h = new Harness();
        h.AddTemplate(0, "A5", [(0, 5), (1, 7), (2, 7)], [(0, 1), (1, 3), (2, 4)]);
        h.AddTemplate(1, "Other", [(0, 5), (1, 7), (2, 1)], [(0, 1), (1, 2), (2, 4)]);
        var chords = h.AddChords(0, 3, (0, 5), (1, 7), (2, 7));
        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);
        vm.ApplyRung = ChordApplyRung.ThisChord;

        vm.SetHoveredFretCommand.Execute(new ChordBoxFretEdit(2, 1, ContinuesRun: false));
        vm.SetHoveredFretCommand.Execute(new ChordBoxFretEdit(2, 12, ContinuesRun: true));
        h.History.Undo();

        Assert.False(h.History.CanUndo);
        Assert.Equal(0, chords[0].TemplateId);
        Assert.Equal(7, chords[0].ChordNotes.Single(n => n.StringIndex == 2).Fret);
        Assert.Equal(2, h.Arrangement.ChordTemplates.Count);
    }

    [Fact]
    public void AHoverRun_WhoseFirstDigitMatchesNothing_StillWorks()
    {
        var h = new Harness();
        h.AddTemplate(0, "A5", [(0, 5), (1, 7), (2, 7)], [(0, 1), (1, 3), (2, 4)]);
        var chords = h.AddChords(0, 3, (0, 5), (1, 7), (2, 7));
        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);
        vm.ApplyRung = ChordApplyRung.ThisChord;

        vm.SetHoveredFretCommand.Execute(new ChordBoxFretEdit(2, 1, ContinuesRun: false));
        vm.SetHoveredFretCommand.Execute(new ChordBoxFretEdit(2, 12, ContinuesRun: true));

        Assert.Equal(12, chords[0].ChordNotes.Single(n => n.StringIndex == 2).Fret);
        Assert.Equal(2, h.Arrangement.ChordTemplates.Count);
        Assert.Equal([0, 0], chords.Skip(1).Select(c => c.TemplateId));
    }

    private sealed class Harness
    {
        public Harness()
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var config = new FakeConfigurationService();
            var project = new ChartProject();
            Arrangement.Levels.Add(new ChartLevel());
            project.Arrangements.Add(Arrangement);
            State.ActiveProject = project;
            State.ActiveArrangement = Arrangement;
            State.ActiveLevel = Arrangement.Levels[0];
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), State, config, FakeLocalization.KeysOnly());
            Editor = new ChartEditorViewModel(State, transport, History, Selection, new TempoService(logger),
                new TrackContextRegistry(State, Selection), FakeLocalization.KeysOnly());
        }

        public Arrangement Arrangement { get; } = new() { Name = "Lead", ArrangementType = ArrangementType.Lead };
        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public HistoryService History { get; }
        public ChartEditorViewModel Editor { get; }

        public ChordTemplate AddTemplate(int id, string name, (int String, int Fret)[] frets, (int String, int Finger)[] fingers)
        {
            var template = new ChordTemplate { Id = id, Name = name };
            foreach (var (s, f) in frets) template.Frets[s] = f;
            foreach (var (s, f) in fingers) template.Fingers[s] = f;
            Arrangement.ChordTemplates.Add(template);
            return template;
        }

        public List<Chord> AddChords(int templateId, int count, params (int String, int Fret)[] notes)
        {
            var result = new List<Chord>();
            for (var i = 0; i < count; i++)
            {
                var chord = new Chord { TemplateId = templateId, Position = new FractionalPosition(i * 4) };
                foreach (var (s, f) in notes) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f });
                Arrangement.Levels[0].Chords.Add(chord);
                result.Add(chord);
            }

            return result;
        }

        public ChordTemplateEditorViewModel CreateChordEditor() =>
            new(Editor, History, new FingeringGenerationService(), FakeLocalization.English(), new InlineDispatcher());

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
    }
}
