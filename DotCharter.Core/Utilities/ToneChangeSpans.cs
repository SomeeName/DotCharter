using DotCharter.Core.Domain.Events;

namespace DotCharter.Core.Utilities;

/// <summary>One placed tone change and the stretch of song it governs.</summary>
/// <param name="Index">Index into the SORTED change list.</param>
/// <param name="ToneName">The name the change carries; may be empty.</param>
/// <param name="StartMs">Where the change sits.</param>
/// <param name="EndMs">Where the next change takes over, or the song ends.</param>
public readonly record struct ToneSpan(int Index, string ToneName, double StartMs, double EndMs)
{
    public double DurationMs => EndMs - StartMs;

    /// <summary>
    ///     Long enough to say anything about. Under this a span is a stab, a
    ///     stinger or a mis-drop, and averaging a spectrum over it produces a
    ///     confident answer from one chord.
    /// </summary>
    public const double MinUsefulMs = 750.0;

    public bool IsUseful => DurationMs >= MinUsefulMs;
}

/// <summary>
///     Which audio belongs to which tone change.
///     <para>
///         The owner's rule for tone generation: <b>the PLACEMENT is not
///         generated.</b> The charter drops a tone change where the tone should
///         change, and the app builds the tone from the audio between that change
///         and the next one. This is the "between" — pure, and the only place
///         that answers it.
///     </para>
///     <para>
///         It deliberately knows nothing about audio, gear or analysis: it turns
///         placements into time ranges, which is a chart question, so it is
///         testable without a single sample of PCM.
///     </para>
/// </summary>
public static class ToneChangeSpans
{
    /// <summary>
    ///     Every change's span, in time order.
    ///     <para>
    ///         The last change runs to <paramref name="songLengthMs" />. Changes
    ///         are sorted first — the collection's order is not guaranteed to be
    ///         chronological, and a span computed from an unsorted list would
    ///         silently pair a change with the wrong audio.
    ///     </para>
    ///     <para>
    ///         Two changes at the same position produce a zero-length span for
    ///         the earlier one. That is kept rather than dropped: it is a real
    ///         (broken) chart state, <see cref="ToneSpan.IsUseful" /> is false for
    ///         it, and silently discarding it would renumber every later index.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<ToneSpan> Resolve(
        IReadOnlyList<ToneChange>? changes, IList<BeatMarker>? beats, double songLengthMs)
    {
        if (changes is null || changes.Count == 0 || beats is null || beats.Count == 0)
            return Array.Empty<ToneSpan>();

        var ordered = changes
            .Select(c => (Change: c, TimeMs: c.GetTimeMs(beats)))
            .OrderBy(x => x.TimeMs)
            .ToList();

        var end = songLengthMs > 0 ? songLengthMs : ordered[^1].TimeMs;
        var spans = new List<ToneSpan>(ordered.Count);

        for (var i = 0; i < ordered.Count; i++)
        {
            var start = Math.Max(0, ordered[i].TimeMs);
            var stop = i + 1 < ordered.Count ? ordered[i + 1].TimeMs : end;

            // A change past the end of the audio governs nothing. Clamping keeps
            // the span degenerate instead of negative, which every consumer would
            // otherwise have to guard.
            stop = Math.Max(start, Math.Min(stop, Math.Max(end, start)));

            spans.Add(new ToneSpan(i, ordered[i].Change.ToneName ?? "", start, stop));
        }

        return spans;
    }

    /// <summary>
    ///     The span governed by one particular change, or <c>null</c> if that
    ///     change is not in the list.
    ///     <para>
    ///         Matched by REFERENCE, not by position or name: two changes can
    ///         share a position, and several legitimately share a name (a song
    ///         returns to its clean tone), so any value-based match would pick the
    ///         wrong span exactly when a chart is at its most confusing.
    ///     </para>
    /// </summary>
    public static ToneSpan? SpanFor(
        ToneChange? change, IReadOnlyList<ToneChange>? changes, IList<BeatMarker>? beats, double songLengthMs)
    {
        if (change is null || changes is null) return null;

        var ordered = changes
            .Select(c => (Change: c, TimeMs: c.GetTimeMs(beats ?? [])))
            .OrderBy(x => x.TimeMs)
            .ToList();

        var index = ordered.FindIndex(x => ReferenceEquals(x.Change, change));
        if (index < 0) return null;

        var spans = Resolve(changes, beats, songLengthMs);
        return index < spans.Count ? spans[index] : null;
    }
}
