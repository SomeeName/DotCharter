namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
///     One immutable read of a paused run. Empty means "there is no judged run
///     behind this pause" — the header row hides rather than printing zeroes.
/// </summary>
public sealed record PausedRunFacts
{
    public static readonly PausedRunFacts Empty = new();

    public string AccuracyLabel { get; init; } = "";
    public string StreakLabel { get; init; } = "";
    public string ScoreLabel { get; init; } = "";

    /// <summary>The section marker the playhead is inside, or empty before the
    /// first one.</summary>
    public string SectionLabel { get; init; } = "";

    public bool HasSection => SectionLabel.Length > 0;

    public string ElapsedLabel { get; init; } = "";
    public string TotalLabel { get; init; } = "";

    /// <summary>False when nothing judged this run — see the type remarks.</summary>
    public bool HasRun { get; init; }
}
