using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     `ApplyTemplateEdit` computed
///     <c>selectionBound = templateIsReal &amp;&amp; selectedChords.All(…) &amp;&amp;
///     selectedHandShapes.All(…)</c>, and <c>All</c> on an EMPTY list is
///     vacuously TRUE — so with NOTHING selected the guard collapsed to
///     "is this a real template" and took the GLOBAL edit branch.
///     <para>
///         The chord-template panel is deliberately usable with no selection
///         (browse mode loads <c>ChordTemplates.First()</c>) and its fret
///         <c>TextBox</c> commits per keystroke (Avalonia's default
///         <c>UpdateSourceTrigger</c>), so typing a fret while merely BROWSING
///         rewrote every chord in the active level bound to that template — once
///         per keystroke. Typing "12" over "5" wrote fret 1 to all of them and
///         then 12, leaving two history nodes and a transient chart in which
///         every bound chord played fret 1 (editing-rework audit 01 §D.3).
///     </para>
/// </summary>
public sealed class ChordTemplateBrowseModeGuardTests
{
    [Fact]
    public void TypingAFretWithNothingSelected_DoesNotRewriteTheChordsOnThatTemplate()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Template(0, (0, 3), (1, 5)));
        var bound = BoundChord(0, (0, 3), (1, 5));
        arr.Levels[0].Chords.Add(bound);

        var vm = h.CreateChordEditor(); // no selection ⇒ browse mode

        vm.StringInputs[0].FretText = "7";

        Assert.Equal(new[] { 3, 5 }, bound.ChordNotes.Select(n => n.Fret));
        Assert.Equal(3, arr.ChordTemplates[0].Frets[0]);
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void ClickingTheFretboardWithNothingSelected_DoesNotRewriteTheChordsOnThatTemplate()
    {
        // Same branch, reached through ToggleNote instead of the fret column.
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Template(0, (0, 3), (1, 5)));
        var bound = BoundChord(0, (0, 3), (1, 5));
        arr.Levels[0].Chords.Add(bound);

        var vm = h.CreateChordEditor();

        vm.ToggleNoteCommand.Execute((stringIdx: 2, fret: 9));

        Assert.Equal(2, bound.ChordNotes.Count);
        Assert.Equal(2, arr.ChordTemplates[0].Frets.Count);
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void TypingAFretWITHTheChordSelected_StillReshapesIt()
    {
        // The guard must not break the primary edit verb.
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Template(0, (0, 3), (1, 5)));
        var chord = BoundChord(0, (0, 3), (1, 5));
        arr.Levels[0].Chords.Add(chord);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chord);

        vm.StringInputs[0].FretText = "7";

        Assert.Equal(7, chord.ChordNotes.Single(n => n.StringIndex == 0).Fret);
        Assert.True(h.History.CanUndo);
    }

    [Fact]
    public void RenamingWithNothingSelected_StillWorks()
    {
        // Browse mode exists to name/finger templates; only SHAPE edits need a
        // selection to scope them. The fingering triage loop
        // (SelectNextMissingFingering) SEEKS to a template's first usage without
        // selecting anything, so gating everything would kill it.
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Template(0, (0, 3), (1, 5)));

        var vm = h.CreateChordEditor();

        vm.ChordName = "Cadd9";

        Assert.Equal("Cadd9", arr.ChordTemplates[0].Name);
    }

    [Fact]
    public void SettingFingeringWithNothingSelected_StillWorks()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Template(0, (0, 3), (1, 5)));

        var vm = h.CreateChordEditor();

        Assert.True(vm.SetFingerForStrings(2, new[] { 0 }));
        Assert.Equal(2, arr.ChordTemplates[0].Fingers[0]);
        Assert.Equal(3, arr.ChordTemplates[0].Frets[0]); // shape untouched
    }

    private static ChordTemplate Template(int id, params (int stringIdx, int fret)[] frets)
    {
        var t = new ChordTemplate { Id = id, Name = "shape" };
        foreach (var (stringIdx, fret) in frets) t.Frets[stringIdx] = fret;
        return t;
    }

    private static Chord BoundChord(int templateId, params (int stringIdx, int fret)[] notes)
    {
        var c = new Chord { TemplateId = templateId };
        foreach (var (stringIdx, fret) in notes)
            c.ChordNotes.Add(new ChordNote { StringIndex = stringIdx, Fret = fret });
        return c;
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
