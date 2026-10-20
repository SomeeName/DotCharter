using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     The chord-shape editor's own authoring gestures minted inspector rule
///     <c>L06</c> (<c>FingerSetForOpenString</c>) — a finger on an open string,
///     reported by the panel's own Issues tab.
/// </summary>
public sealed class ChordTemplateOpenStringFingerTests
{
    /// <summary>
    ///     Clicking an OPEN string on the chord box stamped finger 1 on it —
    ///     which is inspector rule <c>L06</c> (<c>FingerSetForOpenString</c>),
    ///     reported by the panel's own Issues tab. The primary shape-authoring
    ///     gesture minted the finding the app reports, the same class of fault as
    ///     an auto-fix inventing one.
    /// </summary>
    [Fact]
    public void ClickingAnOpenString_LeavesNoFingerOnIt()
    {
        var h = new Harness();
        var (vm, template) = h.BoundShape(fretted: (4, 2), finger: 2);

        vm.ToggleNoteCommand.Execute((0, 0));

        Assert.Equal(0, template.Frets[0]);
        Assert.False(template.Fingers.ContainsKey(0));
        // The fretted string keeps the finger it always did.
        Assert.Single(template.Fingers);
    }

    /// <summary>Frets are ABSOLUTE and fret 0 is the capo, so "open" is
    /// <c>fret &lt;= capo</c> — the same reading the L06 rule itself uses.</summary>
    [Fact]
    public void UnderACapo_AStringAtTheCapoFretIsOpenToo()
    {
        var h = new Harness();
        h.Editor.ActiveArrangement!.Capo = 3;
        var (vm, template) = h.BoundShape(fretted: (4, 5), finger: 1);

        vm.ToggleNoteCommand.Execute((1, 3));

        Assert.Equal(3, template.Frets[1]);
        Assert.False(template.Fingers.ContainsKey(1));

        // …and a genuinely fretted string still gets one.
        vm.ToggleNoteCommand.Execute((2, 7));
        Assert.Equal(7, template.Frets[2]);
        Assert.Equal(1, template.Fingers[2]);
    }

    /// <summary>The hover-typed fret is the OTHER shape-authoring gesture, and
    /// typing a string down to open left the finger it used to carry standing on
    /// an open string.</summary>
    [Fact]
    public void TypingAStringDownToOpen_DropsItsStaleFinger()
    {
        var h = new Harness();
        var (vm, template) = h.BoundShape(fretted: (5, 3), finger: 3);

        vm.SetHoveredFretCommand.Execute(new DotCharter.UI.Controls.ChordBoxFretEdit(5, 0, false));

        Assert.Equal(0, template.Frets[5]);
        Assert.False(template.Fingers.ContainsKey(5));
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
