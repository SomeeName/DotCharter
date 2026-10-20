using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     <b>A gesture is ONE undo node</b> (editing-invariants.md) — and "Save
///     fingering" is one gesture even though it does two things.
///     <para>
///         <c>CommitPendingFingerEdits</c> called <c>ApplyTemplateEdit</c> and
///         then propagated the same fingering to every OTHER template with the
///         identical grip that still lacked one. Each pushed its own history
///         node, so a single Ctrl+Z un-propagated the siblings and left the
///         fingering that caused the propagation applied — a chart state the
///         user never authored and cannot reach any other way. The propagation
///         is now built as actions and folded into the edit's own node.
///     </para>
/// </summary>
public sealed class ChordTemplateFingeringPropagationUndoTests
{
    [Fact]
    public void SaveFingering_PropagatesToTheSiblingGrip_AndOneUndoRestoresBoth()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        // Two templates, same grip, neither fingered. Editing the first is what
        // teaches the second its fingering.
        arr.ChordTemplates.Add(Shape(0, "A5", [(1, 7), (2, 7)]));
        arr.ChordTemplates.Add(Shape(1, "A5 (again)", [(1, 7), (2, 7)]));

        var onZero = AddBoundChords(arr, 0, 2, (1, 7), (2, 7));
        AddBoundChords(arr, 1, 1, (1, 7), (2, 7));

        var vm = h.CreateChordEditor();
        // "+ the same grip elsewhere" (#484) is the rung that reaches the sibling.
        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.SameGrip);
        foreach (var chord in onZero) h.Selection.SelectedChords.Add(chord);

        vm.StringInputs[1].FingerText = "1";
        vm.StringInputs[2].FingerText = "3";
        vm.SaveFingerEditsCommand.Execute(null);

        // The edit landed…
        var edited = arr.ChordTemplates.Single(t => t.Id == 0);
        Assert.Equal(1, edited.Fingers[1]);
        Assert.Equal(3, edited.Fingers[2]);
        // …and it propagated to the sibling grip.
        var sibling = arr.ChordTemplates.Single(t => t.Id == 1);
        Assert.Equal(1, sibling.Fingers[1]);
        Assert.Equal(3, sibling.Fingers[2]);

        // ONE Ctrl+Z puts the WHOLE pre-edit state back. Two nodes reverted only
        // the propagation and left `edited` fingered.
        Assert.True(h.History.CanUndo);
        h.History.Undo();

        Assert.Empty(arr.ChordTemplates.Single(t => t.Id == 0).Fingers);
        Assert.Empty(arr.ChordTemplates.Single(t => t.Id == 1).Fingers);
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void SaveFingering_ThroughTheRECONCILEPath_IsAlsoOneNode()
    {
        // Freshly imported chords sit at TemplateId -1, so the selection is not
        // bound to the loaded template and the save reconciles onto a minted
        // one. That path pushed its own node too.
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Shape(0, "A5", [(1, 7), (2, 7)], [(1, 1), (2, 3)]));

        var unbound = AddBoundChords(arr, -1, 2, (1, 7), (2, 7));

        var vm = h.CreateChordEditor();
        // The default rung, "selected": the selection is rebound onto a mint.
        Assert.Equal(ChordApplyRung.Selected, vm.ApplyRung);
        foreach (var chord in unbound) h.Selection.SelectedChords.Add(chord);

        vm.StringInputs[2].FingerText = "4";
        vm.SaveFingerEditsCommand.Execute(null);

        var mintedId = unbound[0].TemplateId;
        Assert.True(mintedId >= 0);
        Assert.Equal(4, arr.ChordTemplates.Single(t => t.Id == mintedId).Fingers[2]);

        h.History.Undo();

        Assert.All(unbound, c => Assert.Equal(-1, c.TemplateId));
        // The template the panel was showing is untouched by the undo, and the
        // mint is gone with it.
        Assert.Equal(3, arr.ChordTemplates.Single(t => t.Id == 0).Fingers[2]);
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void SaveFingering_NeverPropagatesOntoTheTemplateItIsEditing()
    {
        // The follow-up is built from the template's POST-edit state, which on
        // the global path is a clone of the loaded template — so the loaded
        // template must be excluded by reference or it would be re-fingered
        // twice inside one node.
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Shape(0, "A5", [(1, 7), (2, 7)]));

        var chords = AddBoundChords(arr, 0, 2, (1, 7), (2, 7));
        var vm = h.CreateChordEditor();
        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.SameGrip);
        foreach (var chord in chords) h.Selection.SelectedChords.Add(chord);

        vm.StringInputs[1].FingerText = "1";
        vm.StringInputs[2].FingerText = "3";
        vm.SaveFingerEditsCommand.Execute(null);

        Assert.Single(arr.ChordTemplates);
        Assert.Equal(1, arr.ChordTemplates[0].Fingers[1]);

        h.History.Undo();
        Assert.Empty(arr.ChordTemplates[0].Fingers);
        Assert.False(h.History.CanUndo);
    }

    private static ChordTemplate Shape(int id, string name,
        (int stringIndex, int fret)[] frets, (int stringIndex, int finger)[]? fingers = null)
    {
        var template = new ChordTemplate { Id = id, Name = name };
        foreach (var (stringIndex, fret) in frets) template.Frets[stringIndex] = fret;
        foreach (var (stringIndex, finger) in fingers ?? [])
            template.Fingers[stringIndex] = finger;
        return template;
    }

    private static List<Chord> AddBoundChords(Arrangement arr, int templateId, int count,
        params (int stringIndex, int fret)[] notes)
    {
        var chords = new List<Chord>();
        var offset = arr.Levels[0].Chords.Count;
        for (var i = 0; i < count; i++)
        {
            var chord = new Chord
            {
                TemplateId = templateId,
                Position = new FractionalPosition((offset + i) * 4)
            };
            foreach (var (stringIndex, fret) in notes)
                chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
            arr.Levels[0].Chords.Add(chord);
            chords.Add(chord);
        }

        return chords;
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
