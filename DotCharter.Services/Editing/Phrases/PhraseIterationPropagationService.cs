using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Phrases;

/// <summary>
/// "This chorus appears 4× — edit one repeat, apply to all": replicates the
/// sound content of one <see cref="PhraseIteration"/>'s span into every other
/// iteration of the same phrase (<see cref="PhraseIteration.PhraseId"/>).
///
/// Span and clipping rules:
/// - An iteration's span runs from its position to the next phrase
///   iteration's position (any phrase), open-ended for the last iteration.
/// - Content is selected by its start position within the source span and
///   shifted by (targetStart − sourceStart) in beat space — a beat-grid shift,
///   so the copy lands on the corresponding beats of the target and its
///   wall-clock time follows the tempo map (see <c>TimelineMath</c>).
/// - When the target span is SHORTER than the source, shifted content that
///   would start at/after the target's end is dropped and sustains /
///   hand-shape ends are clamped to the target end.
/// - When the target span is LONGER, the remainder past the copied region is
///   left empty (the target's whole span is still cleared first — replace,
///   not merge).
/// - Sounds that start before a span but sustain into it belong to the
///   preceding span: they are neither copied from the source nor removed
///   from targets.
///
/// All removals + additions across every target go through the history
/// service as one <see cref="CompositeAction"/>, so a single undo restores
/// every target's previous content.
/// </summary>
public sealed class PhraseIterationPropagationService : IPhraseIterationPropagationService
{
    private readonly IHistoryService _historyManager;
    private readonly IAppLogger _logger;
    private readonly IEditingScope _scope;

    public PhraseIterationPropagationService(IHistoryService historyManager, IEditingScope scope, IAppLogger logger)
    {
        _historyManager = historyManager;
        _scope = scope;
        _logger = logger;
    }

    public int PropagateToAllIterations(PhraseIteration source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var arrangement = _scope.Arrangement;
        if (arrangement == null) return 0;

        // Both stores: a chart placed in the editor has markers and no explicit iterations.
        var iterations = PhraseMarkers.Iterations(arrangement).Iterations.OrderBy(pi => pi.Position).ToList();
        var resolvedSource = iterations.FirstOrDefault(pi => ReferenceEquals(pi, source))
                             ?? iterations.FirstOrDefault(pi =>
                                 pi.Position == source.Position && pi.PhraseId == source.PhraseId);
        if (resolvedSource == null)
        {
            _logger.Warning(AppLogCategory.Events,
                $"Phrase propagation: source iteration at {source.Position} (phrase {source.PhraseId}) not found in the arrangement.");
            return 0;
        }

        var targets = iterations
            .Where(pi => !ReferenceEquals(pi, resolvedSource) && pi.PhraseId == resolvedSource.PhraseId)
            .ToList();
        if (targets.Count == 0) return 0;

        var sourceStart = resolvedSource.Position;
        var sourceEnd = SpanEnd(iterations, resolvedSource);

        var actions = new List<IChartAction>();
        foreach (var target in targets)
        {
            var targetStart = target.Position;
            var targetEnd = SpanEnd(iterations, target);
            var offset = targetStart - sourceStart;

            // Replace: clear the target's whole span first.
            actions.Add(new RemoveNotesAction(_scope.Notes,
                InSpan(_scope.Notes, n => n.Position, targetStart, targetEnd).ToList()));
            actions.Add(new RemoveChordsAction(_scope.Chords,
                InSpan(_scope.Chords, c => c.Position, targetStart, targetEnd).ToList()));
            actions.Add(new RemoveAnchorsAction(_scope.Anchors,
                InSpan(_scope.Anchors, a => a.Position, targetStart, targetEnd).ToList()));
            actions.Add(new RemoveHandShapesAction(_scope.HandShapes,
                InSpan(_scope.HandShapes, h => h.Position, targetStart, targetEnd).ToList()));

            actions.Add(new AddNotesAction(_scope.Notes,
                CloneNotes(sourceStart, sourceEnd, offset, targetEnd)));
            actions.Add(new AddChordsAction(_scope.Chords,
                CloneChords(sourceStart, sourceEnd, offset, targetEnd)));
            actions.Add(new AddAnchorsAction(_scope.Anchors,
                CloneAnchors(sourceStart, sourceEnd, offset, targetEnd)));
            actions.Add(new AddHandShapesAction(_scope.HandShapes,
                CloneHandShapes(sourceStart, sourceEnd, offset, targetEnd)));
        }

        if (!_historyManager.Do(new CompositeAction(actions) { LabelKey = "History_PropagatePhrase" })) return 0;

        _logger.Info(AppLogCategory.Events,
            $"Propagated phrase iteration at {sourceStart} (phrase {resolvedSource.PhraseId}) to {targets.Count} other iteration(s).");
        return targets.Count;
    }

    /// <summary>The span of an iteration ends at the next iteration's position
    /// (any phrase); null = open-ended (last iteration).</summary>
    private static FractionalPosition? SpanEnd(IReadOnlyList<PhraseIteration> sorted, PhraseIteration iteration)
    {
        var index = 0;
        while (index < sorted.Count && !ReferenceEquals(sorted[index], iteration)) index++;
        return index + 1 < sorted.Count ? sorted[index + 1].Position : null;
    }

    private static IEnumerable<T> InSpan<T>(IEnumerable<T> items, Func<T, FractionalPosition> position,
        FractionalPosition start, FractionalPosition? end)
    {
        foreach (var item in items)
        {
            var p = position(item);
            if (p < start) continue;
            if (end.HasValue && p >= end.Value) continue;
            yield return item;
        }
    }

    private List<GuitarNote> CloneNotes(FractionalPosition sourceStart, FractionalPosition? sourceEnd,
        FractionalPosition offset, FractionalPosition? targetEnd)
    {
        var clones = new List<GuitarNote>();
        foreach (var note in InSpan(_scope.Notes, n => n.Position, sourceStart, sourceEnd))
        {
            var clone = note.Clone();
            clone.Position += offset;
            if (targetEnd.HasValue && clone.Position >= targetEnd.Value) continue;
            for (var i = 0; i < clone.BendValues.Count; i++)
                clone.BendValues[i].Position += offset;

            if (targetEnd.HasValue && clone.Position + clone.Sustain > targetEnd.Value)
                clone.Sustain = targetEnd.Value - clone.Position;
            clones.Add(clone);
        }

        return clones;
    }

    private List<Chord> CloneChords(FractionalPosition sourceStart, FractionalPosition? sourceEnd,
        FractionalPosition offset, FractionalPosition? targetEnd)
    {
        var clones = new List<Chord>();
        foreach (var chord in InSpan(_scope.Chords, c => c.Position, sourceStart, sourceEnd))
        {
            var clone = chord.Clone();
            clone.Position += offset;
            if (targetEnd.HasValue && clone.Position >= targetEnd.Value) continue;
            foreach (var chordNote in clone.ChordNotes)
            {
                for (var i = 0; i < chordNote.BendValues.Count; i++)
                    chordNote.BendValues[i].Position += offset;
                if (targetEnd.HasValue && clone.Position + chordNote.Sustain > targetEnd.Value)
                    chordNote.Sustain = targetEnd.Value - clone.Position;
            }

            clones.Add(clone);
        }

        return clones;
    }

    private List<FretAnchor> CloneAnchors(FractionalPosition sourceStart, FractionalPosition? sourceEnd,
        FractionalPosition offset, FractionalPosition? targetEnd)
    {
        var clones = new List<FretAnchor>();
        foreach (var anchor in InSpan(_scope.Anchors, a => a.Position, sourceStart, sourceEnd))
        {
            var clone = anchor.Clone();
            clone.Position += offset;
            if (targetEnd.HasValue && clone.Position >= targetEnd.Value) continue;
            clones.Add(clone);
        }

        return clones;
    }

    private List<HandShape> CloneHandShapes(FractionalPosition sourceStart, FractionalPosition? sourceEnd,
        FractionalPosition offset, FractionalPosition? targetEnd)
    {
        var clones = new List<HandShape>();
        foreach (var handShape in InSpan(_scope.HandShapes, h => h.Position, sourceStart, sourceEnd))
        {
            var clone = handShape.Clone();
            clone.Position += offset;
            clone.EndPosition += offset;
            if (targetEnd.HasValue && clone.Position >= targetEnd.Value) continue;
            if (targetEnd.HasValue && clone.EndPosition > targetEnd.Value)
                clone.EndPosition = targetEnd.Value;
            clones.Add(clone);
        }

        return clones;
    }
}
