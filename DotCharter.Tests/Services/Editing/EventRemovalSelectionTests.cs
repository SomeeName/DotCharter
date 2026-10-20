using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Events;
using DotCharter.Services.Editing.Selection;

namespace DotCharter.Tests.Services.Editing;

/// <summary>
///     Removing an event point drops it from the selection too. A removed point
///     left in <c>SelectedEventPoints</c> is what the next Delete would name
///     again — and (before <c>RemoveElementsAction</c> learnt to restore only
///     what it removed) an undo of THAT put it back.
/// </summary>
public sealed class EventRemovalSelectionTests
{
    [Fact]
    public void RemoveEventPoints_DropsThemFromTheSelection()
    {
        var state = new ProjectState();
        var logger = new FakeAppLogger();
        var history = new HistoryService(logger);
        var selection = new SelectionService();

        var project = new ChartProject();
        for (var i = 0; i < 16; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = i % 4 == 0 });
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        project.Arrangements.Add(arrangement);
        state.ActiveProject = project;
        state.ActiveArrangement = arrangement;
        state.ActiveLevel = arrangement.Levels[0];

        var service = new EventEditingService(state, selection, history, new EditingScope(state), logger);

        var kept = new EventPoint { Position = new FractionalPosition(0), Section = "intro" };
        var doomed = new EventPoint { Position = new FractionalPosition(4), Section = "verse" };
        arrangement.EventPoints.Add(kept);
        arrangement.EventPoints.Add(doomed);
        selection.SelectedEventPoints.Add(kept);
        selection.SelectedEventPoints.Add(doomed);

        service.RemoveEventPoints([doomed]);

        Assert.DoesNotContain(doomed, arrangement.EventPoints);
        Assert.DoesNotContain(doomed, selection.SelectedEventPoints);
        Assert.Contains(kept, selection.SelectedEventPoints);

        // The gesture a stale selection used to break: Delete over what is
        // still selected, then Ctrl+Z, must not bring the removed point back.
        history.Do(new RemoveEventPointsAction(arrangement.EventPoints, selection.SelectedEventPoints.ToList()));
        history.Undo();
        Assert.Contains(kept, arrangement.EventPoints);
        Assert.DoesNotContain(doomed, arrangement.EventPoints);
    }

    /// <summary>
    ///     …and the line it writes on the way out is capped, like every other
    ///     editing verb (<c>EditLogDetail</c>). It was a bare
    ///     <c>string.Join</c> over the whole removal, so "select all markers,
    ///     Delete" formatted every one of them before the logger was asked
    ///     whether it wanted them.
    /// </summary>
    [Fact]
    public void RemoveEventPoints_LogLineIsCapped()
    {
        var state = new ProjectState();
        var logger = new FakeAppLogger();
        var selection = new SelectionService();

        var project = new ChartProject();
        for (var i = 0; i < 4_100; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = i % 4 == 0 });
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        project.Arrangements.Add(arrangement);
        state.ActiveProject = project;
        state.ActiveArrangement = arrangement;
        state.ActiveLevel = arrangement.Levels[0];

        var service = new EventEditingService(state, selection, new HistoryService(logger),
            new EditingScope(state), logger);

        for (var i = 0; i < 4_000; i++)
        {
            arrangement.EventPoints.Add(new EventPoint
            { Position = new FractionalPosition(i), Section = $"section-{i}", Phrase = $"phrase-{i}" });
        }

        var before = logger.Entries.Count;
        service.RemoveEventPoints(arrangement.EventPoints.ToList());

        var line = Assert.Single(logger.Entries.Skip(before), e => e.Message.StartsWith("Removing "));
        Assert.Contains("4000 event point(s)", line.Message);
        Assert.Contains("more", line.Message);
        Assert.True(line.Message.Length < 8_000,
            $"the removal line is {line.Message.Length} chars for 4,000 points — it is spelling out the whole removal.");
    }

    /// <summary>
    ///     The drag-and-drop commit was the last event verb still writing ONE
    ///     LINE PER POINT — the #1 log-volume shape session 6 named and left.
    ///     A drag over "select all markers" produced thousands of Info lines,
    ///     each formatted (two beat→ms lookups apiece) before the logger was
    ///     asked whether it wanted them.
    /// </summary>
    [Fact]
    public void CommitEventDragAndDrop_WritesOneCappedLine()
    {
        var state = new ProjectState();
        var logger = new FakeAppLogger();
        var selection = new SelectionService();

        var project = new ChartProject();
        for (var i = 0; i < 4_100; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = i % 4 == 0 });
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        project.Arrangements.Add(arrangement);
        state.ActiveProject = project;
        state.ActiveArrangement = arrangement;
        state.ActiveLevel = arrangement.Levels[0];

        var service = new EventEditingService(state, selection, new HistoryService(logger),
            new EditingScope(state), logger);

        var oldStates = new List<EventPoint>();
        var newStates = new List<EventPoint>();
        for (var i = 0; i < 4_000; i++)
        {
            var point = new EventPoint { Position = new FractionalPosition(i), Section = $"s{i}", Phrase = $"p{i}" };
            arrangement.EventPoints.Add(point);
            selection.SelectedEventPoints.Add(point);
            oldStates.Add(point);
            newStates.Add(new EventPoint { Position = new FractionalPosition(i + 1), Section = $"s{i}", Phrase = $"p{i}" });
        }

        var before = logger.Entries.Count;
        Assert.True(service.CommitEventDragAndDrop(oldStates, newStates));

        var lines = logger.Entries.Skip(before).Where(e => e.Message.StartsWith("Moved ")).ToList();
        var line = Assert.Single(lines);
        Assert.Contains("4000 event point(s)", line.Message);
        Assert.Contains("more", line.Message);
        Assert.True(line.Message.Length < 8_000,
            $"the drag line is {line.Message.Length} chars for 4,000 points — it is spelling out the whole move.");
    }
}
