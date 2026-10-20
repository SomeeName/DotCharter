using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     A finger only goes on a string the grip actually FRETS.
///     <para>
///         A finger standing on an open string is inspector rule <c>L06</c>
///         (<c>FingerSetForOpenString</c>, <c>TemplateAndOverlapRule</c>), and
///         "open" is <c>fret &lt;= capo</c> — frets are absolute, so on a capo'd
///         chart it is not just fret 0. The three SHAPE-authoring gestures each
///         strip it deliberately (<c>ToggleNote</c>, <c>SetHoveredFret</c>,
///         <c>ClearString</c>), and so does
///         <c>ChordTemplateFingerSetter.SetSuggestedFingers</c>. The
///         FINGER-authoring ones did not: the global <c>SetFinger1..4</c>
///         keybinds go Inspector → <c>SetFingerForStrings</c> →
///         <c>SetFingerDraftForStrings</c>, and with no string mask that means
///         "every string of the chord" — so one keystroke on an open-string grip
///         minted the exact finding the panel's own Issues tab reports. The chord
///         box's hover-typing shares the draft path and shared the bug.
///     </para>
///     <para>
///         CLEARING is deliberately still allowed everywhere: that is how an
///         existing bad finger is repaired, and it is what the "no finger"
///         keybind is for.
///     </para>
/// </summary>
public sealed class ChordTemplateFingerOpenStringTests
{
    [Fact]
    public void TheKeybindSkipsAnOpenStringAndFingersTheRest()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Template(0, (0, 0), (1, 3), (2, 5)));
        var vm = h.CreateChordEditor();

        Assert.True(vm.SetFingerForStrings(1, null));

        var fingers = arr.ChordTemplates[0].Fingers;
        Assert.False(fingers.ContainsKey(0));
        Assert.Equal(1, fingers[1]);
        Assert.Equal(1, fingers[2]);
    }

    [Fact]
    public void BehindACapoOpenIsFretAtOrBelowTheCapo()
    {
        // Frets are ABSOLUTE: with a capo at 5, fret 5 sounds the open string
        // and no finger holds it. This is the half a `fret == 0` check misses.
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.Capo = 5;
        arr.ChordTemplates.Add(Template(0, (0, 5), (1, 8)));
        var vm = h.CreateChordEditor();

        Assert.True(vm.SetFingerForStrings(3, null));

        var fingers = arr.ChordTemplates[0].Fingers;
        Assert.False(fingers.ContainsKey(0));
        Assert.Equal(3, fingers[1]);
    }

    [Fact]
    public void AStringTheShapeDoesNotPlayNeverGetsAFinger()
    {
        // StringInputs is one row per LANE, not per played string, and the commit
        // wrote a Fingers entry for every row whose text parsed — so an unmuted
        // 6-string arrangement got fingers on the three strings the grip does not
        // touch.
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Template(0, (0, 3), (1, 5)));
        var vm = h.CreateChordEditor();

        Assert.True(vm.SetFingerForStrings(2, null));

        Assert.Equal([0, 1], arr.ChordTemplates[0].Fingers.Keys.Order());
    }

    [Fact]
    public void AGripWithNoFrettedStringAtAllRecordsNothing()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Template(0, (0, 0), (1, 0)));
        var vm = h.CreateChordEditor();

        Assert.False(vm.SetFingerForStrings(1, null));

        Assert.Empty(arr.ChordTemplates[0].Fingers);
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void ClearingStillReachesAnOpenStringSoABadFingerCanBeRepaired()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        var template = Template(0, (0, 0), (1, 3));
        template.Fingers[0] = 1; // the L06 an older build wrote
        template.Fingers[1] = 1;
        arr.ChordTemplates.Add(template);
        var vm = h.CreateChordEditor();

        Assert.True(vm.SetFingerForStrings(-1, null));

        Assert.Empty(arr.ChordTemplates[0].Fingers);
    }

    [Fact]
    public void TheThumbIsNotBlocked()
    {
        // L06 is `finger > 0`: the thumb (0) over the top of the neck is not one
        // of the four that hold a fret down, and the rule does not flag it. The
        // guard has to draw its line in the same place the validator does.
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Template(0, (0, 0), (1, 3)));
        var vm = h.CreateChordEditor();

        Assert.True(vm.SetFingerForStrings(0, [0]));

        Assert.Equal(0, arr.ChordTemplates[0].Fingers[0]);
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
