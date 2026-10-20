using DotCharter.Services.Ai.Chart;

namespace DotCharter.Services.Ai.Chat;

/// <summary>What part of the chart an AI chat instruction covers.</summary>
public enum AiChatScopeKind
{
    /// <summary>The editor's selected sounds.</summary>
    Selection = 0,

    /// <summary>One section (the phrase marker at a position up to the next one).</summary>
    Section = 1,

    /// <summary>The whole arrangement.</summary>
    Arrangement = 2
}

/// <summary>
///     The scope of an AI chat instruction: which arrangement (null = the active one) and
///     which sounds, as the inclusive sound range <see cref="AiChartPassRequest" /> takes
///     (<paramref name="From" /> / <paramref name="To" />, null = open). The pass chunks only
///     the sounds inside it; the rest of the chart is at most context, never changed.
/// </summary>
public sealed record AiChatScope(
    AiChatScopeKind Kind,
    Arrangement? Arrangement,
    FractionalPosition? From,
    FractionalPosition? To)
{
    /// <summary>
    /// The placed tone changes the Tones task generates for, in time order; null for
    ///     every other task (they read <see cref="From" /> / <see cref="To" />).
    /// </summary>
    public IReadOnlyList<ToneChange>? ToneChanges { get; init; }

    /// <summary>
    /// The Tones task's scope — the same three picker entries, read as TONES: the
    ///     <paramref name="selected" /> tone changes (the timeline's tone selection); the tone
    ///     change in force at <paramref name="playheadMs" /> (the last one at or before it); or
    ///     every placed tone change of the arrangement. Null when the choice covers none (nothing
    ///     selected, the playhead before the first change, no placed changes) — never silently all.
    /// </summary>
    /// <param name="kind">Which picker entry.</param>
    /// <param name="arrangement">The arrangement whose placed changes are read.</param>
    /// <param name="selected">The selected tone changes; only this arrangement's count.</param>
    /// <param name="playheadMs">The playhead, for <see cref="AiChatScopeKind.Section" />.</param>
    /// <param name="beats">The beat map that places the changes in time.</param>
    public static AiChatScope? Tones(AiChatScopeKind kind, Arrangement arrangement, IEnumerable<ToneChange> selected,
        double playheadMs, IList<BeatMarker> beats)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(beats);

        // Time order, ties in list order: the order the window planner numbers them in.
        var ordered = arrangement.ToneChanges
            .Select((change, i) => (Change: change, Ms: change.GetTimeMs(beats), Original: i))
            .OrderBy(x => x.Ms)
            .ThenBy(x => x.Original)
            .ToList();
        var chosen = kind switch
        {
            AiChatScopeKind.Selection => ordered.Where(x => selected.Any(s => ReferenceEquals(s, x.Change)))
                .Select(x => x.Change).ToList(),
            AiChatScopeKind.Section => ordered.Where(x => x.Ms <= playheadMs).Select(x => x.Change).TakeLast(1).ToList(),
            _ => ordered.Select(x => x.Change).ToList()
        };
        return chosen.Count == 0 ? null : new AiChatScope(kind, arrangement, null, null) { ToneChanges = chosen };
    }

    /// <summary>The whole of <paramref name="arrangement" /> (null = the active one).</summary>
    public static AiChatScope WholeArrangement(Arrangement? arrangement = null)
        => new(AiChatScopeKind.Arrangement, arrangement, null, null);

    /// <summary>
    ///     The selection, as <see cref="AiChartPassRun.RangeOf" /> reads it from the editor,
    ///     or null when nothing is selected (a "selection" scope over nothing is not the
    ///     whole arrangement: the pane says so instead of running on everything).
    /// </summary>
    public static AiChatScope? Selection(
        (FractionalPosition From, FractionalPosition To)? range, Arrangement? arrangement = null)
        => range is { } r ? new AiChatScope(AiChatScopeKind.Selection, arrangement, r.From, r.To) : null;

    /// <summary>
    ///     The section <paramref name="position" /> (the playhead) is in: from the phrase
    ///     marker at or before it — the song's start when there is none — to the last sound
    ///     before the next marker. The markers are the ones the pass chunks by
    ///     (<see cref="PhraseMarkers.All" />). Null when that section holds no sound.
    /// </summary>
    /// <param name="arrangement">The arrangement.</param>
    /// <param name="level">The level whose sounds are counted.</param>
    /// <param name="position">A position inside the section.</param>
    public static AiChatScope? SectionAt(Arrangement arrangement, ChartLevel level, FractionalPosition position)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(level);

        FractionalPosition? from = null, next = null;
        foreach (var marker in PhraseMarkers.All(arrangement))
        {
            if (marker.Position <= position)
            {
                from = marker.Position;
                continue;
            }

            next = marker.Position;
            break;
        }

        var inside = level.Notes.Select(n => n.Position).Concat(level.Chords.Select(c => c.Position))
            .Where(p => (from is not { } f || p >= f) && (next is not { } n || p < n))
            .ToList();
        return inside.Count == 0 ? null : new AiChatScope(AiChatScopeKind.Section, arrangement, from, inside.Max());
    }
}
