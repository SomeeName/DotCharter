namespace DotCharter.Services.Editing.Highway;

/// <summary>How a time nudge of the selection ended.</summary>
public enum TimeMoveOutcome
{
    /// <summary>Nothing (no note, chord, hand shape or anchor) was selected.</summary>
    NothingSelected,

    /// <summary>Every selected item moved by the full amount.</summary>
    Moved,

    /// <summary>The move would have crossed the first beat, so the selection moved only as far as the first beat.</summary>
    Clamped,

    /// <summary>The earliest selected item is already on the first beat: refused.</summary>
    AtStart,

    /// <summary>A moved item would have landed on an existing, unselected item: refused.</summary>
    Collision,

    /// <summary>A moved hand shape's span would have overlapped an unselected hand shape: refused.</summary>
    HandShapeOverlap
}

/// <summary>What <see cref="IHighwayEditingService.MoveSelectedInTime" /> did.</summary>
/// <param name="Outcome">How the move ended.</param>
/// <param name="ItemCount">How many items moved (0 when refused).</param>
/// <param name="DeltaMs">How far the earliest item moved, in ms (negative = earlier).</param>
public readonly record struct TimeMoveResult(TimeMoveOutcome Outcome, int ItemCount, double DeltaMs)
{
    /// <summary>True when the chart changed.</summary>
    public bool Changed => Outcome is TimeMoveOutcome.Moved or TimeMoveOutcome.Clamped;
}
