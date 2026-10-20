using DotCharter.Core.Domain.Primitives;

namespace DotCharter.Core.Domain.Guitar;

public class BendValue : IComparable<BendValue>
{
    public BendValue()
    {
    }

    public BendValue(FractionalPosition position, double step)
    {
        Position = position;
        Step = step;
    }

    public FractionalPosition Position { get; set; } = FractionalPosition.Zero;
    public double Step { get; set; } // 1.0 = half step, 2.0 = whole step, etc.

    public int CompareTo(BendValue? other)
    {
        if (ReferenceEquals(this, other)) return 0;
        if (other is null) return 1;
        return Position.CompareTo(other.Position);
    }

    public BendValue Clone() => new(Position, Step);
}
