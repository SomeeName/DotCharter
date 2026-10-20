using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Structure;

/// <summary>What one structure pass actually changed.</summary>
public readonly record struct StructureGenerationResult(int CountPhrases, int EndPhrases, int NoGuitarSections)
{
    public int Total => CountPhrases + EndPhrases + NoGuitarSections;
    public bool IsEmpty => Total == 0;
}

public interface IStructureGenerationService
{
    /// <summary>
    ///     Fills in the structure a chart can be certain of — the COUNT and END
    ///     phrases and a <c>noguitar</c> section over each bracketed silence — as
    ///     ONE undoable step. Never invents a section boundary; see
    ///     <see cref="StructureGenerator" /> for why that is not derivable.
    /// </summary>
    StructureGenerationResult Generate(Arrangement? arrangement = null);
}

/// <summary>
///     The history-backed driver for <see cref="StructureGenerator" />. All the
///     decisions live in the pure planner; this only merges the plan into the
///     arrangement's existing markers and books it as a single action.
/// </summary>
public sealed class StructureGenerationService : IStructureGenerationService
{
    private readonly IHistoryService _history;
    private readonly IAppLogger _logger;
    private readonly ProjectState _projectState;

    public StructureGenerationService(ProjectState projectState, IHistoryService history, IAppLogger logger)
    {
        _projectState = projectState;
        _history = history;
        _logger = logger;
    }

    public StructureGenerationResult Generate(Arrangement? arrangement = null)
    {
        var target = arrangement ?? _projectState.ActiveArrangement;
        if (target == null)
        {
            _logger.Info(AppLogCategory.Events, "Structure generation skipped: no active arrangement.");
            return default;
        }

        var beats = _projectState.Beats;
        if (beats.Count == 0)
        {
            _logger.Info(AppLogCategory.Events, "Structure generation skipped: the project has no beat map.");
            return default;
        }

        var action = BuildAction(target, beats, out var result, out var added, out var merged);
        if (action == null)
        {
            _logger.Info(AppLogCategory.Events,
                $"Structure generation found nothing to add in '{target.Name}'.");
            return default;
        }

        if (!_history.Do(action))
        {
            _logger.Warning(AppLogCategory.Events, "Structure generation could not be recorded in history.");
            return default;
        }

        _logger.Info(AppLogCategory.Events,
            $"Structure generated for '{target.Name}': COUNT={result.CountPhrases}, END={result.EndPhrases}, " +
            $"noguitar={result.NoGuitarSections} (new markers={added}, merged onto existing={merged}).");
        return result;
    }

    /// <summary>
    ///     The pass as ONE action, not yet executed, or null when there is nothing to add. The
    ///     service commits it through the history; the export findings' Fix
    ///     executes it on the project snapshot it records as its own single undo step.
    /// </summary>
    public static IChartAction? BuildAction(Arrangement target, IList<BeatMarker> beats,
        out StructureGenerationResult result, out int addedMarkers, out int mergedMarkers)
    {
        result = default;
        addedMarkers = 0;
        mergedMarkers = 0;
        var plan = StructureGenerator.Plan(target, beats);
        if (plan.Count == 0) return null;

        // Merged onto EXISTING markers where one is already at the position: an
        // EventPoint carries a section AND a phrase AND event codes, so adding a
        // second point at the same instant would be two markers the exporter then
        // has to reconcile.
        //
        // The merge is PLANNED here and committed below — a bare
        // assignment onto the live marker, outside the history action, would
        // break the "one gesture is one undo node" rule in both directions: a
        // Ctrl+Z would remove the added markers and leave every merged one wearing the
        // COUNT/END/noguitar the generator had written; and when EVERY placement
        // merged (`additions` empty) no node would be recorded at all, so the pass
        // would mutate the chart with no undo whatsoever while still reporting a
        // non-empty result. Same shape as InsertVocalAtPlayhead's
        // phrase-end stretch.
        var additions = new List<EventPoint>();
        var mergeTargets = new List<EventPoint>();
        var mergeNewStates = new List<EventPoint>();
        int counts = 0, ends = 0, noGuitar = 0;

        void Place(FractionalPosition position, string? section, string? phrase)
        {
            var pending = additions.FirstOrDefault(ep => ep.Position.Equals(position));
            if (pending != null)
            {
                // Not in the chart yet, so this one IS the new state.
                if (section != null && string.IsNullOrWhiteSpace(pending.Section)) pending.Section = section;
                if (phrase != null && string.IsNullOrWhiteSpace(pending.Phrase)) pending.Phrase = phrase;
                return;
            }

            var existing = target.EventPoints.FirstOrDefault(ep => ep.Position.Equals(position));
            if (existing != null)
            {
                // Stage the merge on a CLONE; the action carries the before/after
                // pair and does the writing.
                var index = mergeTargets.IndexOf(existing);
                if (index < 0)
                {
                    mergeTargets.Add(existing);
                    mergeNewStates.Add(Snapshot(existing));
                    index = mergeTargets.Count - 1;
                }

                var staged = mergeNewStates[index];
                if (section != null && string.IsNullOrWhiteSpace(staged.Section)) staged.Section = section;
                if (phrase != null && string.IsNullOrWhiteSpace(staged.Phrase)) staged.Phrase = phrase;
                return;
            }

            additions.Add(new EventPoint
            {
                Position = position,
                Section = section ?? "",
                Phrase = phrase ?? ""
            });
        }

        // Only the triple MutateEventPointsAction transfers. Events and Status
        // stay on the live marker on purpose — see the action's transfer
        // function; carrying them here would be the same DELETE the drag path
        // deliberately avoids.
        static EventPoint Snapshot(EventPoint source) => new()
        {
            Position = source.Position, Phrase = source.Phrase, Section = source.Section
        };

        if (plan.CountPhrase is { } countAt)
        {
            Place(countAt, null, "COUNT");
            counts = 1;
        }

        if (plan.EndPhrase is { } endAt)
        {
            Place(endAt, null, "END");
            ends = 1;
        }

        foreach (var at in plan.NoGuitarSections)
        {
            // Phrase follows the section, exactly as the event dialog now does and
            // as 93.2% of the corpus stores it.
            Place(at, "noguitar", "noguitar");
            noGuitar++;
        }

        // ONE gesture, ONE node: the merges onto existing markers and the new
        // markers travel together, so a single Ctrl+Z takes the whole pass back.
        var actions = new List<IChartAction>();
        if (mergeTargets.Count > 0)
        {
            actions.Add(new MutateEventPointsAction(target.EventPoints, mergeTargets,
                mergeTargets.Select(Snapshot).ToList(), mergeNewStates));
        }

        if (additions.Count > 0)
            actions.Add(new AddEventPointsAction(target.EventPoints, additions));

        if (actions.Count == 0) return null;

        result = new StructureGenerationResult(counts, ends, noGuitar);
        addedMarkers = additions.Count;
        mergedMarkers = mergeTargets.Count;
        return actions.Count == 1 ? actions[0] : new CompositeAction(actions) { LabelKey = "History_GenerateStructure" };
    }
}
