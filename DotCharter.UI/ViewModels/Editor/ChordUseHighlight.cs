using System.Collections.ObjectModel;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>The marked chords and hand shapes on ONE difficulty level, in the
/// level's own order, and its marked anchors: <paramref name="Anchors" /> are live
/// anchors a pending change removes, <paramref name="ProposedAnchors" /> the ones it
/// would add (not on the chart yet), both by position (the AI pass review).</summary>
public sealed record ChordUseLevelMarks(
    IReadOnlyList<Chord> Chords,
    IReadOnlyList<HandShape> HandShapes,
    IReadOnlyList<FretAnchor> Anchors,
    IReadOnlyList<FretAnchor> ProposedAnchors)
{
    public static readonly ChordUseLevelMarks None = new([], [], [], []);

    public ChordUseLevelMarks(IReadOnlyList<Chord> chords, IReadOnlyList<HandShape> handShapes)
        : this(chords, handShapes, [], [])
    {
    }

    public int Count => Chords.Count + HandShapes.Count + Anchors.Count + ProposedAnchors.Count;
}

/// <summary>
///     A set of chords and hand shapes the editor marks across the whole song —
/// the chord panel's bulk-edit preview: every sound an edit on the
///     "Apply to" rung would change, read off <c>ChordApplyScope.Preview</c>, the
///     resolver the edit itself writes through. Published on
///     <see cref="ChartEditorViewModel.UseHighlight" />; the 2D timeline outlines
///     the members (snapshot flags) and the song map draws a mark at each one on
///     the level on screen. Immutable: a new set is a new instance.
///     <para>
///         Sets are by REFERENCE: a chord replaced by an undo is a different chord
///         and simply stops matching until the panel re-resolves.
///     </para>
/// </summary>
public sealed class ChordUseHighlight
{
    // Before None: static fields initialize in declaration order, and None's constructor reads it.
    private static readonly HashSet<FretAnchor> NoAnchors = new(ReferenceEqualityComparer.Instance);

    public static readonly ChordUseHighlight None = new(
        new HashSet<Chord>(ReferenceEqualityComparer.Instance),
        new HashSet<HandShape>(ReferenceEqualityComparer.Instance),
        new Dictionary<ObservableCollection<Chord>, ChordUseLevelMarks>(ReferenceEqualityComparer.Instance));

    private readonly HashSet<Chord> _chords;
    private readonly HashSet<HandShape> _handShapes;
    private readonly HashSet<FretAnchor> _anchors;
    private readonly HashSet<FretAnchor> _proposedAnchors;
    private readonly EventMarks _events;

    /// <summary>Keyed by the level's <c>Chords</c> collection — what
    /// <c>ChartEditorViewModel.PlacedChords</c> hands out for the level on screen.</summary>
    private readonly Dictionary<ObservableCollection<Chord>, ChordUseLevelMarks> _byLevel;

    private ChordUseHighlight(HashSet<Chord> chords, HashSet<HandShape> handShapes,
        Dictionary<ObservableCollection<Chord>, ChordUseLevelMarks> byLevel,
        HashSet<FretAnchor>? anchors = null, HashSet<FretAnchor>? proposedAnchors = null, EventMarks? events = null)
    {
        _events = events ?? EventMarks.None;
        _chords = chords;
        _handShapes = handShapes;
        _byLevel = byLevel;
        _anchors = anchors ?? NoAnchors;
        _proposedAnchors = proposedAnchors ?? NoAnchors;
    }

    /// <summary>Every marked chord, on every level.</summary>
    public IReadOnlySet<Chord> Chords => _chords;

    /// <summary>Every marked hand shape, on every level.</summary>
    public IReadOnlySet<HandShape> HandShapes => _handShapes;

    /// <summary>Every marked LIVE anchor (one a pending change removes), on every level.</summary>
    public IReadOnlySet<FretAnchor> Anchors => _anchors;

    public int Count => _chords.Count + _handShapes.Count + _anchors.Count + _proposedAnchors.Count + _events.Count;

    public bool IsEmpty => Count == 0;

    /// <summary>The set of <paramref name="chords" /> and <paramref name="handShapes" />,
    /// grouped by the level of <paramref name="arrangement" /> each sits on. A sound
    /// on no level of it is dropped.</summary>
    public static ChordUseHighlight From(IReadOnlyList<Chord> chords, IReadOnlyList<HandShape> handShapes,
        Arrangement? arrangement)
    {
        if (arrangement == null || chords.Count + handShapes.Count == 0) return None;
        var wantedChords = new HashSet<Chord>(chords, ReferenceEqualityComparer.Instance);
        var wantedHandShapes = new HashSet<HandShape>(handShapes, ReferenceEqualityComparer.Instance);
        var keptChords = new HashSet<Chord>(ReferenceEqualityComparer.Instance);
        var keptHandShapes = new HashSet<HandShape>(ReferenceEqualityComparer.Instance);
        var byLevel = new Dictionary<ObservableCollection<Chord>, ChordUseLevelMarks>(ReferenceEqualityComparer.Instance);
        foreach (var level in arrangement.Levels)
        {
            var onLevelChords = level.Chords.Where(wantedChords.Contains).ToArray();
            var onLevelHandShapes = level.HandShapes.Where(wantedHandShapes.Contains).ToArray();
            if (onLevelChords.Length + onLevelHandShapes.Length == 0) continue;
            keptChords.UnionWith(onLevelChords);
            keptHandShapes.UnionWith(onLevelHandShapes);
            byLevel[level.Chords] = new ChordUseLevelMarks(onLevelChords, onLevelHandShapes);
        }

        return keptChords.Count + keptHandShapes.Count == 0
            ? None
            : new ChordUseHighlight(keptChords, keptHandShapes, byLevel);
    }

    /// <summary>
    ///     A pending change on ONE level of <paramref name="arrangement" /> — the AI chart
    /// pass review: the live <paramref name="chords" />,
    ///     <paramref name="handShapes" /> and <paramref name="anchors" /> it changes, and the
    ///     <paramref name="proposedAnchors" /> it would add. Empty when
    ///     <paramref name="level" /> is not one of the arrangement's.
    /// </summary>
    public static ChordUseHighlight ForLevel(Arrangement? arrangement, ChartLevel level, IReadOnlyList<Chord> chords,
        IReadOnlyList<HandShape> handShapes, IReadOnlyList<FretAnchor> anchors,
        IReadOnlyList<FretAnchor> proposedAnchors)
    {
        ArgumentNullException.ThrowIfNull(level);
        if (arrangement == null || !arrangement.Levels.Contains(level) ||
            chords.Count + handShapes.Count + anchors.Count + proposedAnchors.Count == 0)
        {
            return None;
        }

        // By REFERENCE: Chord and HandShape compare by value, and two identical sounds on
        // one level are ordinary chart state.
        var wantedChords = new HashSet<Chord>(chords, ReferenceEqualityComparer.Instance);
        var wantedHandShapes = new HashSet<HandShape>(handShapes, ReferenceEqualityComparer.Instance);
        var wantedAnchors = new HashSet<FretAnchor>(anchors, ReferenceEqualityComparer.Instance);
        var onLevelChords = level.Chords.Where(wantedChords.Contains).ToArray();
        var onLevelHandShapes = level.HandShapes.Where(wantedHandShapes.Contains).ToArray();
        var onLevelAnchors = level.Anchors.Where(wantedAnchors.Contains).OrderBy(a => a.Position).ToArray();
        var proposed = proposedAnchors.OrderBy(a => a.Position).ToArray();
        if (onLevelChords.Length + onLevelHandShapes.Length + onLevelAnchors.Length + proposed.Length == 0) return None;

        var keptChords = new HashSet<Chord>(onLevelChords, ReferenceEqualityComparer.Instance);
        var keptHandShapes = new HashSet<HandShape>(onLevelHandShapes, ReferenceEqualityComparer.Instance);
        var keptAnchors = new HashSet<FretAnchor>(onLevelAnchors, ReferenceEqualityComparer.Instance);
        var marks = new ChordUseLevelMarks(onLevelChords, onLevelHandShapes, onLevelAnchors, proposed);
        var byLevel = new Dictionary<ObservableCollection<Chord>, ChordUseLevelMarks>(ReferenceEqualityComparer.Instance)
        {
            [level.Chords] = marks
        };
        return new ChordUseHighlight(keptChords, keptHandShapes, byLevel, keptAnchors,
            new HashSet<FretAnchor>(proposed, ReferenceEqualityComparer.Instance));
    }

    /// <summary>
    ///     A pending section and phrase change on <paramref name="arrangement" /> — the AI chat's
    /// sections &amp; phrases review: the LIVE event points whose section or phrase it
    ///     replaces (drawn faded) and the <paramref name="proposed" /> new markers (not on the chart
    ///     yet). Arrangement-wide: markers belong to no level. <paramref name="proposedAnchors" /> are
    /// the hand positions a proposed phrase brings, each on its level of the arrangement
    ///     (one on any other level is dropped), marked like the FHP review's proposed anchors.
    /// </summary>
    public static ChordUseHighlight ForEvents(Arrangement? arrangement, IReadOnlyList<EventPoint> sections,
        IReadOnlyList<EventPoint> phrases, IReadOnlyList<EventPoint> proposed,
        IReadOnlyList<(ChartLevel Level, FretAnchor Anchor)>? proposedAnchors = null)
    {
        if (arrangement == null) return None;
        var live = new HashSet<EventPoint>(arrangement.EventPoints, ReferenceEqualityComparer.Instance);
        var marks = new EventMarks(arrangement,
            new HashSet<EventPoint>(sections.Where(live.Contains), ReferenceEqualityComparer.Instance),
            new HashSet<EventPoint>(phrases.Where(live.Contains), ReferenceEqualityComparer.Instance),
            proposed.OrderBy(e => e.Position).ToArray());
        var byLevel = new Dictionary<ObservableCollection<Chord>, ChordUseLevelMarks>(ReferenceEqualityComparer.Instance);
        var anchors = new HashSet<FretAnchor>(ReferenceEqualityComparer.Instance);
        foreach (var level in arrangement.Levels)
        {
            var onLevel = (proposedAnchors ?? []).Where(p => ReferenceEquals(p.Level, level)).Select(p => p.Anchor)
                .OrderBy(a => a.Position).ToArray();
            if (onLevel.Length == 0) continue;
            byLevel[level.Chords] = new ChordUseLevelMarks([], [], [], onLevel);
            anchors.UnionWith(onLevel);
        }

        return marks.Count + anchors.Count == 0
            ? None
            : new ChordUseHighlight(new HashSet<Chord>(ReferenceEqualityComparer.Instance),
                new HashSet<HandShape>(ReferenceEqualityComparer.Instance), byLevel, proposedAnchors: anchors,
                events: marks);
    }

    /// <summary>The section and phrase marks on <paramref name="arrangement" />: the live
    /// markers a pending change replaces and the ones it proposes; empty for any other arrangement.</summary>
    public (IReadOnlySet<EventPoint> Sections, IReadOnlySet<EventPoint> Phrases, IReadOnlyList<EventPoint> Proposed)
        EventsOn(Arrangement? arrangement) => arrangement != null && ReferenceEquals(arrangement, _events.Arrangement)
        ? (_events.Sections, _events.Phrases, _events.Proposed)
        : (EventMarks.None.Sections, EventMarks.None.Phrases, EventMarks.None.Proposed);

    /// <summary>The marks on the level whose chord list is <paramref name="levelChords" />
    /// (the level on screen: <c>ChartEditorViewModel.PlacedChords</c>).</summary>
    public ChordUseLevelMarks On(ObservableCollection<Chord> levelChords) =>
        _byLevel.GetValueOrDefault(levelChords) ?? ChordUseLevelMarks.None;

    /// <summary>How many marked sounds sit on OTHER levels than this one — the
    /// timeline shows one level, so the panel counts these separately.</summary>
    public int ElsewhereThan(ObservableCollection<Chord> levelChords) => Count - On(levelChords).Count;

    /// <summary>Same members. Proposed anchors are new objects per publish, so they compare
    /// by value (position, fret, width).</summary>
    public bool SameAs(ChordUseHighlight other) =>
        ReferenceEquals(this, other) || (_chords.SetEquals(other._chords) && _handShapes.SetEquals(other._handShapes) &&
                                         _anchors.SetEquals(other._anchors) &&
                                         SameValues(_proposedAnchors, other._proposedAnchors) &&
                                         _events.SameAs(other._events));

    private static bool SameValues(HashSet<FretAnchor> a, HashSet<FretAnchor> b) =>
        a.Count == b.Count && a.Select(Key).ToHashSet().SetEquals(b.Select(Key));

    private static (FractionalPosition, int, int) Key(FretAnchor a) => (a.Position, a.Fret, a.Width);

    /// <summary>
    ///     The start time of the marked sound after (<paramref name="direction" />
    ///     &gt; 0) or before <paramref name="fromMs" /> on the level on screen,
    ///     wrapping round at either end; null when that level has none. Half a
    ///     millisecond of slack, so a seek that landed ON a mark moves on to the next.
    /// </summary>
    public double? Step(ObservableCollection<Chord> levelChords, IList<BeatMarker> beats, double fromMs, int direction)
    {
        var marks = On(levelChords);
        if (marks.Count + _events.Count == 0) return null;
        double? best = null;
        double? wrap = null;
        foreach (var timeMs in TimesOf(marks, beats).Concat(_events.Times(beats)))
        {
            var ahead = direction > 0 ? timeMs > fromMs + 0.5 : timeMs < fromMs - 0.5;
            if (ahead && (best == null || (direction > 0 ? timeMs < best : timeMs > best))) best = timeMs;
            if (wrap == null || (direction > 0 ? timeMs < wrap : timeMs > wrap)) wrap = timeMs;
        }

        return best ?? wrap;
    }

    /// <summary>The marked sound on the level on screen whose start is nearest
    /// <paramref name="timeMs" />, when it is within <paramref name="toleranceMs" />
    /// — what a click on a song-map mark seeks to.</summary>
    public double? Nearest(ObservableCollection<Chord> levelChords, IList<BeatMarker> beats, double timeMs,
        double toleranceMs)
    {
        double? best = null;
        foreach (var markMs in TimesOf(On(levelChords), beats).Concat(_events.Times(beats)))
        {
            var distance = Math.Abs(markMs - timeMs);
            if (distance <= toleranceMs && (best == null || distance < Math.Abs(best.Value - timeMs))) best = markMs;
        }

        return best;
    }

    private static IEnumerable<double> TimesOf(ChordUseLevelMarks marks, IList<BeatMarker> beats)
    {
        foreach (var chord in marks.Chords) yield return chord.Position.GetPositionMs(beats);
        foreach (var handShape in marks.HandShapes) yield return handShape.Position.GetPositionMs(beats);
        foreach (var anchor in marks.Anchors) yield return anchor.Position.GetPositionMs(beats);
        foreach (var anchor in marks.ProposedAnchors) yield return anchor.Position.GetPositionMs(beats);
    }

    /// <summary>The section and phrase marks, by reference for live markers and by value
    /// (position, section, phrase) for proposed ones, which are new objects per publish.</summary>
    private sealed record EventMarks(
        Arrangement? Arrangement,
        HashSet<EventPoint> Sections,
        HashSet<EventPoint> Phrases,
        EventPoint[] Proposed)
    {
        public static readonly EventMarks None = new(null, new HashSet<EventPoint>(ReferenceEqualityComparer.Instance),
            new HashSet<EventPoint>(ReferenceEqualityComparer.Instance), []);

        public int Count => Sections.Count + Phrases.Count + Proposed.Length;

        public bool SameAs(EventMarks other) =>
            ReferenceEquals(Arrangement, other.Arrangement) && Sections.SetEquals(other.Sections) &&
            Phrases.SetEquals(other.Phrases) &&
            Proposed.Select(Key).SequenceEqual(other.Proposed.Select(Key));

        public IEnumerable<double> Times(IList<BeatMarker> beats) =>
            Sections.Concat(Phrases).Concat(Proposed).Select(e => e.Position.GetPositionMs(beats));

        private static (FractionalPosition, string, string) Key(EventPoint e) => (e.Position, e.Section, e.Phrase);
    }
}
