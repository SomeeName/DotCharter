using Chord = DotCharter.Core.Domain.Guitar.Chord;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;
using HandShape = DotCharter.Core.Domain.Guitar.HandShape;

namespace DotCharter.Services.Editing.Targets;

/// <summary>Which rung of the resolution ladder produced an
/// <see cref="EditTarget" />. See <see cref="EditTargetResolver" /> for the
/// order and why it is that order.</summary>
public enum EditTargetSource
{
    /// <summary>Nothing resolved — the verb must be a strict no-op and record
    /// no history node.</summary>
    None,

    /// <summary>Notes and/or chords are selected; the verb edits exactly
    /// those.</summary>
    Selection,

    /// <summary>No sound is selected but a HAND SHAPE is; the verb edits the
    /// sounds the shape spans.</summary>
    HandShape,

    /// <summary>Nothing is selected; the verb edits the sound under the pen
    /// (the playhead). EOF calls this the "implied" selection.</summary>
    Pen
}

/// <summary>
///     What a keyboard editing verb acts on, resolved once by
///     <see cref="EditTargetResolver" /> instead of every verb reaching into
///     the selection on its own.
///     <para>
///         <see cref="Notes" /> and <see cref="Chords" /> are always the SOUNDS
///         the verb edits, whichever rung produced them.
///         <see cref="HandShapes" /> is non-empty only on the
///         <see cref="EditTargetSource.HandShape" /> rung and carries the shapes
///         that produced the sounds — a facet Stage 5's inspector needs and a
///         sound-mutation verb ignores.
///     </para>
/// </summary>
public sealed record EditTarget(
    EditTargetSource Source,
    IReadOnlyList<GuitarNote> Notes,
    IReadOnlyList<Chord> Chords,
    IReadOnlyList<HandShape> HandShapes)
{
    public static readonly EditTarget None =
        new(EditTargetSource.None, [], [], []);

    /// <summary>True when the ladder found nothing at all. Distinct from
    /// "resolved to no SOUNDS": a selected hand shape spanning empty bars
    /// resolves (<see cref="EditTargetSource.HandShape" />) with no sounds, and
    /// must NOT fall through to the pen.</summary>
    public bool IsNone => Source == EditTargetSource.None;

    /// <summary>No sound to edit. A sound verb stops here.</summary>
    public bool HasNoSounds => Notes.Count == 0 && Chords.Count == 0;

    /// <summary>True when the user did not pick these sounds — the caller
    /// should PROMOTE them to a real selection so the edit is visible.</summary>
    public bool IsImplied => Source is EditTargetSource.HandShape or EditTargetSource.Pen;

    /// <summary>
    ///     A stable reference-identity handle on "what this target is", for
    ///     callers that must notice the target CHANGING between two keystrokes
    ///     (the pen's typing run: seeking between two digits must not land the
    ///     extended value on a different sound). Reference identity on purpose —
    ///     domain objects are mutated in place, so value equality would say
    ///     "same target" across an edit.
    /// </summary>
    public object? Anchor =>
        Chords.Count > 0 ? Chords[0] :
        Notes.Count > 0 ? Notes[0] :
        HandShapes.Count > 0 ? HandShapes[0] : null;
}
