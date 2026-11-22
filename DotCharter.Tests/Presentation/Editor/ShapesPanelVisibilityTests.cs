using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Presentation.Navigation;
using DotCharter.Presentation.Screens.Editor;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tests.Services.Editing.ChordShapes;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Tests.Presentation.Editor;

public sealed class ShapesPanelVisibilityTests
{
    [Fact]
    public void HiddenSwitchesWaitForOneRefreshWhenShown()
    {
        var rig = new ShapesSwitchRig();
        var rows = rig.Screen.Rows;
        var changes = 0;
        rig.Screen.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShapesPanelViewModel.Rows)) changes++;
        };
        Hide(rig.Screen);

        rig.Chart.State.ActiveArrangement = rig.Chart.Project.Arrangements[1];
        rig.Chart.State.ActiveArrangement = rig.Chart.Project.Arrangements[2];
        rig.Dispatcher.Drain();

        Assert.Same(rows, rig.Screen.Rows);
        Assert.Equal(0, changes);
        Assert.Empty(rig.Dispatcher.Posted);

        Assert.IsAssignableFrom<IScreenLifecycle>(rig.Screen).OnShown();
        rig.Dispatcher.Drain();

        Assert.Equal(1, changes);
        Assert.Same(rig.Chart.Project.Arrangements[2].ChordTemplates[0], rig.Screen.AllRows[0].Template);
    }

    [Fact]
    public void HidingAfterAPostParksTheAlreadyQueuedRefresh()
    {
        var rig = new ShapesSwitchRig();
        var rows = rig.Screen.Rows;
        rig.Chart.State.ActiveArrangement = rig.Chart.Project.Arrangements[1];
        Assert.Single(rig.Dispatcher.Posted);
        Hide(rig.Screen);

        rig.Dispatcher.Drain();

        Assert.Same(rows, rig.Screen.Rows);
        Assert.IsAssignableFrom<IScreenLifecycle>(rig.Screen).OnShown();
        var shown = rig.Screen.Rows;
        Assert.NotSame(rows, shown);
        Assert.IsAssignableFrom<IScreenLifecycle>(rig.Screen).OnShown();
        rig.Dispatcher.Drain();
        Assert.Same(shown, rig.Screen.Rows);
    }

    [Fact]
    public void HiddenEditsUndoRedoAndAnInactiveArrangementAreReadOnShow()
    {
        var rig = new ShapesSwitchRig();
        var rows = rig.Screen.Rows;
        var target = rig.Chart.Project.Arrangements[0].ChordTemplates[0];
        Hide(rig.Screen);
        rig.Chart.State.ActiveArrangement = rig.Chart.Project.Arrangements[1];
        rig.Chart.History.Do(new Rename(target, "Revised"));
        rig.Chart.History.Undo();
        rig.Chart.History.Redo();
        rig.Chart.State.ActiveArrangement = rig.Chart.Project.Arrangements[0];
        rig.Dispatcher.Drain();

        Assert.Same(rows, rig.Screen.Rows);
        Assert.NotEqual("Revised", rig.Screen.AllRows[0].NameText);
        Assert.IsAssignableFrom<IScreenLifecycle>(rig.Screen).OnShown();
        Assert.Equal("Revised", rig.Screen.AllRows[0].NameText);
    }

    [Fact]
    public void AHiddenProjectReloadReleasesTheClosedChartsRows()
    {
        var rig = new ShapesSwitchRig();
        Hide(rig.Screen);
        rig.Chart.Editor.ApplyProject(new ChartProject());

        Assert.Empty(rig.Screen.Rows);
        Assert.Empty(rig.Screen.AllRows);
        rig.Dispatcher.Drain();
        Assert.IsAssignableFrom<IScreenLifecycle>(rig.Screen).OnShown();
        Assert.False(rig.Screen.HasRows);
    }

    private static void Hide(IShapePanelScreen screen) => (screen as IScreenLifecycle)?.OnHidden();

    private sealed class Rename(ChordTemplate template, string name) : IChartAction
    {
        private readonly string _previous = template.Name;
        public void Execute() => template.Name = name;
        public void Undo() => template.Name = _previous;
    }
}

internal sealed class ShapesSwitchRig
{
    public ChartEditHarness Chart { get; } = new();
    public ShapesQueuedDispatcher Dispatcher { get; } = new();
    public ShapesPanelViewModel Screen { get; }

    public ShapesSwitchRig()
    {
        Chart.Project.Beats.Clear();
        for (var i = 0; i < 846; i++)
            Chart.Project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, IsMeasure = i % 4 == 0 });
        Chart.Project.Arrangements.Clear();
        for (var part = 0; part < 3; part++)
        {
            var arrangement = new Arrangement { Name = $"Part {part}", StringCount = 6 };
            for (var shape = 0; shape < 64; shape++)
            {
                arrangement.ChordTemplates.Add(new ChordTemplate
                {
                    Id = shape, Name = $"Shape {shape}", Frets = new() { [0] = shape % 20, [1] = shape / 20 + 1 },
                    Fingers = new() { [0] = 1, [1] = 3 }
                });
            }
            for (var level = 0; level < 18; level++)
            {
                var chartLevel = new ChartLevel { DifficultyIndex = level };
                for (var phrase = 0; phrase < 40; phrase++)
                    chartLevel.Chords.Add(ChordShapesFixture.ChordOn(arrangement.ChordTemplates[phrase], phrase * 20));
                arrangement.Levels.Add(chartLevel);
            }
            for (var phrase = 0; phrase < 40; phrase++)
                arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(phrase * 20), Phrase = $"Phrase {phrase}" });
            Chart.Project.Arrangements.Add(arrangement);
        }
        Chart.State.ActiveArrangement = Chart.Project.Arrangements[0];
        Screen = new ShapesPanelViewModel(Chart.Editor, Chart.History, Chart.State, FakeLocalization.English(), Dispatcher,
            new ShapesPanelParts(ChordShapePlacementServiceTests.Build(Chart.State, Chart.Editor.Selection, Chart.History),
                Chart.Transport, new ScriptedDialogs(_ => throw new InvalidOperationException(), _ => { })));
    }
}

internal sealed class ShapesQueuedDispatcher : IUiDispatcher
{
    public Queue<Action> Posted { get; } = new();
    public bool CheckAccess() => true;
    public void Post(Action action) => Posted.Enqueue(action);
    public void PostNormal(Action action) => Posted.Enqueue(action);
    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }
    public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
    public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
    public void Drain()
    {
        while (Posted.TryDequeue(out var action)) action();
    }
}
