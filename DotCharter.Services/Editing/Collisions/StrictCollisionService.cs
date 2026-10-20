using Chord = DotCharter.Core.Domain.Guitar.Chord;
using FretAnchor = DotCharter.Core.Domain.Events.FretAnchor;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;
using HandShape = DotCharter.Core.Domain.Guitar.HandShape;

namespace DotCharter.Services.Editing.Collisions;

public class StrictCollisionService : IStrictCollisionService
{
    public CollisionResolution CalculateNoteCollisions(
        IEnumerable<GuitarNote> incomingNotes,
        IEnumerable<GuitarNote> existingNotes,
        IEnumerable<GuitarNote> explicitlyIgnoredNotes)
    {
        var result = new CollisionResolution();
        var ignoreSet = explicitlyIgnoredNotes.ToHashSet();

        // FIX: Materialize existing notes once!
        var existingNotesList = existingNotes.ToList();

        foreach (var incoming in incomingNotes)
        {
            var collisions = existingNotesList.Where(existing => // Query the List, not the IEnumerable
                !ignoreSet.Contains(existing) &&
                existing.Position == incoming.Position &&
                existing.StringIndex == incoming.StringIndex);

            result.NotesToDestroy.AddRange(collisions);
        }

        return result;
    }

    public CollisionResolution CalculateChordCollisions(
        IEnumerable<Chord> incomingChords,
        IEnumerable<Chord> existingChords,
        IEnumerable<Chord> explicitlyIgnoredChords)
    {
        var result = new CollisionResolution();
        var ignoreSet = explicitlyIgnoredChords.ToHashSet();

        // FIX: Materialize existing chords once!
        var existingChordsList = existingChords.ToList();

        foreach (var incoming in incomingChords)
        {
            var collisions = existingChordsList.Where(existing => // Query the List
                !ignoreSet.Contains(existing) &&
                existing.Position == incoming.Position);

            result.ChordsToDestroy.AddRange(collisions);
        }

        return result;
    }

    public CollisionResolution CalculateAnchorCollisions(
        IEnumerable<FretAnchor> incomingAnchors,
        IEnumerable<FretAnchor> existingAnchors,
        IEnumerable<FretAnchor> explicitlyIgnoredAnchors)
    {
        var result = new CollisionResolution();
        var ignoreSet = explicitlyIgnoredAnchors.ToHashSet();
        var existingList = existingAnchors.ToList();

        foreach (var incoming in incomingAnchors)
        {
            // Position ALONE, not position+fret: two anchors on one beat are a
            // duplicate whatever fret each names, and the arriving one wins the
            // same way an arriving note does.
            var collisions = existingList.Where(existing =>
                !ignoreSet.Contains(existing) &&
                !ReferenceEquals(existing, incoming) &&
                existing.Position == incoming.Position);

            result.AnchorsToDestroy.AddRange(collisions.Where(a => !result.AnchorsToDestroy.Contains(a)));
        }

        return result;
    }

    public CollisionResolution CalculateHandShapeOverride(
        IEnumerable<HandShape> incomingHandShapes,
        IEnumerable<HandShape> existingHandShapes,
        IEnumerable<HandShape> explicitlyIgnoredHandShapes)
    {
        var result = new CollisionResolution();
        var ignoredHsSet = explicitlyIgnoredHandShapes.ToHashSet();

        // FIX: Materialize all existing collections once!
        var existingHandShapesList = existingHandShapes.ToList();

        foreach (var hs in incomingHandShapes)
        {
            var hsCollisions = existingHandShapesList.Where(existing =>
                !ignoredHsSet.Contains(existing) &&
                existing.Position < hs.EndPosition &&
                existing.EndPosition > hs.Position);

            result.HandShapesToDestroy.AddRange(hsCollisions);
        }

        return result;
    }
}
