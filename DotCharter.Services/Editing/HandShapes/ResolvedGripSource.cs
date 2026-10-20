using Chord = DotCharter.Core.Domain.Guitar.Chord;

namespace DotCharter.Services.Editing.HandShapes;

/// <summary>
///     The editing paths' grip source: the template a chord already names when that
///     template describes the chord's frets, else <see cref="ChordTemplateResolver" />'s
///     find-or-create in the chord's OWN arpeggio flavour (a chord on an arpeggio
///     template gets an arpeggio bar; a chord on the <c>-1</c> sentinel is plain).
///     <para>
///         A chord that names a template describing its frets is held only by THAT
///         template: two same-fret templates with different fingering are different
///         grips, so their strums do not share a bar.
///     </para>
/// </summary>
public sealed class ResolvedGripSource(Arrangement arrangement) : IHandShapeGripSource
{
    private readonly Dictionary<int, ChordTemplate> _byId = ChordTemplateResolver.IndexById(arrangement.ChordTemplates);

    public ChordTemplate GripFor(Chord chord, IList<ChordTemplate> minted)
    {
        var frets = ChordTemplateResolver.FretsOf(chord);
        var own = Own(chord);
        if (own != null && own.HasFrets(frets)) return own;
        return ChordTemplateResolver.Resolve(arrangement, frets, ArpeggioFlavour.Of(own), null, minted);
    }

    public bool Holds(ChordTemplate grip, Chord chord)
    {
        var frets = ChordTemplateResolver.FretsOf(chord);
        if (!grip.HasFrets(frets)) return false;
        var own = Own(chord);
        if (own != null && own.HasFrets(frets)) return ReferenceEquals(own, grip);
        return ArpeggioFlavour.Of(own).Matches(grip);
    }

    /// <summary>The template the chord names — through the id index, so a dangling or
    /// sentinel id is null, never a list position.</summary>
    public ChordTemplate? Own(Chord chord) => _byId.GetValueOrDefault(chord.TemplateId);
}
