namespace DotCharter.Services.Editing.FretAnchors;

/// <summary>
///     One fretted sound as the FHP walk (<see cref="FretAnchorGenerationService" />) indexes it: a single note, or one
///     string of a chord with the finger its template — or a FREE chord's base grip — puts on it.
/// </summary>
internal readonly record struct SoundAtPosition(
    FractionalPosition Position,
    FractionalPosition Sustain,
    int Fret,
    HopoType Hopo,
    int? SlideTo,
    bool UnpitchedSlide,
    int Finger,
    int FreeSlack,
    int StringIndex)
{
    /// <summary><see cref="FreeSlack" /> for a sound whose fingering may not move.</summary>
    public const int NotFree = -1;

    public static SoundAtPosition FromNote(GuitarNote note) =>
        new(note.Position, note.Sustain, note.Fret, note.Hopo, note.SlideTo, note.UnpitchedSlide, -1, NotFree,
            note.StringIndex);

    public static SoundAtPosition FromChordNote(FractionalPosition position, ChordNote note, int finger,
        int freeSlack) =>
        new(position, note.Sustain, note.Fret, note.Hopo, note.SlideTo, note.UnpitchedSlide, finger, freeSlack,
            note.StringIndex);
}
