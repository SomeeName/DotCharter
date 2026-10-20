using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     A history move can ADD or REMOVE arrangement templates
///     (<c>CreateAndSwitchTemplateAction</c> does both), and the panel's
///     <c>HistoryChanged</c> handler only re-read the ROWS. So undoing the mint
///     that an isolated reshape had just made left the panel holding — and the
///     dropdown offering — a template the arrangement no longer contains.
/// </summary>
public sealed class ChordTemplateHistoryRefreshTests
{
    [Fact]
    public void UndoingAMintedTemplate_DropsItFromThePickerAndTheLoadedShape()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0, Name = "Am",
            Frets = new Dictionary<int, int> { { 4, 5 }, { 5, 5 } }
        });

        var edited = BoundChord(0, (4, 5), (5, 5));
        var sibling = BoundChord(0, (4, 5), (5, 5));
        sibling.Position = new FractionalPosition(8);
        arr.Levels[0].Chords.Add(edited);
        arr.Levels[0].Chords.Add(sibling);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(edited);

        // The sibling makes this an ISOLATED edit, which mints a template.
        vm.StringInputs[5].FretText = "7";
        Assert.Equal(2, arr.ChordTemplates.Count);
        var minted = arr.ChordTemplates[1];
        Assert.Equal(2, vm.AvailableTemplates.Count);

        h.History.Undo();

        Assert.Single(arr.ChordTemplates);
        // The picker no longer offers a shape the chart has not got…
        Assert.Single(vm.AvailableTemplates);
        Assert.DoesNotContain(vm.AvailableTemplates, i => ReferenceEquals(i.Template, minted));
        // …and the panel is holding one the arrangement really contains, so an
        // edit from here cannot hand a chord a dangling TemplateId.
        Assert.NotNull(vm.Template);
        Assert.Contains(arr.ChordTemplates, t => ReferenceEquals(t, vm.Template));
    }

    /// <summary>Redo puts it back, so the picker has to grow again too.</summary>
    [Fact]
    public void RedoingTheMint_PutsItBackInThePicker()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0, Name = "Am",
            Frets = new Dictionary<int, int> { { 4, 5 }, { 5, 5 } }
        });

        var edited = BoundChord(0, (4, 5), (5, 5));
        var sibling = BoundChord(0, (4, 5), (5, 5));
        sibling.Position = new FractionalPosition(8);
        arr.Levels[0].Chords.Add(edited);
        arr.Levels[0].Chords.Add(sibling);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(edited);
        vm.StringInputs[5].FretText = "7";

        h.History.Undo();
        h.History.Redo();

        Assert.Equal(2, arr.ChordTemplates.Count);
        Assert.Equal(2, vm.AvailableTemplates.Count);
        Assert.Contains(arr.ChordTemplates, t => ReferenceEquals(t, vm.Template));
    }

    /// <summary>An ordinary in-place undo still reloads the rows — the refresh
    /// must not have replaced that.</summary>
    [Fact]
    public void UndoingAnInPlaceEdit_ReloadsTheRowsFromTheRestoredShape()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        var template = new ChordTemplate
        {
            Id = 0, Name = "Am",
            Frets = new Dictionary<int, int> { { 4, 5 }, { 5, 5 } }
        };
        arr.ChordTemplates.Add(template);
        var only = BoundChord(0, (4, 5), (5, 5));
        arr.Levels[0].Chords.Add(only);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(only);

        vm.StringInputs[5].FretText = "7";
        Assert.Single(arr.ChordTemplates);
        Assert.Equal("7", vm.StringInputs[5].FretText);

        h.History.Undo();

        Assert.Equal(5, template.Frets[5]);
        Assert.Equal("5", vm.StringInputs[5].FretText);
        Assert.Same(template, vm.Template);
    }

    private static Chord BoundChord(int templateId, params (int stringIdx, int fret)[] notes)
    {
        var chord = new Chord { TemplateId = templateId };
        foreach (var (stringIdx, fret) in notes)
            chord.ChordNotes.Add(new ChordNote { StringIndex = stringIdx, Fret = fret });
        return chord;
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
