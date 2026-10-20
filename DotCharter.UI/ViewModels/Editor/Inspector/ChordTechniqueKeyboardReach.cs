using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
/// The inspector's answer to the keyboard: a technique key on a chord reaches what a click
///     on the same technique would — <see cref="ChordApplyScope.TechniqueTargets" /> on the lit rung,
///     the set the ladder counts and the preview marks — so "palm mute every use of this shape"
///     is one key and one undo, like it is one click.
///     <para>
///         The key keeps to the selection unless all three hold:
///     </para>
///     <list type="bullet">
///         <item>
///             the inspector's own edits are on the ladder (<c>ladderTargets</c> is the
///             inspector's <c>LadderTargets</c>: a chord in the selection, a rung past "selected");
///         </item>
///         <item>
///             a ladder is ON SCREEN (<see cref="ChordApplyPreview.IsHostShown" />): the rung is session
///             state, and a key that silently reached forty chords from a hidden picker would be an
///             edit broader than anything the charter can see;
///         </item>
///         <item>
///             the key acts on the selection the ladder was resolved for — never an implied target
///             (the service asks only for the user's own selection) or a stale one.
///         </item>
///     </list>
///     <para>
///         Pure apart from its delegates, so the default gate drives it with the real panel and the
///         real <see cref="HighwayEditingService" /> (<c>ChordTechniqueKeyboardApplyToTests</c>).
///     </para>
/// </summary>
internal sealed class ChordTechniqueKeyboardReach(
    Func<ChordTechniqueTargets?> ladderTargets,
    Func<bool> ladderOnScreen,
    Func<IReadOnlyCollection<Chord>> shownSelection,
    Action<IReadOnlyList<Chord>> report) : IChordTechniqueReach
{
    public IReadOnlyList<Chord>? ChordsFor(IReadOnlyList<Chord> selectedChords)
    {
        if (!ladderOnScreen() || !SameChords(selectedChords, shownSelection())) return null;
        return ladderTargets() is { IsSelection: false, Chords.Count: > 0 } targets ? targets.Chords : null;
    }

    public void Reached(IReadOnlyList<Chord> chords) => report(chords);

    private static bool SameChords(IReadOnlyList<Chord> keyed, IReadOnlyCollection<Chord> shown) =>
        keyed.Count == shown.Count &&
        new HashSet<Chord>(keyed, ReferenceEqualityComparer.Instance).SetEquals(shown);
}
