namespace DotCharter.UI.ViewModels.Editor;

/// <summary>
///     The Sound tab's state matrix (04-inspector-redesign.md §3). Derived from
///     <c>ISelectionService</c> in THIS priority order — a hand shape wins over
///     the sounds inside it, because reshaping the bar is a different job from
///     editing one strum under it.
///     <para>
///         It exists so the tab's content is a function of the SELECTION rather
///         than of which tab header was last clicked. A note and a
///         chord as two tabs would mean two headers, two footers and three
///         fretboards, authoring the same thing.
///     </para>
/// </summary>
public enum SoundSelectionKind
{
    /// <summary>Nothing selected. Most of a real charting session — the
    /// playhead block, the health tiles and the chord library live here.</summary>
    Empty,

    /// <summary>Exactly one note.</summary>
    Note,

    /// <summary>Exactly one chord.</summary>
    Chord,

    /// <summary>One or more hand shapes and no sounds.</summary>
    HandShape,

    /// <summary>More than one sound, all of one kind.</summary>
    MultiSame,

    /// <summary>More than one sound, notes AND chords.</summary>
    MultiMixed,

    /// <summary>At least one hand shape AND at least one sound.</summary>
    ShapeAndSounds
}
