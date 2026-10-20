using DotCharter.Core.Domain.Primitives;
using DotCharter.Core.Utilities;

namespace DotCharter.Core.Domain.Events;

public class FretAnchor : IComparable<FretAnchor>, ITimedElement
{
    public FretAnchor() => Width = 4; // Default span for a standard hand position

    public FretAnchor(FractionalPosition position, int fret, int width = 4)
    {
        Position = position;
        Fret = fret;
        Width = width;
    }

    public int Fret { get; set; } = 1;
    public FractionalPosition Position { get; set; } = FractionalPosition.Zero;
    public int Width { get; set; }

    public int CompareTo(FretAnchor? other)
    {
        if (ReferenceEquals(this, other)) return 0;
        if (other is null) return 1;
        return Position.CompareTo(other.Position);
    }

    public double GetTimeMs(IList<BeatMarker> beats) => Position.GetPositionMs(beats);

    public FretAnchor Clone() => new(Position, Fret, Width);

    /// <summary>
    ///     The <c>Clone</c>/<c>CopyFrom</c> pair every domain type carries — what
    ///     lets an anchor be the target of a <c>MutateElementsAction</c>, so an
    ///     anchor edit is undoable in place like every other element's.
    /// </summary>
    public void CopyFrom(FretAnchor other)
    {
        ArgumentNullException.ThrowIfNull(other);
        Position = other.Position;
        Fret = other.Fret;
        Width = other.Width;
    }
}
