namespace DotCharter.Core.Utilities;

/// <summary>
///     Which two fingers play a TWO-STRING grip whose upper note sits two frets
///     above its root — a two-string power chord or a two-fret octave: the index
/// on the root, and the pinky or the ring finger on the other note.
///     <para>
///         The fret shape cannot choose. In the owner's verified charts each song
///         keeps one of the two and the next song takes the other, so the choice is
///         an INPUT to <see cref="ChordTemplateFingerSetter" />, decided per
///         arrangement on the Services side, never by the setter itself.
///     </para>
/// </summary>
public enum PairFingering
{
    /// <summary><c>1·4</c>, index and pinky.</summary>
    IndexPinky,

    /// <summary><c>1·3</c>, index and ring.</summary>
    IndexRing
}
