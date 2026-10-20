using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     A <c>ChordTemplate</c> is ARRANGEMENT-scoped, so "this chord only" has to
///     ask every difficulty level who else carries the shape — not just the one
///     on screen.
/// </summary>
public sealed class ChordTemplateScopeAcrossLevelsTests
{
    /// <summary>
    ///     "This chord only" asked <c>ChartEditorViewModel.PlacedChords</c> —
    ///     the ACTIVE level's chords — whether anything else used the shape. A
    ///     <c>ChordTemplate</c> is ARRANGEMENT-scoped, so on any chart with
    ///     dynamic difficulty the answer was "nobody", the isolate branch was
    ///     skipped, and the edit fell through to the in-place global rewrite.
    ///     That rewrite projects notes onto the active level's chords only, so
    ///     every other level's chords kept their old notes under a template that
    ///     no longer describes them.
    /// </summary>
    [Fact]
    public void ThisChordOnly_ProtectsChordsOnOtherDifficultyLevels()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        var template = new ChordTemplate
        {
            Id = 0, Name = "Am",
            Frets = new Dictionary<int, int> { { 4, 5 }, { 5, 5 } },
            Fingers = new Dictionary<int, int> { { 4, 1 }, { 5, 1 } }
        };
        arr.ChordTemplates.Add(template);

        var edited = BoundChord(templateId: 0, (4, 5), (5, 5));
        arr.Levels[0].Chords.Add(edited);

        // The SAME shape one difficulty down — the ordinary state of every
        // psarc import and of anything after Tools ▸ Generate Levels.
        var deeper = new ChartLevel();
        var sibling = BoundChord(templateId: 0, (4, 5), (5, 5));
        deeper.Chords.Add(sibling);
        arr.Levels.Add(deeper);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(edited);
        Assert.Equal(ChordApplyRung.Selected, vm.ApplyRung);

        vm.StringInputs[5].FretText = "7";

        // The shared template is untouched, so the sibling still plays what it
        // has always played and still agrees with the template it names.
        Assert.Equal(5, template.Frets[5]);
        Assert.Equal(0, sibling.TemplateId);
        Assert.Equal(5, sibling.ChordNotes.Single(n => n.StringIndex == 5).Fret);

        // …and the edited chord moved onto a shape of its own.
        Assert.NotEqual(0, edited.TemplateId);
        Assert.Equal(7, edited.ChordNotes.Single(n => n.StringIndex == 5).Fret);
    }

    /// <summary>The active level's own siblings are protected exactly as they
    /// always were — the widened lookup must not have lost the case it started
    /// from.</summary>
    [Fact]
    public void ThisChordOnly_StillProtectsSiblingsOnTheActiveLevel()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        var template = new ChordTemplate
        {
            Id = 0, Name = "Am",
            Frets = new Dictionary<int, int> { { 4, 5 }, { 5, 5 } }
        };
        arr.ChordTemplates.Add(template);

        var edited = BoundChord(templateId: 0, (4, 5), (5, 5));
        var sibling = BoundChord(templateId: 0, (4, 5), (5, 5));
        sibling.Position = new FractionalPosition(8);
        arr.Levels[0].Chords.Add(edited);
        arr.Levels[0].Chords.Add(sibling);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(edited);

        vm.StringInputs[5].FretText = "7";

        Assert.Equal(5, template.Frets[5]);
        Assert.Equal(5, sibling.ChordNotes.Single(n => n.StringIndex == 5).Fret);
    }

    /// <summary>…and with nothing else on the shape anywhere, the global
    /// in-place rewrite is still the path taken (no template is minted for a
    /// shape only this chord plays).</summary>
    [Fact]
    public void ThisChordOnly_WithNoOtherUser_StillEditsInPlace()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        var template = new ChordTemplate
        {
            Id = 0, Name = "Am",
            Frets = new Dictionary<int, int> { { 4, 5 }, { 5, 5 } }
        };
        arr.ChordTemplates.Add(template);

        var edited = BoundChord(templateId: 0, (4, 5), (5, 5));
        arr.Levels[0].Chords.Add(edited);
        arr.Levels.Add(new ChartLevel());

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(edited);

        vm.StringInputs[5].FretText = "7";

        Assert.Single(arr.ChordTemplates);
        Assert.Equal(7, template.Frets[5]);
        Assert.Equal(0, edited.TemplateId);
    }

    /// <summary>
    ///     The other half of the same rule, and it stayed broken when the isolate
    ///     branch was fixed. Choosing "all N" rewrites the SHARED template in
    ///     place and re-projects every bound chord's notes — but it collected
    ///     those chords from <c>ChartEditorViewModel.PlacedChords</c>, the ACTIVE
    ///     level's collection, so a chord one difficulty down kept its OLD notes
    ///     under a template that no longer describes them. That is the template
    ///     drift resolve-or-create exists to prevent, reached DELIBERATELY through
    ///     the scope control the isolate fix tells the charter to use.
    /// </summary>
    [Fact]
    public void ScopeAll_ReshapesTheChordsOnEveryDifficultyLevel()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        var template = new ChordTemplate
        {
            Id = 0, Name = "Am",
            Frets = new Dictionary<int, int> { { 4, 5 }, { 5, 5 } }
        };
        arr.ChordTemplates.Add(template);

        var edited = BoundChord(templateId: 0, (4, 5), (5, 5));
        arr.Levels[0].Chords.Add(edited);

        var deeper = new ChartLevel();
        var sibling = BoundChord(templateId: 0, (4, 5), (5, 5));
        deeper.Chords.Add(sibling);
        arr.Levels.Add(deeper);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(edited);
        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.EveryUse);
        Assert.Equal(ChordApplyRung.EveryUse, vm.ApplyRung);

        vm.StringInputs[5].FretText = "7";

        // The shared template moved, and so did EVERY chord that names it.
        Assert.Equal(7, template.Frets[5]);
        Assert.Equal(7, edited.ChordNotes.Single(n => n.StringIndex == 5).Fret);
        Assert.Equal(0, sibling.TemplateId);
        Assert.Equal(7, sibling.ChordNotes.Single(n => n.StringIndex == 5).Fret);
    }

    /// <summary>…and the count on the button says so. It read the active level
    /// alone while the edit rewrote the arrangement, so on any chart with
    /// dynamic difficulty the control offered "all 1" for an edit that reshaped
    /// two.</summary>
    [Fact]
    public void TheScopeCount_CountsEveryLevelsChords()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        var template = new ChordTemplate
        {
            Id = 0, Name = "Am",
            Frets = new Dictionary<int, int> { { 4, 5 }, { 5, 5 } }
        };
        arr.ChordTemplates.Add(template);

        arr.Levels[0].Chords.Add(BoundChord(templateId: 0, (4, 5), (5, 5)));
        var deeper = new ChartLevel();
        deeper.Chords.Add(BoundChord(templateId: 0, (4, 5), (5, 5)));
        deeper.Chords.Add(BoundChord(templateId: 0, (4, 5), (5, 5)));
        arr.Levels.Add(deeper);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(arr.Levels[0].Chords[0]);

        Assert.Equal(3, vm.Ladder.EveryUse.Reach.Sounds);
    }

    /// <summary>An undo of that edit puts every level back, not just the one on
    /// screen — the widened target list is what the action snapshots.</summary>
    [Fact]
    public void UndoingAGlobalReshape_RestoresTheDeeperLevelToo()
    {
        var h = new Harness();
        var arr = h.Editor.ActiveArrangement!;
        var template = new ChordTemplate
        {
            Id = 0, Name = "Am",
            Frets = new Dictionary<int, int> { { 4, 5 }, { 5, 5 } }
        };
        arr.ChordTemplates.Add(template);

        var edited = BoundChord(templateId: 0, (4, 5), (5, 5));
        arr.Levels[0].Chords.Add(edited);
        var deeper = new ChartLevel();
        var sibling = BoundChord(templateId: 0, (4, 5), (5, 5));
        deeper.Chords.Add(sibling);
        arr.Levels.Add(deeper);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(edited);
        vm.ChooseApplyRungCommand.Execute(ChordApplyRung.EveryUse);

        vm.StringInputs[5].FretText = "7";
        Assert.Equal(7, sibling.ChordNotes.Single(n => n.StringIndex == 5).Fret);

        h.History.Undo();

        Assert.Equal(5, template.Frets[5]);
        Assert.Equal(5, edited.ChordNotes.Single(n => n.StringIndex == 5).Fret);
        Assert.Equal(5, sibling.ChordNotes.Single(n => n.StringIndex == 5).Fret);
    }

    private static Chord BoundChord(int templateId, params (int stringIdx, int fret)[] notes)
    {
        var chord = new Chord { TemplateId = templateId };
        foreach (var (stringIdx, fret) in notes)
            chord.ChordNotes.Add(new ChordNote { StringIndex = stringIdx, Fret = fret });
        return chord;
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
