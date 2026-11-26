namespace DotCharter.Services.Editing.Fingering;

/// <summary>
///     A grip that is PART of the grip next to it takes that grip's fingers:
///     <c>x 14 16 x 16</c> played beside <c>x 14 16 16 16</c> (fingered
///     <c>1 3 3 3</c>, a ring barre) is fingered <c>1 3 x 3</c>, the hand that
///     is already there, not the context-free ring + pinky. "Part of" means
///     every string the grip names sits on the same fret in the neighbour, and
///     the neighbour names more strings. The neighbours are the grips played
///     just before and just after it (chords and hand shapes, in time order, a
///     repeat of the same grip collapsed); where more than one qualifies, over
///     every place the grip is played, the one with the most strings wins, then
///     the one met most often, then the earliest.
///     <para>
///         A pure function of the grip sequence and the fingers each template
///         has (<c>fingersOf</c>): the callers decide which templates may be
///         re-fingered (a free suggestion, never an authored or pinned grip)
///         and how the result is written (<c>ImportFhpPass</c> repoints the
///         chords onto a new template; Generate fingerings writes it in place
///         of the suggestion).
///     </para>
/// </summary>
public static class NeighbourGripFingering
{
    /// <summary>
    ///     The template ids <paramref name="level" />'s chords and hand shapes
    ///     play, in time order, each run of one id collapsed to a single entry.
    ///     A position holding a chord and the hand shape over it counts once.
    /// </summary>
    /// <param name="level">The level whose grips are read; never modified.</param>
    /// <param name="chordId">The template a chord plays (default: its <c>TemplateId</c>).</param>
    /// <param name="shapeId">The template a hand shape holds (default: its <c>TemplateId</c>).</param>
    public static List<int> GripSequence(ChartLevel level, Func<Chord, int>? chordId = null,
        Func<HandShape, int>? shapeId = null)
    {
        ArgumentNullException.ThrowIfNull(level);
        chordId ??= c => c.TemplateId;
        shapeId ??= h => h.TemplateId;
        var events = level.Chords.Select(c => (c.Position, Id: chordId(c)))
            .Concat(level.HandShapes.Select(h => (h.Position, Id: shapeId(h))))
            .Where(e => e.Id >= 0)
            .OrderBy(e => e.Position)
            .ToList();
        var sequence = new List<int>(events.Count);
        foreach (var (_, id) in events)
        {
            if (sequence.Count == 0 || sequence[^1] != id) sequence.Add(id);
        }

        return sequence;
    }

    /// <summary>
    ///     For each template <paramref name="mayRefinger" /> admits, the fingers
    ///     of the neighbour grip it is part of, when they differ from its own.
    ///     Keyed by template id.
    /// </summary>
    /// <param name="sequences">One <see cref="GripSequence" /> per level.</param>
    /// <param name="byId">The templates by id.</param>
    /// <param name="mayRefinger">Whether a template's fingering may change.</param>
    /// <param name="fingersOf">The fingers a template has (or is about to get).</param>
    /// <param name="capo">Frets at or below it are open strings, which take no finger.</param>
    public static Dictionary<int, Dictionary<int, int>> Plan(
        IEnumerable<IReadOnlyList<int>> sequences,
        IReadOnlyDictionary<int, ChordTemplate> byId,
        Func<ChordTemplate, bool> mayRefinger,
        Func<ChordTemplate, IReadOnlyDictionary<int, int>> fingersOf,
        int capo = 0)
    {
        ArgumentNullException.ThrowIfNull(sequences);
        ArgumentNullException.ThrowIfNull(byId);
        ArgumentNullException.ThrowIfNull(mayRefinger);
        ArgumentNullException.ThrowIfNull(fingersOf);

        // template id -> neighbour id -> (times met, first met)
        var met = new Dictionary<int, Dictionary<int, (int Count, int First)>>();
        var order = 0;
        foreach (var sequence in sequences)
        {
            for (var i = 0; i < sequence.Count; i++)
            {
                if (!byId.TryGetValue(sequence[i], out var grip) || !mayRefinger(grip)) continue;
                if (i > 0) Meet(met, grip, sequence[i - 1], byId, capo, fingersOf, ref order);
                if (i + 1 < sequence.Count) Meet(met, grip, sequence[i + 1], byId, capo, fingersOf, ref order);
            }
        }

        var plan = new Dictionary<int, Dictionary<int, int>>();
        foreach (var (id, neighbours) in met)
        {
            var best = neighbours
                .OrderByDescending(n => byId[n.Key].Frets.Count)
                .ThenByDescending(n => n.Value.Count)
                .ThenBy(n => n.Value.First)
                .First().Key;
            var grip = byId[id];
            var fingers = Restrict(grip, fingersOf(byId[best]));
            if (!MapEquality.SameEntries(fingers, fingersOf(grip))) plan[id] = fingers;
        }

        return plan;
    }

    /// <summary>
    ///     Whether <paramref name="grip" /> is part of <paramref name="neighbour" />:
    ///     every string it names on the same fret there, and the neighbour names
    ///     more strings.
    /// </summary>
    public static bool IsPartOf(ChordTemplate grip, ChordTemplate neighbour)
    {
        ArgumentNullException.ThrowIfNull(grip);
        ArgumentNullException.ThrowIfNull(neighbour);
        return grip.Frets.Count > 0 && neighbour.Frets.Count > grip.Frets.Count
            && grip.Frets.All(kv => neighbour.Frets.TryGetValue(kv.Key, out var fret) && fret == kv.Value);
    }

    private static void Meet(Dictionary<int, Dictionary<int, (int Count, int First)>> met, ChordTemplate grip,
        int neighbourId, IReadOnlyDictionary<int, ChordTemplate> byId, int capo,
        Func<ChordTemplate, IReadOnlyDictionary<int, int>> fingersOf, ref int order)
    {
        if (!byId.TryGetValue(neighbourId, out var neighbour) || !IsPartOf(grip, neighbour)) return;
        // The neighbour must finger every fretted string the grip plays.
        var fingers = fingersOf(neighbour);
        if (grip.Frets.Any(kv => kv.Value > capo && (!fingers.TryGetValue(kv.Key, out var f) || f < 0))) return;
        if (!met.TryGetValue(grip.Id, out var neighbours)) met[grip.Id] = neighbours = [];
        neighbours[neighbourId] = neighbours.TryGetValue(neighbourId, out var seen)
            ? (seen.Count + 1, seen.First)
            : (1, order++);
    }

    private static Dictionary<int, int> Restrict(ChordTemplate grip, IReadOnlyDictionary<int, int> fingers)
    {
        var restricted = new Dictionary<int, int>();
        foreach (var stringIndex in grip.Frets.Keys)
        {
            if (fingers.TryGetValue(stringIndex, out var finger)) restricted[stringIndex] = finger;
        }

        return restricted;
    }
}
