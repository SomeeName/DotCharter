namespace DotCharter.Services.State;

public enum PasteSpecialMode
{
    /// <summary>
    /// Merge: paste content alongside existing items in the destination range, deleting nothing.
    /// </summary>
    Preserve,

    /// <summary>
    /// Only delete existing items whose type also appears in the clipboard payload before pasting.
    /// </summary>
    OverwriteSameType,

    /// <summary>
    /// Wipe the entire destination range (notes, chords, hand shapes, anchors, events) before pasting.
    /// </summary>
    OverwriteAll
}
