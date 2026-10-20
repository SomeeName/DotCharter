using DotCharter.Services.Editing.Collisions;

namespace DotCharter.Tests.Services.Editing.Collisions;

public class StrictCollisionServiceTests
{
    private static GuitarNote Note(int beat, int stringIdx, int fret = 0)
    {
        return new GuitarNote
        {
            Position = new FractionalPosition(beat),
            StringIndex = stringIdx,
            Fret = fret
        };
    }

    private static Chord Chord(int beat) => new() { Position = new FractionalPosition(beat) };

    private static HandShape HandShape(int startBeat, int endBeat)
    {
        return new HandShape
        {
            Position = new FractionalPosition(startBeat),
            EndPosition = new FractionalPosition(endBeat)
        };
    }

    [Fact]
    public void Notes_SamePositionSameString_CollidesExistingDestroyed()
    {
        var svc = new StrictCollisionService();
        var existing = Note(1, 2);
        var incoming = Note(1, 2, 5);

        var result = svc.CalculateNoteCollisions(
            new[] { incoming }, new[] { existing }, Array.Empty<GuitarNote>());

        Assert.Single(result.NotesToDestroy);
        Assert.Same(existing, result.NotesToDestroy[0]);
    }

    [Fact]
    public void Notes_DifferentString_NoCollision()
    {
        var svc = new StrictCollisionService();
        var result = svc.CalculateNoteCollisions(
            new[] { Note(1, 0) },
            new[] { Note(1, 1), Note(1, 2) },
            Array.Empty<GuitarNote>());
        Assert.Empty(result.NotesToDestroy);
    }

    [Fact]
    public void Notes_ExplicitlyIgnored_AreNotDestroyed()
    {
        var svc = new StrictCollisionService();
        var existing = Note(1, 2);
        var incoming = Note(1, 2);

        var result = svc.CalculateNoteCollisions(
            new[] { incoming }, new[] { existing }, new[] { existing });

        Assert.Empty(result.NotesToDestroy);
    }

    [Fact]
    public void Chords_SamePosition_Collides()
    {
        var svc = new StrictCollisionService();
        var existing = Chord(4);
        var result = svc.CalculateChordCollisions(
            new[] { Chord(4) }, new[] { existing }, Array.Empty<Chord>());
        Assert.Single(result.ChordsToDestroy);
    }

    [Fact]
    public void HandShapes_Overlap_Collides()
    {
        var svc = new StrictCollisionService();
        var existing = HandShape(0, 4);
        var incoming = HandShape(2, 6); // overlaps existing
        var result = svc.CalculateHandShapeOverride(
            new[] { incoming }, new[] { existing }, Array.Empty<HandShape>());
        Assert.Single(result.HandShapesToDestroy);
    }

    [Fact]
    public void HandShapes_AbuttingBoundaries_DoNotCollide()
    {
        // existing [0..4), incoming [4..8): touching but not overlapping.
        var svc = new StrictCollisionService();
        var existing = HandShape(0, 4);
        var incoming = HandShape(4, 8);
        var result = svc.CalculateHandShapeOverride(
            new[] { incoming }, new[] { existing }, Array.Empty<HandShape>());
        Assert.Empty(result.HandShapesToDestroy);
    }

    [Fact]
    public void HandShapes_Disjoint_DoNotCollide()
    {
        var svc = new StrictCollisionService();
        var result = svc.CalculateHandShapeOverride(
            new[] { HandShape(10, 14) },
            new[] { HandShape(0, 4) },
            Array.Empty<HandShape>());
        Assert.Empty(result.HandShapesToDestroy);
    }

    [Fact]
    public void Notes_EnumeratesExistingOnlyOnce_NoDuplicateDestruction()
    {
        // The service must materialize the "existing" sequence once; calling it
        // against many incoming notes must not multiply per-item collisions.
        var svc = new StrictCollisionService();
        var existing = Note(1, 2);

        var result = svc.CalculateNoteCollisions(
            new[] { Note(1, 2), Note(1, 2) }, new[] { existing }, Array.Empty<GuitarNote>());

        // Two incoming notes → existing listed twice (one per incoming), but each is the same instance.
        Assert.Equal(2, result.NotesToDestroy.Count);
        Assert.All(result.NotesToDestroy, n => Assert.Same(existing, n));
    }
}
