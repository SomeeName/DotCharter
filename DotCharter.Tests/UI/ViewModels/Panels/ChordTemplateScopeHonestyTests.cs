using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     The chord panel's "Apply to" ladder says what the next edit reaches, and
///     the edit does exactly that (#483, then #484's ladder). Each case is one state of the issue: the
///     switch used to show "this chord" lit with nothing selected while the edit
///     rewrote every use, refused a fret edit without a word, counted chords but
///     not hand shapes, and named an isolated reshape "(Isolated)".
/// </summary>
public sealed class ChordTemplateScopeHonestyTests
{
    [Fact]
    public void NothingSelected_StatesTheShapeItself_AndOffersNoThisChord()
    {
        var h = new Harness();
        var shape = h.AddTemplate(0, "A5", (0, 5), (1, 7), (2, 7));
        var chords = h.AddChords(0, 2, (0, 5), (1, 7), (2, 7));

        var vm = h.CreateChordEditor();

        Assert.Same(shape, vm.Template);
        // Nothing selected: "this chord" and "selected" have nothing to reach,
        // so the lit rung is "every use" — what browse mode edits.
        Assert.True(vm.Ladder.IsVisible);
        Assert.False(vm.Ladder.ThisChord.IsAvailable);
        Assert.False(vm.Ladder.Selected.IsAvailable);
        Assert.Equal(ChordApplyRung.EveryUse, vm.Ladder.Effective);
        Assert.True(vm.Ladder.EveryUse.IsEffective);
        Assert.Equal(2, vm.Ladder.EveryUse.Reach.Chords);

        // …and the statement is TRUE: a rename lands on the shape itself, every
        // use of it, in place.
        vm.ChordName = "A5 power";
        Assert.Equal("A5 power", shape.Name);
        Assert.All(chords, c => Assert.Equal(0, c.TemplateId));
    }

    [Fact]
    public void ASelectionOnASharedShape_OffersEveryRung()
    {
        var h = new Harness();
        h.AddTemplate(0, "A5", (0, 5), (1, 7), (2, 7));
        var chords = h.AddChords(0, 3, (0, 5), (1, 7), (2, 7));

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);

        Assert.True(vm.Ladder.ThisChord.IsAvailable);
        Assert.True(vm.Ladder.Selected.IsEffective);
        Assert.Equal(1, vm.Ladder.Selected.Reach.Sounds);
        Assert.Equal(3, vm.Ladder.EveryUse.Reach.Sounds);
        // No other template holds this grip.
        Assert.False(vm.Ladder.SameGrip.IsAvailable);
    }

    /// <summary>Selecting every use makes "selected" and "every use" the same
    /// edit, so it is made in place — no template is minted and orphaned.</summary>
    [Fact]
    public void SelectingEveryUse_EditsInPlace()
    {
        var h = new Harness();
        h.AddTemplate(0, "A5", (0, 5), (1, 7), (2, 7));
        var chords = h.AddChords(0, 2, (0, 5), (1, 7), (2, 7));

        var vm = h.CreateChordEditor();
        foreach (var chord in chords) h.Selection.SelectedChords.Add(chord);

        vm.ChordName = "A5 power";

        Assert.Single(h.Arrangement.ChordTemplates);
        Assert.Equal("A5 power", h.Arrangement.ChordTemplates[0].Name);
        Assert.All(chords, c => Assert.Equal(0, c.TemplateId));
    }

    [Fact]
    public void AFretEditWithNothingSelected_ShowsTheHint_PushesNoHistory_AndPutsTheBoxBack()
    {
        var h = new Harness();
        var shape = h.AddTemplate(0, "A5", (0, 5), (1, 7), (2, 7));
        var chords = h.AddChords(0, 2, (0, 5), (1, 7), (2, 7));

        var vm = h.CreateChordEditor();
        Assert.False(vm.Ladder.ShowsReshapeHint);

        vm.StringInputs[2].FretText = "9";

        Assert.True(vm.Ladder.ShowsReshapeHint, "a refused reshape must say why");
        Assert.False(h.History.CanUndo);
        Assert.Equal(7, shape.Frets[2]);
        Assert.Equal("7", vm.StringInputs[2].FretText);

        // Selecting a chord answers the hint, so it goes.
        h.Selection.SelectedChords.Add(chords[0]);
        Assert.False(vm.Ladder.ShowsReshapeHint);
    }

    /// <summary>
    ///     A hover-typed fret run anchors on the node its first digit pushed, and
    ///     the next digit UNDOES that node. A refused digit pushes nothing, so the
    ///     run anchored on whatever node was current — somebody else's work — and
    ///     the second digit undid it.
    /// </summary>
    [Fact]
    public void ARefusedHoverFretRun_NeverUndoesEarlierWork()
    {
        var h = new Harness();
        var shape = h.AddTemplate(0, "A5", (0, 5), (1, 7), (2, 7));
        h.AddChords(0, 1, (0, 5), (1, 7), (2, 7));

        var vm = h.CreateChordEditor();
        vm.ChordName = "A5 power";
        Assert.True(h.History.CanUndo);

        vm.SetHoveredFretCommand.Execute(new ChordBoxFretEdit(2, 1, ContinuesRun: false));
        vm.SetHoveredFretCommand.Execute(new ChordBoxFretEdit(2, 12, ContinuesRun: true));

        Assert.Equal("A5 power", shape.Name);
        Assert.Equal(7, shape.Frets[2]);
        Assert.True(vm.Ladder.ShowsReshapeHint);
    }

    /// <summary>The global edit rewrites the shared template in place, so every
    /// hand shape on it changes too — on every level. The count says so.</summary>
    [Fact]
    public void TheCount_IncludesHandShapes_AndCountsTheLevels()
    {
        var h = new Harness();
        h.AddTemplate(0, "A5", (0, 5), (1, 7), (2, 7));
        var chords = h.AddChords(0, 2, (0, 5), (1, 7), (2, 7));
        h.Arrangement.Levels[0].HandShapes.Add(new HandShape { TemplateId = 0, Position = new FractionalPosition(40) });

        var deeper = new ChartLevel();
        deeper.Chords.Add(Chord(0, new FractionalPosition(0), (0, 5), (1, 7), (2, 7)));
        deeper.HandShapes.Add(new HandShape { TemplateId = 0, Position = new FractionalPosition(40) });
        h.Arrangement.Levels.Add(deeper);

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);

        var everyUse = vm.Ladder.EveryUse.Reach;
        Assert.Equal(3, everyUse.Chords);
        Assert.Equal(2, everyUse.HandShapes);
        Assert.Equal(2, everyUse.Levels);
        Assert.Equal(5, everyUse.Sounds);
    }

    [Fact]
    public void AnIsolatedReshape_TakesTheDestinationTemplatesName()
    {
        var h = new Harness();
        h.AddTemplate(0, "A5", (0, 5), (1, 7), (2, 7));
        // The shape the reshape lands on already exists, under its own name.
        var destination = h.AddTemplate(1, "Asus2 voicing", (0, 5), (1, 7), (2, 9));
        var chords = h.AddChords(0, 2, (0, 5), (1, 7), (2, 7));

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);
        Assert.Equal(ChordApplyRung.Selected, vm.ApplyRung);

        vm.StringInputs[2].FretText = "9";

        Assert.Equal(destination.Id, chords[0].TemplateId);
        Assert.Equal("Asus2 voicing", destination.Name);
        Assert.Equal("Asus2 voicing", vm.ChordName);
        Assert.Equal(0, chords[1].TemplateId);
        Assert.DoesNotContain(h.Arrangement.ChordTemplates, t => t.Name.Contains("(Isolated)"));
    }

    [Fact]
    public void AnIsolatedReshapeThatMints_TakesTheSuggestersName()
    {
        var h = new Harness();
        var shared = h.AddTemplate(0, "A5", (0, 5), (1, 7), (2, 7));
        var chords = h.AddChords(0, 2, (0, 5), (1, 7), (2, 7));

        var vm = h.CreateChordEditor();
        h.Selection.SelectedChords.Add(chords[0]);

        vm.StringInputs[0].FretText = "7";

        var minted = h.Arrangement.ChordTemplates.Single(t => t.Id == chords[0].TemplateId);
        Assert.NotSame(shared, minted);
        var suggested = ChordNameSuggester.SuggestFirstChordName(h.Arrangement, minted.Frets);
        Assert.False(string.IsNullOrEmpty(suggested), "the fixture must be a shape the suggester can name");
        Assert.Equal(suggested, minted.Name);
        Assert.Equal("A5", shared.Name);
    }

    private static Chord Chord(int templateId, FractionalPosition position, params (int stringIdx, int fret)[] notes)
    {
        var chord = new Chord { TemplateId = templateId, Position = position };
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
            var config = new FakeConfigurationService();
            var project = new ChartProject();
            Arrangement.Levels.Add(new ChartLevel());
            project.Arrangements.Add(Arrangement);

            State.ActiveProject = project;
            State.ActiveArrangement = Arrangement;
            State.ActiveLevel = Arrangement.Levels[0];

            var transport = new TransportViewModel(new FakeAudioPlaybackService(), State, config, FakeLocalization.KeysOnly());
            Editor = new ChartEditorViewModel(State, transport, History, Selection, new TempoService(logger),
                new TrackContextRegistry(State, Selection), FakeLocalization.KeysOnly());
        }

        public Arrangement Arrangement { get; } = new() { Name = "Lead", ArrangementType = ArrangementType.Lead };
        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public HistoryService History { get; }
        public ChartEditorViewModel Editor { get; }

        public ChordTemplate AddTemplate(int id, string name, params (int stringIdx, int fret)[] frets)
        {
            var template = new ChordTemplate { Id = id, Name = name };
            foreach (var (stringIdx, fret) in frets)
                template.Frets[stringIdx] = fret;
            Arrangement.ChordTemplates.Add(template);
            return template;
        }

        public List<Chord> AddChords(int templateId, int count, params (int stringIdx, int fret)[] notes)
        {
            var result = new List<Chord>();
            for (var i = 0; i < count; i++)
            {
                var chord = Chord(templateId, new FractionalPosition(i * 4), notes);
                Arrangement.Levels[0].Chords.Add(chord);
                result.Add(chord);
            }

            return result;
        }

        public ChordTemplateEditorViewModel CreateChordEditor() =>
            new(Editor, History, new FingeringGenerationService(), FakeLocalization.English(), new InlineDispatcher());

        private sealed class InlineDispatcher : IUiDispatcher
        {
            public bool CheckAccess() => true;
            public void Post(Action action) => action();
            public void PostNormal(Action action) => action();
            public Task InvokeAsync(Action action)
            {
                action();
                return Task.CompletedTask;
            }

            public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
            public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
        }
    }
}
