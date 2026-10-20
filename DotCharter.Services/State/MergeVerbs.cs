namespace DotCharter.Services.State;

/// <summary>
///     The gesture names used with <see cref="IMergeableAction.MergeVerb" />.
///     <para>
///         They live in one place because the merge rule is "equal verbs may
///         fold together" — a typo does not fail, it silently stops a gesture
///         coalescing (back to one undo node per wheel tick), and two gestures
///         that accidentally share a spelling would fold into each other, which
///         is worse. Neither shows up as an error.
///     </para>
///     <para>
///         A verb names what the USER is doing, not which action class runs:
///         several verbs are served by <c>MutateChordsAction</c>, and it is
///         exactly those that must not merge with one another.
///     </para>
/// </summary>
public static class MergeVerbs
{
    /// <summary>Lengthening/shortening the selected sounds (wheel or held key).</summary>
    public const string Sustain = "sustain";

    /// <summary>Dragging or wheeling a hand shape's end.</summary>
    public const string HandShapeLength = "handshape-length";

    /// <summary>Nudging an FHP anchor's fret.</summary>
    public const string AnchorFret = "anchor-fret";

    /// <summary>Nudging an FHP anchor's width.</summary>
    public const string AnchorWidth = "anchor-width";

    /// <summary>
    ///     Nudging the selected sounds' FRET (±1 or ±12; held key).
    ///     <para>
    ///         <b>Separate from <see cref="String" /> on purpose</b> — rule (a):
    ///         a fret run and a string run are both a
    ///         <c>MutateNotesAction</c>/<c>MutateChordsAction</c> over the same
    ///         sounds, so one shared verb would let a string move fold into the
    ///         preceding fret run and make one Ctrl+Z revert both. Exactly the
    ///         reason <see cref="AnchorFret" /> and <see cref="AnchorWidth" />
    ///         are two verbs.
    ///     </para>
    ///     <para>
    ///         ±1 and ±12 DO share this verb: both are "I moved the fret", the
    ///         same way <see cref="Sustain" /> covers a wheel tick and a held
    ///         key. The pen's typed fret (<c>SetFret</c>/<c>TransposeShapeTo</c>)
    ///         deliberately carries NO verb — it owns its own run granularity,
    ///         and a second mechanism on top of that is the "two nudge verbs
    ///         over one concept" problem this file exists to prevent.
    ///     </para>
    /// </summary>
    public const string Fret = "fret";

    /// <summary>Moving the selected sounds one STRING up or down (held key).
    /// See <see cref="Fret" /> for why the two are not one verb.</summary>
    public const string String = "string";

    /// <summary>Stretching a lyric syllable's end (wheel).</summary>
    public const string LyricLength = "lyric-length";
}
