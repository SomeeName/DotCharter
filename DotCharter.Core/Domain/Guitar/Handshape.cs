using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Primitives;
using DotCharter.Core.Utilities;

namespace DotCharter.Core.Domain.Guitar;

public class HandShape : ITimedElement
{
    public FractionalPosition EndPosition { get; set; } = FractionalPosition.Zero;
    public string Name { get; set; } = "Chord";
    public FractionalPosition Position { get; set; } = FractionalPosition.Zero;
    public int TemplateId { get; set; } = -1;

    public double GetTimeMs(IList<BeatMarker> beats) => Position.GetPositionMs(beats);

    /// <summary>
    ///     True when a sound at <paramref name="position" /> is played INSIDE this
    ///     hand shape. Start is inclusive, <see cref="EndPosition" /> is EXCLUSIVE.
    ///     <para>
    ///         Exclusive because shapes ABUT: the per-chord shapes every tab importer
    ///         emits park one shape's end exactly on the next one's start, so a
    ///         half-open span is the only reading under which each chord belongs to
    ///         exactly one shape. <c>FretAnchorGenerationService.FindActiveHandShape</c>
    ///         has always used it — a closed span locks a new chord to the previous
    ///         chord's root and skips its anchor. The two refret sites
    ///         (<c>RefretPlanner.ApplyHandShapeFixing</c>,
    ///         <c>RefretService.ApplyChordGroup</c>) open-coded <c>&lt;=</c> instead
    ///         and disagreed with it: the planner over-pinned a movable run because
    ///         the NEXT shape's opening chord counted as inside this one, and the
    ///         service repointed the PREVIOUS shape's <c>TemplateId</c> from a chord
    ///         belonging to the next.
    ///     </para>
    /// </summary>
    public bool Covers(FractionalPosition position) => position.IsWithin(Position, EndPosition);

    public HandShape Clone()
    {
        var clone = new HandShape();
        clone.CopyFrom(this);
        return clone;
    }

    public void CopyFrom(HandShape other)
    {
        Position = other.Position;
        EndPosition = other.EndPosition;
        TemplateId = other.TemplateId;
        Name = other.Name;
    }
}
