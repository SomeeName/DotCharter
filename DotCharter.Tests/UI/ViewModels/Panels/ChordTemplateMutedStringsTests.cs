using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     The chord box's ✕ mute markers could never render.
/// </summary>
public sealed class ChordTemplateMutedStringsTests
{
    /// <summary>
    ///     <c>MutedStrings</c> asked <c>PreviewChord</c>, and that answer was
    ///     structurally always empty: the preview is synthesised in
    ///     <c>RefreshPreviewChord</c> from <c>Template.Frets</c>, a
    ///     <c>ChordTemplate</c> carries no mute, and every synthesised
    ///     <c>ChordNote</c> is therefore left at <c>MuteType.None</c>. The ✕ the
    ///     control draws from it never appeared for any chord in the app, while
    ///     the property, its doc comment and its XAML binding all read like a
    ///     shipped feature. Mute is authored on the SOUND.
    /// </summary>
    [Fact]
    public void TheChordBox_MarksTheSelectedChordsFullyMutedStrings()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0, Name = "A5",
            Frets = new Dictionary<int, int> { { 4, 5 }, { 3, 7 }, { 2, 7 } }
        });

        var chord = new Chord { TemplateId = 0 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 4, Fret = 5 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 7, Mute = MuteType.Full });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 7, Mute = MuteType.Palm });
        arr.Levels[0].Chords.Add(chord);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chord);

        // Only the FULLY muted string: a palm mute is a sounded note.
        Assert.Equal([3], vm.MutedStrings);
    }

    [Fact]
    public void WithNoChordSelected_NothingIsMarkedMuted()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(new ChordTemplate
        { Id = 0, Name = "A5", Frets = new Dictionary<int, int> { { 4, 5 } } });

        var vm = h.CreateChordEditor();

        Assert.Empty(vm.MutedStrings);
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

        /// <summary>A real arrangement template with ONE fretted string, and a
        /// chord bound to it and selected — the state a shape edit needs, since
        /// ApplyTemplateEdit deliberately refuses a reshape with nothing
        /// selected (the browse-mode guard).</summary>
        public (ChordTemplateEditorViewModel Vm, ChordTemplate Template) BoundShape(
            (int StringIndex, int Fret) fretted, int finger)
        {
            var arr = Editor.ActiveArrangement!;
            var template = new ChordTemplate
            {
                Id = 0, Name = "shape",
                Frets = new Dictionary<int, int> { { fretted.StringIndex, fretted.Fret } },
                Fingers = new Dictionary<int, int> { { fretted.StringIndex, finger } }
            };
            arr.ChordTemplates.Add(template);

            var chord = new Chord { TemplateId = 0 };
            chord.ChordNotes.Add(new ChordNote { StringIndex = fretted.StringIndex, Fret = fretted.Fret });
            arr.Levels[0].Chords.Add(chord);

            var vm = CreateChordEditor();
            Selection.SelectedChords.Add(chord);
            return (vm, template);
        }

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
