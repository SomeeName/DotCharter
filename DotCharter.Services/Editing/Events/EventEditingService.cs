using DotCharter.Services.State;
using EventPoint = DotCharter.Core.Domain.Events.EventPoint;
using ProjectState = DotCharter.Services.State.ProjectState;

namespace DotCharter.Services.Editing.Events;

public class EventEditingService : IEventEditingService
{
    private readonly IHistoryService _historyManager;
    private readonly IAppLogger _logger;
    private readonly IEditingScope _scope;
    private readonly ISelectionService _selectionManager;

    public EventEditingService(
        ProjectState projectState,
        ISelectionService selectionManager,
        IHistoryService historyManager,
        IEditingScope scope,
        IAppLogger logger)
    {
        _ = projectState; // injected for DI symmetry; not currently used here.
        _selectionManager = selectionManager;
        _historyManager = historyManager;
        _scope = scope;
        _logger = logger;
    }

    /// <inheritdoc />
    public string LastSection { get; private set; } = string.Empty;

    /// <inheritdoc />
    public string LastPhrase { get; private set; } = string.Empty;

    /// <inheritdoc />
    public void RememberLastMarker(string section, string phrase)
    {
        // Only a marker that names a SECTION is worth repeating — a phrase-only
        // marker is a one-off (COUNT, END, a DD move marker), and stamping one
        // again would put a second copy of a unique phrase in the chart.
        if (string.IsNullOrWhiteSpace(section)) return;
        LastSection = section.Trim();
        LastPhrase = phrase?.Trim() ?? string.Empty;
    }

    /// <inheritdoc />
    public string LastEvents { get; private set; } = string.Empty;

    /// <inheritdoc />
    public IReadOnlyList<string> LastArrangementNames { get; private set; } = Array.Empty<string>();

    /// <inheritdoc />
    public void RememberLastTargets(string events, IEnumerable<string> arrangementNames)
    {
        // No section-only guard here, unlike RememberLastMarker: a phrase-only
        // marker (END, COUNT) is still placed on a chosen SET of arrangements,
        // and that set is exactly what the charter should not have to re-tick.
        LastEvents = events ?? string.Empty;
        LastArrangementNames = arrangementNames?.Where(n => !string.IsNullOrWhiteSpace(n)).ToList()
                               ?? (IReadOnlyList<string>)[];
    }

    /// <inheritdoc />
    public bool StampLastMarker(FractionalPosition position)
    {
        if (string.IsNullOrWhiteSpace(LastSection))
        {
            _logger.Info(AppLogCategory.Events, "Stamp section skipped: no marker has been saved yet.");
            return false;
        }

        var existing = _scope.EventPoints.FirstOrDefault(ep => ep.Position.Equals(position));
        if (existing != null)
        {
            if (string.Equals(existing.Section, LastSection, StringComparison.Ordinal) &&
                string.Equals(existing.Phrase, LastPhrase, StringComparison.Ordinal))
            {
                _logger.Info(AppLogCategory.Events,
                    $"Stamp section skipped: '{LastSection}' is already the marker at {position}.");
                return false;
            }

            // Replace rather than mutate in place: an EventPoint is captured by
            // reference in history, so editing one behind the undo stack's back
            // leaves a node that cannot restore what it replaced.
            var replacement = new EventPoint
            {
                Position = existing.Position,
                Section = LastSection,
                Phrase = LastPhrase,
                Status = existing.Status,
                Events = existing.Events
            };

            var replaced = _historyManager.Do(new CompositeAction(new List<IChartAction>
            {
                new RemoveEventPointsAction(_scope.EventPoints, new[] { existing }),
                new AddEventPointsAction(_scope.EventPoints, new[] { replacement })
            }));

            if (replaced)
            {
                _logger.Info(AppLogCategory.Events,
                    $"Stamped section '{LastSection}' (phrase '{LastPhrase}') over the marker at {position}.");
            }

            return replaced;
        }

        var added = _historyManager.Do(new AddEventPointsAction(_scope.EventPoints, new[]
        {
            new EventPoint { Position = position, Section = LastSection, Phrase = LastPhrase }
        }));

        if (added)
        {
            _logger.Info(AppLogCategory.Events,
                $"Stamped section '{LastSection}' (phrase '{LastPhrase}') at {position}.");
        }

        return added;
    }

    public void RemoveEventPoints(IEnumerable<EventPoint> eventPoints)
    {
        var points = eventPoints.ToList();
        if (!points.Any()) return;

        // Capped, not string.Join: a Delete over every section marker on a long
        // chart formatted the whole list before the logger was asked whether it
        // wanted it. See EditLogDetail.
        _logger.Info(AppLogCategory.Events,
            $"Removing {points.Count} event point(s): {EditLogDetail.Describe(points, FormatEventPoint)}.");
        // A selection entry naming a removed marker is not cosmetic: the Delete
        // key turns the selection into another RemoveEventPointsAction whose
        // HasEffect only counts items, so it pushes a node that removes nothing
        // and whose UNDO re-inserts the marker. The hover is live state the
        // same way (the wheel and the right-click both target it).
        foreach (var point in points) _selectionManager.SelectedEventPoints.Remove(point);
        if (_selectionManager.HoveredEventPoint != null &&
            points.Contains(_selectionManager.HoveredEventPoint))
        {
            _selectionManager.HoveredEventPoint = null;
        }

        _historyManager.Do(new RemoveEventPointsAction(_scope.EventPoints, points));
    }

    public bool CommitEventDragAndDrop(IEnumerable<EventPoint> oldStates, IEnumerable<EventPoint> newStates)
    {
        var oldList = oldStates.ToList();
        var newList = newStates.ToList();
        if (_historyManager.Do(new MutateEventPointsAction(_scope.EventPoints,
                _selectionManager.SelectedEventPoints, oldList, newList)))
        {
            // ONE capped line for the whole gesture (EditLogDetail), not one
            // line per moved point: a drag over "select all markers" wrote
            // thousands of lines, each formatted before the logger was asked.
            var moved = Math.Min(oldList.Count, newList.Count);
            var pairs = Enumerable.Range(0, moved).ToList();
            _logger.Info(AppLogCategory.Events,
                $"Moved {moved} event point(s): " + EditLogDetail.Describe(pairs, i =>
                    $"{FormatPosition(oldList[i].Position)}→{FormatPosition(newList[i].Position)} (section='{oldList[i].Section}'→'{newList[i].Section}', phrase='{oldList[i].Phrase}'→'{newList[i].Phrase}')"));
            return true;
        }

        return false;
    }

    private string FormatEventPoint(EventPoint point) => $"position={FormatPosition(point.Position)}, section='{point.Section}', phrase='{point.Phrase}'";

    private string FormatPosition(FractionalPosition position)
        => position.FormatWithMs(_scope.Beats);
}
