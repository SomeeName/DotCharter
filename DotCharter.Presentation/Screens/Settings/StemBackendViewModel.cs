using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Gpu;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     The backend block of Settings ▸ Stem models: the execution provider, the
///     GPU (<c>StemBackendViewModel.Gpu.cs</c>) and the line describing what the
///     resolved pipeline actually is. Every value
///     persists the moment it changes — they are hardware choices, not a form
///     the footer saves.
/// </summary>
public sealed partial class StemBackendViewModel : ObservableObject, IStemBackendPart
{
    private readonly StemModelManager _manager;
    private readonly IConfigurationService _config;
    private readonly ILocalizationService _localization;
    private readonly IStemSeparationService? _separation;
    private readonly IAppLogger? _logger;
    private bool _loading;
    private bool _isSearchHidden;
    private string _storedProvider = "";

    public StemBackendViewModel(
        StemModelManager manager,
        IConfigurationService config,
        ILocalizationService localization,
        IStemSeparationService? separation = null,
        IAppLogger? logger = null,
        IStemGpuEnvironment? gpu = null)
    {
        _manager = manager;
        _config = config;
        _localization = localization;
        _separation = separation;
        _logger = logger;
        _gpu = gpu;

        // Before the hardware is probed (on first show) DirectML is assumed to run
        // where an environment exists; with none there is no DirectML to offer.
        _directMlBlocker = gpu == null ? StemExecutionReason.NoDirectMlInRuntime : null;
        _options = StemExecutionProviderOptions.For(localization, _directMlBlocker);
    }

    public bool IsGroupVisible => !_isSearchHidden;

    public string Hint => _localization["Settings_StemModels_BackendHint"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedExecutionProvider))]
    private IReadOnlyList<StemExecutionProviderOption> _options;

    IReadOnlyList<IStemExecutionProviderOption> IStemBackendPart.ExecutionProviderOptions => Options;

    /// <summary>The entry for <see cref="PreferredExecutionProvider" />. Setting a greyed entry is refused, and the
    /// change notification sends the picker back to the entry in effect.</summary>
    public IStemExecutionProviderOption SelectedExecutionProvider
    {
        get => Options.FirstOrDefault(o => o.Value == PreferredExecutionProvider) ?? Options[0];
        set
        {
            if (value is StemExecutionProviderOption { IsAvailable: true } option)
                PreferredExecutionProvider = option.Value;
            else
                OnPropertyChanged();
        }
    }

    [ObservableProperty] private bool _isExecutionProviderRowVisible = true;

    [ObservableProperty] private bool _isStatusRowVisible = true;

    /// <summary>The stored name (<see cref="StemExecutionProviderOptions" />), always an available one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedExecutionProvider))]
    [NotifyPropertyChangedFor(nameof(IsGpuRowVisible))]
    private string _preferredExecutionProvider = StemExecutionProviderOptions.DirectMl;

    [ObservableProperty] private string _status = "";

    ICommand IStemBackendPart.RefreshCommand => RefreshCommand;

    /// <summary>
    ///     Re-reads the provider and GPU choice from disk. This runs on every open
    ///     of the screen and on "Reset section": a config-backup import replaces
    ///     the file underneath, and a screen still showing the pre-import values
    ///     writes them straight back on the next touch of either picker.
    /// </summary>
    public void Load()
    {
        var config = _config.LoadAppConfig();
        _loading = true;
        try
        {
            _storedProvider = config.StemSeparationExecutionProvider;
            PreferredExecutionProvider = NormalizeProvider(_storedProvider);
            LoadGpuChoice(config.StemSeparationGpuAdapter);
        }
        finally
        {
            _loading = false;
        }
    }

    public void SetRowVisibility(bool executionProvider, bool gpu, bool status, bool group)
    {
        IsExecutionProviderRowVisible = executionProvider;
        SetGpuRowSearchVisibility(gpu);
        IsStatusRowVisible = status;
        if (_isSearchHidden == !group) return;
        _isSearchHidden = !group;
        OnPropertyChanged(nameof(IsGroupVisible));
    }

    /// <summary>
    ///     Probes the hardware (DirectML, the GPUs), then asks the resolved pipeline to
    ///     describe itself. Both initialise native state, so this never runs at construction.
    /// </summary>
    public async Task RefreshStatusAsync()
    {
        await RefreshHardwareAsync().ConfigureAwait(true);
        if (_separation == null)
        {
            Status = _localization["Settings_StemModels_NoService"];
            return;
        }

        try
        {
            Status = await _separation.DescribeBackendAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = _localization.Format("Settings_StemModels_ProbeFailedFmt", ex.Message);
        }
    }

    [RelayCommand]
    private Task Refresh() => RefreshStatusAsync();

    partial void OnPreferredExecutionProviderChanged(string value)
    {
        var normalized = NormalizeProvider(value);
        if (!string.Equals(normalized, value, StringComparison.Ordinal))
        {
            PreferredExecutionProvider = normalized;
            return;
        }

        if (_loading) return;
        _storedProvider = normalized;
        Persist();
    }

    private string NormalizeProvider(string? raw) =>
        StemExecutionProviderOptions.Normalize(raw, _directMlBlocker == null);

    private void Persist()
    {
        try
        {
            var config = _config.LoadAppConfig();
            config.StemSeparationExecutionProvider = _storedProvider;
            _config.SaveAppConfig(config);
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, $"Persisting stem-separation settings failed: {ex.Message}");
        }
    }
}
