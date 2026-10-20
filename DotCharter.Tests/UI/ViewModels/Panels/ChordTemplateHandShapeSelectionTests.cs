using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     Selecting is not editing. A hand-shape selection made the panel OVERLAY
///     the frets of the sounds the bar encloses, and those writes escaped the
///     caller's own UI-update guard.
/// </summary>
public sealed class ChordTemplateHandShapeSelectionTests
{
    /// <summary>
    ///     <c>LoadTemplate</c> cleared <c>_isUpdatingUi</c> at its END even
    ///     though every caller had set it, so the code that ran AFTER it inside
    ///     the caller's own guarded region was no longer guarded.
    ///     <para>
    ///         <c>UpdateFromSelection</c>'s hand-shape branch is exactly that
    ///         shape: it loads the bar's template and then OVERLAYS the frets of
    ///         the sounds the bar encloses onto any row the template leaves
    ///         empty. Each of those writes reached <c>Input_PropertyChanged</c>
    ///         un-guarded and pushed an <c>EditTemplateGlobalAction</c> — so
    ///         merely CLICKING a hand shape whose template declares fewer strings
    ///         than its sounds play reshaped the shared template and dirtied the
    ///         project.
    ///     </para>
    /// </summary>
    [Fact]
    public void SelectingAHandShape_WhoseSoundsExceedItsTemplate_CommitsNothing()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        var template = new ChordTemplate
        {
            Id = 0, Name = "bar",
            Frets = new Dictionary<int, int> { { 4, 5 } },
            Fingers = new Dictionary<int, int> { { 4, 1 } }
        };
        arr.ChordTemplates.Add(template);

        var shape = new HandShape
        {
            TemplateId = 0,
            Position = new FractionalPosition(0),
            EndPosition = new FractionalPosition(4)
        };
        arr.Levels[0].HandShapes.Add(shape);

        // A strum under the bar that plays a string the template does not declare.
        var chord = new Chord { TemplateId = 0, Position = new FractionalPosition(1) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 4, Fret = 5 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 5, Fret = 7 });
        arr.Levels[0].Chords.Add(chord);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedHandShapes.Add(shape);

        // The panel may SHOW the enclosed note…
        Assert.Equal("7", vm.StringInputs[5].FretText);
        // …but nothing was written to the chart.
        Assert.False(h.History.CanUndo);
        Assert.Single(template.Frets);
        Assert.Equal(5, template.Frets[4]);
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
