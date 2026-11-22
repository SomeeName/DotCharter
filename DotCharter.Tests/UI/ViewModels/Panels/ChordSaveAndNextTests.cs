using System.Windows.Input;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Panels;

public sealed class ChordSaveAndNextTests
{
    [Theory]
    [InlineData(ChordApplyRung.Selected, false)]
    [InlineData(ChordApplyRung.EveryUse, true)]
    [InlineData(ChordApplyRung.SameGrip, true)]
    public void SaveAndNext_CommitsOnTheChosenRung_AsOneUndoNode_ThenBrowses(ChordApplyRung rung, bool allUses)
    {
        var h = new Harness();
        h.Selection.SelectedChords.Add(h.First);
        var vm = h.CreateEditor();
        vm.ApplyRung = rung;
        vm.StringInputs[0].FingerText = "1";
        vm.StringInputs[1].FingerText = "3";

        SaveAndNext(vm).Execute(null);

        Assert.Equal(1, h.Arrangement.ChordTemplates.Single(t => t.Id == h.First.TemplateId).Fingers[0]);
        Assert.Equal(allUses ? 1 : -1, h.Original.Fingers.GetValueOrDefault(0, -1));
        Assert.Equal(rung == ChordApplyRung.SameGrip ? 1 : -1, h.SameGrip.Fingers.GetValueOrDefault(0, -1));
        Assert.Same(h.Next, vm.Template);
        Assert.False(vm.HasPendingFingerEdits);
        h.History.Undo();
        Assert.Empty(h.Original.Fingers);
        Assert.Empty(h.SameGrip.Fingers);
        Assert.Equal(0, h.First.TemplateId);
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void SaveAndNext_WithNoDraft_DoesNotAdvance()
    {
        var h = new Harness();
        var vm = h.CreateEditor();
        var command = SaveAndNext(vm);
        Assert.False(command.CanExecute(null));
        command.Execute(null);
        Assert.Same(h.Original, vm.Template);
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void SaveAndNext_WhenCommitIsRefused_KeepsTheShapeAndDraft()
    {
        var h = new Harness();
        var vm = h.CreateEditor();
        vm.StringInputs[0].FingerText = "1";
        ((IChordEditSurface)vm).IsUpdatingUi = true;

        SaveAndNext(vm).Execute(null);

        Assert.Same(h.Original, vm.Template);
        Assert.True(vm.HasPendingFingerEdits);
        Assert.Empty(h.Original.Fingers);
        Assert.False(h.History.CanUndo);
    }

    private static ICommand SaveAndNext(ChordTemplateEditorViewModel vm)
    {
        var property = vm.GetType().GetProperty("SaveAndNextFingeringCommand");
        Assert.NotNull(property);
        return Assert.IsAssignableFrom<ICommand>(property.GetValue(vm));
    }

    private sealed class Harness
    {
        public readonly SelectionService Selection = new();
        public readonly HistoryService History = new(new FakeAppLogger());
        public readonly Arrangement Arrangement = new() { ArrangementType = ArrangementType.Lead };
        public readonly ChordTemplate Original = new() { Id = 0, Frets = new() { [0] = 5, [1] = 7 } };
        public readonly ChordTemplate Next = new() { Id = 1, Frets = new() { [0] = 7, [1] = 10 } };
        public readonly ChordTemplate SameGrip = new() { Id = 2, Frets = new() { [0] = 5, [1] = 7 } };
        public readonly Chord First = new() { TemplateId = 0, ChordNotes = [new() { StringIndex = 0, Fret = 5 }, new() { StringIndex = 1, Fret = 7 }] };

        public ChordTemplateEditorViewModel CreateEditor()
        {
            foreach (var template in new[] { Original, Next, SameGrip }) Arrangement.ChordTemplates.Add(template);
            var level = new ChartLevel();
            level.Chords.Add(First);
            level.Chords.Add(new Chord { TemplateId = 0, Position = new FractionalPosition(4), ChordNotes = [new() { StringIndex = 0, Fret = 5 }, new() { StringIndex = 1, Fret = 7 }] });
            Arrangement.Levels.Add(level);
            var project = new ChartProject();
            project.Arrangements.Add(Arrangement);
            var state = new ProjectState { ActiveProject = project, ActiveArrangement = Arrangement, ActiveLevel = level };
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), state, new FakeConfigurationService(), FakeLocalization.KeysOnly());
            var editor = new ChartEditorViewModel(state, transport, History, Selection, new TempoService(new FakeAppLogger()),
                new TrackContextRegistry(state, Selection), FakeLocalization.KeysOnly());
            return new ChordTemplateEditorViewModel(editor, History, new FingeringGenerationService(), FakeLocalization.KeysOnly(), new InlineDispatcher());
        }
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
}
