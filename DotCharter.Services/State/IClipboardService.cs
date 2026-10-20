using DotCharter.Services.Editing.Catalog;

namespace DotCharter.Services.State;

public interface IClipboardService
{
    bool HasData { get; }

    /// <summary>
    ///     <c>chordTemplates</c> are the templates the copied chords / hand shapes
    ///     reference. Required for a cross-arrangement paste to mean anything — a
    ///     raw <c>TemplateId</c> is only valid inside its own arrangement.
    ///     <c>source</c> is the arrangement copied from, so a paste can be checked
    /// against its destination.
    /// </summary>
    void Copy(IEnumerable<GuitarNote> notes, IEnumerable<Chord> chords, IEnumerable<HandShape> handShapes,
        IEnumerable<EventPoint> eventPoints, IEnumerable<ToneChange> toneChanges, IEnumerable<FretAnchor> anchors,
        IEnumerable<ChordTemplate>? chordTemplates = null, PhraseSourceContext? source = null);

    ClipboardPayload? GetPastedData(FractionalPosition targetPos);
}
