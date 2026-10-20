using DotCharter.Localization;
using System.Security.Cryptography;
using System.Text.Json;

namespace DotCharter.Services.Ai.Match.Model;

/// <summary>
///     What one install attempt did. Never an exception for an ordinary
///     failure, because a missing optional model must degrade the MATCHER to
///     search-only rather than take a settings page or a tone panel down.
/// </summary>
/// <param name="Installed">True when the model is usable afterwards.</param>
/// <param name="Reason">
///     Why not, for the UI to show verbatim. Null on success. Deliberately a
///     string and not an enum: every one of these is read by a human once and
///     the set grows with the failure modes of a network.
/// </param>
public sealed record RigEstimatorModelInstallResult(bool Installed, string? Reason)
{
    /// <summary>The success answer.</summary>
    public static RigEstimatorModelInstallResult Ok { get; } = new(true, null);

    /// <summary>A failure carrying its reason.</summary>
    /// <param name="reason">What went wrong, in one line.</param>
    /// <returns>The failure result.</returns>
    public static RigEstimatorModelInstallResult Failed(string reason) => new(false, reason);
}

/// <summary>What an installed model says about itself, read from its sidecar.</summary>
/// <param name="ModelId">The export's own id (<c>export.modelId</c>).</param>
/// <param name="CheckpointEpoch">The epoch the weights came from, or -1.</param>
/// <param name="Frontend">CNN or CLAP, when the sidecar names one.</param>
/// <param name="Head">v1 or v2, when the sidecar names one.</param>
/// <param name="TrainedOnCrudeSmokeSet">
///     True when the export came off the synthetic smoke set, whose knob labels
///     carry no signal by construction. Surfaced rather than hidden: a user
///     whose estimates are nonsense should be able to see WHY without a log.
/// </param>
/// <param name="BytesOnDisk">Total size of the installed folder.</param>
public sealed record RigEstimatorModelInfo(
    string ModelId, int CheckpointEpoch, string? Frontend, string? Head,
    bool TrainedOnCrudeSmokeSet, long BytesOnDisk)
{
    /// <summary>
    ///     Reads the sidecar of an installed model. Null when the folder holds
    ///     no readable sidecar — which is the same answer as "not installed"
    ///     for every caller, so a corrupt install never throws into the UI.
    /// </summary>
    /// <param name="directory">The model folder.</param>
    /// <returns>The info, or null.</returns>
    public static RigEstimatorModelInfo? Read(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var sidecar = Path.Combine(directory, RigEstimatorModelInstaller.SidecarFileName);
        if (!File.Exists(sidecar)) return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(sidecar));
            if (!document.RootElement.TryGetProperty("export", out var export) ||
                export.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new RigEstimatorModelInfo(
                String(export, "modelId") ?? RigEstimatorModelCatalog.RigEstimatorId,
                export.TryGetProperty("checkpointEpoch", out var epoch) &&
                epoch.ValueKind == JsonValueKind.Number && epoch.TryGetInt32(out var e) ? e : -1,
                String(export, "frontend"),
                String(export, "head"),
                export.TryGetProperty("trainedOnCrudeSmokeSet", out var crude) &&
                crude.ValueKind == JsonValueKind.True,
                DirectoryBytes(directory));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? String(JsonElement export, string name) =>
        export.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long DirectoryBytes(string directory)
    {
        try
        {
            return Directory
                .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}

/// <summary>
///     Fetches the rig-estimator model ONCE and never touches it again.
///     <para>
///         The twin of <c>LyricModelInstaller</c>, re-pointed at this lane's
///         root. It is a separate type rather than a shared one because the two
///         differ in the three things an installer IS: the root
///         (<c>%LOCALAPPDATA%</c> here, <c>%APPDATA%</c> there), the required
///         file set, and the presence of an OPTIONAL payload file.
///     </para>
///     <para>
///         <b>Never overwrite.</b> An existing install is authoritative whatever
///         its provenance — hand-placed by <c>export_onnx.py</c>, a newer
///         export, or mid-inference on another thread. The presence check is
///         therefore the whole gate, which also makes this idempotent and safe
///         to call on every launch.
///     </para>
///     <para>
///         Extraction goes through <see cref="BoundedZipExtractor" /> (byte
///         budget counted on what the inflater really produces, entry cap read
///         from the end-of-central-directory record, zip-slip refused), into a
///         sibling <c>.incoming</c> folder that is MOVED into place only once it
///         is complete — so an interrupted download cannot leave a half-model
///         that the presence check would then accept for ever.
///     </para>
/// </summary>
/// <param name="fetchAsset">Opens a stream over a named release asset.</param>
/// <param name="localization">Resolves the status and progress text the user reads.</param>
/// <param name="logger">Optional log sink.</param>
/// <param name="modelRoot">
///     Overrides <see cref="RigEstimatorModelCatalog.ModelRoot" />; tests pass a
///     temp folder. Production passes null.
/// </param>
public sealed class RigEstimatorModelInstaller(
    Func<string, CancellationToken, Task<Stream>> fetchAsset,
    ILocalizationService localization,
    IAppLogger? logger = null,
    string? modelRoot = null)
{
    /// <summary>The ONNX graph.</summary>
    public const string ModelFileName = "model.onnx";

    /// <summary>The label space and export record the estimator reads.</summary>
    public const string SidecarFileName = "model.sidecar.json";

    /// <summary>
    ///     The external-data blob an ONNX export larger than the 2 GB protobuf
    ///     limit writes beside the graph. OPTIONAL: a self-contained export has
    ///     none, and requiring it would refuse every model we ship today.
    /// </summary>
    public const string ExternalDataFileName = "model.onnx.data";

    /// <summary>Files that have to be present for a MODEL to count as installed.
    /// Another entry (the tone library) names its own in
    /// <see cref="RigEstimatorModelDownload.RequiredFiles" />.</summary>
    public static IReadOnlyList<string> ModelFiles { get; } = [ModelFileName, SidecarFileName];

    /// <summary>The files an id needs: its catalogue entry's, else the model's.</summary>
    private static IReadOnlyList<string> FilesFor(string id) =>
        RigEstimatorModelCatalog.Find(id)?.Files ?? ModelFiles;

    /// <summary>
    ///     Bound on the extracted size. The v1 export is ~200 MB; the headroom
    ///     is for an external-data export, whose <c>.data</c> blob carries the
    ///     weights the graph then references.
    /// </summary>
    internal const long MaxExtractedBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>
    ///     Members allowed. A model export is two or three files; anything near
    ///     this is not one.
    /// </summary>
    internal const int MaxEntries = 32;

    /// <summary>
    ///     Floor for the plausibility check that stands in for a hash while
    ///     none is published. It is NOT an integrity check and is not pretended
    ///     to be one — it catches the shape of failure that actually happens
    ///     with an unauthenticated asset endpoint (an HTML error page, a
    ///     zero-length body, a truncated first chunk), and nothing else.
    /// </summary>
    internal const long MinimumPlausibleBytes = 1L * 1024 * 1024;

    private string Root => modelRoot ?? RigEstimatorModelCatalog.ModelRoot;

    /// <summary>Where the model is, or would be written.</summary>
    /// <param name="id">The model id.</param>
    /// <returns>The model folder.</returns>
    public string TargetDirectory(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Path.Combine(Root, id);
    }

    /// <summary>True when the download with this id is usable.</summary>
    /// <param name="id">The model or library id.</param>
    /// <returns>Whether every required file is present.</returns>
    public bool IsInstalled(string id) => IsInstalled(id, FilesFor(id));

    /// <summary>True when every one of <paramref name="files" /> is under the
    /// id's folder.</summary>
    /// <param name="id">The model or library id.</param>
    /// <param name="files">The files that make it installed.</param>
    /// <returns>Whether every one is present.</returns>
    public bool IsInstalled(string id, IReadOnlyList<string> files) =>
        files.All(f => File.Exists(Path.Combine(TargetDirectory(id), f)));

    /// <summary>Reads the installed model's sidecar, or null when none is installed.</summary>
    /// <param name="id">The model id.</param>
    /// <returns>The info, or null.</returns>
    public RigEstimatorModelInfo? DescribeInstalled(string id) =>
        IsInstalled(id) ? RigEstimatorModelInfo.Read(TargetDirectory(id)) : null;

    /// <summary>
    ///     Ensures the model is present, downloading it only if it is not.
    ///     <para>
    ///         Never throws for an ordinary failure — no network, a refused
    ///         asset, a hash mismatch, a full disk. Cancellation is the one
    ///         thing that propagates, because a user's Cancel must not read to
    ///         the caller as "the model is unavailable".
    ///     </para>
    /// </summary>
    /// <param name="model">What to fetch.</param>
    /// <param name="progress">Optional progress text.</param>
    /// <param name="ct">Cancels the download.</param>
    /// <returns>Whether the model is usable afterwards, and why not if it is not.</returns>
    public async Task<RigEstimatorModelInstallResult> EnsureInstalledAsync(
        RigEstimatorModelDownload model,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        var files = model.Files;
        if (IsInstalled(model.Id, files))
        {
            logger?.Debug(AppLogCategory.Audio, $"Rig estimator model '{model.Id}' is already installed.");
            return RigEstimatorModelInstallResult.Ok;
        }

        var target = TargetDirectory(model.Id);
        var staging = target + ".incoming";

        try
        {
            progress?.Report(localization.Format("Progress_DownloadingModel",
                model.Description, model.ApproximateBytes / (1024 * 1024)));
            logger?.Info(AppLogCategory.Audio,
                $"Fetching rig estimator model '{model.Id}' from asset '{model.AssetName}'.");

            if (!model.HasPublishedHash)
            {
                logger?.Warning(AppLogCategory.Audio,
                    $"Rig estimator model '{model.Id}' has no published SHA-256 in " +
                    "RigEstimatorModelCatalog, so the download is SIZE-CHECKED ONLY. " +
                    "Fill in the hash once the asset is published.");
            }

            Directory.CreateDirectory(Root);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);

            var archive = Path.Combine(Path.GetTempPath(), $"dc-rig-model-{Guid.NewGuid():N}.zip");
            try
            {
                await using (var source = await fetchAsset(model.AssetName, ct).ConfigureAwait(false))
                await using (var file = File.Create(archive))
                    await source.CopyToAsync(file, ct).ConfigureAwait(false);

                var verdict = VerifyArchive(archive, model);
                if (verdict is not null)
                {
                    logger?.Error(AppLogCategory.Audio,
                        $"Rig estimator model '{model.Id}' was discarded: {verdict}");
                    return RigEstimatorModelInstallResult.Failed(verdict);
                }

                progress?.Report(localization.Format("Progress_UnpackingModel", model.Description));
                Directory.CreateDirectory(staging);
                var extraction = BoundedZipExtractor.ExtractBounded(
                    archive, staging, MaxExtractedBytes, MaxEntries);
                if (extraction.Refusal != ZipExtractionRefusal.None)
                {
                    logger?.Error(AppLogCategory.Audio,
                        $"Rig estimator model '{model.Id}' archive was refused: {extraction.Refusal}.");
                    return RigEstimatorModelInstallResult.Failed(
                        $"the archive was refused ({extraction.Refusal})");
                }
            }
            finally
            {
                BestEffortDelete.File(archive);
            }

            // Some exports wrap their files in a folder; accept either shape
            // rather than installing a model that is present but one level too
            // deep, which the estimator would then report as missing for ever.
            var payload = ExtractedPayload.FindRoot(staging, files);
            if (payload is null)
            {
                logger?.Error(AppLogCategory.Audio,
                    $"Rig estimator model '{model.Id}' archive contained no " +
                    $"{string.Join(" + ", files)}.");
                return RigEstimatorModelInstallResult.Failed(
                    $"the archive contained no {string.Join(" + ", files)}");
            }

            // Publish atomically. Another process may have installed it while
            // this one downloaded, and that copy wins — never overwrite.
            if (IsInstalled(model.Id, files)) return RigEstimatorModelInstallResult.Ok;
            Directory.Move(payload, target);

            logger?.Info(AppLogCategory.Audio,
                $"Rig estimator model '{model.Id}' installed at '{target}'.");
            return RigEstimatorModelInstallResult.Ok;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.Error(AppLogCategory.Audio, $"Could not install rig estimator model '{model.Id}'.", ex);
            return RigEstimatorModelInstallResult.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            BestEffortDelete.Directory(staging);
        }
    }

    /// <summary>
    ///     Checks a downloaded archive before anything is extracted. Null means
    ///     accepted; otherwise the reason to show the user.
    ///     <para>
    ///         <b>A published hash is ENFORCED and is the whole integrity
    ///         story.</b> With none published the archive is only checked for
    ///         PLAUSIBLE SIZE — at least <see cref="MinimumPlausibleBytes" />,
    ///         and within 4x of the catalogue's estimate when it has one. That
    ///         is a sanity check, not authentication, and the install path logs
    ///         a warning saying so.
    ///     </para>
    /// </summary>
    /// <param name="path">The downloaded archive.</param>
    /// <param name="model">The catalogue entry it was fetched for.</param>
    /// <returns>Null when accepted, else the refusal reason.</returns>
    internal static string? VerifyArchive(string path, RigEstimatorModelDownload model)
    {
        var length = new FileInfo(path).Length;

        if (model.HasPublishedHash)
        {
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return string.Equals(actual, model.Sha256.Trim().ToLowerInvariant(), StringComparison.Ordinal)
                ? null
                : $"the download failed its SHA-256 check (expected {model.Sha256.Trim()[..12]}…, " +
                  $"got {actual[..12]}…)";
        }

        if (length < MinimumPlausibleBytes)
            return $"the download was only {length} bytes, which is not a model archive";

        if (model.ApproximateBytes > 0 && length > model.ApproximateBytes * 4)
        {
            return $"the download was {length / (1024 * 1024)} MB, far larger than the expected " +
                   $"{model.ApproximateBytes / (1024 * 1024)} MB";
        }

        return null;
    }
}
