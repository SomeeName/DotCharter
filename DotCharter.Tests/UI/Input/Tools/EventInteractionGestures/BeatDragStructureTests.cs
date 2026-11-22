using Avalonia;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.UI.Input.Tools;
using DotCharter.UI.Input.Tools.EventInteractionGestures;

namespace DotCharter.Tests.UI.Input.Tools.EventInteractionGestures;

public sealed class BeatDragStructureTests
{
    [Theory]
    [InlineData("insert", "move")]
    [InlineData("insert", "commit")]
    [InlineData("insert", "cancel")]
    [InlineData("remove", "move")]
    [InlineData("remove", "commit")]
    [InlineData("remove", "cancel")]
    [InlineData("replace", "move")]
    [InlineData("replace", "commit")]
    [InlineData("replace", "cancel")]
    [InlineData("move", "move")]
    [InlineData("move", "commit")]
    [InlineData("move", "cancel")]
    public void AStructuralBeatEditEndsTheDragWithoutReplayingItsStaleSnapshot(string edit, string nextEvent)
    {
        var project = new ChartProject();
        for (var i = 0; i < 4; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
        var track = new TrackEditorContext(new Arrangement(), new SelectionService()) { Project = project };
        var history = new RecordingHistory();
        var gesture = new BeatDragGesture(history, new FakeConfigurationService(), new FakeAppLogger());
        gesture.Start(track, project.Beats[1], 1, 500, new Point(0, 0));
        var context = new ToolContext(null!, null!, null!) { Position = new Point(20, 0), ExactTimeMs = 600 };
        gesture.OnMoved(context);
        Assert.True(gesture.HasDragged);

        switch (edit)
        {
            case "insert":
                project.Beats.Insert(1, new BeatMarker { Time = 250, Bpm = 90 });
                break;
            case "remove":
                project.Beats.RemoveAt(1);
                break;
            case "replace":
                project.Beats[1] = new BeatMarker { Time = 550, Bpm = 100 };
                break;
            case "move":
                project.Beats.Move(1, 2);
                break;
        }

        var times = project.Beats.Select(b => b.Time).ToArray();
        var tempos = project.Beats.Select(b => b.Bpm).ToArray();
        switch (nextEvent)
        {
            case "move":
                gesture.OnMoved(context);
                break;
            case "commit":
                gesture.Commit(context);
                break;
            case "cancel":
                gesture.Cancel(context);
                break;
        }

        Assert.False(gesture.IsActive);
        Assert.False(gesture.HasDragged);
        Assert.Equal(times, project.Beats.Select(b => b.Time));
        Assert.Equal(tempos, project.Beats.Select(b => b.Bpm));
        Assert.False(history.CanUndo);
        Assert.Equal(0, history.ActionCount);
    }

    // Commit must not reach history at all after a structural edit. Refusing
    // actions also keeps the unrelated shell/redraw callback out of this fixture.
    private sealed class RecordingHistory() : HistoryService(new FakeAppLogger())
    {
        public int ActionCount { get; private set; }

        public override bool Do(IChartAction action)
        {
            _ = action;
            ActionCount++;
            return false;
        }
    }
}
