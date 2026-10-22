using System.Runtime.CompilerServices;
using HandShape = DotCharter.Core.Domain.Guitar.HandShape;

namespace DotCharter.Services.Editing.HandShapes;

/// <summary>
///     What <see cref="HandShapeSuggestionLatch" /> answers: the suggested bars the timeline draws dashed
///     and, index for index, the suggestions behind them — what a click on a drawn bar accepts. Immutable
///     once published and shared by every reader, so nobody may add to or remove from either list.
/// </summary>
public sealed class HandShapeSuggestionSet(IList<HandShape> bars, IReadOnlyList<SuggestedHandShape> suggestions)
{
    /// <summary>No bars: a hidden View toggle, or no level.</summary>
    public static HandShapeSuggestionSet Empty { get; } = new(Array.Empty<HandShape>(), []);

    public IList<HandShape> Bars { get; } = bars;

    public IReadOnlyList<SuggestedHandShape> Suggestions { get; } = suggestions;
}

/// <summary>
///     Access to <see cref="HandShapeSuggester" />'s proposals for every surface that shows or hits them
///     — the timeline and each stacked row, the song map, the hand-shape tool. The detector reads the
///     whole level, so ONE instance is shared by all of them (the editor owns it) and it runs once per
///     level and edit: again only when the level or arrangement changes identity, a collection it reads
///     changes count, or the caller's content version moves (the editor's undo-history revision — an
///     in-place fret edit counts); unversioned (negative) it also reruns every
///     <see cref="StaleAfterMs" />. A call that changes nothing returns the same set and allocates
///     nothing. Thread-safe: a result is published whole by a reference write and a recompute is
///     serialised per level, so two readers on one edit wait for ONE run. <c>RepeatChordLatch</c> is the
///     same contract for the repeat box.
/// </summary>
public sealed class HandShapeSuggestionLatch
{
    /// <summary>The staleness bound for an UNVERSIONED caller's in-place edits.</summary>
    public const long StaleAfterMs = 250;

    private readonly ConditionalWeakTable<ChartLevel, Slot> _slots = [];
    private int _computeCount;

    /// <summary>How many times the detector ran, over every level — a test seam.</summary>
    public int ComputeCount => Volatile.Read(ref _computeCount);

    /// <summary>The suggestions for <paramref name="level" />, or the empty set when
    /// <paramref name="enabled" /> is off or there is no level.</summary>
    public HandShapeSuggestionSet Get(Arrangement? arrangement, ChartLevel? level, bool enabled, long contentVersion,
        long nowMs)
    {
        if (!enabled || arrangement == null || level == null) return HandShapeSuggestionSet.Empty;

        var slot = _slots.GetValue(level, static _ => new Slot());
        if (Volatile.Read(ref slot.Built) is { } built && built.Serves(arrangement, level, contentVersion, nowMs))
            return built.Set;

        lock (slot)
        {
            if (slot.Built is { } again && again.Serves(arrangement, level, contentVersion, nowMs)) return again.Set;

            var suggestions = HandShapeSuggester.Suggest(arrangement, level);
            var bars = new List<HandShape>(suggestions.Count);
            foreach (var suggestion in suggestions)
                bars.Add(HandShapeSuggestionAcceptor.ToPreview(arrangement, suggestion));

            var fresh = new Built(new HandShapeSuggestionSet(bars, suggestions), arrangement, level, contentVersion, nowMs);
            Volatile.Write(ref slot.Built, fresh);
            Interlocked.Increment(ref _computeCount);
            return fresh.Set;
        }
    }

    private sealed class Slot
    {
        public Built? Built;
    }

    /// <summary>One published result and the inputs it answers.</summary>
    private sealed class Built(HandShapeSuggestionSet set, Arrangement arrangement, ChartLevel level,
        long contentVersion, long builtAtMs)
    {
        private readonly int _templates = arrangement.ChordTemplates.Count;
        private readonly int _notes = level.Notes.Count;
        private readonly int _chords = level.Chords.Count;
        private readonly int _handShapes = level.HandShapes.Count;
        private readonly int _anchors = level.Anchors.Count;

        public HandShapeSuggestionSet Set { get; } = set;

        public bool Serves(Arrangement forArrangement, ChartLevel forLevel, long version, long nowMs) =>
            contentVersion == version
            && (version >= 0 || nowMs - builtAtMs < StaleAfterMs)
            && ReferenceEquals(arrangement, forArrangement) && ReferenceEquals(level, forLevel)
            && _templates == forArrangement.ChordTemplates.Count && _notes == forLevel.Notes.Count
            && _chords == forLevel.Chords.Count && _handShapes == forLevel.HandShapes.Count
            && _anchors == forLevel.Anchors.Count;
    }
}
