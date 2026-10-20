using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Services.Audio.StemSeparation.Gpu;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     The CUDA runtime an earlier version downloaded, in Settings ▸ Stem models.
///     Separation runs on DirectML now, so the row exists only to let the user
///     reclaim the ~1.4 GB still on disk: it shows while the folder holds bytes and
///     offers nothing but Remove. Measuring walks a folder, so it is taken when the
///     screen is shown rather than when it is built.
/// </summary>
public sealed partial class GpuRuntimeViewModel : ObservableObject, IGpuRuntimePart
{
    private readonly ILocalizationService _localization;
    private readonly IUiDispatcher _ui;
    private readonly LegacyGpuRuntimeFolder? _folder;
    private readonly IAppLogger? _logger;
    private bool _isSearchHidden;
    private long _bytes;

    public GpuRuntimeViewModel(
        ILocalizationService localization,
        IUiDispatcher ui,
        LegacyGpuRuntimeFolder? folder = null,
        IAppLogger? logger = null)
    {
        _localization = localization;
        _ui = ui;
        _folder = folder;
        _logger = logger;
    }

    /// <summary>Only while there is something to reclaim — or a failed removal to explain.</summary>
    public bool IsGroupVisible => _folder != null && !_isSearchHidden && (_bytes > 0 || HasDetail);

    [ObservableProperty] private bool _isStatusRowVisible = true;

    [ObservableProperty] private string _status = "";

    [ObservableProperty] private bool _canRemove;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    [NotifyPropertyChangedFor(nameof(IsGroupVisible))]
    private string _detail = "";

    public bool HasDetail => Detail.Length > 0;

    ICommand IGpuRuntimePart.RemoveCommand => RemoveCommand;

    ICommand IGpuRuntimePart.RefreshCommand => RefreshCommand;

    public void SetRowVisibility(bool status, bool group)
    {
        IsStatusRowVisible = status;
        if (_isSearchHidden == !group) return;
        _isSearchHidden = !group;
        OnPropertyChanged(nameof(IsGroupVisible));
    }

    public async Task RefreshStatusAsync()
    {
        if (_folder == null)
        {
            await _ui.InvokeAsync(() => Apply(0)).ConfigureAwait(true);
            return;
        }

        try
        {
            var bytes = await Task.Run(_folder.InstalledBytes).ConfigureAwait(true);
            await _ui.InvokeAsync(() => Apply(bytes)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, $"Measuring the old CUDA runtime failed: {ex.Message}");
        }
    }

    private void Apply(long bytes)
    {
        _bytes = bytes;
        Status = bytes > 0
            ? _localization.Format("Settings_StemModels_GpuAccelLegacyStatusFmt", FormatSize(bytes))
            : "";
        CanRemove = bytes > 0;
        OnPropertyChanged(nameof(IsGroupVisible));
    }

    private static string FormatSize(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):0.0} GB"
        : $"{bytes / (double)(1L << 20):0} MB";

    [RelayCommand]
    private Task Refresh() => RefreshStatusAsync();

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (_folder == null) return;
        CanRemove = false;
        try
        {
            await Task.Run(_folder.Remove).ConfigureAwait(true);
            Detail = "";
        }
        catch (Exception ex)
        {
            _logger?.Error(AppLogCategory.Audio, $"Removing the old CUDA runtime failed: {ex.Message}", ex);
            Detail = _localization.Format("Settings_Ai_MatchModel_Failed", ex.Message);
        }
        finally
        {
            await RefreshStatusAsync().ConfigureAwait(true);
        }
    }
}
