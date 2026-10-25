namespace DotCharter.Services.Editing.ChordShapes;

/// <summary>
///     The seam a dialog marks chords and hand shapes on the editor's timeline and song map through,
///     so the uses an edit would reach light up where the charter works. The editor owns the marks; a
///     dialog sets them while it is open and clears them when it closes.
/// </summary>
public interface IChordUseMarks
{
    /// <summary>Marks exactly these sounds (by reference) across the whole song, replacing any earlier marks.</summary>
    void Mark(IReadOnlyList<Chord> chords, IReadOnlyList<HandShape> handShapes);

    /// <summary>Takes the marks away.</summary>
    void Clear();
}
