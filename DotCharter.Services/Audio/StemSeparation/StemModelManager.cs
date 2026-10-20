// ReSharper disable LocalizableElement -- these are developer-facing error strings, not user-visible UI.
using System.Text;
using JuceSharp.StemSeparation.Onnx;
using JuceSharp.StemSeparation.Processing;
using DotCharter.Services.Update;
using JuceSharp.StemSeparation.Runtime;

namespace DotCharter.Services.Audio.StemSeparation;

/// <summary>Progress snapshot reported during a model download.</summary>
public readonly record struct ModelDownloadProgress(long BytesRead, long TotalBytes)
{
    public double Fraction => TotalBytes <= 0 ? 0.0 : Math.Clamp(BytesRead / (double)TotalBytes, 0.0, 1.0);
}

/// <summary>Why a catalogue model cannot be downloaded right now.</summary>
public enum StemModelDownloadBlock
{
    /// <summary>Nothing stops it.</summary>
    None,

    /// <summary>It is a release asset of a repository that needs a sign-in
    /// (<see cref="IGitHubReleaseAssetDownloader.RequiresSignIn"/>) and nobody is signed in to
    /// GitHub (Settings ▸ Updates). It must show as unavailable, never as a live download. The
    /// app's own downloader reads the public repo, so the public build never reports it.</summary>
    NeedsGitHubSignIn,
}

/// <summary>
/// Manages the on-disk cache of stem-separation model files. Independent of
/// the separation backend so the UI can list, download, and delete models
/// without touching the audio engine.
/// <para>
/// <b>It does not download anything itself.</b> Every fetch goes through
/// JuceSharp's <see cref="OnnxModelCatalog.EnsureAsync(OnnxModelEntry,string,IProgress{DownloadProgress}?,bool,Action{string}?,CancellationToken)" />
/// or the kind-less <see cref="VerifiedDownload.EnsureFileAsync" />, both of them
/// the one <c>VerifiedDownloader</c>: HTTP Range resume, mirror failover, a
/// per-URL part file, hash and exact-size checked before the atomic rename, and
/// an installed file that fails its hash discarded rather than trusted. The
/// <c>HttpClient</c>, the <c>.partial</c> dance and the hash check kept here
/// would be a second, weaker copy of all of that — no resume, and a hash
/// for two of eleven catalogue entries.
/// </para>
/// </summary>
public sealed class StemModelManager
{
    private readonly IConfigurationService? _configService;
    private readonly IAppLogger? _logger;
    // Disk IO (directory creation + custom-model JSON load) is deferred to
    // first use: this manager is constructed during application bootstrap on
    // the UI thread, and the stem features that need it may never be opened
    // in a session.
    private readonly Lazy<CustomStemModelStore> _customStore;
    private readonly IGitHubReleaseAssetDownloader? _releaseAssets;

    /// <summary>The user's models folder (<c>%APPDATA%/DotCharter/models</c>), through
    /// <see cref="UserDataFolders" /> so a data-root override moves it.</summary>
    internal static string DefaultModelsDirectory =>
        Path.Combine(UserDataFolders.RoamingAppData, "DotCharter", "models");

    /// <param name="configService">The Active model and the backend preferences.</param>
    /// <param name="logger">Optional log.</param>
    /// <param name="modelsDirectoryOverride">Tests and tools: a models folder other than the user's.</param>
    /// <param name="releaseAssets">The signed-in download for rows hosted on the app's private
    /// releases (<see cref="StemModelDescriptor.ReleaseAsset"/>); without it those rows are
    /// unavailable (<see cref="StemModelDownloadBlock.NeedsGitHubSignIn"/>).</param>
    public StemModelManager(IConfigurationService? configService = null, IAppLogger? logger = null,
        string? modelsDirectoryOverride = null, IGitHubReleaseAssetDownloader? releaseAssets = null)
    {
        _configService = configService;
        _logger = logger;
        _releaseAssets = releaseAssets;
        // Signing in or out changes which rows can be downloaded: the same event a download
        // raises, so every list of models refreshes.
        releaseAssets?.SignInChanged += () => ModelsChanged?.Invoke(this, EventArgs.Empty);
        ModelsDirectory = !string.IsNullOrWhiteSpace(modelsDirectoryOverride)
            ? modelsDirectoryOverride
            : DefaultModelsDirectory;

        _customStore = new Lazy<CustomStemModelStore>(() =>
        {
            Directory.CreateDirectory(ModelsDirectory);
            var store = new CustomStemModelStore(ModelsDirectory, logger);
            foreach (var custom in store.Load())
                StemModelCatalog.RegisterCustom(custom);
            return store;
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Directory where models are cached on disk.</summary>
    public string ModelsDirectory { get; }

    /// <summary>The static catalog (built-ins plus persisted custom models).</summary>
    public IReadOnlyList<StemModelDescriptor> Catalog
    {
        get
        {
            _ = _customStore.Value; // ensure persisted custom models are registered
            return StemModelCatalog.All;
        }
    }

    /// <summary>Raised after Download / Delete completes (UI thread is the caller's responsibility).</summary>
    public event EventHandler? ModelsChanged;

    /// <summary>What, if anything, stops <paramref name="id"/> from being downloaded now.</summary>
    /// <param name="id">Catalogue id.</param>
    /// <returns><see cref="StemModelDownloadBlock.None"/> for an unknown id too: the download
    /// itself reports that.</returns>
    public StemModelDownloadBlock DownloadBlock(string id)
    {
        var desc = FindById(id);
        if (desc?.ReleaseAsset == null) return StemModelDownloadBlock.None;
        return CanReachReleaseAssets
            ? StemModelDownloadBlock.None
            : StemModelDownloadBlock.NeedsGitHubSignIn;
    }

    /// <summary>A release-asset downloader exists and needs no sign-in, or someone is signed in.</summary>
    private bool CanReachReleaseAssets => _releaseAssets is { RequiresSignIn: false } or { IsSignedIn: true };

    /// <summary>Whether <paramref name="id"/> can be downloaded now (<see cref="DownloadBlock"/>).</summary>
    /// <param name="id">Catalogue id.</param>
    /// <returns><c>true</c> when nothing blocks it.</returns>
    public bool CanDownload(string id) => DownloadBlock(id) == StemModelDownloadBlock.None;

    /// <summary>
    /// The default-model upgrade the user said no to (<see cref="StemDefaultModel.UpgradeTarget"/>),
    /// so the Separate Stems verb does not ask again; empty when none. A download of that model
    /// from Settings still works.
    /// </summary>
    public string DeclinedDefaultUpgradeId
    {
        get => _configService?.LoadAppConfig().StemDefaultUpgradeDeclinedId ?? string.Empty;
        set
        {
            if (_configService == null) return;
            var normalized = value?.Trim() ?? string.Empty;
            var cfg = _configService.LoadAppConfig();
            if (string.Equals(cfg.StemDefaultUpgradeDeclinedId, normalized, StringComparison.Ordinal)) return;
            cfg.StemDefaultUpgradeDeclinedId = normalized;
            _configService.SaveAppConfig(cfg);
            _logger?.Info(AppLogCategory.Audio, $"Default stem model upgrade declined: '{normalized}'.");
        }
    }

    /// <summary>True if the model's file exists on disk.</summary>
    public bool IsInstalled(string id)
    {
        var desc = FindById(id);
        return desc != null && File.Exists(GetLocalPath(desc));
    }

    /// <summary>Catalog lookup that first ensures persisted custom models are registered.</summary>
    private StemModelDescriptor? FindById(string id)
    {
        _ = _customStore.Value;
        return StemModelCatalog.FindById(id);
    }

    /// <summary>Absolute path the model would have on disk (whether or not it exists).</summary>
    public string GetLocalPath(StemModelDescriptor descriptor) =>
        Path.Combine(ModelsDirectory, descriptor.FileName);

    public string? GetLocalPathIfInstalled(string id)
    {
        var desc = FindById(id);
        if (desc == null) return null;
        var p = GetLocalPath(desc);
        return File.Exists(p) ? p : null;
    }

    /// <summary>Size on disk in bytes, or 0 if not installed.</summary>
    public long GetInstalledSize(string id)
    {
        var p = GetLocalPathIfInstalled(id);
        if (p == null) return 0;
        try { return new FileInfo(p).Length; } catch { return 0; }
    }

    /// <summary>The currently configured active model id (or empty).</summary>
    public string ActiveModelId
    {
        get
        {
            var cfg = _configService?.LoadAppConfig();
            return cfg?.HtDemucsActiveModelId ?? string.Empty;
        }
        set
        {
            if (_configService == null) return;
            var cfg = _configService.LoadAppConfig();
            cfg.HtDemucsActiveModelId = value;
            _configService.SaveAppConfig(cfg);
            _logger?.Info(AppLogCategory.Audio, $"Active htdemucs model set to '{cfg.HtDemucsActiveModelId}'.");
        }
    }

    /// <summary>
    /// User preference for the ONNX execution provider. Persisted as a string
    /// to allow forward-compatible additions ("Auto" / "DirectML" / "Cpu"). The
    /// managed ONNX backend honours this through <c>StemExecutionDecision</c>;
    /// values are case-insensitive. A stored "Cuda" (the automatic choice of
    /// versions that shipped the CUDA build) reads as "Auto": this build's ONNX
    /// Runtime has no CUDA, and Auto is DirectML whenever a usable GPU exists.
    /// </summary>
    public string ExecutionProvider
    {
        get
        {
            var cfg = _configService?.LoadAppConfig();
            var raw = cfg?.StemSeparationExecutionProvider;
            return string.IsNullOrWhiteSpace(raw) || IsRetiredProvider(raw) ? "Auto" : raw;
        }
        set
        {
            if (_configService == null) return;
            var normalized = string.IsNullOrWhiteSpace(value) ? "Auto" : value.Trim();
            var cfg = _configService.LoadAppConfig();
            if (string.Equals(cfg.StemSeparationExecutionProvider, normalized, StringComparison.OrdinalIgnoreCase))
                return;
            cfg.StemSeparationExecutionProvider = normalized;
            _configService.SaveAppConfig(cfg);
            _logger?.Info(AppLogCategory.Audio, $"Stem separation execution provider set to '{normalized}'.");
        }
    }

    private static bool IsRetiredProvider(string raw) =>
        raw.Trim().Equals("Cuda", StringComparison.OrdinalIgnoreCase) ||
        raw.Trim().Equals("Nvidia", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The GPU stem separation runs on, as <c>StemGpuRanking.KeyFor</c> wrote it
    /// ("deviceId:name"); empty means the automatic choice (StemGpuRanking). Read on every
    /// separation, so a pick in Settings applies to the next run.
    /// </summary>
    public string GpuAdapterKey
    {
        get => _configService?.LoadAppConfig().StemSeparationGpuAdapter ?? string.Empty;
        set
        {
            if (_configService == null) return;
            var normalized = value?.Trim() ?? string.Empty;
            var cfg = _configService.LoadAppConfig();
            if (string.Equals(cfg.StemSeparationGpuAdapter, normalized, StringComparison.Ordinal)) return;
            cfg.StemSeparationGpuAdapter = normalized;
            _configService.SaveAppConfig(cfg);
            _logger?.Info(AppLogCategory.Audio, $"Stem separation GPU set to '{normalized}'.");
        }
    }

    /// <summary>
    /// Resolve a path to an htdemucs model file the engine should use.
    /// Priority: explicitly active model → first installed catalog entry →
    /// <c>DOTCHARTER_HTDEMUCS_MODEL</c> env var → null.
    /// </summary>
    public string? ResolveActiveModelPath()
    {
        var active = ActiveModelId;
        if (!string.IsNullOrWhiteSpace(active))
        {
            var p = GetLocalPathIfInstalled(active);
            if (p != null) return p;
        }
        foreach (var d in Catalog)
        {
            var p = GetLocalPath(d);
            if (File.Exists(p)) return p;
        }
        var env = Environment.GetEnvironmentVariable("DOTCHARTER_HTDEMUCS_MODEL");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        return null;
    }

    /// <summary>
    /// Ensures a catalogue model is present and CORRECT in the local cache.
    /// <para>
    /// Handed straight to JuceSharp's verified downloader, so: an already
    /// installed file is HASHED rather than trusted (a half-written model from
    /// a killed process is the common case, and "the file exists" reading as
    /// "the model is installed" is how that becomes a permanent, mystifying
    /// load failure); an interrupted 300 MB download RESUMES; a derived entry's
    /// mirror list is walked on a transport failure but never on a user Cancel;
    /// and the hash and exact size are checked BEFORE the atomic rename, so a
    /// rejected download never becomes "installed".
    /// </para>
    /// <para>
    /// An UNPINNED entry — every user import, and any built-in nobody can source
    /// a hash for — is fetched anyway, and warns. That is a deliberate call-site
    /// decision, not a default: this app has always offered those entries, and
    /// refusing them here would withdraw a working feature rather than make it
    /// safe.
    /// </para>
    /// <para>
    /// <b>A row goes back out through the catalogue it came IN from.</b> A row
    /// derived from <see cref="OnnxModelCatalog"/> goes back through that
    /// catalogue and a row derived from <see cref="DemucsGgmlModelCatalog"/>
    /// through ITS catalogue — each owns its own mirror list, its own
    /// <c>ValidateEntry</c> and its own unpinned-entry refusal, and neither is
    /// re-typed here. Only a USER IMPORT, which no catalogue knows about, takes
    /// the kind-less <see cref="VerifiedDownload.EnsureFileAsync"/>.
    /// <para>
    /// The ggml half does not take the kind-less door, which fits only what
    /// no catalogue knows (<see cref="DemucsGgmlModelCatalog"/> knows the ggml rows;
    /// dressing a ggml weight as an ONNX entry with "the
    /// nearest kind label" is not an option). The cost of the kind-less door for a CATALOGUE row
    /// is that the refusal is lost: the kind-less door has no notion of a pinned
    /// entry, so an unpinned ggml descriptor would be fetched with no
    /// <c>allowUnverified</c> decision made anywhere, while the ONNX half beside
    /// it takes that decision explicitly. Both halves state it at this call
    /// site. Pinned by <c>StemModelDownloadIntegrityTests</c>.
    /// </para>
    /// </para>
    /// </summary>
    /// <param name="id">Catalogue id.</param>
    /// <param name="progress">Download progress.</param>
    /// <param name="ct">Cancellation; keeps the part file for a resume.</param>
    /// <param name="activateIfNoneActive">Make the model Active when none is. <c>false</c>
    /// for a DEFAULT model fetched on demand (<see cref="StemDefaultModel"/>): activating it
    /// would turn "no choice made" into an explicit choice, and a later change of default
    /// would not reach that user.</param>
    public async Task DownloadAsync(
        string id,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken ct = default,
        bool activateIfNoneActive = true)
    {
        var desc = FindById(id)
                   ?? throw new ArgumentException($"Unknown model id '{id}'.", nameof(id));
        if (desc.ReleaseAsset is { } asset)
        {
            await DownloadReleaseAssetAsync(desc, asset, progress, ct, activateIfNoneActive).ConfigureAwait(false);
            return;
        }

        var onnx = desc.Onnx;
        var ggml = desc.Ggml;
        var catalogued = onnx is not null || ggml is not null;
        var request = catalogued ? default : StemModelDownloadPlan.FileRequestFor(desc, ModelsDirectory);

        var mirrors = onnx?.Urls ?? ggml?.Urls;
        _logger?.Info(AppLogCategory.Audio,
            mirrors is not null
                ? $"Downloading stem model '{desc.Id}' from {mirrors[0]}" +
                  (mirrors.Count > 1 ? $" (+{mirrors.Count - 1} mirror(s))." : ".")
                : $"Downloading stem model '{desc.Id}' from {request.Url}.");

        try
        {
            var path = onnx is not null
                ? await OnnxModelCatalog.EnsureAsync(
                    onnx,
                    ModelsDirectory,
                    Throttled(progress),
                    allowUnverified: onnx.Sha256 is null,
                    log: line => _logger?.Info(AppLogCategory.Audio, line),
                    ct).ConfigureAwait(false)
                : ggml is not null
                ? await DemucsGgmlModelCatalog.EnsureAsync(
                    ggml,
                    ModelsDirectory,
                    Throttled(progress),
                    allowUnverified: ggml.Sha256 is null,
                    log: line => _logger?.Info(AppLogCategory.Audio, line),
                    ct).ConfigureAwait(false)
                : await VerifiedDownload.EnsureFileAsync(
                    request.Url,
                    request.DestinationPath,
                    request.Sha256,
                    request.ExpectedBytes,
                    Throttled(progress),
                    log: line => _logger?.Info(AppLogCategory.Audio, line),
                    ct).ConfigureAwait(false);

            _logger?.Info(AppLogCategory.Audio,
                $"Model '{desc.Id}' ready ({new FileInfo(path).Length:N0} bytes) at '{path}'.");

            // Auto-activate the first model we download if nothing was set.
            if (activateIfNoneActive && string.IsNullOrWhiteSpace(ActiveModelId))
                ActiveModelId = desc.Id;
        }
        catch (OperationCanceledException)
        {
            // The part file is KEPT on cancel — that is what makes the next
            // attempt a resume — so this is an info line, not a failure.
            _logger?.Info(AppLogCategory.Audio, $"Model download cancelled for '{desc.Id}'.");
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Error(AppLogCategory.Audio, $"Model download failed for '{desc.Id}'.", ex);
            throw;
        }
        finally
        {
            ModelsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// A row hosted on the app's releases: the download (with the token when someone is signed in),
    /// verified against the row's own pin before it lands. From a repository that needs a sign-in,
    /// without one, it throws <see cref="GitHubSignInRequiredException"/> and touches nothing.
    /// </summary>
    private async Task DownloadReleaseAssetAsync(
        StemModelDescriptor desc,
        GitHubReleaseAssetRef asset,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken ct,
        bool activateIfNoneActive)
    {
        if (!CanReachReleaseAssets || _releaseAssets is not { } releaseAssets)
        {
            throw new GitHubSignInRequiredException(
                $"Stem model '{desc.Id}' is published on DotCharter's own releases and needs a GitHub " +
                "sign-in (Settings, Updates) to download.");
        }

        _logger?.Info(AppLogCategory.Audio,
            $"Downloading stem model '{desc.Id}' from release {asset.Tag} ('{asset.AssetName}', " +
            (releaseAssets.IsSignedIn ? "signed in)." : "anonymous)."));
        try
        {
            var path = await releaseAssets.DownloadVerifiedAsync(
                asset, GetLocalPath(desc), desc.Sha256!, desc.ExactBytes, Throttled(progress), ct).ConfigureAwait(false);
            _logger?.Info(AppLogCategory.Audio,
                $"Model '{desc.Id}' ready ({new FileInfo(path).Length:N0} bytes) at '{path}'.");
            if (activateIfNoneActive && string.IsNullOrWhiteSpace(ActiveModelId))
                ActiveModelId = desc.Id;
        }
        catch (OperationCanceledException)
        {
            _logger?.Info(AppLogCategory.Audio, $"Model download cancelled for '{desc.Id}'.");
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Error(AppLogCategory.Audio, $"Model download failed for '{desc.Id}'.", ex);
            throw;
        }
        finally
        {
            ModelsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    ///     Whether the installed copy of a PINNED model still matches its hash.
    ///     <para>
    ///         <c>null</c> means "I cannot answer" — no such id, nothing on disk,
    ///         or an entry with no pin — and is deliberately not <c>false</c>:
    ///         "unverifiable" and "corrupt" must not be the same value, or a
    ///         caller deletes a perfectly good model because nobody had hashed
    ///         it.
    ///     </para>
    /// </summary>
    public async Task<bool?> VerifyInstalledAsync(string id, CancellationToken ct = default)
    {
        var desc = FindById(id);
        if (desc == null) return null;
        var path = GetLocalPath(desc);
        if (!File.Exists(path)) return null;

        if (string.IsNullOrWhiteSpace(desc.Sha256)) return null;

        try
        {
            return desc.Onnx is { } entry
                ? await OnnxModelCatalog.VerifyAsync(entry, path, ct).ConfigureAwait(false)
                : await VerifiedDownload.VerifyFileAsync(
                    path, desc.Sha256, desc.ExactBytes > 0 ? desc.ExactBytes : null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An UNREADABLE file is a file presumed intact, not a corrupt one.
            _logger?.Warning(AppLogCategory.Audio,
                $"Could not read '{path}' to verify model '{id}': {ex.Message}");
            return null;
        }
    }

    /// <summary>The downloader reports per chunk; the UI wants ~12 fps. One
    /// throttle, with the final report always let through so a bar never parks
    /// short of the end.</summary>
    private static IProgress<DownloadProgress>? Throttled(IProgress<ModelDownloadProgress>? progress)
    {
        if (progress == null) return null;
        var last = 0L;
        return new Progress<DownloadProgress>(p =>
        {
            var now = Environment.TickCount64;
            var complete = p.TotalBytes is > 0 && p.BytesReceived >= p.TotalBytes.Value;
            if (!complete && now - last < 80) return;
            last = now;
            progress.Report(StemModelDownloadPlan.Adapt(p));
        });
    }

    public void Delete(string id)
    {
        var desc = FindById(id);
        if (desc == null) return;
        var p = GetLocalPath(desc);
        try
        {
            if (File.Exists(p)) File.Delete(p);
            _logger?.Info(AppLogCategory.Audio, $"Deleted model '{desc.Id}'.");
            // If we just deleted the active model, clear the setting.
            if (string.Equals(ActiveModelId, desc.Id, StringComparison.OrdinalIgnoreCase))
                ActiveModelId = string.Empty;
        }
        catch (Exception ex)
        {
            _logger?.Error(AppLogCategory.Audio, $"Failed to delete model '{desc.Id}'.", ex);
        }
        ModelsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Registers a user-supplied model so it appears in the catalog alongside
    /// the built-in entries. Accepts a direct HuggingFace download URL
    /// (e.g. <c>https://huggingface.co/owner/repo/resolve/main/file.onnx</c>)
    /// or any HTTPS link that streams a single file. The kind is inferred
    /// from the filename so the right inference path is selected:
    /// <c>htdemucs*</c> → HtdemucsOnnx, <c>*roformer*</c> → BsRoformerOnnx,
    /// <c>*mdx*</c> or other <c>.onnx</c> → MdxNetOnnx, <c>.bin</c>/<c>.ggml</c> → DemucsGgml.
    /// </summary>
    /// <summary>
    ///     The same import with the converter left to its default, for a caller
    ///     that cannot name it.
    ///     <para>
    ///         The full overload's optional <c>converter</c> parameter types
    ///         against the JuceSharp assembly, and binding a call to it emits a
    ///         reference to that assembly even when the argument is omitted —
    ///         which the presentation layer is not allowed to have
    ///         (<c>AssemblyLayeringTests</c>).
    ///     </para>
    /// </summary>
    public Task<StemModelDescriptor> ImportCheckpointFromUrlAsync(
        string ckptUrl,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken ct = default) =>
        ImportCheckpointAsync(ckptUrl, progress: progress, ct: ct);

    /// <summary>
    /// Downloads a PyTorch <c>.ckpt</c> + sibling <c>.yaml</c> config and runs
    /// the bundled <see cref="CkptToOnnxConverter"/> to produce a runtime-loadable
    /// <c>.onnx</c>. The resulting ONNX file is registered as a custom catalog
    /// entry. <paramref name="yamlUrl"/> is optional — if omitted, we try
    /// derived URLs (replacing the extension with .yaml/.yml) so HF mirrors
    /// like <c>jarredou/BS-ROFO-SW-Fixed</c> "just work" with the .ckpt URL.
    /// </summary>
    public async Task<StemModelDescriptor> ImportCheckpointAsync(
        string ckptUrl,
        string? yamlUrl = null,
        string? displayName = null,
        int? sourceCount = null,
        IProgress<ModelDownloadProgress>? progress = null,
        CkptToOnnxConverter? converter = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ckptUrl))
            throw new ArgumentException("URL is required.", nameof(ckptUrl));
        if (!Uri.TryCreate(ckptUrl, UriKind.Absolute, out var uri))
            throw new ArgumentException($"Not a valid URL: '{ckptUrl}'.", nameof(ckptUrl));

        var ckptName = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(ckptName) || !ckptName.Contains('.'))
            throw new ArgumentException($"Could not derive filename from '{ckptUrl}'.", nameof(ckptUrl));

        Directory.CreateDirectory(ModelsDirectory);
        var ckptPath = Path.Combine(ModelsDirectory, ckptName);
        var onnxName = Path.GetFileNameWithoutExtension(ckptName) + ".onnx";
        var onnxPath = Path.Combine(ModelsDirectory, onnxName);
        var yamlPath = Path.Combine(ModelsDirectory, Path.GetFileNameWithoutExtension(ckptName) + ".yaml");

        await DownloadToAsync(ckptUrl, ckptPath, progress, ct).ConfigureAwait(false);

        var yamlCandidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(yamlUrl))
        {
            yamlCandidates.Add(yamlUrl);
        }
        else
        {
            var basePart = ckptUrl[..(ckptUrl.LastIndexOf('.'))];
            yamlCandidates.Add(basePart + ".yaml");
            yamlCandidates.Add(basePart + ".yml");
            yamlCandidates.Add(basePart + ".json");
        }
        foreach (var candidate in yamlCandidates)
        {
            try
            {
                await DownloadToAsync(candidate, yamlPath, null, ct).ConfigureAwait(false);
                _logger?.Info(AppLogCategory.Audio, $"Fetched companion config '{candidate}'.");
                break;
            }
            // A missing companion config is the NORMAL case for most repos, so
            // every transport-shaped failure just means "try the next
            // extension". A user Cancel is not one of those and must not be
            // swallowed into a silent skip.
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                _logger?.Info(AppLogCategory.Audio, $"No companion config at '{candidate}': {ex.Message}");
            }
        }

        var conv = converter ?? new CkptToOnnxConverter();
        await conv.ConvertAsync(ckptPath, File.Exists(yamlPath) ? yamlPath : null, onnxPath, progress: null, ct).ConfigureAwait(false);

        // Register the produced ONNX as a custom catalog entry. Reuse the
        // synchronous importer's metadata logic by feeding it a faux URL so
        // the kind/stems inference picks the right backend.
        var fauxUrl = "https://local.imported/" + onnxName;
        var desc = ImportFromUrl(fauxUrl, displayName ?? $"{ckptName} (ckpt → onnx)", sourceCount);
        return desc;
    }

    /// <summary>
    ///     Fetches a loose user-supplied URL (the importer's <c>.ckpt</c> and its
    ///     companion config) to a destination path, through the SAME verified
    ///     downloader every catalogue model uses — atomic rename, resume, and a
    ///     body checked against the server's own length before it lands.
    ///     <para>
    ///         Unpinned by construction: the user typed the URL, so nothing
    ///         published a hash for it. It still matters that the bytes are not
    ///         silently truncated — they go straight into the embedded-Python
    ///         <see cref="CkptToOnnxConverter" />, i.e. into a code generator.
    ///     </para>
    /// </summary>
    private async Task DownloadToAsync(string url, string dest, IProgress<ModelDownloadProgress>? progress,
        CancellationToken ct)
    {
        var request = StemModelDownloadPlan.LooseFileRequest(url, dest);

        await VerifiedDownload.EnsureFileAsync(
            request.Url,
            request.DestinationPath,
            request.Sha256,
            request.ExpectedBytes,
            Throttled(progress),
            log: line => _logger?.Info(AppLogCategory.Audio, line),
            ct).ConfigureAwait(false);
    }

    public StemModelDescriptor ImportFromUrl(string url, string? displayName = null, int? sourceCount = null)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL is required.", nameof(url));
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException($"Not a valid URL: '{url}'.", nameof(url));

        var fileName = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.Contains('.'))
        {
            throw new ArgumentException(
                $"Could not derive a model filename from URL '{url}'. " +
                "Use a direct file link like https://huggingface.co/owner/repo/resolve/main/model.onnx.",
                nameof(url));
        }

        // PyTorch checkpoints (BS-RoFormer / MDX-Net) need a separate
        // download+convert pipeline — see ImportCheckpointAsync. This sync
        // path only handles formats the runtime can load directly.
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is ".ckpt" or ".pt" or ".pth" or ".safetensors")
        {
            throw new NotSupportedException(
                $"'{fileName}' is a PyTorch checkpoint ({ext}). Use ImportCheckpointAsync " +
                "(or the 'Import .ckpt' button in Settings → Stem Models) which downloads " +
                "the checkpoint + matching .yaml and runs the bundled ckpt→onnx converter. " +
                "Conversion runs inside DotCharter's self-contained embedded Python " +
                "environment (under %LOCALAPPDATA%/JuceSharp/python-embed) — you do NOT " +
                "need to install Python, PyTorch, or anything else on your system. The " +
                "first .ckpt import will trigger a one-time bootstrap of the embedded " +
                "interpreter (~5% of progress) before conversion begins.");
        }

        if (ext is ".yaml" or ".yml" or ".json" or ".md")
        {
            throw new ArgumentException(
                $"'{fileName}' is a config / readme, not a model. Point the importer at the .onnx file URL.",
                nameof(url));
        }

        var (kind, defaultStems) = InferKind(fileName);
        var stems = sourceCount ?? defaultStems;

        var idBase = Path.GetFileNameWithoutExtension(fileName)
            .ToLowerInvariant().Replace(' ', '-');
        if (uri.Host.Contains("huggingface", StringComparison.OrdinalIgnoreCase))
        {
            // Encode the HF owner into the id so two repos with the same
            // filename don't collide.
            var segments = uri.AbsolutePath.Trim('/').Split('/');
            if (segments.Length >= 2)
                idBase = $"{segments[0]}-{idBase}".ToLowerInvariant();
        }
        var id = "custom-" + Sanitize(idBase);

        var desc = new StemModelDescriptor
        {
            Id = id,
            DisplayName = displayName ?? $"{fileName} (custom)",
            Description = $"User-imported model from {uri.Host}.",
            DownloadUrl = url,
            FileName = fileName,
            ApproxBytes = 0,
            SourceCount = stems,
            Kind = kind,
            HashSource = "UNVERIFIED: a user-imported URL. Nothing published a hash for it, so the " +
                          "download is length-checked and atomically renamed but not authenticated.",
        };

        StemModelCatalog.RegisterCustom(desc);
        _customStore.Value.Save(StemModelCatalog.All.Where(d =>
            !StemModelCatalog.BuiltIn.Any(b => string.Equals(b.Id, d.Id, StringComparison.OrdinalIgnoreCase))));
        _logger?.Info(AppLogCategory.Audio,
            $"Registered custom stem model '{id}' (kind={kind}, stems={stems}, file='{fileName}').");
        ModelsChanged?.Invoke(this, EventArgs.Empty);
        return desc;
    }

    /// <summary>Removes a custom model registration (does not delete its file unless requested).</summary>
    public bool RemoveCustom(string id, bool alsoDeleteFile = true)
    {
        if (StemModelCatalog.BuiltIn.Any(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (alsoDeleteFile) Delete(id);
        var removed = StemModelCatalog.UnregisterCustom(id);
        if (removed)
        {
            _customStore.Value.Save(StemModelCatalog.All.Where(d =>
                !StemModelCatalog.BuiltIn.Any(b => string.Equals(b.Id, d.Id, StringComparison.OrdinalIgnoreCase))));
            ModelsChanged?.Invoke(this, EventArgs.Empty);
        }
        return removed;
    }

    internal static (StemModelKind Kind, int Stems) InferKind(string fileName)
    {
        var name = fileName.ToLowerInvariant();
        if (name.EndsWith(".bin") || name.Contains("ggml"))
            return (StemModelKind.DemucsGgml, name.Contains("6s") ? 6 : 4);
        if (name.Contains("htdemucs") || name.Contains("hdemucs") || name.Contains("demucs"))
            return (StemModelKind.HtdemucsOnnx, name.Contains("6s") ? 6 : 4);
        if (name.Contains("roformer"))
            return (StemModelKind.BsRoformerOnnx, 2);
        // Default: MDX-Net (covers Kim_Vocal, UVR-MDX-NET-*, etc.)
        return (StemModelKind.MdxNetOnnx, 2);
    }

    private static string Sanitize(string s)
    {
        var ok = "abcdefghijklmnopqrstuvwxyz0123456789-_";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(ok.Contains(c) ? c : '-');
        return sb.ToString().Trim('-');
    }

}
