using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     The chord panel's pin box (#795): a chord's own grip can be unpinned where it was pinned
///     (a manual fret/finger edit), not only from the hand-shape card. In place, one undo node;
///     locked on while the grip holds ghost gems and off for a display-only seed.
/// </summary>
public sealed class ChordTemplatePinToggleTests
{
    [Fact]
    public void TheBox_ShowsThePin_AndUnticking_UnpinsInPlace_AsOneUndoNode()
    {
        var h = new Harness();
        var grip = h.AddTemplate();
        grip.Pinned = true;
        var chord = h.AddChord(grip);
        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chord);

        Assert.Same(grip, vm.Template);
        Assert.True(vm.IsPinned);
        Assert.True(vm.CanTogglePin);

        vm.IsPinned = false;

        Assert.False(grip.Pinned);
        Assert.Same(grip, h.Arrangement.ChordTemplates.Single());
        Assert.Equal(grip.Id, chord.TemplateId);
        Assert.Single(h.History.GetUndoSteps());

        h.History.Undo();

        Assert.True(grip.Pinned);
        Assert.True(vm.IsPinned);
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void Ticking_PinsAnUnpinnedGrip()
    {
        var h = new Harness();
        var grip = h.AddTemplate();
        h.Selection.SelectedChords.Add(h.AddChord(grip));
        var vm = h.CreateChordEditor();
        Assert.False(vm.IsPinned);

        vm.IsPinned = true;

        Assert.True(grip.Pinned);
        Assert.Single(h.History.GetUndoSteps());
    }

    [Fact]
    public void AGripWithGhostGems_IsLockedPinned_AndUntickingIsPutBack()
    {
        var h = new Harness();
        var grip = h.AddTemplate();
        grip.Frets[3] = 7;
        grip.GhostMask = ChordTemplate.GhostBit(3);
        var shape = new HandShape { Position = new FractionalPosition(0), EndPosition = new FractionalPosition(4), TemplateId = grip.Id };
        h.Arrangement.Levels[0].HandShapes.Add(shape);
        var vm = h.CreateChordEditor();
        h.Selection.SelectedHandShapes.Add(shape);

        Assert.Same(grip, vm.Template);

        Assert.True(vm.IsPinned);
        Assert.False(vm.CanTogglePin);

        vm.IsPinned = false;

        Assert.True(vm.IsPinned);
        Assert.False(grip.Pinned);
        Assert.False(h.History.CanUndo);
    }

    private sealed class Harness
    {
        public Harness()
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var project = new ChartProject();
            Arrangement.Levels.Add(new ChartLevel());
            project.Arrangements.Add(Arrangement);
            State.ActiveProject = project;
            State.ActiveArrangement = Arrangement;
            State.ActiveLevel = Arrangement.Levels[0];
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), State, new FakeConfigurationService(),
                FakeLocalization.KeysOnly());
            Editor = new ChartEditorViewModel(State, transport, History, Selection, new TempoService(logger),
                new TrackContextRegistry(State, Selection), FakeLocalization.KeysOnly());
        }

        public Arrangement Arrangement { get; } = new() { Name = "Lead", ArrangementType = ArrangementType.Lead };
        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public HistoryService History { get; }
        public ChartEditorViewModel Editor { get; }

        public ChordTemplate AddTemplate()
        {
            var template = new ChordTemplate { Id = 0, Name = "A5", Frets = { [0] = 5, [1] = 7, [2] = 7 } };
            Arrangement.ChordTemplates.Add(template);
            return template;
        }

        public Chord AddChord(ChordTemplate template)
        {
            var chord = new Chord { TemplateId = template.Id, Position = new FractionalPosition(0) };
            foreach (var (stringIndex, fret) in template.Frets)
            {
                if (!template.IsGhost(stringIndex)) chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
            }

            Arrangement.Levels[0].Chords.Add(chord);
            return chord;
        }

        public ChordTemplateEditorViewModel CreateChordEditor() =>
            new(Editor, History, new FingeringGenerationService(), FakeLocalization.English(), new InlineDispatcher(), projectState: State);

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
