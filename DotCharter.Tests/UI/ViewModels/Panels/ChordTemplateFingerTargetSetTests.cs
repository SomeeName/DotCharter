using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     "Which strings does this finger edit target" had ONE encoding for two
///     opposite answers.
///     <para>
///         <c>SetFingerDraftForStrings</c> read an empty collection as "every
///         string" — the "empty mask means all strings" convention, spelled at the
///         wrong layer. Its caller,
///         <c>InspectorViewModel.SetFingerOnSelectedStrings</c>, produces an empty
///         list in TWO cases: the mask is genuinely empty (all strings), and the
///         mask is SET but intersects none of this chord's strings (no strings).
///         The second answer came back inverted: mask strings 1–2 with
///         <c>Ctrl+1/2</c>, select a chord that only plays 4–6, press a SetFinger
///         keybind, and the finger was written to every string of the chord — the
///         exact opposite of what the mask asked for, and committed.
///     </para>
///     <para>
///         The inspector already knows this state exists — its scope bar renders
///         <c>Inspector_Scope_NoStringInMask</c> for it — and the sibling funnel
///         <c>ApplyChordNoteMutation</c> goes through <c>MaskedChordNotes</c> and
///         correctly no-ops. <c>null</c> now means "all strings"; an empty
///         collection means "none", and the two are no longer the same value.
///     </para>
/// </summary>
public sealed class ChordTemplateFingerTargetSetTests
{
    [Fact]
    public void ANullTargetSet_StillMeansEveryString()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Template(0, (0, 3), (1, 5)));
        var vm = h.CreateChordEditor();

        Assert.True(vm.SetFingerForStrings(2, null));

        Assert.Equal(2, arr.ChordTemplates[0].Fingers[0]);
        Assert.Equal(2, arr.ChordTemplates[0].Fingers[1]);
    }

    [Fact]
    public void AnEmptyTargetSet_TouchesNothing()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Template(0, (0, 3), (1, 5)));
        var vm = h.CreateChordEditor();

        Assert.False(vm.SetFingerForStrings(2, []));

        Assert.Empty(arr.ChordTemplates[0].Fingers);
        Assert.False(h.History.CanUndo);
    }

    private static ChordTemplate Template(int id, params (int stringIdx, int fret)[] frets)
    {
        var t = new ChordTemplate { Id = id, Name = "shape" };
        foreach (var (stringIdx, fret) in frets) t.Frets[stringIdx] = fret;
        return t;
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
    }
}
