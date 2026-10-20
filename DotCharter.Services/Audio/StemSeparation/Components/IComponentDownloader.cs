namespace DotCharter.Services.Audio.StemSeparation.Components;

/// <summary>
/// Seam for fetching a missing component. In this pass network download is NOT
/// implemented — <see cref="DisabledComponentDownloader"/> is registered so the
/// UI can bind a disabled "Download" affordance and explain why. A future pass
/// can supply a real implementation (delegating to <c>StemModelManager</c> for
/// models) without any UI change.
/// </summary>
public interface IComponentDownloader
{
    /// <summary>Whether in-app download is available for the given component.</summary>
    bool CanDownload(ComponentDefinition definition);

    /// <summary>Human-readable reason shown next to a disabled Download button.</summary>
    string UnavailableReason { get; }

    /// <summary>Download the component. Throws when unavailable.</summary>
    Task DownloadAsync(ComponentDefinition definition, IProgress<double>? progress = null, CancellationToken ct = default);
}

/// <summary>
/// The intentionally-inert downloader used this pass. Everything is a no-op /
/// throw so the manager stays inventory-and-verify only.
/// </summary>
public sealed class DisabledComponentDownloader : IComponentDownloader
{
    public static DisabledComponentDownloader Instance { get; } = new();

    public bool CanDownload(ComponentDefinition definition) => false;

    public string UnavailableReason =>
        "In-app download isn't available yet. Models can be fetched from " +
        "Settings → Stem Models; external tools (ffmpeg, yt-dlp) must be installed manually.";

    public Task DownloadAsync(ComponentDefinition definition, IProgress<double>? progress = null, CancellationToken ct = default)
        => throw new NotSupportedException(UnavailableReason);
}
