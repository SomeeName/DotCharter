using DotCharter.Localization;
using System.Security.Cryptography;

namespace DotCharter.Services.Audio.Lyrics;

/// <summary>A downloadable lyric-alignment model.</summary>
/// <param name="Id">Folder name under the models root (<see cref="LyricModelCatalog" />).</param>
/// <param name="AssetName">Release asset to fetch.</param>
/// <param name="Sha256">Expected SHA-256 of the archive, hex (case-insensitive); required - an empty value refuses the install.</param>
/// <param name="ApproximateBytes">For the progress message — these are hundreds of megabytes.</param>
public sealed record LyricModelDownload(string Id, string AssetName, string Sha256, long ApproximateBytes);

/// <summary>
///     Fetches a lyric-alignment model ONCE and never touches it again.
///     <para>
///         <b>Why the models are not in the release zip.</b> They are 340 MB
///         (multilingual) and 303 MB (English) and they do not change between
///         releases — bundling them would make every update re-download 640 MB of
///         bytes the machine already has. So they are their own release assets,
///         fetched on demand into the models root and then left alone.
///     </para>
///     <para>
///         <b>Never overwrite.</b> An existing model is authoritative, whatever its
///         provenance: it may be hand-installed, it may be a newer export, it may be
///         mid-use by an alignment on another thread. So the presence check is the
///         whole gate — this only ever writes into a folder that has no usable model
///         in it. That also makes the operation naturally idempotent and safe to
///         call on every launch.
///     </para>
///     <para>
///         Extraction goes through <see cref="BoundedZipExtractor" /> like every
///         other archive from outside, and lands in a sibling temp folder that is
///         MOVED into place only once it is complete — so a download interrupted
///         half way cannot leave a half-model that the presence check would then
///         accept forever.
///     </para>
/// </summary>
public sealed class LyricModelInstaller(
    Func<string, CancellationToken, Task<Stream>> fetchAsset,
    ILocalizationService localization,
    IAppLogger? logger = null,
    IReadOnlyList<string>? modelRoots = null)
{
    /// <summary>Files that have to be present for a model to count as installed.</summary>
    private static readonly string[] Required = ["model.onnx", "vocab.json"];

    /// <summary>Bound on the extracted size — the largest model is ~340 MB.</summary>
    private const long MaxExtractedBytes = 2L * 1024 * 1024 * 1024;

    private const int MaxEntries = 64;

    private IReadOnlyList<string> Roots => modelRoots ?? LyricModelCatalog.ModelRoots;

    /// <summary>Where a model would be written if it had to be fetched.</summary>
    public string TargetDirectory(string id) => Path.Combine(Roots[0], id);

    /// <summary>True when the model is usable from any root.</summary>
    /// <summary>Deletes the copy this installer writes (the first root), so a repair downloads it afresh.</summary>
    public void Remove(string id)
    {
        var target = TargetDirectory(id);
        if (!Directory.Exists(target)) return;
        Directory.Delete(target, recursive: true);
        logger?.Info(AppLogCategory.Audio, $"Lyric model '{id}' removed from '{target}' for a repair.");
    }

    public bool IsInstalled(string id) =>
        Roots.Any(root => Required.All(f => File.Exists(Path.Combine(root, id, f))));

    /// <summary>
    ///     Ensures the model is present, downloading it only if it is not.
    ///     Returns true when the model is usable afterwards.
    ///     <para>
    ///         Never throws for an ordinary failure — no network, a refused asset, a
    ///         hash mismatch, a full disk. An optional model that cannot be fetched
    ///         has to degrade to "not installed" (the aligner then falls back to the
    ///         multilingual model), not take the caller down.
    ///     </para>
    /// </summary>
    public async Task<bool> EnsureInstalledAsync(
        LyricModelDownload model, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (IsInstalled(model.Id))
        {
            logger?.Debug(AppLogCategory.Audio, $"Lyric model '{model.Id}' is already installed.");
            return true;
        }

        // The archive is checked against a published hash before anything is
        // extracted, so an entry with no hash cannot be installed at all - and is
        // refused before a few hundred megabytes are fetched for nothing.
        if (string.IsNullOrWhiteSpace(model.Sha256))
        {
            logger?.Error(AppLogCategory.Audio,
                $"Lyric model '{model.Id}' has no expected hash; refusing to download or install it.");
            return false;
        }

        var target = TargetDirectory(model.Id);
        var staging = target + ".incoming";

        try
        {
            progress?.Report(localization.Format("Progress_DownloadingLyricModel", model.ApproximateBytes / (1024 * 1024)));
            logger?.Info(AppLogCategory.Audio,
                $"Fetching lyric model '{model.Id}' from asset '{model.AssetName}'.");

            Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);

            var archive = Path.Combine(Path.GetTempPath(), $"dc-lyric-model-{Guid.NewGuid():N}.zip");
            try
            {
                await using (var source = await fetchAsset(model.AssetName, ct).ConfigureAwait(false))
                await using (var file = File.Create(archive))
                    await source.CopyToAsync(file, ct).ConfigureAwait(false);

                if (!VerifyHash(archive, model.Sha256))
                {
                    logger?.Error(AppLogCategory.Audio,
                        $"Lyric model '{model.Id}' failed its hash check (or has no expected hash); the download was discarded.");
                    return false;
                }

                progress?.Report(localization["Progress_UnpackingLyricModel"]);
                Directory.CreateDirectory(staging);
                var extraction = BoundedZipExtractor.ExtractBounded(
                    archive, staging, MaxExtractedBytes, MaxEntries);
                if (extraction.Refusal != ZipExtractionRefusal.None)
                {
                    logger?.Error(AppLogCategory.Audio,
                        $"Lyric model '{model.Id}' archive was refused: {extraction.Refusal}.");
                    return false;
                }
            }
            finally
            {
                BestEffortDelete.File(archive);
            }

            // Some exports wrap their files in a folder; accept either shape rather
            // than shipping a model that is present but one level too deep.
            var payload = ExtractedPayload.FindRoot(staging, Required);
            if (payload == null)
            {
                logger?.Error(AppLogCategory.Audio,
                    $"Lyric model '{model.Id}' archive contained no {string.Join(" + ", Required)}.");
                return false;
            }

            // Publish atomically. Another process may have installed it while this
            // one downloaded, and that copy wins -- never overwrite.
            if (IsInstalled(model.Id)) return true;
            Directory.Move(payload, target);

            logger?.Info(AppLogCategory.Audio, $"Lyric model '{model.Id}' installed at '{target}'.");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.Error(AppLogCategory.Audio, $"Could not install lyric model '{model.Id}'.", ex);
            return false;
        }
        finally
        {
            BestEffortDelete.Directory(staging);
        }
    }

    private static bool VerifyHash(string path, string expected)
    {
        // No expected hash means nothing to verify against, and an archive that was
        // never verified is not installed: fail CLOSED, never "nothing to compare".
        if (string.IsNullOrWhiteSpace(expected)) return false;

        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return string.Equals(actual, expected.Trim().ToLowerInvariant(), StringComparison.Ordinal);
    }
}
