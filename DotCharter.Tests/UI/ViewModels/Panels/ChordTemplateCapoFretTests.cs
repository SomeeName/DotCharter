using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     Every verb that WRITES a fret has to land inside the set the neck
///     actually has — <c>{0} ∪ [capo, 24]</c>, <see cref="SoundingPitch.SnapToNeck" />.
///     The chord-template panel's grid is a SHARED-template authoring surface, so
///     an illegal fret there is inherited by every chord bound to the template:
///     inspector rule <c>L01</c> (<c>NoteFretBelowCapo</c>) minted by the app
///     itself, from a gesture the charter cannot see is illegal.
///     <para>
///         The chord box's POINTER path was always safe (<c>ChordBoxGeometry</c>
///         floors its fret window at <c>capo + 1</c>), which is why this went
///         unnoticed: the holes are the two KEYBOARD paths, which build their
///         value from the digit accumulator / the raw text box and never consult
///         the geometry.
///     </para>
/// </summary>
public sealed class ChordTemplateCapoFretTests
{
    /// <summary>Hover-typing a digit below the capo. This is the path the
    /// charter reaches by hovering a string on the chord box and pressing
    /// <c>3</c>; the accumulator caps the TOP of the range and nothing capped
    /// the bottom, so fret 3 landed verbatim under a capo 5.</summary>
    [Fact]
    public void HoverTypedFret_BelowTheCapo_LandsOnTheCapo()
    {
        var h = new Harness(capo: 5);
        var (vm, template) = h.LoadedShape((4, 7));

        vm.SetHoveredFretCommand.Execute(new ChordBoxFretEdit(4, 3, ContinuesRun: false));

        Assert.Equal(5, template.Frets[4]);
    }

    /// <summary>…and the finger that was standing on the fretted string comes
    /// off, because the snapped fret sounds OPEN. The strip used to read the
    /// TYPED fret, so a value that snapped up to the capo kept its finger and
    /// traded an L01 for an L06.</summary>
    [Fact]
    public void HoverTypedFret_SnappedToTheCapo_TakesTheFingerOff()
    {
        var h = new Harness(capo: 5);
        var (vm, template) = h.LoadedShape((4, 7));
        template.Fingers[4] = 2;

        vm.SetHoveredFretCommand.Execute(new ChordBoxFretEdit(4, 2, ContinuesRun: false));

        Assert.Equal(5, template.Frets[4]);
        Assert.False(template.Fingers.ContainsKey(4));
    }

    /// <summary>The per-row fret box parses whatever was typed into it and wrote
    /// it verbatim — including a fret the neck does not have at all.</summary>
    [Theory]
    [InlineData("3", 5)] // below a capo 5 → the capo, the answer FixCapoFrets gives
    [InlineData("0", 0)] // the open (capo'd) string is always legal
    [InlineData("99", 24)] // off the end of a 24-fret neck
    [InlineData("-4", 0)] // behind the nut
    public void TypedFretText_LandsOnTheNeck(string typed, int expected)
    {
        var h = new Harness(capo: 5);
        var (vm, template) = h.LoadedShape((4, 7));

        vm.StringInputs[4].FretText = typed;

        Assert.Equal(expected, template.Frets[4]);
    }

    /// <summary>Capo 0 is a strict no-op: no un-capo'd chart changes
    /// behaviour.</summary>
    [Fact]
    public void WithNoCapo_ATypedFretIsUntouched()
    {
        var h = new Harness(capo: 0);
        var (vm, template) = h.LoadedShape((4, 7));

        vm.StringInputs[4].FretText = "3";

        Assert.Equal(3, template.Frets[4]);
    }

    /// <summary>The click path snaps too — <c>ToggleNote</c> is a public command
    /// and its payload is a bare tuple, so it is not the geometry's guard that
    /// makes it safe.</summary>
    [Fact]
    public void ToggleNote_BelowTheCapo_AuthorsTheCapoAndNoFinger()
    {
        var h = new Harness(capo: 5);
        var (vm, template) = h.LoadedShape((4, 7));

        vm.ToggleNoteCommand.Execute((2, 3));

        Assert.Equal(5, template.Frets[2]);
        // Fret 5 under a capo 5 sounds OPEN, so it gets no finger — the same
        // line ToggleNote already drew for fret 0.
        Assert.False(template.Fingers.ContainsKey(2));
    }

    /// <summary>Toggling OFF still works from a sub-capo click: the string is
    /// already at the capo, and the click asks for the capo, so the second click
    /// removes it rather than re-authoring it.</summary>
    [Fact]
    public void ToggleNote_OnAStringAlreadyAtTheCapo_RemovesIt()
    {
        var h = new Harness(capo: 5);
        var (vm, template) = h.LoadedShape((4, 7), (2, 5));

        vm.ToggleNoteCommand.Execute((2, 3));

        Assert.False(template.Frets.ContainsKey(2));
    }

    private sealed class Harness
    {
        private readonly Arrangement _arr;

        public Harness(int capo)
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var tempo = new TempoService(logger);
            var config = new FakeConfigurationService();
            var project = new ChartProject();
            _arr = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead, Capo = capo };
            _arr.Levels.Add(new ChartLevel());
            project.Arrangements.Add(_arr);

            State.ActiveProject = project;
            State.ActiveArrangement = _arr;
            State.ActiveLevel = _arr.Levels[0];

            var transport = new TransportViewModel(Audio, State, config, FakeLocalization.KeysOnly());
            var tracks = new TrackContextRegistry(State, Selection);
            Editor = new ChartEditorViewModel(State, transport, History, Selection, tempo, tracks, FakeLocalization.KeysOnly());
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public FakeAudioPlaybackService Audio { get; } = new();
        public HistoryService History { get; }
        public ChartEditorViewModel Editor { get; }

        /// <summary>A real arrangement template with one selected chord bound to
        /// it — the state every shape-authoring gesture needs, since a reshape
        /// with nothing selected is refused outright (browse-mode guard).</summary>
        public (ChordTemplateEditorViewModel Vm, ChordTemplate Template) LoadedShape(
            params (int stringIdx, int fret)[] frets)
        {
            var template = new ChordTemplate
            {
                Id = 0, Name = "Grip",
                Frets = frets.ToDictionary(f => f.stringIdx, f => f.fret)
            };
            _arr.ChordTemplates.Add(template);

            var chord = new Chord { TemplateId = 0 };
            foreach (var (stringIdx, fret) in frets)
                chord.ChordNotes.Add(new ChordNote { StringIndex = stringIdx, Fret = fret });
            _arr.Levels[0].Chords.Add(chord);

            var vm = new ChordTemplateEditorViewModel(Editor, History, new FingeringGenerationService(), FakeLocalization.English(),
                new InlineDispatcher());
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
