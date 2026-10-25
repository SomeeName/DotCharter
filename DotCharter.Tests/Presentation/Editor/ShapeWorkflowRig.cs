using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Presentation.Screens.Editor;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tests.Services.Editing.ChordShapes;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.Presentation.Editor;

/// <summary>
///     What the user does, counted. A <see cref="Do" /> is a click or a typed edit that costs a step; a
///     <see cref="Pick" /> only sets up what the verb acts on (ticking cards, marking a passage in the chart,
///     choosing the scope the dialog states), which the design's step tally does not charge. Both are logged,
///     so a failure prints the whole path.
/// </summary>
internal sealed class UserSteps
{
    private readonly List<string> _log = [];

    public int Charged { get; private set; }

    public int Total => _log.Count;

    public string Path => string.Join(" > ", _log);

    public void Do(string what, Action act)
    {
        Charged++;
        _log.Add(what);
        act();
    }

    public void Pick(string what, Action act)
    {
        _log.Add("(" + what + ")");
        act();
    }
}

/// <summary>
///     An old, messy 7-string chart (<see cref="MessyShapesFixture" />) behind the REAL Shapes panel, shape editor
///     and undo history, with the user's path scripted through the commands a click would run.
/// </summary>
internal sealed class ShapeWorkflowRig
{
    public ShapeWorkflowRig()
    {
        Chart.Project.Arrangements.Add(Old.Arrangement);
        Chart.Editor.ActiveArrangement = Old.Arrangement;
        Chart.State.ActiveLevel = Old.Level0;
        var dialogs = new ScriptedDialogs(
            _ => new ShapeEditViewModel(Chart.Services, new ChordUseMarks(Chart.Editor)),
            screen => EditDialog((IShapeEditScreen)screen));
        Panel = new ShapesPanelViewModel(Chart.Editor, Chart.History, Chart.State, FakeLocalization.English(), new TestUiDispatcher(),
            new ShapesPanelParts(ChordShapePlacementServiceTests.Build(Chart.State, Chart.Editor.Selection, Chart.History),
                Chart.Transport, dialogs));
    }

    public ChartEditHarness Chart { get; } = new();

    /// <summary>The old chart the editor holds.</summary>
    public MessyShapesFixture Old { get; } = new();

    public ShapesPanelViewModel Panel { get; }

    public UserSteps Steps { get; } = new();

    /// <summary>What the user does inside the Edit shape dialog once it opens.</summary>
    public Action<IShapeEditScreen> EditDialog { get; set; } = screen => screen.CancelCommand.Execute(null);

    public ShapePanelRowViewModel Row(ChordTemplate template) => Panel.AllRows.Single(r => ReferenceEquals(r.Template, template));

    public void Tick(params ChordTemplate[] shapes) => Steps.Pick("tick " + shapes.Length, () =>
    {
        foreach (var shape in shapes) Row(shape).IsSelected = true;
    });

    /// <summary>The chart as a user could read it: every shape, then which shape every chord and hand shape wears.</summary>
    public string Describe()
    {
        var lines = new List<string>();
        foreach (var t in Old.Arrangement.ChordTemplates)
            lines.Add($"{t.Id} {(t.Name.Length == 0 ? "-" : t.Name)} frets {Join(t.Frets)} fingers {Join(t.Fingers)}");
        foreach (var level in Old.Arrangement.Levels)
        {
            lines.Add($"L{level.DifficultyIndex} chords {string.Join(' ', level.Chords.Select(c => $"{c.TemplateId}@{c.Position.BeatId}"))}");
            lines.Add($"L{level.DifficultyIndex} hands {string.Join(' ', level.HandShapes.Select(h => $"{h.TemplateId}@{h.Position.BeatId}"))}");
        }

        lines.Add($"voice chords {string.Join(' ', Old.Arrangement.NotationVoices.SelectMany(v => v.Chords).Select(c => $"{c.TemplateId}@{c.Position.BeatId}"))}");
        return string.Join('\n', lines);
    }

    /// <summary>Every chord still plays exactly the frets of the shape it wears.</summary>
    public void AssertChordsPlayTheirShapes()
    {
        var byId = Old.Arrangement.ChordTemplates.ToDictionary(t => t.Id);
        foreach (var chord in Old.AllChords)
        {
            var frets = chord.ChordNotes.ToDictionary(n => n.StringIndex, n => n.Fret);
            Assert.Equal(Join(byId[chord.TemplateId].Frets), Join(frets));
        }
    }

    private static string Join(Dictionary<int, int> map) => string.Join(' ', map.OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value}"));

    /// <summary>The path costs no more than the design's tally and fewer steps than delete-and-redo, whose count includes its own selecting.</summary>
    public void AssertSteps(int design, int deleteAndRedo)
    {
        Assert.True(Steps.Charged <= design, $"{Steps.Charged} steps, the design allows {design}: {Steps.Path}");
        Assert.True(Steps.Total < deleteAndRedo, $"{Steps.Total} steps including selecting, delete-and-redo takes {deleteAndRedo}: {Steps.Path}");
    }

    /// <summary>One undo restores the chart exactly and leaves nothing more to undo.</summary>
    public void AssertOneUndoRestores(string before)
    {
        Assert.Single(Chart.History.GetUndoSteps());
        Chart.History.Undo();
        Assert.Equal(before, Describe());
        Assert.False(Chart.History.CanUndo);
        AssertChordsPlayTheirShapes();
    }
}
