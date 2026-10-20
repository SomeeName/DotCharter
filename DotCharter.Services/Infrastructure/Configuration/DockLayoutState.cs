namespace DotCharter.Services.Infrastructure.Configuration;

/// <summary>
/// Serializable snapshot of the UI dock layout, persisted inside
/// <see cref="AppConfig"/>. Deliberately lightweight — per-pane mode + float
/// bounds + active tab — instead of serializing the whole dock object graph:
/// it is versioned, trivially forward-compatible, and a stale or corrupt
/// snapshot degrades to the default layout instead of crashing inflation.
/// </summary>
public sealed class DockLayoutState
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public Dictionary<string, DockPaneState> Panes { get; set; } = [];
}

public sealed class DockPaneState
{
    /// <summary>"Docked", "Floating", "Pinned", or "Closed".</summary>
    public string Mode { get; set; } = "Docked";

    public double? FloatX { get; set; }
    public double? FloatY { get; set; }
    public double? FloatWidth { get; set; }
    public double? FloatHeight { get; set; }

    /// <summary>Whether this pane was the active tab in its dock.</summary>
    public bool IsActive { get; set; }
}
