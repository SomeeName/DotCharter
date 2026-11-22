namespace DotCharter.Services.Infrastructure.Configuration;

/// <summary>
///     What the owner had on screen in ONE project: restored when that project
///     opens. Arrangements are named by <c>Name</c> plus their index (the
///     model has no id), so two arrangements sharing a name still resolve.
/// </summary>
public sealed class ProjectViewState
{
    public string ActiveArrangementName { get; set; } = string.Empty;
    public int ActiveArrangementIndex { get; set; } = -1;
    public List<ArrangementRef> VisibleGhosts { get; set; } = [];
    public List<ArrangementRef> StackedArrangements { get; set; } = [];
    public double PlayheadMs { get; set; }

    /// <summary>Last write, UTC ticks: the cap drops the least recently used entries.</summary>
    public long LastUsedTicks { get; set; }
}

public sealed class ArrangementRef
{
    public string Name { get; set; } = string.Empty;
    public int Index { get; set; } = -1;
}
