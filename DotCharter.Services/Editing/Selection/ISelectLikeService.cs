using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;

namespace DotCharter.Services.Editing.Selection;

/// <summary>Chords and hand shapes elsewhere in a track that play the same
/// shape as a seed selection.</summary>
/// <param name="Chords">Matching chords, excluding the seeds themselves.</param>
/// <param name="HandShapes">Matching hand shapes, excluding the seeds themselves.</param>
public readonly record struct LikeShapeMatches(
    IReadOnlyList<Chord> Chords,
    IReadOnlyList<HandShape> HandShapes);

public interface ISelectLikeService
{
    IReadOnlyList<GuitarNote> FindMatches(
        IReadOnlyList<GuitarNote> templateNotes,
        IEnumerable<GuitarNote> source,
        bool allowTransposition = true);

    /// <summary>
    ///     Every chord AND hand shape whose SHAPE matches one of the seeds —
    ///     same string→fret map, no conflicting finger — regardless of which
    ///     <see cref="ChordTemplate" /> object it points at and regardless of
    ///     technique. See <see cref="ChordShapeSignature" /> for why identity is
    ///     not the key and why an unauthored finger is a hole rather than a
    ///     value.
    ///     <para>
    ///         Chords and hand shapes are matched against the SAME seed set:
    ///         a hand shape's template is a chord shape, and the bar has to
    ///         follow its chords or the 3D fingering detaches from the note
    ///         heads underneath it.
    ///     </para>
    /// </summary>
    LikeShapeMatches FindLikeShapes(
        Arrangement? arrangement,
        IReadOnlyList<Chord> seedChords,
        IReadOnlyList<HandShape> seedHandShapes,
        IEnumerable<Chord> chords,
        IEnumerable<HandShape> handShapes);

    /// <summary>
    ///     Every fret-hand position in the level where the hand is doing the
    ///     same JOB as a seed anchor: same fret, same width, and the same run of
    ///     sounds underneath it.
    ///     <para>
    ///         <b>The fret alone is not the key, on purpose.</b> Selecting
    ///         "every anchor on fret 7" is meaningless — there are many reasons
    ///         to park the hand there, and re-fretting all of them because one
    ///         was wrong corrupts passages the user never looked at. See
    ///         <see cref="AnchorSpanSignature" /> for the span rule.
    ///     </para>
    /// </summary>
    /// <param name="level">The level the anchors live in; supplies the spans.</param>
    /// <param name="seedAnchors">The already-selected anchors.</param>
    IReadOnlyList<FretAnchor> FindLikeAnchors(ChartLevel? level, IReadOnlyList<FretAnchor> seedAnchors);
}
