using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     The Chord panel's writes (dropdown pick, Create new, the containing-shape offer, a rebind of a
///     chord whose template drifted) move chords and hand shapes off a template. When that was its last
///     use the template leaves in the same undo node, and one undo brings it back with its id.
/// </summary>
public sealed class ChordTemplateStrandedShapeTests
{
    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly SelectionService _selection = new();
    private readonly ProjectState _state = new();
    private readonly Arrangement _arrangement = new() { Name = "Lead", ArrangementType = ArrangementType.Lead };
    private readonly ChartEditorViewModel _editor;
    private readonly ChordTemplateEditPlanner _planner;

    public ChordTemplateStrandedShapeTests()
    {
        var logger = new FakeAppLogger();
        var project = new ChartProject();
        _arrangement.Levels.Add(new ChartLevel());
        project.Arrangements.Add(_arrangement);
        _state.ActiveProject = project;
        _state.ActiveArrangement = _arrangement;
        _state.ActiveLevel = _arrangement.Levels[0];

        var config = new FakeConfigurationService();
        var transport = new TransportViewModel(new FakeAudioPlaybackService(), _state, config, FakeLocalization.KeysOnly());
        _editor = new ChartEditorViewModel(_state, transport, _history, _selection, new TempoService(logger),
            new TrackContextRegistry(_state, _selection), FakeLocalization.KeysOnly());
        _planner = new ChordTemplateEditPlanner(_editor, _history, new FingeringGenerationService(), config, logger);
    }

    private ChartLevel Level => _arrangement.Levels[0];

    [Fact]
    public void SwitchSelection_TakesTheShapeTheTargetsLeftOutWithIt()
    {
        var old = AddTemplate(0, (4, 5), (5, 5));
        var target = AddTemplate(1, (3, 7), (4, 7));
        var chord = AddChord(0, (4, 5), (5, 5));
        var bar = AddBar(0);

        _planner.SwitchSelection(target, [chord], [bar]);

        Assert.Same(target, Assert.Single(_arrangement.ChordTemplates));
        Assert.Equal((0, 0, 0), (target.Id, chord.TemplateId, bar.TemplateId));
        Assert.Single(_history.GetUndoSteps());

        _history.Undo();
        Assert.Equal([old, target], _arrangement.ChordTemplates);
        Assert.Equal([0, 1], _arrangement.ChordTemplates.Select(t => t.Id));
        Assert.Equal((0, 0), (chord.TemplateId, bar.TemplateId));

        _history.Redo();
        Assert.Same(target, Assert.Single(_arrangement.ChordTemplates));
    }

    [Fact]
    public void SwitchSelection_KeepsAShapeSomethingElseStillUses()
    {
        AddTemplate(0, (4, 5), (5, 5));
        var target = AddTemplate(1, (3, 7), (4, 7));
        var chord = AddChord(0, (4, 5), (5, 5));
        AddChord(0, (4, 5), (5, 5)).Position = new FractionalPosition(8);

        _planner.SwitchSelection(target, [chord], []);

        Assert.Equal(2, _arrangement.ChordTemplates.Count);
    }

    [Fact]
    public void ADropdownPick_TakesTheShapeTheSelectionLeftOutWithIt()
    {
        var old = AddTemplate(0, (4, 5), (5, 5));
        var target = AddTemplate(1, (3, 7), (4, 7));
        var chord = AddChord(0, (4, 5), (5, 5));
        var panel = CreatePanel();
        _selection.SelectedChords.Add(chord);

        panel.SelectedDropdownTemplate = panel.AvailableTemplates.Single(t => ReferenceEquals(t.Template, target));

        Assert.Same(target, Assert.Single(_arrangement.ChordTemplates));
        Assert.Equal(target.Id, chord.TemplateId);
        Assert.Single(_history.GetUndoSteps());

        _history.Undo();
        Assert.Equal([old, target], _arrangement.ChordTemplates);
        Assert.Equal(0, chord.TemplateId);
    }

    [Fact]
    public void CreateNew_TakesTheShapeItCopiedOutWithIt_WhenTheSelectionWasItsOnlyUse()
    {
        var old = AddTemplate(0, (4, 5), (5, 5));
        var chord = AddChord(0, (4, 5), (5, 5));
        var bar = AddBar(0);
        var panel = CreatePanel();
        _selection.SelectedChords.Add(chord);
        _selection.SelectedHandShapes.Add(bar);

        panel.CreateNewTemplateCommand.Execute(null);

        var copy = Assert.Single(_arrangement.ChordTemplates);
        Assert.NotSame(old, copy);
        Assert.Equal(old.Frets, copy.Frets);
        Assert.EndsWith("(Copy)", copy.Name, StringComparison.Ordinal);
        Assert.Equal((copy.Id, copy.Id), (chord.TemplateId, bar.TemplateId));
        Assert.Single(_history.GetUndoSteps());

        _history.Undo();
        Assert.Same(old, Assert.Single(_arrangement.ChordTemplates));
        Assert.Equal((0, 0), (chord.TemplateId, bar.TemplateId));
    }

    [Fact]
    public void CreateNew_KeepsTheShapeItCopied_WhenAnotherChordStillUsesIt()
    {
        var old = AddTemplate(0, (4, 5), (5, 5));
        var chord = AddChord(0, (4, 5), (5, 5));
        AddChord(0, (4, 5), (5, 5)).Position = new FractionalPosition(8);
        var panel = CreatePanel();
        _selection.SelectedChords.Add(chord);

        panel.CreateNewTemplateCommand.Execute(null);

        Assert.Equal(2, _arrangement.ChordTemplates.Count);
        Assert.Same(old, _arrangement.ChordTemplates[0]);
    }

    [Fact]
    public void ARebindOfAChordWhoseTemplateDrifted_TakesTheStaleShapeOutWithIt()
    {
        // The chord points at a template that does not describe its notes: the edit rebinds the chord to the
        // shape it plays, and nothing else uses the stale one.
        var stale = AddTemplate(0, (2, 12), (3, 12));
        var chord = AddChord(0, (4, 14), (5, 15));
        var panel = CreatePanel();
        _selection.SelectedChords.Add(chord);

        panel.StringInputs[5].FretText = "16";

        Assert.DoesNotContain(stale, _arrangement.ChordTemplates);
        var survivor = Assert.Single(_arrangement.ChordTemplates);
        Assert.Equal(16, survivor.Frets[5]);
        Assert.Equal(survivor.Id, chord.TemplateId);

        _history.Undo();
        Assert.Same(stale, Assert.Single(_arrangement.ChordTemplates));
        Assert.Equal(0, chord.TemplateId);
        Assert.Equal(15, chord.ChordNotes.Single(n => n.StringIndex == 5).Fret);
    }

    private ChordTemplateEditorViewModel CreatePanel() =>
        new(_editor, _history, new FingeringGenerationService(), FakeLocalization.English(), new InlineDispatcher());

    private ChordTemplate AddTemplate(int id, params (int stringIdx, int fret)[] frets)
    {
        var template = new ChordTemplate { Id = id, Name = "Shape" };
        foreach (var (stringIdx, fret) in frets) template.Frets[stringIdx] = fret;
        _arrangement.ChordTemplates.Add(template);
        return template;
    }

    private Chord AddChord(int templateId, params (int stringIdx, int fret)[] notes)
    {
        var chord = new Chord { TemplateId = templateId };
        foreach (var (stringIdx, fret) in notes) chord.ChordNotes.Add(new ChordNote { StringIndex = stringIdx, Fret = fret });
        Level.Chords.Add(chord);
        return chord;
    }

    private HandShape AddBar(int templateId)
    {
        var bar = new HandShape
        {
            TemplateId = templateId, Position = FractionalPosition.Zero, EndPosition = new FractionalPosition(4)
        };
        Level.HandShapes.Add(bar);
        return bar;
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
