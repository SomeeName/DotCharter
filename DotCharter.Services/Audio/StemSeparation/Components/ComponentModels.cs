namespace DotCharter.Services.Audio.StemSeparation.Components;

/// <summary>Broad grouping that organises the component manager UI.</summary>
public enum ComponentCategory
{
    /// <summary>An htdemucs / ONNX stem-separation model weight file.</summary>
    StemModel,
    /// <summary>An external command-line media tool (ffmpeg, yt-dlp, …).</summary>
    MediaTool,
}

/// <summary>Installed / active status of a single component.</summary>
public enum ComponentState
{
    /// <summary>No matching file found in any search location.</summary>
    Missing,
    /// <summary>A matching file is present but not the selected/active one.</summary>
    Installed,
    /// <summary>Present AND selected as the active component (models only).</summary>
    Active,
}

/// <summary>
/// A pure, immutable description of one component the inventory knows how to
/// look for. Built by <see cref="ComponentCatalog"/> from the stem-model
/// catalog plus the known external tools; consumed by <see cref="ComponentInventory"/>.
/// </summary>
public sealed record ComponentDefinition
{
    /// <summary>Stable identifier (matches the stem-model id for models).</summary>
    public required string Id { get; init; }

    /// <summary>User-facing label.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Long-form description for the manager UI.</summary>
    public string Description { get; init; } = string.Empty;

    public ComponentCategory Category { get; init; } = ComponentCategory.StemModel;

    /// <summary>Candidate file names, tried in order (e.g. ffmpeg.exe then ffmpeg).</summary>
    public required IReadOnlyList<string> FileNames { get; init; }

    /// <summary>Directories searched in order for <see cref="FileNames"/>.</summary>
    public IReadOnlyList<string> SearchDirectories { get; init; } = Array.Empty<string>();

    /// <summary>Also search each directory on the process PATH (tools only).</summary>
    public bool SearchPath { get; init; }

    /// <summary>
    /// True when this component is the currently-selected active one (models
    /// only). Combined with "installed" to yield <see cref="ComponentState.Active"/>.
    /// </summary>
    public bool IsActiveSelection { get; init; }

    /// <summary>Approximate expected size in bytes (0 = unknown). Advisory for verify.</summary>
    public long ExpectedBytes { get; init; }

    /// <summary>
    /// Whether this component can, in principle, be downloaded in-app. Network
    /// download is not implemented in this pass — this only gates the (disabled)
    /// Download affordance in the UI.
    /// </summary>
    public bool Downloadable { get; init; }

    /// <summary>Optional guidance shown when the component is missing.</summary>
    public string? Note { get; init; }
}

/// <summary>Scan result for one <see cref="ComponentDefinition"/>.</summary>
public sealed record ComponentStatus
{
    public required ComponentDefinition Definition { get; init; }
    public ComponentState State { get; init; }

    /// <summary>Absolute path of the resolved file, or null when missing.</summary>
    public string? ResolvedPath { get; init; }

    /// <summary>Size on disk in bytes, or 0 when missing.</summary>
    public long SizeBytes { get; init; }

    public bool IsInstalled => State != ComponentState.Missing;
    public bool IsActive => State == ComponentState.Active;
}

/// <summary>
/// Result of verifying a component file: presence, size (vs. approximate
/// expected) and — when requested — a content hash.
/// </summary>
public sealed record ComponentVerification
{
    public required string ComponentId { get; init; }
    public bool Exists { get; init; }
    public string? Path { get; init; }
    public long SizeBytes { get; init; }
    public long ExpectedBytes { get; init; }

    /// <summary>
    /// True when the size is plausible: no expectation recorded, or the actual
    /// size is within ±25% of the approximate expected size. (Catalog sizes are
    /// advisory, so an exact match is never required.)
    /// </summary>
    public bool SizeLooksRight { get; init; }

    /// <summary>Lowercase hex SHA-256 when hashing was requested and succeeded.</summary>
    public string? Sha256 { get; init; }

    /// <summary>Overall verdict: the file exists, is non-empty and the size looks right.</summary>
    public bool Ok => Exists && SizeBytes > 0 && SizeLooksRight;
}
