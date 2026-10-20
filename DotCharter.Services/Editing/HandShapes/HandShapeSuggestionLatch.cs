using HandShape = DotCharter.Core.Domain.Guitar.HandShape;

namespace DotCharter.Services.Editing.HandShapes;

/// <summary>
///     Per-frame access to <see cref="HandShapeSuggester" />'s proposals for a render loop, as the bars
/// the timeline draws dashed. The detector reads the whole level, so it runs only when the
///     level or arrangement changes identity, a collection it reads changes count, or the caller's
///     content version moves (the editor's undo-history revision — an in-place fret edit counts);
///     unversioned (negative) it also reruns every <see cref="StaleAfterMs" />. A frame that changes
///     nothing returns the same list and allocates nothing. One instance per render loop; not
///     thread-safe. <c>RepeatChordLatch</c> is the same contract for the repeat box.
/// </summary>
public sealed class HandShapeSuggestionLatch
{
    /// <summary>The staleness bound for an UNVERSIONED caller's in-place edits.</summary>
    public const long StaleAfterMs = 250;

    private IList<HandShape> _current = Array.Empty<HandShape>();
    private bool _built;
    private long _builtAtMs;
    private long _contentVersion;
    private object? _arrangement, _level;
    private int _templates, _notes, _chords, _handShapes, _anchors;

    /// <summary>The suggestions behind the bars <see cref="Get" /> last returned, index for index —
    /// what a click on a drawn bar accepts. Empty after a disabled call.</summary>
    public IReadOnlyList<SuggestedHandShape> Suggestions { get; private set; } = [];

    /// <summary>How many times the detector ran — a test seam.</summary>
    public int ComputeCount { get; private set; }

    /// <summary>The suggested bars for <paramref name="level" />, or an empty list when
    /// <paramref name="enabled" /> is off or there is no level.</summary>
    public IList<HandShape> Get(Arrangement? arrangement, ChartLevel? level, bool enabled, long contentVersion,
        long nowMs)
    {
        if (!enabled || arrangement == null || level == null)
        {
            _built = false;
            Suggestions = [];
            return Array.Empty<HandShape>();
        }

        if (_built && _contentVersion == contentVersion
                   && (contentVersion >= 0 || nowMs - _builtAtMs < StaleAfterMs)
                   && ReferenceEquals(_arrangement, arrangement) && ReferenceEquals(_level, level)
                   && _templates == arrangement.ChordTemplates.Count && _notes == level.Notes.Count
                   && _chords == level.Chords.Count && _handShapes == level.HandShapes.Count
                   && _anchors == level.Anchors.Count)
        {
            return _current;
        }

        var suggestions = HandShapeSuggester.Suggest(arrangement, level);
        var bars = new List<HandShape>(suggestions.Count);
        foreach (var suggestion in suggestions)
            bars.Add(HandShapeSuggestionAcceptor.ToPreview(arrangement, suggestion));

        _current = bars;
        Suggestions = suggestions;
        ComputeCount++;
        _built = true;
        _builtAtMs = nowMs;
        _contentVersion = contentVersion;
        (_arrangement, _level) = (arrangement, level);
        (_templates, _notes, _chords) = (arrangement.ChordTemplates.Count, level.Notes.Count, level.Chords.Count);
        (_handShapes, _anchors) = (level.HandShapes.Count, level.Anchors.Count);
        return _current;
    }
}
