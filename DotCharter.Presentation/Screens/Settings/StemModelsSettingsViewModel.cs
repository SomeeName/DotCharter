using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Navigation;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     Settings ▸ Stem models: the models folder, the catalogue, a custom-model
///     import, and three blocks with their own lifetimes
///     (<see cref="StemBackendViewModel" />, <see cref="GpuRuntimeViewModel" />,
///     <see cref="StemCacheViewModel" />).
///     <para>
///         Nothing here persists through the footer. The backend knobs save the
///         moment they change and everything else is a verb, so this screen's
///         change set is empty by construction.
///     </para>
/// </summary>
public sealed partial class StemModelsSettingsViewModel : ObservableObject,
    IStemModelsSettingsScreen, ISettingsTab, ISettingsChangeSource, IScreenLifecycle, IDisposable
{
    private readonly StemModelManager _manager;
    private readonly ILocalizationService _localization;
    private readonly IUiDispatcher _ui;
    private readonly StemBackendViewModel _backend;
    private readonly GpuRuntimeViewModel _gpu;
    private readonly StemCacheViewModel _cache;
    private readonly IAppLogger? _logger;
    private readonly IDialogs _dialogs;
    private readonly List<StemModelRowViewModel> _models = [];
    private readonly EventHandler _onModelsChanged;
    private int _probed;

    public StemModelsSettingsViewModel(
        StemModelManager manager,
        ILocalizationService localization,
        IUiDispatcher ui,
        StemModelsBlocks blocks,
        IDialogs dialogs,
        IAppLogger? logger = null)
    {
        _manager = manager;
        _localization = localization;
        _ui = ui;
        _backend = blocks.Backend;
        _gpu = blocks.Gpu;
        _cache = blocks.Cache;
        _logger = logger;
        _dialogs = dialogs;
        ImportCheckpoint = (url, progress) => _manager.ImportCheckpointFromUrlAsync(url, progress);

        _onModelsChanged = (_, _) => _ui.PostNormal(OnModelsChanged);
        _manager.ModelsChanged += _onModelsChanged;


        Load();
    }

    public string ModelsDirectory => _manager.ModelsDirectory;

    public IStemBackendPart Backend => _backend;

    public IGpuRuntimePart Gpu => _gpu;

    public IStemCachePart Cache => _cache;

    public IReadOnlyList<StemModelRowViewModel> Models => _models;

    IReadOnlyList<IStemModelRow> IStemModelsSettingsScreen.Models => _models;

    [ObservableProperty] private bool _isFolderGroupVisible = true;

    [ObservableProperty] private bool _isCustomGroupVisible = true;

    [ObservableProperty] private bool _isCatalogGroupVisible = true;

    [ObservableProperty] private string _customModelUrl = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomModelImportStatus))]
    private string _customModelImportStatus = "";

    [ObservableProperty] private double _customModelImportProgress;

    [ObservableProperty] private bool _isCustomModelImportInProgress;

    public bool HasCustomModelImportStatus => CustomModelImportStatus.Length > 0;

    /// <summary>The download + convert step; a test replaces it to see whether an import was attempted.</summary>
    internal Func<string, IProgress<ModelDownloadProgress>, Task<StemModelDescriptor>> ImportCheckpoint { get; set; }

    ICommand IStemModelsSettingsScreen.ImportCustomModelCommand => ImportCustomModelCommand;

    public string Key => SettingsTabKeys.StemModels;

    public Type? Screen => typeof(IStemModelsSettingsScreen);

    /// <summary>Always empty: every value on this screen persists the moment it changes.</summary>
    public IReadOnlyList<string> UnsavedSettings => [];

    /// <summary>The screen's load path — the footer's Save, Revert and "Reset section" all route through <see cref="Load" />.</summary>
    public void Load()
    {
        _backend.Load();
        RebuildCatalog();
    }

    public void Save()
    {
    }

    public void Revert() => Load();

    public void Reset() => Load();

    public void Rebaseline()
    {
    }

    /// <summary>
    ///     Runs the three machine-state probes once, when the screen is first
    ///     shown. Idempotent on purpose: re-entering re-probes nothing, and each
    ///     block's own Refresh command is how a user asks for a fresh reading.
    /// </summary>
    public void OnShown()
    {
        if (Interlocked.Exchange(ref _probed, 1) != 0) return;
        _ = _backend.RefreshStatusAsync();
        _ = _gpu.RefreshStatusAsync();
        _ = _cache.RefreshUsageAsync();
    }

    public void OnHidden()
    {
    }

    public void Dispose() => _manager.ModelsChanged -= _onModelsChanged;

    /// <summary>A model arriving or leaving changes the catalogue AND what the backend can report.</summary>
    private void OnModelsChanged()
    {
        RebuildCatalog();
        _ = _backend.RefreshStatusAsync();
    }



    private void RebuildCatalog()
    {
        var active = _manager.ActiveModelId;
        _models.Clear();
        foreach (var descriptor in _manager.Catalog)
        {
            var row = new StemModelRowViewModel(_manager, descriptor, _localization, _logger)
            {
                IsActive = string.Equals(active, descriptor.Id, StringComparison.OrdinalIgnoreCase)
            };
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(StemModelRowViewModel.IsActive) || !row.IsActive) return;
                foreach (var other in _models)
                {
                    if (!ReferenceEquals(other, row) && other.IsActive)
                        other.SetActiveSilently(false);
                }

                _manager.ActiveModelId = row.Descriptor.Id;
            };
            _models.Add(row);
        }

        OnPropertyChanged(nameof(Models));
    }

    [RelayCommand]
    private async Task ImportCustomModelAsync()
    {
        var url = CustomModelUrl.Trim();
        if (url.Length == 0)
        {
            CustomModelImportStatus = _localization["Settings_StemModels_CustomNeedsUrl"];
            return;
        }

        // A PyTorch checkpoint goes through the async download + ckpt→onnx
        // conversion pipeline, which bootstraps the embedded interpreter. A
        // format the runtime can already load needs only the cheap synchronous
        // registration.
        var extension = "";
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                extension = Path.GetExtension(Uri.UnescapeDataString(uri.AbsolutePath)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            // Fall through: the synchronous importer surfaces the URL error.
        }

        if (extension is ".ckpt" or ".pt" or ".pth" or ".safetensors")
        {
            await ImportCheckpointAsync(url).ConfigureAwait(true);
            return;
        }

        try
        {
            var descriptor = _manager.ImportFromUrl(url);
            CustomModelImportStatus = _localization.Format(
                "Settings_StemModels_CustomAdded", descriptor.DisplayName, descriptor.SourceCount);
            CustomModelUrl = "";
        }
        catch (Exception ex)
        {
            CustomModelImportStatus = _localization.Format("Settings_Ai_MatchModel_Failed", ex.Message);
            _logger?.Warning(AppLogCategory.Audio, $"Custom model import failed for '{url}': {ex.Message}");
        }
    }

    private async Task ImportCheckpointAsync(string url)
    {
        if (IsCustomModelImportInProgress) return;
        if (!await ConfirmPickleAsync(url).ConfigureAwait(true)) return;
        IsCustomModelImportInProgress = true;
        CustomModelImportProgress = 0;
        CustomModelImportStatus = _localization["Settings_StemModels_CustomBootstrapping"];

        var progress = new Progress<ModelDownloadProgress>(p =>
        {
            CustomModelImportProgress = p.Fraction;
            CustomModelImportStatus = p.TotalBytes > 0
                ? $"{p.BytesRead / 1024.0 / 1024.0:0.#} / {p.TotalBytes / 1024.0 / 1024.0:0.#} MB ({p.Fraction * 100:0.#}%)"
                : $"{p.BytesRead / 1024.0 / 1024.0:0.#} MB";
        });
        try
        {
            var descriptor = await ImportCheckpoint(url, progress).ConfigureAwait(true);
            CustomModelImportStatus = _localization.Format(
                "Settings_StemModels_CustomConverted", descriptor.DisplayName, descriptor.SourceCount);
            CustomModelUrl = "";
            CustomModelImportProgress = 1.0;
        }
        catch (CheckpointConsentException ex)
        {
            CustomModelImportStatus = _localization["Settings_StemModels_PickleChanged"];
            _logger?.Warning(AppLogCategory.Audio, $"Checkpoint import refused for '{url}': {ex.Message}");
        }
        catch (Exception ex)
        {
            CustomModelImportStatus = _localization.Format("Settings_Ai_MatchModel_Failed", ex.Message);
            _logger?.Warning(AppLogCategory.Audio, $"Checkpoint import failed for '{url}': {ex.Message}");
        }
        finally
        {
            IsCustomModelImportInProgress = false;
        }
    }

    /// <summary>
    ///     A <c>.ckpt</c> / <c>.pt</c> / <c>.pth</c> is a pickle and can run code when loaded.
    ///     Importing one needs the user to accept that risk for that URL first; the
    ///     answer is remembered per URL (and pinned to the file's hash by the manager).
    ///     <c>.safetensors</c> is not a pickle and is never asked about.
    /// </summary>
    private async Task<bool> ConfirmPickleAsync(string url)
    {
        if (!CheckpointPickleConsent.IsPickleFormat(url) || _manager.PickleConsent.IsGranted(url)) return true;

        var name = Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath))
            : url;
        var allowed = await _dialogs.MessageAsync(
            _localization["Settings_StemModels_PickleConfirmTitle"],
            _localization.Format("Settings_StemModels_PickleConfirmMessage", name, url),
            isConfirmation: true).ConfigureAwait(true);
        if (!allowed)
        {
            CustomModelImportStatus = _localization["Settings_StemModels_PickleDeclined"];
            return false;
        }

        _manager.PickleConsent.Grant(url);
        _logger?.Info(AppLogCategory.Audio, $"User allowed checkpoint '{url}' to run code on load.");
        return true;
    }
}
