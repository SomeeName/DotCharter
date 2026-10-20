using System.Security.Cryptography;
using System.Text.Json;

namespace DotCharter.Tone.Online.Tone3000;

/// <summary>One local capture: its manifest plus the absolute file path.</summary>
public sealed record NamCaptureEntry(NamCaptureManifest Manifest, string FilePath);

/// <summary>
///     The local NAM-capture store the pedalboard's "NAM Captures" section
///     browses: downloaded TONE3000 model files plus a JSON manifest sidecar
///     per capture (tone/title/author/gear type/format/size/license/
///     attribution URL/file hash) under the models directory.
/// </summary>
public interface INamCaptureLibrary
{
    /// <summary>Where captures live (<c>%APPDATA%/DotCharter/models/nam</c>).</summary>
    string RootDirectory { get; }

    /// <summary>Every locally stored capture, newest first. Never throws —
    /// unreadable manifests are skipped.</summary>
    IReadOnlyList<NamCaptureEntry> GetAll();

    bool IsModelDownloaded(long modelId);

    bool IsToneDownloaded(long toneId);

    /// <summary>
    ///     Downloads one model of a tone into the library: temp file + size
    ///     sanity + hash + atomic rename, then the manifest sidecar. Typed
    ///     failure (never a throw) when the service fails, the size cap is
    ///     exceeded, or the payload does not match the declared size.
    /// </summary>
    Task<OnlineResult<NamCaptureEntry>> DownloadAsync(
        Tone3000Tone tone,
        Tone3000Model model,
        IProgress<double>? progress = null,
        CancellationToken ct = default);

    /// <summary>Deletes a capture (file + manifest). False when unknown.</summary>
    bool Delete(long modelId);

    /// <summary>
    ///     Repairs capture files stored with the WRONG extension (a pre-fix
    ///     download that wrote <c>.bin</c>, or a wrong-case <c>.NAM</c>): the
    ///     neural loader dispatches by lowercase extension, so those load
    ///     silently as nothing. Renames each offending file (+ its sidecar) to
    ///     the correct lowercase extension inferred from the manifest format or,
    ///     when unknown, sniffed from the file content, and rewrites the manifest
    ///     <c>FileName</c>. Idempotent + throw-free; returns the number repaired.
    ///     Called once at startup.
    /// </summary>
    int MigrateFileExtensions();

    /// <summary>
    ///     Imports a capture that ships WITH the app (bundled, freely-licensed)
    ///     into the local library so it appears in the "Library" view with no
    ///     sign-in: copies <paramref name="sourceFilePath"/> into the library dir
    ///     under its bundled file name and writes the manifest sidecar (license +
    ///     attribution carried through). Idempotent — a second call for a capture
    ///     already present is a throw-free no-op that returns true. False on a
    ///     missing source / copy failure. Used by
    ///     <see cref="BundledNamCaptureInstaller"/> at startup.
    /// </summary>
    bool ImportBundled(string sourceFilePath, BundledNamCapture meta);

    /// <summary>Raised after a download or delete changed the library.</summary>
    event EventHandler? Changed;
}

/// <summary>
///     <see cref="INamCaptureLibrary"/> implementation. Mirrors the
///     <c>Audio.StemSeparation.StemModelManager</c> models-dir layout
///     (one flat dir under <c>%APPDATA%/DotCharter/models</c>, override for
///     tests) and applies the usual download hardening: remote names never reach the filesystem raw
///     (<see cref="NamCaptureIndex.BuildFileName"/> slugs them), payloads are
///     size-capped, written to a <c>.partial</c> sibling and only atomically
///     renamed into place after the size/hash sanity passed — an interrupted
///     download never leaves a half-file a future engine would load.
/// </summary>
public sealed class NamCaptureLibrary : INamCaptureLibrary
{
    /// <summary>Hard cap per capture download. NAM weights are single-digit
    /// MB and IR wavs smaller still — anything near this cap is not a capture.</summary>
    public const long MaxDownloadBytes = 128L * 1024 * 1024;

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ITone3000Service _service;
    private readonly IAppLogger? _logger;
    private readonly object _gate = new();

    public NamCaptureLibrary(
        ITone3000Service service,
        IAppLogger? logger = null,
        string? rootDirectoryOverride = null)
    {
        _service = service;
        _logger = logger;
        // Resolution mirrors the htdemucs convention (StemModelManager):
        // explicit override → DOTCHARTER_NAM_DIR env var →
        // the models dir default. Directory policy is entirely managed; the
        // future namamp module only ever receives an absolute path.
        var env = Environment.GetEnvironmentVariable("DOTCHARTER_NAM_DIR");
        RootDirectory = !string.IsNullOrWhiteSpace(rootDirectoryOverride)
            ? rootDirectoryOverride
            : !string.IsNullOrWhiteSpace(env)
                ? env
                : DefaultRootDirectory;
    }

    /// <summary>The default capture folder, <c>%APPDATA%/DotCharter/models/nam</c>,
    /// through <see cref="UserDataFolders" /> so a data-root override moves it.</summary>
    internal static string DefaultRootDirectory =>
        Path.Combine(UserDataFolders.RoamingAppData, "DotCharter", "models", "nam");

    public string RootDirectory { get; }

    public event EventHandler? Changed;

    public IReadOnlyList<NamCaptureEntry> GetAll()
    {
        try
        {
            if (!Directory.Exists(RootDirectory)) return [];

            var entries = new List<NamCaptureEntry>();
            foreach (var manifestPath in Directory.EnumerateFiles(
                         RootDirectory, "*" + NamCaptureIndex.ManifestSuffix))
            {
                var manifest = TryReadManifest(manifestPath);
                if (manifest == null) continue;

                var filePath = Path.Combine(RootDirectory, manifest.FileName);
                if (!File.Exists(filePath)) continue; // orphan sidecar — ignore

                entries.Add(new NamCaptureEntry(manifest, filePath));
            }

            return entries.OrderByDescending(e => e.Manifest.DownloadedUtc).ToList();
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Application,
                $"NAM capture library scan failed: {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

    public bool IsModelDownloaded(long modelId) =>
        GetAll().Any(e => e.Manifest.ModelId == modelId);

    public bool IsToneDownloaded(long toneId) =>
        GetAll().Any(e => e.Manifest.ToneId == toneId);

    public async Task<OnlineResult<NamCaptureEntry>> DownloadAsync(
        Tone3000Tone tone,
        Tone3000Model model,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (tone == null || model == null)
            return OnlineResult<NamCaptureEntry>.Fail(OnlineError.InvalidRequest, "A tone and model are required.");

        // The EXTENSION is load-bearing (NeuralAudio dispatches by it), and the
        // API's model objects carry NO format — it is a property of the parent
        // TONE. Fall back to the tone's own format so an .aidax / IR capture
        // whose download URL happens to be extensionless cannot land as ".nam".
        var format = model.Format is { Length: > 0 }
            ? model.Format
            : tone.Formats.FirstOrDefault() ?? string.Empty;

        var fileName = NamCaptureIndex.BuildFileName(
            model.Id, tone.Title, model.SizeTier, format, model.ModelUrl);
        var destPath = Path.Combine(RootDirectory, fileName);
        var partialPath = destPath + ".partial";

        try
        {
            Directory.CreateDirectory(RootDirectory);
            if (File.Exists(partialPath)) File.Delete(partialPath);

            long written;
            await using (var dest = new FileStream(
                             partialPath, FileMode.Create, FileAccess.Write, FileShare.None,
                             1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var download = await _service.DownloadModelAsync(
                    model, dest, MaxDownloadBytes, progress, ct).ConfigureAwait(false);
                if (!download.Ok)
                {
                    await dest.DisposeAsync().ConfigureAwait(false);
                    BestEffortDelete.File(partialPath);
                    return OnlineResult<NamCaptureEntry>.Fail(download.Error, download.Message);
                }

                written = download.Value;
            }

            // Size sanity: an empty payload or one that contradicts the
            // declared size is a broken/foreign response — never keep it.
            if (written <= 0)
            {
                BestEffortDelete.File(partialPath);
                return OnlineResult<NamCaptureEntry>.Fail(OnlineError.Server, "The capture download was empty.");
            }

            if (model.FileBytes > 0 && written != model.FileBytes)
            {
                BestEffortDelete.File(partialPath);
                return OnlineResult<NamCaptureEntry>.Fail(OnlineError.Server,
                    $"The capture download was {written:N0} bytes but {model.FileBytes:N0} were declared.");
            }

            var sha256 = ComputeSha256(partialPath);

            lock (_gate)
            {
                if (File.Exists(destPath)) File.Delete(destPath);
                File.Move(partialPath, destPath);
            }

            var manifest = new NamCaptureManifest(
                tone.Id,
                model.Id,
                tone.Title,
                tone.Author,
                tone.GearType,
                format,
                model.SizeTier,
                tone.License,
                tone.LicenseUrl,
                tone.PageUrl,
                fileName,
                sha256,
                written,
                DateTimeOffset.UtcNow);
            WriteManifest(manifest);

            _logger?.Info(AppLogCategory.Application,
                $"NAM capture '{tone.Title}' (model {model.Id}, {written:N0} bytes, license '{tone.License}') stored in the library.");
            Changed?.Invoke(this, EventArgs.Empty);
            return OnlineResult<NamCaptureEntry>.Success(new NamCaptureEntry(manifest, destPath));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            BestEffortDelete.File(partialPath);
            return OnlineResult<NamCaptureEntry>.Fail(OnlineError.Network, "Cancelled.");
        }
        catch (Exception ex)
        {
            BestEffortDelete.File(partialPath);
            _logger?.Warning(AppLogCategory.Application,
                $"NAM capture download failed: {ex.GetType().Name}: {ex.Message}");
            return OnlineResult<NamCaptureEntry>.Fail(OnlineError.Network, ex.Message);
        }
    }

    public bool ImportBundled(string sourceFilePath, BundledNamCapture meta)
    {
        if (meta is null || string.IsNullOrWhiteSpace(sourceFilePath)) return false;

        var fileName = string.IsNullOrWhiteSpace(meta.FileName)
            ? Path.GetFileName(sourceFilePath)
            : meta.FileName;
        if (string.IsNullOrWhiteSpace(fileName)) return false;

        var destPath = Path.Combine(RootDirectory, fileName);
        var manifestPath = Path.Combine(RootDirectory, NamCaptureIndex.ManifestFileName(fileName));

        try
        {
            // Already installed (file + sidecar present) — idempotent no-op.
            if (File.Exists(destPath) && File.Exists(manifestPath)) return true;
            if (!File.Exists(sourceFilePath))
            {
                _logger?.Warning(AppLogCategory.Application,
                    $"Bundled NAM capture source missing: '{sourceFilePath}'.");
                return false;
            }

            Directory.CreateDirectory(RootDirectory);
            lock (_gate)
            {
                if (File.Exists(destPath)) File.Delete(destPath);
                File.Copy(sourceFilePath, destPath);
            }

            var written = new FileInfo(destPath).Length;
            var sha256 = ComputeSha256(destPath);
            var format = string.IsNullOrWhiteSpace(meta.Format)
                ? Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant()
                : meta.Format;

            // Synthetic NEGATIVE ids from the file name keep bundled captures
            // distinct from TONE3000's positive ids (IsModelDownloaded / Delete
            // still work) without a TONE3000 account.
            var id = BundledNamCapture.SyntheticId(fileName);
            var manifest = new NamCaptureManifest(
                id,
                id,
                meta.Title,
                meta.Author,
                meta.GearType,
                format,
                meta.SizeTier,
                meta.License,
                meta.LicenseUrl,
                meta.AttributionUrl,
                fileName,
                sha256,
                written,
                DateTimeOffset.UtcNow);
            WriteManifest(manifest);

            _logger?.Info(AppLogCategory.Application,
                $"Bundled NAM capture '{meta.Title}' (license '{meta.License}') installed into the library.");
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Application,
                $"Bundled NAM capture import failed for '{fileName}': {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public bool Delete(long modelId)
    {
        var entry = GetAll().FirstOrDefault(e => e.Manifest.ModelId == modelId);
        if (entry == null) return false;

        try
        {
            lock (_gate)
            {
                if (File.Exists(entry.FilePath)) File.Delete(entry.FilePath);
                var manifestPath = Path.Combine(
                    RootDirectory, NamCaptureIndex.ManifestFileName(entry.Manifest.FileName));
                if (File.Exists(manifestPath)) File.Delete(manifestPath);
            }

            _logger?.Info(AppLogCategory.Application,
                $"NAM capture '{entry.Manifest.Title}' (model {modelId}) deleted.");
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Application,
                $"NAM capture delete failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public int MigrateFileExtensions()
    {
        var repaired = 0;
        try
        {
            if (!Directory.Exists(RootDirectory)) return 0;

            foreach (var entry in GetAll())
            {
                try
                {
                    var current = entry.Manifest.FileName;
                    var currentExt = Path.GetExtension(current); // case-preserving
                    var desiredExt = DesiredExtension(entry);
                    if (desiredExt is null) continue;
                    // Ordinal (case-sensitive) compare: the loader needs lowercase.
                    if (string.Equals(currentExt, desiredExt, StringComparison.Ordinal)) continue;

                    var baseName = Path.GetFileNameWithoutExtension(current);
                    var newName = baseName + desiredExt;
                    if (string.Equals(newName, current, StringComparison.Ordinal)) continue;

                    var oldPath = Path.Combine(RootDirectory, current);
                    var newPath = Path.Combine(RootDirectory, newName);
                    var oldSidecar = Path.Combine(RootDirectory, NamCaptureIndex.ManifestFileName(current));
                    var newSidecar = Path.Combine(RootDirectory, NamCaptureIndex.ManifestFileName(newName));

                    // A case-only rename (.NAM → .nam) targets the SAME file on a
                    // case-insensitive filesystem — deleting "newPath" would delete
                    // the source. Route those through a temp step.
                    var caseOnly = string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase);

                    lock (_gate)
                    {
                        if (!File.Exists(oldPath)) continue;
                        if (caseOnly)
                        {
                            var tmp = newPath + ".casetmp";
                            if (File.Exists(tmp)) File.Delete(tmp);
                            File.Move(oldPath, tmp);
                            File.Move(tmp, newPath);
                        }
                        else
                        {
                            if (File.Exists(newPath)) File.Delete(newPath);
                            File.Move(oldPath, newPath);
                        }
                        // Rewrite the manifest with the corrected file name, then
                        // drop the stale sidecar (only when it's a genuinely
                        // different path — not a case-only twin of the new one).
                        var fixedManifest = entry.Manifest with { FileName = newName };
                        AtomicFile.WriteAllText(newSidecar, JsonSerializer.Serialize(fixedManifest, ManifestJson));
                        if (!string.Equals(oldSidecar, newSidecar, StringComparison.OrdinalIgnoreCase)
                            && File.Exists(oldSidecar))
                        {
                            File.Delete(oldSidecar);
                        }
                    }

                    repaired++;
                    _logger?.Info(AppLogCategory.Application,
                        $"Repaired NAM capture extension: '{current}' → '{newName}'.");
                }
                catch (Exception ex)
                {
                    _logger?.Warning(AppLogCategory.Application,
                        $"NAM capture extension repair skipped for '{entry.Manifest.FileName}': {ex.GetType().Name}.");
                }
            }

            if (repaired > 0) Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Application,
                $"NAM capture extension migration failed: {ex.GetType().Name}: {ex.Message}");
        }

        return repaired;
    }

    /// <summary>The correct lowercase extension for a stored capture, or null to
    /// leave it alone. Uses the manifest format first, then sniffs the file
    /// content (a NAM/AIDA-X model is JSON with a tell-tale key).</summary>
    private string? DesiredExtension(NamCaptureEntry entry)
    {
        var norm = NamCaptureIndex.NormalizeFormat(entry.Manifest.Format);
        if (norm.Length > 0) return NamCaptureIndex.ExtensionFor(norm);

        var sniff = SniffModelFormat(entry.FilePath);
        return sniff is null ? null : NamCaptureIndex.ExtensionFor(sniff);
    }

    /// <summary>Best-effort content sniff for a neural model file: NAM captures
    /// are JSON carrying <c>"architecture"</c>; AIDA-X/keras carry
    /// <c>"in_shape"</c>. Returns <c>nam</c>/<c>aidax</c> or null (leave alone).</summary>
    private static string? SniffModelFormat(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buf = new byte[Math.Min(4096, (int)Math.Min(fs.Length, 4096))];
            var read = fs.Read(buf, 0, buf.Length);
            var head = System.Text.Encoding.UTF8.GetString(buf, 0, read);
            if (head.IndexOf("\"architecture\"", StringComparison.OrdinalIgnoreCase) >= 0) return "nam";
            if (head.IndexOf("\"in_shape\"", StringComparison.OrdinalIgnoreCase) >= 0
                || head.IndexOf("\"in_skip\"", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "aidax";
            }
        }
        catch { /* unreadable — leave alone */ }
        return null;
    }

    // ─────────────────────────────── internals ───────────────────────────────

    private NamCaptureManifest? TryReadManifest(string path)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<NamCaptureManifest>(
                AtomicFile.ReadAllText(path), ManifestJson);
            return manifest is { FileName.Length: > 0 } ? manifest : null;
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Application,
                $"Unreadable NAM capture manifest '{Path.GetFileName(path)}' skipped: {ex.GetType().Name}.");
            return null;
        }
    }

    private void WriteManifest(NamCaptureManifest manifest)
    {
        var path = Path.Combine(RootDirectory, NamCaptureIndex.ManifestFileName(manifest.FileName));
        // AtomicFile: temp → Flush(true) → atomic rename-over. Closes the
        // Delete-then-Move window (a crash there lost the manifest) and adds
        // the fsync the hand-rolled temp+Move was missing.
        lock (_gate)
        {
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(manifest, ManifestJson));
        }
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
