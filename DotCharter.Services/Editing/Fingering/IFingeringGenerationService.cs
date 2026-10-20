namespace DotCharter.Services.Editing.Fingering;

/// <summary>
/// Generates and propagates chord-template fingerings, EOF style: shape-based
/// auto fingering for templates that lack one and DRY copying of fingerings to
/// other templates that share the same fret pattern.
/// </summary>
public interface IFingeringGenerationService
{
    /// <summary>
    /// All templates that have ≥1 fretted string (>0) but are missing a finger
    /// for at least one of those fretted strings.
    /// </summary>
    IReadOnlyList<ChordTemplate> FindTemplatesNeedingFingering(Arrangement arrangement);

    /// <summary>
    /// Returns a clone of <paramref name="template" /> with shape-recognised
    /// fingers applied, or null when the recogniser had nothing to suggest.
    /// </summary>
    ChordTemplate? SuggestFingeringFor(ChordTemplate template, int stringCount);

    /// <summary>
    /// <see cref="SuggestFingeringFor(ChordTemplate, int)" /> following
    /// <paramref name="arrangement" />'s own style for a power chord or a two-fret
    /// octave (<see cref="ArrangementFingeringStyle" />).
    /// </summary>
    ChordTemplate? SuggestFingeringFor(Arrangement arrangement, ChordTemplate template);

    /// <summary>
    /// Templates that share <paramref name="source" />'s fret pattern but are
    /// still missing a fingering. For propagating user-set fingerings DRY-style.
    /// </summary>
    IReadOnlyList<ChordTemplate> FindMatchingTemplatesNeedingFingering(
        Arrangement arrangement, ChordTemplate source);

    /// <summary>
    /// Builds a fret-only ChordTemplate from a chord's chord-notes, for
    /// giving untemplated chords a template so they can be picked up by the
    /// auto-fingering pass.
    /// </summary>
    ChordTemplate? BuildTemplateForChord(Chord chord);

    /// <summary>
    /// Builds a fret-only ChordTemplate from all notes/chords inside a time
    /// range. Mirrors EOF's "auto-build chord template from notes inside
    /// handshape" behaviour. Returns null when the range contains nothing
    /// fretted.
    /// </summary>
    ChordTemplate? BuildTemplateForRange(Arrangement arrangement,
        IEnumerable<GuitarNote> notes, IEnumerable<Chord> chords,
        FractionalPosition start, FractionalPosition end);

    /// <summary>
    /// Returns an existing non-arpeggio template in <paramref name="arrangement"/>
    /// whose fret pattern matches <paramref name="template"/>, or null if none
    /// exists. For reusing templates instead of bloating the list.
    /// </summary>
    ChordTemplate? FindEquivalentTemplate(Arrangement arrangement, ChordTemplate template);

    /// <summary>
    ///     The STRICTER sibling of <see cref="FindEquivalentTemplate" />: a
    ///     template that already IS <paramref name="template" /> — same arpeggio
    ///     flavour, same frets, same fingers, same name, same Nop and Fingerless
    ///     flags. Returns null when nothing matches, i.e. "you have to mint this one".
    ///     <para>
    ///         It exists because the frets-only key answers a different question.
    ///         Frets-only is right for a SHAPE change ("bind this chord to the
    ///         shape it now plays; that shape's authored name and fingering
    ///         win"), and it is the documented find-or-create key everywhere
    ///         else. But for a FINGERING, NAME or ARPEGGIO edit — where the
    ///         frets did not move — it resolves straight back onto the template
    ///         the edit came from and the edit is silently discarded. That was
    ///         the owner's "I have to click save fingering TWICE": click one
    ///         logged a <c>SwitchTemplateAction</c> back onto the identical-fret
    ///         template and dropped the new fingering on the floor.
    ///     </para>
    ///     <para>
    ///         Matching the whole authored state is also what keeps duplicates
    ///         down: re-fingering ten chords one at a time converges on ONE
    ///         minted template, because the second one finds the first.
    ///     </para>
    /// </summary>
    ChordTemplate? FindIdenticalTemplate(Arrangement arrangement, ChordTemplate template);

    /// <summary>
    /// Next free <see cref="ChordTemplate.Id"/> for the arrangement.
    /// </summary>
    int NextTemplateId(Arrangement arrangement);
}
