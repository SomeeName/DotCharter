using DotCharter.Services.Editing.Targets;

namespace DotCharter.Tests.Services.Editing.Targets;

/// <summary>
///     The ONE keyboard target ladder — selection → hand shape → pen — as a
///     pure function. Stage 5 builds the inspector's own target on this, so the
///     contract is pinned here rather than only through its call sites.
/// </summary>
public sealed class EditTargetResolverTests
{
    private static readonly FractionalPosition Beat2 = new(2);
    private static readonly FractionalPosition Beat4 = new(4);

    // === Rung 1 — the selection wins =========================================

    [Fact]
    public void ASelectedNoteWins()
    {
        var selected = MakeNote(Beat2, 0);
        var atPen = MakeNote(Beat4, 1);

        var target = Resolve(
            selectedNotes: [selected],
            levelNotes: [selected, atPen],
            penPosition: Beat4);

        Assert.Equal(EditTargetSource.Selection, target.Source);
        Assert.Same(selected, Assert.Single(target.Notes));
        Assert.False(target.IsImplied);
    }

    [Fact]
    public void ASelectedChordWinsOverAHandShapeAndThePen()
    {
        var chord = MakeChord(Beat2);
        var shape = MakeShape(0, 8);
        var atPen = MakeNote(Beat4, 1);

        var target = Resolve(
            selectedChords: [chord],
            selectedHandShapes: [shape],
            levelNotes: [atPen],
            levelChords: [chord],
            penPosition: Beat4);

        Assert.Equal(EditTargetSource.Selection, target.Source);
        Assert.Same(chord, Assert.Single(target.Chords));
        Assert.Empty(target.Notes);
        Assert.Empty(target.HandShapes);
    }

    [Fact]
    public void TheSelectionIsSnapshotted()
    {
        // Load-bearing: the actions capture these instances and the spacing
        // enforcer running in their post-execute can mutate the live selection
        // collections mid-flight.
        var live = new List<GuitarNote> { MakeNote(Beat2, 0) };

        var target = Resolve(selectedNotes: live, levelNotes: live);
        live.Clear();

        Assert.Single(target.Notes);
    }

    // === Rung 2 — a selected hand shape means the run under the bar ==========

    [Fact]
    public void ASelectedHandShapeTargetsTheSoundsItSpans()
    {
        var inside = MakeNote(Beat2, 0);
        var outside = MakeNote(new FractionalPosition(9), 1);
        var chordInside = MakeChord(new FractionalPosition(3));

        var target = Resolve(
            selectedHandShapes: [MakeShape(0, 8)],
            levelNotes: [inside, outside],
            levelChords: [chordInside]);

        Assert.Equal(EditTargetSource.HandShape, target.Source);
        Assert.Same(inside, Assert.Single(target.Notes));
        Assert.Same(chordInside, Assert.Single(target.Chords));
        Assert.Single(target.HandShapes);
        Assert.True(target.IsImplied);
    }

    [Fact]
    public void AHandShapesEndIsExclusive()
    {
        // HandShape.EndPosition is EXCLUSIVE — shapes abut, so the sound sitting
        // exactly on one shape's end belongs to the NEXT one. Two refret sites
        // that open-coded `<=` produced real bugs.
        var onTheEnd = MakeNote(Beat4, 0);

        var target = Resolve(
            selectedHandShapes: [MakeShape(0, 4)],
            levelNotes: [onTheEnd]);

        Assert.Equal(EditTargetSource.HandShape, target.Source);
        Assert.Empty(target.Notes);
    }

    [Fact]
    public void AHandShapeOverEmptyBarsClaimsTheResolutionAnyway()
    {
        // A rung claims as soon as its own selector is non-empty, even with no
        // sounds to show for it: falling through would silently overrule the
        // user's own selection with whatever sits under the playhead.
        // rscharter's HAND_SHAPE branch returns unconditionally too.
        var atPen = MakeNote(Beat4, 1);

        var target = Resolve(
            selectedHandShapes: [MakeShape(0, 1)],
            levelNotes: [atPen],
            penPosition: Beat4);

        Assert.Equal(EditTargetSource.HandShape, target.Source);
        Assert.True(target.HasNoSounds);
        Assert.False(target.IsNone);
    }

    // === Rung 3 — the pen (EOF's implied selection) ==========================

    [Fact]
    public void WithNothingSelectedTheSoundUnderThePenIsTheTarget()
    {
        var atPen = MakeNote(Beat4, 1);
        var elsewhere = MakeNote(Beat2, 1);

        var target = Resolve(levelNotes: [elsewhere, atPen], penPosition: Beat4);

        Assert.Equal(EditTargetSource.Pen, target.Source);
        Assert.Same(atPen, Assert.Single(target.Notes));
        Assert.True(target.IsImplied);
    }

    [Fact]
    public void EveryNoteAtThePenPositionIsTargeted()
    {
        var a = MakeNote(Beat4, 1);
        var b = MakeNote(Beat4, 3);

        var target = Resolve(levelNotes: [a, b], penPosition: Beat4);

        Assert.Equal(2, target.Notes.Count);
    }

    [Fact]
    public void AChordUnderThePenBeatsLooseNotesAtTheSameInstant()
    {
        var chord = MakeChord(Beat4);
        var note = MakeNote(Beat4, 5);

        var target = Resolve(levelNotes: [note], levelChords: [chord], penPosition: Beat4);

        Assert.Equal(EditTargetSource.Pen, target.Source);
        Assert.Same(chord, Assert.Single(target.Chords));
        Assert.Empty(target.Notes);
    }

    [Fact]
    public void NoPenPositionSimplyRemovesTheBottomRung()
    {
        var target = Resolve(levelNotes: [MakeNote(Beat4, 1)], penPosition: null);

        Assert.Equal(EditTargetSource.None, target.Source);
    }

    // === Nothing resolves ====================================================

    [Fact]
    public void AnEmptyChartResolvesToNone()
    {
        var target = Resolve(penPosition: Beat2);

        Assert.Equal(EditTargetSource.None, target.Source);
        Assert.True(target.IsNone);
        Assert.True(target.HasNoSounds);
        Assert.False(target.IsImplied);
        Assert.Null(target.Anchor);
    }

    [Fact]
    public void APenOverEmptySpaceResolvesToNone()
    {
        var target = Resolve(levelNotes: [MakeNote(Beat2, 0)], penPosition: Beat4);

        Assert.Equal(EditTargetSource.None, target.Source);
    }

    // === The run anchor ======================================================

    [Fact]
    public void TheAnchorPrefersAChordThenANoteThenAShape()
    {
        var chord = MakeChord(Beat2);
        var note = MakeNote(Beat2, 0);

        Assert.Same(chord, Resolve(selectedChords: [chord], selectedNotes: [note]).Anchor);
        Assert.Same(note, Resolve(selectedNotes: [note]).Anchor);

        var shape = MakeShape(0, 1);
        Assert.Same(shape, Resolve(selectedHandShapes: [shape]).Anchor);
    }

    // === Helpers =============================================================

    private static EditTarget Resolve(
        IReadOnlyList<GuitarNote>? selectedNotes = null,
        IReadOnlyList<Chord>? selectedChords = null,
        IReadOnlyList<HandShape>? selectedHandShapes = null,
        IReadOnlyList<GuitarNote>? levelNotes = null,
        IReadOnlyList<Chord>? levelChords = null,
        FractionalPosition? penPosition = null)
    {
        return EditTargetResolver.Resolve(
            selectedNotes ?? [], selectedChords ?? [], selectedHandShapes ?? [],
            levelNotes ?? [], levelChords ?? [], penPosition);
    }

    private static GuitarNote MakeNote(FractionalPosition position, int stringIndex)
        => new() { Position = position, StringIndex = stringIndex };

    private static Chord MakeChord(FractionalPosition position)
        => new() { Position = position, TemplateId = -1 };

    private static HandShape MakeShape(int startBeat, int endBeat)
        => new()
        {
            Position = new FractionalPosition(startBeat),
            EndPosition = new FractionalPosition(endBeat)
        };
}
