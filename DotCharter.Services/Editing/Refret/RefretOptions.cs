namespace DotCharter.Services.Editing.Refret;

/// <summary>
///     Cost-model weights and hard limits for the refret engine. All values are
///     dimensionless "effort units" — only their ratios matter. The DP minimizes
///
///     <code>
///         total = Σ intrinsic(candidate)  +  Σ transition(prev, next)
///
///         intrinsic (per sound):
///             + HighFretWeight  · max(0, fret − HighFretThreshold)   per fretted note
///             − OpenStringBonus                                      per open string
///             + InertiaWeight                                        per (string,fret) changed vs. the original
///             + ChordSpanWeight · max(0, frettedSpan − 2)            per chord shape
///             + internal movement of multi-note units (link chains / legato runs)
///
///         transition (consecutive sounds):
///             + MovementWeight · |Δ anchorFret| · timeFactor(Δt)
///             + StringCrossWeight · |Δ stringCenter|
///
///         timeFactor(Δt) = 1 + FastMovementBoost · ReferenceIntervalMs / max(Δt, MinIntervalMs)
///     </code>
///
///     so fast passages (small inter-onset Δt) penalize hand movement harder:
///     at Δt = 250 ms a fret of travel costs 2×, at 50 ms it costs 6×, at
///     1 s it costs 1.25×. Anchors are the lowest fretted fret of a sound;
///     all-open sounds are transparent (the hand is free to travel during them).
///
///     A future options dialog would expose: <see cref="OpenStringBonus" />,
///     <see cref="HighFretWeight" /> + threshold, <see cref="MaxChordSpan" />,
///     <see cref="InertiaWeight" /> (aggressiveness), and a "prefer low
///     positions" toggle. This cycle ships the documented defaults only.
/// </summary>
public sealed class RefretOptions
{
    public static readonly RefretOptions Default = new();

    /// <summary>Hard fret ceiling for any candidate (Rocksmith fretboard).</summary>
    public int MaxFret { get; init; } = 24;

    /// <summary>Max fret span (highest − lowest fretted fret) a chord shape may cover.</summary>
    public int MaxChordSpan { get; init; } = 4;

    /// <summary>
    ///     Candidate-shape beam per chord group. Shapes are ordered by intrinsic
    ///     cost (deterministic tie-breaks) and the list is cut here; the original
    ///     shape always survives the cut. 16 comfortably covers every playable
    ///     voicing of a ≤6-note chord under the span/finger constraints.
    /// </summary>
    public int ChordCandidateBeam { get; init; } = 16;

    /// <summary>Cost per fret of hand-position travel between consecutive sounds (time-weighted).</summary>
    public double MovementWeight { get; init; } = 1.0;

    /// <summary>Extra movement penalty multiplier for fast passages (see class doc).</summary>
    public double FastMovementBoost { get; init; } = 1.0;

    /// <summary>Inter-onset interval at which the fast-passage boost equals 1× movement cost.</summary>
    public double ReferenceIntervalMs { get; init; } = 250;

    /// <summary>Floor for Δt so simultaneous/near-simultaneous sounds don't divide by ~0.</summary>
    public double MinIntervalMs { get; init; } = 50;

    /// <summary>Cost per string of crossing between consecutive sounds.</summary>
    public double StringCrossWeight { get; init; } = 0.35;

    /// <summary>Cost per fret of fretted chord span beyond 2 (stretch).</summary>
    public double ChordSpanWeight { get; init; } = 0.8;

    /// <summary>Cost per fret above <see cref="HighFretThreshold" />, per fretted note.</summary>
    public double HighFretWeight { get; init; } = 0.12;

    /// <summary>Frets above this start accruing the high-fret penalty.</summary>
    public int HighFretThreshold { get; init; } = 12;

    /// <summary>Bonus (subtracted) per open string a candidate uses. Configurable open-string preference.</summary>
    public double OpenStringBonus { get; init; } = 0.4;

    /// <summary>
    ///     Cost per sound part whose (string, fret) differs from the original.
    ///     Keeps the optimizer from churning assignments for negligible wins and
    ///     makes ties resolve toward the authored chart.
    /// </summary>
    public double InertiaWeight { get; init; } = 0.1;
}
