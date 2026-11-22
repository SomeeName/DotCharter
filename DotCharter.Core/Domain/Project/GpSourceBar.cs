namespace DotCharter.Core.Domain.Project;

/// <summary>Guitar Pro provenance for one measure on the imported project grid.</summary>
public sealed record GpSourceBar
{
    public int StartBeat { get; init; }
    public int BeatCount { get; init; }
    public int BeatsInMeasure { get; init; }
    public int NoteDenominator { get; init; }
    public int WrittenBar { get; init; }
    public int Pass { get; init; } = 1;
    public int EndingMask { get; init; }
    public bool StartsBlock { get; init; }
    public bool EndsBlock { get; init; }
    public string Marker { get; init; } = "";
}
