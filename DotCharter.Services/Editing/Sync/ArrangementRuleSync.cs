using DotCharter.Services.Editing.HandShapes;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using FretAnchor = DotCharter.Core.Domain.Events.FretAnchor;

namespace DotCharter.Services.Editing.Sync;

/// <summary>
///     The rule pass of <see cref="ArrangementPitchSync" />: a reduced part re-voices
///     the chords its strings cannot play (a 7-string drop A B5 becomes F#2+B2 on the
///     6-string drop D), so pitch matching finds almost nothing, but the reduction is
///     CONSISTENT. The pass learns each mapping from the two charts — the source chord's
///     sounding pitch set at a position → the target chord's (string, fret) set at the same
///     position — and spreads the hand work authored on ONE target occurrence of a mapping
///     to every other occurrence of it. Notes are never changed.
///     <para>
///         <b>Which occurrence is the authored one.</b> Per mapping and per kind (chord
///         fingering, hand shape, fret-hand position) the target items are grouped by value.
///         The DEFAULT value is the generator's: for fingering the grip whose fingers equal
///         the finger suggester's answer for that voicing, otherwise (and for hand shapes and
///         anchors, which have no per-occurrence generator answer) the value most items hold,
///         with a strict lead. Every other value is authored. Exactly one authored value is
///         copied onto every item still holding the default; items with that authored value
///         keep it. Two or more authored values, or no clear default, copy nothing for that
///         mapping and kind and count as a conflict.
///     </para>
///     <para>
///         <b>Where the hand really sits.</b> A hand shape is an item only when every sound
///         under it is a chord of that one mapping; an anchor only when every sound it
///         governs (up to the next anchor) is. A bar or a window that also covers other
///         sounds is left alone. A fingering is per chord and needs no context; a bar over
///         a repointed chord follows it when it was in step (<see cref="ChordHandShapeSync" />).
///         Templates are shared and never edited: a chord or bar is repointed at the
///         authored occurrence's template, which already plays the same frets.
///     </para>
/// </summary>
internal static class ArrangementRuleSync
{
    /// <summary>What the rule pass did, or would do.</summary>
    public readonly record struct Result(int Mappings, int Fingerings, int HandShapes, int Anchors, int Conflicts);

    public static Result Run(
        Arrangement source, Arrangement target, ChartLevel sourceLevel, ChartLevel targetLevel,
        ArrangementStringMap strings, bool apply)
    {
        var (groups, mappingOf) = Learn(source, sourceLevel, targetLevel, strings);
        if (groups.Count == 0) return new Result(0, 0, 0, 0, 0);

        var sounds = TargetSounds(targetLevel, mappingOf);
        var conflicts = 0;
        var chordMoves = FingeringMoves(target, groups, ref conflicts);
        var shapeMoves = Carry(
            Items(targetLevel.HandShapes, s => OnlyMapping(sounds, s.Position, s.EndPosition)),
            s => new BarValue(target.FindChordTemplate(s.TemplateId), s.GhostMask, s.Name), ref conflicts);

        var anchors = targetLevel.Anchors.OrderBy(a => a.Position).ToList();
        var anchorEnd = new Dictionary<FretAnchor, FractionalPosition?>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < anchors.Count; i++)
            anchorEnd[anchors[i]] = i + 1 < anchors.Count ? anchors[i + 1].Position : null;
        var anchorMoves = Carry(
            Items(anchors, a => OnlyMapping(sounds, a.Position, anchorEnd[a])), a => (a.Fret, a.Width), ref conflicts);

        // A bar over a repointed chord follows it when it was in step, unless the rule
        // already gave that bar its own authored answer.
        var ruled = new HashSet<HandShape>(shapeMoves.Select(m => m.Item), ReferenceEqualityComparer.Instance);
        var followers = ChordHandShapeSync.Follow(targetLevel.HandShapes, targetLevel.Chords, chordMoves)
            .Where(r => !ruled.Contains(r.Shape))
            .ToList();

        if (apply) Write(chordMoves, shapeMoves, followers, anchorMoves);
        return new Result(groups.Count, chordMoves.Count, shapeMoves.Count + followers.Count, anchorMoves.Count,
            conflicts);
    }

    /// <summary>Every target chord with a source chord at its position, grouped by mapping.</summary>
    private static (Dictionary<string, List<Chord>> Groups, Dictionary<Chord, string> MappingOf) Learn(
        Arrangement source, ChartLevel sourceLevel, ChartLevel targetLevel, ArrangementStringMap strings)
    {
        var sourceAt = new Dictionary<FractionalPosition, Chord>();
        foreach (var chord in sourceLevel.Chords) sourceAt.TryAdd(chord.Position, chord);

        var mappingOf = new Dictionary<Chord, string>(ReferenceEqualityComparer.Instance);
        var groups = new Dictionary<string, List<Chord>>(StringComparer.Ordinal);
        foreach (var chord in targetLevel.Chords)
        {
            if (chord.ChordNotes.Count == 0 || !sourceAt.TryGetValue(chord.Position, out var from) ||
                MappingKey(source, from, chord, strings) is not { } key)
            {
                continue;
            }

            mappingOf[chord] = key;
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
            list.Add(chord);
        }

        return (groups, mappingOf);
    }

    /// <summary>The chords to repoint at their mapping's authored grip.</summary>
    private static List<ChordHandShapeSync.ChordMove> FingeringMoves(
        Arrangement target, Dictionary<string, List<Chord>> groups, ref int conflicts)
    {
        var moves = new List<ChordHandShapeSync.ChordMove>();
        foreach (var chords in groups.Values)
        {
            var suggested = SuggestedFingers(target, chords[0]);
            var decision = Decide(chords, c => new GripValue(target.FindChordTemplate(c.TemplateId)),
                v => v.Template is { } t && suggested != null && SameFingers(t.Fingers, suggested));
            if (decision.Conflict) conflicts++;
            if (decision.From is not { } authored) continue;
            // A pinned grip is the user's; only unpinned occurrences are repointed.
            moves.AddRange(decision.Targets!.Where(c => target.FindChordTemplate(c.TemplateId)?.IsPinned != true).Select(c =>
                new ChordHandShapeSync.ChordMove(c, c.TemplateId, authored.TemplateId)));
        }

        return moves;
    }

    /// <summary>The items whose context is one mapping, grouped by it.</summary>
    private static List<List<T>> Items<T>(IEnumerable<T> all, Func<T, string?> mappingOf)
    {
        var byKey = new Dictionary<string, List<T>>(StringComparer.Ordinal);
        foreach (var item in all)
        {
            if (mappingOf(item) is not { } key) continue;
            if (!byKey.TryGetValue(key, out var list)) byKey[key] = list = [];
            list.Add(item);
        }

        return [.. byKey.Values];
    }

    /// <summary>Per mapping, each default item paired with the authored item it takes its value from.</summary>
    private static List<(T Item, T From)> Carry<T, TValue>(
        List<List<T>> groups, Func<T, TValue> valueOf, ref int conflicts)
        where T : class where TValue : notnull
    {
        var moves = new List<(T Item, T From)>();
        foreach (var items in groups)
        {
            var decision = Decide(items, valueOf, null);
            if (decision.Conflict) conflicts++;
            if (decision.From is { } authored) moves.AddRange(decision.Targets!.Select(i => (i, authored)));
        }

        return moves;
    }

    private static void Write(
        List<ChordHandShapeSync.ChordMove> chordMoves, List<(HandShape Item, HandShape From)> shapeMoves,
        List<ChordHandShapeSync.Retarget> followers, List<(FretAnchor Item, FretAnchor From)> anchorMoves)
    {
        foreach (var move in chordMoves) move.Chord.TemplateId = move.NewTemplateId;
        foreach (var (shape, from) in shapeMoves)
        {
            shape.TemplateId = from.TemplateId;
            shape.GhostMask = from.GhostMask;
            shape.Name = from.Name;
        }

        foreach (var follower in followers) follower.Shape.TemplateId = follower.TemplateId;
        foreach (var (anchor, from) in anchorMoves)
        {
            anchor.Fret = from.Fret;
            anchor.Width = from.Width;
        }
    }

    /// <summary>
    ///     "source pitch set -> target string:fret set", or null when the source chord is
    ///     unpriced or the target plays it exactly (a pitch twin, which the pitch pass owns).
    /// </summary>
    private static string? MappingKey(Arrangement source, Chord from, Chord to, ArrangementStringMap strings)
    {
        var pitches = new SortedSet<int>();
        var twin = from.ChordNotes.Count == to.ChordNotes.Count;
        var voicing = to.ChordNotes.Select(n => (n.StringIndex, n.Fret)).ToHashSet();
        foreach (var note in from.ChordNotes)
        {
            if (SoundingPitch.SoundingMidiFor(source, note.StringIndex, note.Fret) is not { } midi) return null;
            pitches.Add(midi);
            twin = twin && strings.TryMap(note.StringIndex, out var t) &&
                   strings.SoundsAlike(note.StringIndex, t, note.Fret) && voicing.Contains((t, note.Fret));
        }

        if (twin || pitches.Count == 0) return null;
        var shape = string.Join(",", voicing.OrderBy(v => v.StringIndex).Select(v => $"{v.StringIndex}:{v.Fret}"));
        return $"{string.Join(",", pitches)}->{shape}";
    }

    /// <summary>Every target note and chord in time order, with its chord's mapping (null for anything else).</summary>
    private static List<(FractionalPosition Position, string? Key)> TargetSounds(
        ChartLevel level, Dictionary<Chord, string> mappingOf)
    {
        var sounds = new List<(FractionalPosition Position, string? Key)>(level.Notes.Count + level.Chords.Count);
        foreach (var note in level.Notes) sounds.Add((note.Position, null));
        foreach (var chord in level.Chords) sounds.Add((chord.Position, mappingOf.GetValueOrDefault(chord)));
        sounds.Sort(static (a, b) => a.Position.CompareTo(b.Position));
        return sounds;
    }

    /// <summary>The one mapping every sound in [start, end) belongs to; null when there is none, or another sound.</summary>
    private static string? OnlyMapping(
        List<(FractionalPosition Position, string? Key)> sounds, FractionalPosition start, FractionalPosition? end)
    {
        var lo = 0;
        var hi = sounds.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (sounds[mid].Position < start) lo = mid + 1;
            else hi = mid;
        }

        string? key = null;
        for (var i = lo; i < sounds.Count && sounds[i].Position.IsWithin(start, end); i++)
        {
            if (sounds[i].Key is not { } k || (key != null && key != k)) return null;
            key = k;
        }

        return key;
    }

    /// <summary>The fingers the suggester gives this voicing, as the importer generated them.</summary>
    private static Dictionary<int, int>? SuggestedFingers(Arrangement target, Chord chord)
    {
        var grip = new ChordTemplate();
        foreach (var note in chord.ChordNotes) grip.Frets[note.StringIndex] = note.Fret;
        ChordTemplateFingerSetter.SetSuggestedFingers(grip, target.StringCount);
        return grip.Fingers.Count > 0 ? grip.Fingers : null;
    }

    private static bool SameFingers(Dictionary<int, int> a, Dictionary<int, int> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var f) && f == kv.Value);

    /// <summary>
    ///     The authored-occurrence rule (see the type summary): the default value (picked by
    ///     <paramref name="isDefault" /> when exactly one value passes it, else a strict
    ///     plurality), the one authored value's first item, and the items to overwrite.
    /// </summary>
    private static Decision<TItem> Decide<TItem, TValue>(
        List<TItem> items, Func<TItem, TValue> valueOf, Func<TValue, bool>? isDefault)
        where TItem : class where TValue : notnull
    {
        var byValue = new Dictionary<TValue, List<TItem>>();
        foreach (var item in items)
        {
            var value = valueOf(item);
            if (!byValue.TryGetValue(value, out var list)) byValue[value] = list = [];
            list.Add(item);
        }

        if (byValue.Count < 2) return default;

        var preferred = isDefault == null ? [] : byValue.Keys.Where(isDefault).ToList();
        List<TItem>? defaults;
        if (preferred.Count == 1)
        {
            defaults = byValue[preferred[0]];
        }
        else
        {
            var ranked = byValue.Values.OrderByDescending(l => l.Count).ToList();
            defaults = ranked[0].Count > ranked[1].Count ? ranked[0] : null;
        }

        if (defaults == null || byValue.Count > 2) return new Decision<TItem>(null, null, true);
        var authored = byValue.Values.First(l => !ReferenceEquals(l, defaults));
        return new Decision<TItem>(authored[0], defaults, false);
    }

    /// <summary>The authored item to copy from (null: nothing to copy), the items it overwrites, and whether the variants conflicted.</summary>
    private readonly record struct Decision<TItem>(TItem? From, List<TItem>? Targets, bool Conflict)
        where TItem : class;

    /// <summary>A chord's grip, by value (<see cref="ChordTemplate.Equals(object?)" />); null when it names none.</summary>
    private readonly record struct GripValue(ChordTemplate? Template);

    /// <summary>A hand shape's grip, ghost override and name, by value.</summary>
    private readonly record struct BarValue(ChordTemplate? Template, int? GhostMask, string Name);
}
