using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Events;
using DotCharter.Services.Editing.Selection;

namespace DotCharter.Tests.Services.Editing;

/// <summary>
///     Removing an event point must take it out of the SELECTION too.
///     <para>
///         A selection entry pointing at an object no longer in the chart is not
///         cosmetic. <c>GlobalEditingCoordinator.BuildDeleteActions</c> turns the
///         selection into a <c>RemoveEventPointsAction</c>, and that action's
///         <c>HasEffect</c> only counts items — so pressing Delete on a stale
///         entry pushes an undo node that removes nothing, whose <b>Undo
///         re-inserts the deleted marker</b>. A Ctrl+Z that CREATES something is
///         the worst shape an undo stack can take.
///     </para>
///     <para>
///         Every other delete path in the editor already prunes:
///         <c>BuildDeleteActions</c> clears each collection it deletes from, and
///         the lyric right-click in <c>EventInteractionTool</c> nulls
///         <c>SelectedLyric</c>. The event-point funnel did not, and the
///         Shift+right-click delete on the event ribbon goes straight through it.
///     </para>
/// </summary>
public sealed class EventPointRemovalSelectionTests
{
    private static (EventEditingService Service, Arrangement Arrangement, ISelectionService Selection) NewService()
    {
        var state = new ProjectState();
        var logger = new FakeAppLogger();
        var history = new HistoryService(logger);

        var project = new ChartProject();
        for (var i = 0; i < 16; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = i % 4 == 0 });

        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        project.Arrangements.Add(arrangement);

        state.ActiveProject = project;
        state.ActiveArrangement = arrangement;
        state.ActiveLevel = arrangement.Levels[0];

        var selection = new SelectionService();
        var service = new EventEditingService(state, selection, history, new EditingScope(state), logger);
        return (service, arrangement, selection);
    }

    [Fact]
    public void RemovingASelectedEventPoint_DropsItFromTheSelection()
    {
        var (service, arrangement, selection) = NewService();
        var marker = new EventPoint { Position = new FractionalPosition(4), Section = "chorus" };
        arrangement.EventPoints.Add(marker);
        selection.SelectedEventPoints.Add(marker);

        service.RemoveEventPoints([marker]);

        Assert.DoesNotContain(marker, arrangement.EventPoints);
        Assert.Empty(selection.SelectedEventPoints);
    }

    [Fact]
    public void RemovingOneMarker_LeavesTheOtherSelectionEntriesAlone()
    {
        var (service, arrangement, selection) = NewService();
        var doomed = new EventPoint { Position = new FractionalPosition(4), Section = "chorus" };
        var keeper = new EventPoint { Position = new FractionalPosition(8), Section = "verse" };
        arrangement.EventPoints.Add(doomed);
        arrangement.EventPoints.Add(keeper);
        selection.SelectedEventPoints.Add(doomed);
        selection.SelectedEventPoints.Add(keeper);

        service.RemoveEventPoints([doomed]);

        Assert.Equal([keeper], selection.SelectedEventPoints);
    }

    /// <summary>The hover highlight names the removed marker too, and the
    /// renderer reads it straight into the frame snapshot.</summary>
    [Fact]
    public void RemovingTheHoveredEventPoint_ClearsTheHover()
    {
        var (service, arrangement, selection) = NewService();
        var marker = new EventPoint { Position = new FractionalPosition(4), Section = "chorus" };
        arrangement.EventPoints.Add(marker);
        selection.HoveredEventPoint = marker;

        service.RemoveEventPoints([marker]);

        Assert.Null(selection.HoveredEventPoint);
    }
}
