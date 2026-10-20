using DotCharter.Core.Domain.Guitar;

namespace DotCharter.Core.Utilities;

/// <summary>
///     When a hand shape's own grip may be rebuilt from the sounds it covers.
///     <para>
///         EOF auto-builds a hand shape's chord template from the notes inside
///         it, and a charter who drags the bar over more notes expects the grip
///         (and its fingering) to follow. DotCharter did not: generating a bar
///         over one note built a ONE-STRING template, and extending the bar left
///         that template behind — the shape covered four notes and the editor
///         still showed a single finger.
///     </para>
///     <para>
///         The rebuild is only ever safe on a template the shape owns ALONE.
///         A <see cref="ChordTemplate" /> is SHARED — one instance backs every
///         chord and hand shape with that grip — so rewriting a shared one is the
///         v0.33.2 corruption, and a shape sitting on a real chord's template is
///         describing that chord's grip on purpose. Those are left alone.
///     </para>
/// </summary>
public static class HandShapeAutoTemplate
{
    /// <summary>
    ///     True when <paramref name="shape" />'s template is referenced by nothing
    ///     else — no chord, and no other hand shape — so rewriting it in place
    ///     changes only this bar.
    /// </summary>
    public static bool OwnsItsTemplateAlone(
        HandShape shape, IEnumerable<Chord> chords, IEnumerable<HandShape> shapes)
    {
        if (shape.TemplateId < 0) return false;
        foreach (var chord in chords)
        {
            if (chord.TemplateId == shape.TemplateId)
                return false;
        }

        foreach (var other in shapes)
        {
            if (!ReferenceEquals(other, shape) && other.TemplateId == shape.TemplateId)
                return false;
        }

        return true;
    }

    /// <summary>
    ///     The grip <paramref name="current" /> should become, given the frets the
    ///     sounds under the bar actually play. Returns <c>null</c> when nothing
    ///     would change, so a wheel tick that moved the end past empty space
    ///     costs no edit at all.
    /// </summary>
    /// <param name="current">The template the shape points at, or <c>null</c> when it has none.</param>
    /// <param name="seed">Frets gathered from the sounds inside the bar; <c>null</c> when it covers nothing fretted.</param>
    /// <param name="stringCount">String count of the arrangement, for the fingering suggester.</param>
    /// <param name="pairHint">
    ///     The 1·3 / 1·4 the arrangement plays for the rebuilt grip when it is a power chord or a
    /// two-fret octave (<c>ArrangementFingeringStyle.RebuildHandShapeGrip</c>); null keeps
    ///     the suggester's own.
    /// </param>
    public static ChordTemplate? Rebuild(ChordTemplate? current, ChordTemplate? seed, int stringCount,
        Func<ChordTemplate, PairFingering?>? pairHint = null)
    {
        if (seed == null || seed.Frets.Count == 0) return null;

        // An AUTHORED grip — pinned, or holding ghost gems the notes under the bar cannot
        // describe — is never re-derived from them: the bar grows, the grip stays.
        if (current is { IsPinned: true }) return null;

        var rebuilt = new ChordTemplate
        {
            // Identity and intent survive: the shape keeps its id, its NAME and
            // its arpeggio flag. Only the grip is derived — a bar the charter
            // named "C#min7" is still that bar one note wider.
            Id = current?.Id ?? -1,
            Name = current?.Name ?? string.Empty,
            Arpeggio = current?.Arpeggio ?? false,
            Frets = new Dictionary<int, int>(seed.Frets)
        };

        // A hand-authored finger on a string whose FRET did not move is kept —
        // re-deriving the whole fingering would throw away the charter's own
        // answer every time the bar grew by one note.
        if (current != null)
        {
            foreach (var (stringIndex, finger) in current.Fingers)
            {
                if (current.Frets.TryGetValue(stringIndex, out var was) &&
                    rebuilt.Frets.TryGetValue(stringIndex, out var now) && was == now)
                {
                    rebuilt.Fingers[stringIndex] = finger;
                }
            }
        }

        if (NeedsFingers(rebuilt))
        {
            var suggestion = rebuilt.Clone();
            ChordTemplateFingerSetter.SetSuggestedFingers(suggestion, Math.Max(1, stringCount), pairHint?.Invoke(suggestion));
            foreach (var (stringIndex, finger) in suggestion.Fingers)
                rebuilt.Fingers.TryAdd(stringIndex, finger);
        }

        // Open strings never carry a finger, whatever was there before.
        foreach (var stringIndex in rebuilt.Fingers.Keys.ToList())
        {
            if (rebuilt.Frets.TryGetValue(stringIndex, out var fret) && fret == 0)
                rebuilt.Fingers.Remove(stringIndex);
        }

        if (current != null && SameGrip(current, rebuilt)) return null;
        return rebuilt;
    }

    private static bool NeedsFingers(ChordTemplate template)
    {
        foreach (var (stringIndex, fret) in template.Frets)
        {
            if (fret > 0 && !template.Fingers.ContainsKey(stringIndex))
                return true;
        }

        return false;
    }

    private static bool SameGrip(ChordTemplate a, ChordTemplate b) =>
        SameMap(a.Frets, b.Frets) && SameMap(a.Fingers, b.Fingers);

    private static bool SameMap(Dictionary<int, int> a, Dictionary<int, int> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (key, value) in a)
        {
            if (!b.TryGetValue(key, out var other) || other != value)
                return false;
        }

        return true;
    }
}
