namespace DotCharter.Services.Audio.StemSeparation.Components;

/// <summary>
/// Thin, DI-friendly coordinator for the in-app component manager. It stitches
/// the real environment (the <see cref="StemModelManager"/> for the models
/// directory + active-model wiring, the app base directory, and a filesystem
/// seam) onto the pure <see cref="ComponentCatalog"/> / <see cref="ComponentInventory"/>.
/// All decision logic lives in those pure pieces; this class only reads the
/// environment and applies activation.
/// </summary>
public sealed class ComponentManager
{
    private readonly StemModelManager _models;
    private readonly IComponentFileSystem _fs;
    private readonly IComponentDownloader _downloader;
    private readonly IAppLogger? _logger;
    private readonly Func<string> _appBaseDirectory;
    private readonly ComponentInventory _inventory;

    public ComponentManager(
        StemModelManager models,
        IComponentFileSystem? fs = null,
        IComponentDownloader? downloader = null,
        IAppLogger? logger = null,
        Func<string>? appBaseDirectory = null)
    {
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _fs = fs ?? SystemComponentFileSystem.Instance;
        _downloader = downloader ?? DisabledComponentDownloader.Instance;
        _logger = logger;
        // The install's top folder: the bundled tools/ live there on a split
        // install, and it is the exe's own folder on a flat one.
        _appBaseDirectory = appBaseDirectory ?? (() => InstallLocation.InstallRoot);
        _inventory = new ComponentInventory(_fs);
    }

    /// <summary>The (this-pass disabled) download seam the UI binds against.</summary>
    public IComponentDownloader Downloader => _downloader;

    /// <summary>Directory htdemucs / ONNX model weights are cached in.</summary>
    public string ModelsDirectory => _models.ModelsDirectory;

    /// <summary>Build the current definition set from the live environment.</summary>
    public IReadOnlyList<ComponentDefinition> BuildDefinitions() =>
        ComponentCatalog.Build(
            _models.ModelsDirectory,
            _models.ActiveModelId,
            _models.Catalog,
            _appBaseDirectory());

    /// <summary>Scan every known component and report its status.</summary>
    public IReadOnlyList<ComponentStatus> Scan() => _inventory.Scan(BuildDefinitions());

    /// <summary>Verify one component by id (presence + size, optionally hash).</summary>
    public ComponentVerification? Verify(string id, bool computeHash = false)
    {
        var def = FindDefinition(id);
        return def == null ? null : _inventory.Verify(def, computeHash);
    }

    /// <summary>
    /// Activate a downloaded stem model: points the active-model selection (and
    /// therefore <c>DOTCHARTER_HTDEMUCS_MODEL</c> resolution) at it. Only
    /// installed stem models can be activated; returns false otherwise.
    /// </summary>
    public bool Activate(string id)
    {
        var status = ScanOne(id);
        if (status == null || status.Definition.Category != ComponentCategory.StemModel || !status.IsInstalled)
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"Component '{id}' cannot be activated (not an installed stem model).");
            return false;
        }

        _models.ActiveModelId = id;
        _logger?.Info(AppLogCategory.Audio, $"Component manager activated stem model '{id}'.");
        return true;
    }

    /// <summary>Scan a single component by id (null when unknown).</summary>
    public ComponentStatus? ScanOne(string id)
    {
        var def = FindDefinition(id);
        return def == null ? null : _inventory.ScanOne(def);
    }

    private ComponentDefinition? FindDefinition(string id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : BuildDefinitions().FirstOrDefault(d =>
                string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
}
