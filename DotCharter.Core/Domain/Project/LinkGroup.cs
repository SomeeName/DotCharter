using System.Collections.ObjectModel;
using DotCharter.Core.Domain.Primitives;

namespace DotCharter.Core.Domain.Project;

/// <summary>
///     A link group (linked measures): runs of whole measures, in one or several
///     arrangements, that are kept equal. <b>Every placement keeps its own real copy</b> of
///     the notes in its arrangement's hardest level; the group is only the rule that keeps
///     the copies equal (<c>HistoryService</c> copies an edit made inside one placement to
///     the others, in the same undo step). So nothing that reads <see cref="ChartLevel" />
///     needs to know about links, and an older build sees ordinary charted measures.
///     <para>
///         Saved as the root node <c>&lt;dcLinkGroups&gt;</c> with the content hash of the
///         copies under <see cref="Rule" />; a placement whose copy does not match on load
///         is unlinked (<c>DotCharter.Core.Linking.LinkGroupVerifier</c>).
///     </para>
/// </summary>
public class LinkGroup
{
    /// <summary>Stable identity (32 lowercase hex digits), minted when the group is created.</summary>
    public string Id { get; set; } = NewId();

    /// <summary>The form letter the user identifies the group by ("A", "B", …).</summary>
    public string Letter { get; set; } = "";

    /// <summary>A display name, usually from the section the first placement starts in ("Chorus").</summary>
    public string Name { get; set; } = "";

    /// <summary>
    ///     The comparison rule the copies are held equal under: a
    ///     <c>MeasureContentOptions.Header()</c> string (format version, role, spacing gap,
    ///     what is compared). Fixed when the group is created, so the save, the load check
    ///     and the history all compare by the SAME rule, and a stored hash from another rule
    ///     is recognised as such instead of reading as a divergence.
    /// </summary>
    public string Rule { get; set; } = "";

    /// <summary>The linked runs. A group with fewer than two placements is dissolved.</summary>
    public ObservableCollection<LinkPlacement> Placements { get; set; } = [];

    public static string NewId() => Guid.NewGuid().ToString("N");

    public LinkGroup Clone()
    {
        var clone = new LinkGroup();
        clone.CopyFrom(this);
        return clone;
    }

    /// <summary>Copies every field; placements are cloned (their arrangement stays the same reference).</summary>
    public void CopyFrom(LinkGroup other)
    {
        Id = other.Id;
        Letter = other.Letter;
        Name = other.Name;
        Rule = other.Rule;
        Placements = [.. other.Placements.Select(p => p.Clone())];
    }
}

/// <summary>
///     One linked run: whole measures <c>[Position, EndPosition)</c> of
///     <see cref="Arrangement" />'s hardest level. Both ends sit on barlines. The positions
///     are ABSOLUTE, so the tools that move the beat grid move them
///     (<c>ChartProjectPositions</c>); a beat inserted or removed inside the run changes its
///     beat span, which unlinks it.
/// </summary>
public class LinkPlacement
{
    /// <summary>
    ///     The arrangement, by REFERENCE in memory (an index goes stale when an arrangement is
    ///     deleted or reordered); saved as its index. A placement whose arrangement is not
    ///     in the project is dead and is dropped.
    /// </summary>
    public Arrangement? Arrangement { get; set; }

    /// <summary>The run's first barline.</summary>
    public FractionalPosition Position { get; set; } = FractionalPosition.Zero;

    /// <summary>The barline after the run's last measure (exclusive end).</summary>
    public FractionalPosition EndPosition { get; set; } = FractionalPosition.Zero;

    public LinkPlacement Clone()
    {
        var clone = new LinkPlacement();
        clone.CopyFrom(this);
        return clone;
    }

    public void CopyFrom(LinkPlacement other)
    {
        Arrangement = other.Arrangement;
        Position = other.Position;
        EndPosition = other.EndPosition;
    }
}
