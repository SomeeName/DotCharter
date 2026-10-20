using JuceSharp.StemSeparation.Onnx;
using JuceSharp.StemSeparation.Runtime;

namespace DotCharter.Services.Audio.StemSeparation;

/// <summary>
///     One verified fetch, fully resolved: where the bytes come from, where they
///     land, and what they must hash and measure. Pure — the network is
///     <see cref="VerifiedDownload.EnsureFileAsync" />'s.
/// </summary>
/// <param name="Url">Absolute source URL. Validated by the downloader
///   (https, or http on loopback only).</param>
/// <param name="DestinationPath">Full path of the finished file.</param>
/// <param name="Sha256">Lower-case hex SHA-256, or null for an honestly
///   unpinned fetch (every user import, and the checkpoint importer's loose
///   files).</param>
/// <param name="ExpectedBytes">Exact size when known, else null.</param>
internal readonly record struct VerifiedFileRequest(
    Uri Url,
    string DestinationPath,
    string? Sha256,
    long? ExpectedBytes);

/// <summary>
///     The pure pieces that sit between this app's model catalogue and
///     JuceSharp's verified downloader: what to hand it, and how its progress
///     reads as ours.
///     <para>
///         <b>This app does not download a model itself.</b> A copy of its own would carry its own
///         <c>HttpClient</c>, its own <c>.partial</c>-plus-rename dance and its own
///         hash check, with no resume and a hash for two of eleven entries;
///         JuceSharp's <c>VerifiedDownloader</c> does all of it — HTTP Range
///         resume, mirror failover, a per-URL part file, hash and exact-size
///         checked BEFORE the rename, and an existing file that fails its hash
///         discarded and re-fetched rather than trusted. Pure, so
///         <c>StemModelDownloadIntegrityTests</c> pins both halves without a network.
///     </para>
///     <para>
///         <b>A row leaves through the catalogue it came IN from.</b> A row
///         derived from <see cref="OnnxModelCatalog" /> goes back through that
///         catalogue and a row derived from <c>DemucsGgmlModelCatalog</c> through
///         ITS catalogue, because a catalogue is the thing that owns its MIRROR
///         LIST and its <c>allowUnverified</c> policy — route a catalogue row
///         through the kind-less door instead and the unpinned refusal is not
///         declined, it is never asked for. (The ggml weights are the example:
///         the kind-less door is right only while <c>DemucsGgmlModelCatalog</c> does not
///         exist, and wrong the moment it does.) Only what NO catalogue knows about —
///         every user import, and the <c>.ckpt</c> / <c>.yaml</c> pair
///         <c>ImportCheckpointAsync</c> fetches — goes through the KIND-LESS
///         <see cref="VerifiedDownload.EnsureFileAsync" />, which is the same
///         downloader with no catalogue on top. Until JuceSharp 0.9 that door did
///         not exist, so those files were dressed as ONNX entries carrying "the
///         nearest kind label" — a field that was a lie so the call would compile.
///         <c>NearestKindLabel</c> and the two synthesizers are gone, and the test
///         suite asserts by reflection that they have not grown back.
///     </para>
/// </summary>
internal static class StemModelDownloadPlan
{
    /// <summary>
    ///     A catalogue descriptor as a kind-less verified fetch — that means a USER IMPORT, the only descriptor left that no
    ///     JuceSharp catalogue knows about. The ggml built-ins do not come through here:
    ///     <c>DemucsGgmlModelCatalog</c> takes them.
    /// </summary>
    /// <exception cref="ArgumentException">The file name is not bare, or the URL
    ///   is not absolute. <see cref="StemModelDescriptor.FileName" /> decides
    ///   WHERE bytes land and can come from a user import or a settings file, so
    ///   a directory component here would be an arbitrary-path writer. The URL is
    ///   re-checked by the downloader as well.</exception>
    public static VerifiedFileRequest FileRequestFor(StemModelDescriptor descriptor, string modelsDirectory)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.IsNullOrWhiteSpace(modelsDirectory))
            throw new ArgumentException("A models directory is required.", nameof(modelsDirectory));

        var name = descriptor.FileName;
        if (string.IsNullOrWhiteSpace(name) ||
            !string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Model '{descriptor.Id}' has FileName '{name}', which is not a bare file name.",
                nameof(descriptor));
        }

        if (!Uri.TryCreate(descriptor.DownloadUrl, UriKind.Absolute, out var url))
        {
            throw new ArgumentException(
                $"Model '{descriptor.Id}' has no absolute download URL ('{descriptor.DownloadUrl}').",
                nameof(descriptor));
        }

        return new VerifiedFileRequest(
            url,
            Path.Combine(modelsDirectory, name),
            string.IsNullOrWhiteSpace(descriptor.Sha256) ? null : descriptor.Sha256.Trim().ToLowerInvariant(),
            descriptor.ExactBytes > 0 ? descriptor.ExactBytes : null);
    }

    /// <summary>An arbitrary URL fetched to an arbitrary path — the checkpoint
    /// importer's <c>.ckpt</c> and its companion config. Unpinned by definition
    /// (the user typed the URL); the point of routing it here at all is the
    /// atomic rename, the resume and the length check, and that its bytes go
    /// straight into an embedded-Python code generator.</summary>
    /// <exception cref="ArgumentException">The URL is not absolute, or the
    ///   destination has no directory component.</exception>
    public static VerifiedFileRequest LooseFileRequest(string url, string destinationPath)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException($"Not a valid URL: '{url}'.", nameof(url));
        if (string.IsNullOrWhiteSpace(Path.GetDirectoryName(destinationPath)))
            throw new ArgumentException($"'{destinationPath}' has no directory component.", nameof(destinationPath));

        return new VerifiedFileRequest(uri, Path.GetFullPath(destinationPath), Sha256: null, ExpectedBytes: null);
    }

    /// <summary>
    ///     JuceSharp's progress as the UI's.
    ///     <para>
    ///         <see cref="DownloadProgress.TotalBytes" /> is NULLABLE (null = a
    ///         chunked response that declared no length) and its
    ///         <c>Fraction</c> is −1 for that case, while
    ///         <see cref="ModelDownloadProgress" /> spells "unknown" as a total
    ///         of 0 and derives a fraction of 0 from it. Mapping null to 0 keeps
    ///         the contract the download dialog has always consumed rather than
    ///         letting a −1 reach a progress bar.
    ///     </para>
    ///     <para>
    ///         <see cref="DownloadProgress.BytesReceived" /> already counts the
    ///         bytes a resumed <c>.part</c> file contributed, and
    ///         <c>TotalBytes</c> is the whole file, so a resumed download opens
    ///         part-way along instead of restarting at 0 %.
    ///     </para>
    /// </summary>
    public static ModelDownloadProgress Adapt(DownloadProgress progress) =>
        new(progress.BytesReceived, progress.TotalBytes is > 0 ? progress.TotalBytes.Value : 0);
}
