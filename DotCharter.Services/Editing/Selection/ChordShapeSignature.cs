namespace DotCharter.Services.Editing.Selection;

/// <summary>
///     What "the same chord" means to select-like: the string→fret map a sound
///     actually PLAYS, plus the string→finger map its template authors. Nothing
///     else. Bends, slides, palm mutes, accents, sustains and the identity of
///     the <see cref="ChordTemplate" /> object behind the sound are all
///     deliberately absent — the owner's definition of "similar" is
///     <i>the same notes and the same finger positions, NOT the same
///     technique</i>.
///     <para>
///         <b>Template IDENTITY is not the key, and must never be.</b> A
///         <c>ChordTemplate</c> is shared and deduped by shape, so two chords
///         playing the same notes USUALLY point at the same template — but the
///         moment one of them is re-fingered, isolated or reshaped it mints its
///         own template and an id-keyed match drops every sibling on the floor.
///         That was the reported bug: re-finger one chord of eight identical
///         ones and select-like returned nothing until the edit was undone.
///         Shape equality subsumes id equality (same template ⇒ same frets ⇒
///         same signature, as long as the chord's notes have not drifted from
///         it), so nothing is lost by dropping the id.
///     </para>
///     <para>
///         <b>An unauthored finger is a HOLE, not a value.</b> Fingers only have
///         to agree on strings where BOTH sides specify one. A freshly imported
///         chart carries no fingering at all, and the whole point of the
///         fingering workflow (<c>FindMatchingTemplatesNeedingFingering</c>,
///         "Save &amp; next missing") is that "unspecified" is a gap to be
///         filled — comparing it against "index finger" as a DIFFERENCE would
///         mean an unfingered chord can never be selected alongside the one you
///         just fingered, which is exactly the workflow this exists to serve.
///         Where both sides do specify, they must match: the same frets fingered
///         1-3-4 and 1-2-3 are two different hand shapes, and the RS chord
///         template stores the fingers precisely because that distinction is
///         real.
///     </para>
/// </summary>
public sealed class ChordShapeSignature
{
    private readonly Dictionary<int, int> _fingers;
    private readonly Dictionary<int, int> _frets;

    private ChordShapeSignature(Dictionary<int, int> frets, Dictionary<int, int> fingers)
    {
        _frets = frets;
        _fingers = fingers;
    }

    /// <summary>The (string → fret) pairs the sound plays.</summary>
    public IReadOnlyDictionary<int, int> Frets => _frets;

    /// <summary>The (string → finger) pairs its template authors, restricted to
    /// strings the sound actually plays.</summary>
    public IReadOnlyDictionary<int, int> Fingers => _fingers;

    /// <summary>A shape with no fretted strings at all — never a seed and never
    /// a match, or a note-less chord would select every other note-less
    /// chord.</summary>
    public bool IsEmpty => _frets.Count == 0;

    /// <summary>
    ///     The shape a chord PLAYS. Frets come from the chord's own notes (a
    ///     freshly imported chord sits at <c>TemplateId -1</c> until the first
    ///     save, so its template is not a source of truth), fingers from its
    ///     template — and only when that template still describes the chord's
    ///     notes, because a drifted template's fingering belongs to some other
    ///     shape.
    /// </summary>
    public static ChordShapeSignature ForChord(Chord chord, Arrangement? arrangement)
    {
        ArgumentNullException.ThrowIfNull(chord);

        var frets = new Dictionary<int, int>(chord.ChordNotes.Count);
        foreach (var chordNote in chord.ChordNotes) frets[chordNote.StringIndex] = chordNote.Fret;

        var template = arrangement == null
            ? null
            : ChordTemplateResolver.FindById(arrangement, chord.TemplateId);

        return new ChordShapeSignature(frets, FingersOf(template, frets));
    }

    /// <summary>
    ///     The shape a hand shape declares. A hand shape has no notes of its
    ///     own — its template IS its shape — so a shape with no resolvable
    ///     template has no signature at all and never matches.
    /// </summary>
    public static ChordShapeSignature? ForHandShape(HandShape handShape, Arrangement? arrangement)
    {
        ArgumentNullException.ThrowIfNull(handShape);
        if (arrangement == null) return null;

        var template = ChordTemplateResolver.FindById(arrangement, handShape.TemplateId);
        if (template == null || template.Frets.Count == 0) return null;

        var frets = new Dictionary<int, int>(template.Frets);
        return new ChordShapeSignature(frets, FingersOf(template, frets));
    }

    /// <summary>
    ///     "Alike" — the frets are identical and no string carries a conflicting
    ///     finger. Symmetric, and deliberately NOT transitive: an unfingered
    ///     shape is alike to every fingering of itself.
    /// </summary>
    public bool Matches(ChordShapeSignature other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (IsEmpty || other.IsEmpty) return false;
        if (!DictionariesEqual(_frets, other._frets)) return false;

        foreach (var (stringIndex, finger) in _fingers)
        {
            if (other._fingers.TryGetValue(stringIndex, out var otherFinger) && otherFinger != finger)
                return false;
        }

        return true;
    }

    /// <summary>Exact equality of both maps — used only to dedupe the seed list,
    /// never to match.</summary>
    public bool SameAs(ChordShapeSignature other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return DictionariesEqual(_frets, other._frets) && DictionariesEqual(_fingers, other._fingers);
    }

    private static Dictionary<int, int> FingersOf(ChordTemplate? template, Dictionary<int, int> frets)
    {
        var fingers = new Dictionary<int, int>();
        if (template == null) return fingers;
        if (!DictionariesEqual(template.Frets, frets)) return fingers;

        foreach (var (stringIndex, finger) in template.Fingers)
        {
            if (frets.ContainsKey(stringIndex))
                fingers[stringIndex] = finger;
        }

        return fingers;
    }

    private static bool DictionariesEqual(Dictionary<int, int> a, Dictionary<int, int> b)
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
