using DotCharter.Services.State;

namespace DotCharter.Services.Editing.ChordShapes;

/// <summary>
///     Blank-name twins: the unnamed copy of a named grip that "Name only the first chord of a hand shape"
///     (<see cref="RepeatedChordNames" />) points a repeated chord at, and the named grip seen from that copy.
///     <para>
///         Two templates are TWINS when they are different objects, exactly one of them has an empty
///         <see cref="ChordTemplate.Name" />, and they are otherwise the same grip: same frets, fingers and
///         ghost mask (<see cref="ChordTemplate.HasSameShapeAs" />), and the same arpeggio, Nop and
///         Fingerless flags. Only the name (and the <see cref="ChordTemplate.Unnamed" /> flag that keeps it
///         empty) differs. The test is taken BEFORE an edit, so a pair that a charter already moved apart by
///         hand is not a pair and stays apart.
///     </para>
///     <para>
///         An in-place fret or fingering edit to one template of a pair is copied onto the other in the same
///         undo step (<see cref="Follow" />), so the repeat never shows a different grip than the chord it
///         repeats.
///     </para>
/// </summary>
public static class ChordTemplateTwins
{
    /// <summary>True when <paramref name="a" /> and <paramref name="b" /> are a named grip and its blank-name twin.</summary>
    public static bool AreTwins(ChordTemplate a, ChordTemplate b) =>
        !ReferenceEquals(a, b)
        && string.IsNullOrEmpty(a.Name) != string.IsNullOrEmpty(b.Name)
        && a.HasSameShapeAs(b)
        && ArpeggioFlavour.Of(a).Matches(b)
        && a.NopInRs == b.NopInRs
        && a.Fingerless == b.Fingerless;

    /// <summary>Every twin of <paramref name="template" /> in <paramref name="arrangement" />, in list order.</summary>
    public static List<ChordTemplate> Of(Arrangement arrangement, ChordTemplate template) =>
        arrangement.ChordTemplates.Where(other => AreTwins(template, other)).ToList();

    /// <summary>
    ///     The actions that carry an in-place edit of <paramref name="template" /> to <paramref name="newState" />
    ///     onto each of its twins: one <see cref="EditTemplateGlobalAction" /> per twin, which keeps the twin's
    ///     own id, name and <see cref="ChordTemplate.Unnamed" /> flag and re-projects every chord bound to it,
    ///     on every level. Empty when the edit leaves the frets and fingers as they were. A twin in
    ///     <paramref name="covered" /> is left out: the edit already writes it.
    /// </summary>
    public static List<IChartAction> Follow(Arrangement arrangement, ChordTemplate template, ChordTemplate newState,
        IChartEditorSurface editor, IReadOnlyCollection<ChordTemplate> covered)
    {
        var actions = new List<IChartAction>();
        if (template.HasSameShapeAs(newState) && template.Fingerless == newState.Fingerless) return actions;

        foreach (var twin in Of(arrangement, template).Where(t => !covered.Contains(t, ReferenceEqualityComparer.Instance)))
        {
            var twinState = newState.Clone();
            twinState.Id = twin.Id;
            twinState.Name = twin.Name;
            twinState.Unnamed = twin.Unnamed;
            var chords = arrangement.Levels.SelectMany(level => level.Chords).Where(c => c.TemplateId == twin.Id).ToList();
            var projected = chords.Select(chord =>
            {
                var clone = chord.Clone();
                clone.ChordNotes = ChordNoteProjection.OnTo(clone, twinState);
                return clone;
            }).ToList();
            actions.Add(new EditTemplateGlobalAction(twin, twin.Clone(), twinState, chords, projected, editor));
        }

        return actions;
    }
}
