using Chord = DotCharter.Core.Domain.Guitar.Chord;
using FretAnchor = DotCharter.Core.Domain.Events.FretAnchor;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;
using HandShape = DotCharter.Core.Domain.Guitar.HandShape;

namespace DotCharter.Services.Editing.Collisions;

public class CollisionResolution
{
    public List<GuitarNote> NotesToDestroy { get; } = [];
    public List<Chord> ChordsToDestroy { get; } = [];
    public List<HandShape> HandShapesToDestroy { get; } = [];
    public List<FretAnchor> AnchorsToDestroy { get; } = [];

    public void Merge(CollisionResolution other)
    {
        NotesToDestroy.AddRange(other.NotesToDestroy.Where(n => !NotesToDestroy.Contains(n)));
        ChordsToDestroy.AddRange(other.ChordsToDestroy.Where(c => !ChordsToDestroy.Contains(c)));
        HandShapesToDestroy.AddRange(other.HandShapesToDestroy.Where(h => !HandShapesToDestroy.Contains(h)));
        AnchorsToDestroy.AddRange(other.AnchorsToDestroy.Where(a => !AnchorsToDestroy.Contains(a)));
    }
}

public interface IStrictCollisionService
{
    CollisionResolution CalculateNoteCollisions(IEnumerable<GuitarNote> incomingNotes,
        IEnumerable<GuitarNote> existingNotes, IEnumerable<GuitarNote> explicitlyIgnoredNotes);

    CollisionResolution CalculateChordCollisions(IEnumerable<Chord> incomingChords, IEnumerable<Chord> existingChords,
        IEnumerable<Chord> explicitlyIgnoredChords);

    CollisionResolution CalculateHandShapeOverride(IEnumerable<HandShape> incomingHandShapes,
        IEnumerable<HandShape> existingHandShapes,
        IEnumerable<HandShape> explicitlyIgnoredHandShapes);

    /// <summary>
    ///     An arriving FHP anchor destroys any anchor already on its beat.
    ///     <para>
    ///         One beat holds ONE fret-hand position — two say different things
    ///         about the same instant, and RS-XML has no way to spell that.
    ///         Notes, chords and hand shapes each had a rule here from the start;
    ///         anchors did not, so a paste that carried anchors simply stacked
    ///         them on whatever was already there and the duplicate first showed
    ///         up as inspector "Two anchors share the same position".
    ///     </para>
    /// </summary>
    CollisionResolution CalculateAnchorCollisions(IEnumerable<FretAnchor> incomingAnchors,
        IEnumerable<FretAnchor> existingAnchors, IEnumerable<FretAnchor> explicitlyIgnoredAnchors);
}
