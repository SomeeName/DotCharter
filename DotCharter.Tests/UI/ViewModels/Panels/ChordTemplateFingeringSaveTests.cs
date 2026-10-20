using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     The inspector's fingering save, and what it is allowed to touch.
///     <para>
///         <b>"I have to click save fingering TWICE."</b>
///         <c>ReconcileSelectionToTemplate</c> resolved the edited state through
///         a FRETS-ONLY find-or-create. A fingering edit moves no fret, so the
///         lookup handed back the very template the edit came from: the save
///         degraded to a <c>SwitchTemplateAction</c> onto the same shape and the
///         new fingering was dropped on the floor. Fingering / name / arpeggio
///         edits now resolve on the whole authored state instead.
///     </para>
///     <para>
///         <b>And what one save reaches.</b> Every write path in the panel
///         projects its targets onto the loaded shape, but the selection is not
///         necessarily homogeneous — <c>Ctrl+Shift+A</c> selects the whole
///         track. The edit is narrowed to the sounds that already play the
///         displayed shape, which both stops the flattening and IS the
///         "apply one fingering to every selected chord" verb.
///     </para>
/// </summary>
public sealed class ChordTemplateFingeringSaveTests
{
    [Fact]
    public void OneSave_Sticks_WhenTheShapeAlreadyHasATemplate()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Shape(0, "A5", [(1, 7), (2, 7)], [(1, 1), (2, 3)]));

        var chords = AddChords(arr, 8, (1, 7), (2, 7));
        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);

        // The panel is showing template 0 (the equivalent shape). Change one
        // finger and press Save ONCE.
        vm.StringInputs[2].FingerText = "4";
        vm.SaveFingerEditsCommand.Execute(null);

        var bound = arr.ChordTemplates.Single(t => t.Id == chords[0].TemplateId);
        Assert.Equal(4, bound.Fingers[2]);
        Assert.False(vm.HasPendingFingerEdits);

        // …and it re-fingered nothing else: the shared template and the seven
        // siblings still on it are untouched.
        Assert.Equal(3, arr.ChordTemplates.Single(t => t.Id == 0).Fingers[2]);
        Assert.All(chords.Skip(1), c => Assert.Equal(-1, c.TemplateId));
    }

    [Fact]
    public void OneSave_RefingersEverySelectedChordOfThatShape_AsOneUndoNode()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Shape(0, "A5", [(1, 7), (2, 7)], [(1, 1), (2, 3)]));

        var chords = AddChords(arr, 8, (1, 7), (2, 7));
        var vm = h.CreateChordEditor();
        foreach (var chord in chords) h.Selection.SelectedChords.Add(chord);

        Assert.Equal(8, vm.FingeringTargetCount);

        vm.StringInputs[2].FingerText = "4";
        vm.SaveFingerEditsCommand.Execute(null);

        var id = chords[0].TemplateId;
        Assert.True(id >= 0);
        Assert.All(chords, c => Assert.Equal(id, c.TemplateId));
        Assert.Equal(4, arr.ChordTemplates.Single(t => t.Id == id).Fingers[2]);

        // ONE history node for the whole run.
        h.History.Undo();
        Assert.All(chords, c => Assert.Equal(-1, c.TemplateId));
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void OneSave_AlsoCarriesTheSelectedHandShapeOverThatShape()
    {
        // Otherwise the 3D hand-shape bar keeps describing the old fingering
        // while the notes underneath it carry the new one.
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Shape(0, "A5", [(1, 7), (2, 7)], [(1, 1), (2, 3)]));

        var chords = AddChords(arr, 1, (1, 7), (2, 7));
        var bar = new HandShape
        {
            TemplateId = 0, Position = FractionalPosition.Zero, EndPosition = new FractionalPosition(2)
        };
        arr.Levels[0].HandShapes.Add(bar);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);
        h.Selection.SelectedHandShapes.Add(bar);

        vm.StringInputs[2].FingerText = "4";
        vm.SaveFingerEditsCommand.Execute(null);

        Assert.Equal(chords[0].TemplateId, bar.TemplateId);
        Assert.Equal(4, arr.ChordTemplates.Single(t => t.Id == bar.TemplateId).Fingers[2]);
    }

    [Fact]
    public void RefingeringTwoSiblingsSeparately_ConvergesOnOneTemplate()
    {
        // Reuse-then-mint, not mint-every-time: the second chord finds the
        // template the first one minted instead of leaving a duplicate.
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Shape(0, "A5", [(1, 7), (2, 7)], [(1, 1), (2, 3)]));

        var chords = AddChords(arr, 3, (1, 7), (2, 7));
        var vm = h.CreateChordEditor();

        h.Selection.SelectedChords.Add(chords[0]);
        vm.StringInputs[2].FingerText = "4";
        vm.SaveFingerEditsCommand.Execute(null);

        h.Selection.SelectedChords.Clear();
        h.Selection.SelectedChords.Add(chords[1]);
        vm.StringInputs[2].FingerText = "4";
        vm.SaveFingerEditsCommand.Execute(null);

        Assert.Equal(chords[0].TemplateId, chords[1].TemplateId);
        Assert.Equal(2, arr.ChordTemplates.Count); // the original + exactly one mint
    }

    [Fact]
    public void ASaveWithTheWholeTrackSelected_LeavesTheOtherSHAPESAlone()
    {
        // Ctrl+Shift+A. Every path below the panel projects its targets onto the
        // loaded shape, so an unnarrowed save flattened every selected chord
        // onto the first one's shape.
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Shape(0, "A5", [(1, 7), (2, 7)], [(1, 1), (2, 3)]));
        arr.ChordTemplates.Add(Shape(1, "C5", [(3, 3), (4, 5)], [(3, 1), (4, 3)]));

        var aShapes = AddChords(arr, 3, (1, 7), (2, 7));
        var cShapes = AddChords(arr, 2, (3, 3), (4, 5));

        var vm = h.CreateChordEditor();
        foreach (var chord in aShapes.Concat(cShapes)) h.Selection.SelectedChords.Add(chord);

        Assert.Equal(3, vm.FingeringTargetCount);

        vm.StringInputs[2].FingerText = "4";
        vm.SaveFingerEditsCommand.Execute(null);

        Assert.All(aShapes, c => Assert.Equal(aShapes[0].TemplateId, c.TemplateId));
        Assert.All(cShapes, c =>
        {
            Assert.Equal(-1, c.TemplateId);
            Assert.Equal([3, 5], c.ChordNotes.OrderBy(n => n.StringIndex).Select(n => n.Fret));
            Assert.Equal([3, 4], c.ChordNotes.OrderBy(n => n.StringIndex).Select(n => n.StringIndex));
        });
    }

    [Fact]
    public void BrowseModeFingering_LandsOnTheBrowsedTemplate_EvenWhenChordsUseIt()
    {
        // The fingering triage loop ("Save & next missing") SEEKS to a
        // template's first usage without selecting anything, so browse mode has
        // to be able to finger a template that chords are bound to. "This chord
        // only" with no chord isolated onto an EMPTY selection: it minted an
        // orphan template and left the browsed one unfingered, i.e. the loop
        // could never clear a template that was actually in use.
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Shape(0, "A5", [(1, 7), (2, 7)]));
        AddBoundChords(arr, 0, 2, (1, 7), (2, 7));

        var vm = h.CreateChordEditor(); // no selection ⇒ browse mode

        Assert.True(vm.SetFingerForStrings(1, [1]));

        Assert.Single(arr.ChordTemplates);
        Assert.Equal(1, arr.ChordTemplates[0].Fingers[1]);
    }

    [Fact]
    public void ASave_NeverFoldsAnArpeggioChordIntoAPlainChordsTemplate()
    {
        // Same frets, different arpeggio flavour = a different template
        // everywhere else in the codebase (ChordMask.Arpeggio is load-bearing on
        // export), so the narrowing has to respect it too.
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Shape(0, "A5", [(1, 7), (2, 7)], [(1, 1), (2, 3)]));
        var arpeggio = Shape(1, "A5", [(1, 7), (2, 7)], [(1, 1), (2, 3)]);
        arpeggio.Arpeggio = true;
        arr.ChordTemplates.Add(arpeggio);

        var plain = AddBoundChords(arr, 0, 1, (1, 7), (2, 7))[0];
        var arped = AddBoundChords(arr, 1, 1, (1, 7), (2, 7))[0];

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(plain);
        h.Selection.SelectedChords.Add(arped);

        vm.StringInputs[2].FingerText = "4";
        vm.SaveFingerEditsCommand.Execute(null);

        Assert.Equal(1, arped.TemplateId);
        Assert.True(arr.ChordTemplates.Single(t => t.Id == 1).Arpeggio);
        Assert.Equal(3, arr.ChordTemplates.Single(t => t.Id == 1).Fingers[2]);
    }

    [Fact]
    public void RenamingAChordThatSharesItsShape_KeepsTheTypedName()
    {
        // The same discarded-edit bug, through the name field: a frets-only
        // resolve handed back the template the name came from, so the typed
        // name went nowhere.
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Shape(0, "A5", [(1, 7), (2, 7)], [(1, 1), (2, 3)]));

        var chords = AddChords(arr, 2, (1, 7), (2, 7));
        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);

        vm.ChordName = "Asus4";

        Assert.Equal("Asus4", arr.ChordTemplates.Single(t => t.Id == chords[0].TemplateId).Name);
        Assert.Equal("A5", arr.ChordTemplates.Single(t => t.Id == 0).Name);
    }

    [Fact]
    public void AnIsolatedFingeringEdit_DoesNotRelabelTheChord()
    {
        // "this chord only" on a template someone else uses spawns a private
        // template — but a fingering edit changed no notes, so calling the
        // result "A5 (Isolated)" renames the chord on the highway for nothing.
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Shape(0, "A5", [(1, 7), (2, 7)], [(1, 1), (2, 3)]));

        var chords = AddBoundChords(arr, 0, 2, (1, 7), (2, 7));
        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);

        Assert.Equal(ChordApplyRung.Selected, vm.ApplyRung);
        vm.StringInputs[2].FingerText = "4";
        vm.SaveFingerEditsCommand.Execute(null);

        var minted = arr.ChordTemplates.Single(t => t.Id == chords[0].TemplateId);
        Assert.NotEqual(0, minted.Id);
        Assert.Equal("A5", minted.Name);
        Assert.Equal(4, minted.Fingers[2]);
        Assert.Equal(0, chords[1].TemplateId);
        Assert.Equal(3, arr.ChordTemplates.Single(t => t.Id == 0).Fingers[2]);
    }

    [Fact]
    public void WhenEveryUserOfTheTemplateIsSelected_TheTemplateItselfIsRefingered()
    {
        // The sanctioned in-place mutation: nothing else points at it, so
        // minting a second template would be pure bloat.
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Shape(0, "A5", [(1, 7), (2, 7)], [(1, 1), (2, 3)]));

        var chords = AddBoundChords(arr, 0, 2, (1, 7), (2, 7));
        var vm = h.CreateChordEditor();
        foreach (var chord in chords) h.Selection.SelectedChords.Add(chord);

        vm.StringInputs[2].FingerText = "4";
        vm.SaveFingerEditsCommand.Execute(null);

        Assert.Single(arr.ChordTemplates);
        Assert.Equal(4, arr.ChordTemplates[0].Fingers[2]);
        Assert.All(chords, c => Assert.Equal(0, c.TemplateId));
    }

    [Fact]
    public void FingeringTargetCount_CountsOnlyTheSoundsCarryingTheDisplayedShape()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        arr.ChordTemplates.Add(Shape(0, "A5", [(1, 7), (2, 7)], [(1, 1), (2, 3)]));
        arr.ChordTemplates.Add(Shape(1, "C5", [(3, 3), (4, 5)]));

        var aShapes = AddChords(arr, 2, (1, 7), (2, 7));
        var cShapes = AddChords(arr, 3, (3, 3), (4, 5));
        var bar = new HandShape
        {
            TemplateId = 0, Position = FractionalPosition.Zero, EndPosition = new FractionalPosition(2)
        };
        arr.Levels[0].HandShapes.Add(bar);

        var vm = h.CreateChordEditor();
        // Nothing selected: Save follows the ladder's "every use" (#484), and
        // the only sound bound to A5 is the bar (the chords are unbound).
        Assert.Equal(1, vm.FingeringTargetCount);

        foreach (var chord in aShapes.Concat(cShapes)) h.Selection.SelectedChords.Add(chord);
        h.Selection.SelectedHandShapes.Add(bar);

        Assert.Equal(3, vm.FingeringTargetCount); // two chords + the bar over them
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

    /// <summary>Freshly imported chords: TemplateId -1 until the first save.</summary>
    private static List<Chord> AddChords(Arrangement arr, int count, params (int stringIndex, int fret)[] notes) =>
        AddBoundChords(arr, -1, count, notes);

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
