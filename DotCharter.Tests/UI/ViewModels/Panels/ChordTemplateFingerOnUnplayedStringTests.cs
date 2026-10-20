using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     A finger belongs to a string the shape FRETS. Two panel paths wrote one
///     anywhere:
///     <list type="bullet">
///         <item>
///             <c>SetFingerForStrings(finger, null)</c> — "every string", what
///             the <c>SetFinger*</c> keybinds send with an empty string-edit
///             mask — walked every ROW of the panel, so a 6-row panel over a
///             two-string grip fingered the four strings the chord does not
///             play, plus any open one.
///         </item>
///         <item>
///             the per-string finger strip, which is bound straight to
///             <c>FingerText</c> and reaches the commit without passing the
///             draft helper at all.
///         </item>
///     </list>
///     <para>
///         Neither is visible to the inspector: rule L06/L07
///         (<c>TemplateAndOverlapRule</c>) iterates <c>template.Frets</c>, so a
///         finger on a string that has no fret is never reported — it just
///         travels to the export, where RS-XML spells it <c>fingerN</c> beside
///         <c>fret="-1"</c>.
///     </para>
/// </summary>
public sealed class ChordTemplateFingerOnUnplayedStringTests
{
    [Fact]
    public void EveryString_MeansEveryStringTheShapeFrets()
    {
        var h = new Harness();
        var template = Shape((0, 3), (1, 5));
        h.Editor.ActiveArrangement!.ChordTemplates.Add(template);
        var vm = h.CreateChordEditor();

        Assert.True(vm.SetFingerForStrings(2, null));

        Assert.Equal(2, template.Fingers[0]);
        Assert.Equal(2, template.Fingers[1]);
        // …and nothing on the four strings the chord does not play.
        Assert.Equal(2, template.Fingers.Count);
    }

    /// <summary>An OPEN string is not fingered either — that is inspector rule
    /// L06, and it is the rule the panel's own shape-authoring gestures already
    /// obey (<c>ToggleNote</c>, <c>SetHoveredFret</c>).</summary>
    [Fact]
    public void EveryString_SkipsAnOpenString()
    {
        var h = new Harness();
        var template = Shape((0, 0), (1, 5));
        h.Editor.ActiveArrangement!.ChordTemplates.Add(template);
        var vm = h.CreateChordEditor();

        Assert.True(vm.SetFingerForStrings(2, null));

        Assert.False(template.Fingers.ContainsKey(0));
        Assert.Equal(2, template.Fingers[1]);
    }

    /// <summary>Frets are ABSOLUTE, so "open" is <c>fret &lt;= capo</c> — the
    /// same reading the L06 rule uses.</summary>
    [Fact]
    public void EveryString_TreatsACapoedFretAsOpen()
    {
        var h = new Harness();
        h.Editor.ActiveArrangement!.Capo = 3;
        var template = Shape((0, 3), (1, 7));
        h.Editor.ActiveArrangement!.ChordTemplates.Add(template);
        var vm = h.CreateChordEditor();

        Assert.True(vm.SetFingerForStrings(1, null));

        Assert.False(template.Fingers.ContainsKey(0));
        Assert.Equal(1, template.Fingers[1]);
    }

    /// <summary>A caller that NAMES a string still gets exactly that string —
    /// the mask path is unchanged.</summary>
    [Fact]
    public void ANamedString_IsStillTargetedExactly()
    {
        var h = new Harness();
        var template = Shape((0, 3), (1, 5));
        h.Editor.ActiveArrangement!.ChordTemplates.Add(template);
        var vm = h.CreateChordEditor();

        Assert.True(vm.SetFingerForStrings(4, new[] { 1 }));

        Assert.False(template.Fingers.ContainsKey(0));
        Assert.Equal(4, template.Fingers[1]);
    }

    /// <summary>The per-row finger strip writes <c>FingerText</c> directly, so
    /// the COMMIT is the backstop: a finger typed onto a row the shape does not
    /// declare never reaches the template.</summary>
    [Fact]
    public void TheFingerStrip_CannotFingerAStringTheShapeDoesNotPlay()
    {
        var h = new Harness();
        var template = Shape((0, 3), (1, 5));
        h.Editor.ActiveArrangement!.ChordTemplates.Add(template);
        var vm = h.CreateChordEditor();

        vm.StringInputs[4].FingerText = "3";
        Assert.True(vm.HasPendingFingerEdits);
        vm.SaveFingerEditsCommand.Execute(null);

        Assert.False(template.Fingers.ContainsKey(4));
    }

    private static ChordTemplate Shape(params (int stringIdx, int fret)[] frets)
    {
        var t = new ChordTemplate { Id = 0, Name = "shape" };
        foreach (var (stringIdx, fret) in frets) t.Frets[stringIdx] = fret;
        return t;
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
