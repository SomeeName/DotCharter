using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;
using DotCharter.Presentation.Shared;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     The stem-cache block of Settings ▸ Stem models: how much the durable
///     <c>.dcstems</c> archive occupies, and the one verb that reclaims it.
///     Measuring walks the whole cache directory, so it is taken when the
///     screen is shown rather than when it is built.
/// </summary>
public sealed partial class StemCacheViewModel : ObservableObject, IStemCachePart
{
    private readonly ILocalizationService _localization;
    private readonly IUiDispatcher _ui;
    private readonly StemCacheStore? _store;
    private readonly IAppLogger? _logger;
    private bool _isSearchHidden;

    public StemCacheViewModel(
        ILocalizationService localization,
        IUiDispatcher ui,
        StemCacheStore? store = null,
        IAppLogger? logger = null)
    {
        _localization = localization;
        _ui = ui;
        _store = store;
        _logger = logger;
    }

    public bool IsGroupVisible => !_isSearchHidden;

    [ObservableProperty] private bool _isUsageRowVisible = true;

    [ObservableProperty] private string _usage = "";

    [ObservableProperty] private bool _hasEntries;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    private string _detail = "";

    public bool HasDetail => Detail.Length > 0;

    ICommand IStemCachePart.ClearCommand => ClearCommand;

    ICommand IStemCachePart.RefreshCommand => RefreshCommand;

    public void SetRowVisibility(bool usage, bool group)
    {
        IsUsageRowVisible = usage;
        if (_isSearchHidden == !group) return;
        _isSearchHidden = !group;
        OnPropertyChanged(nameof(IsGroupVisible));
    }

    /// <summary>Re-measures the cache. The walk runs off the dispatcher; only the two writes come back to it.</summary>
    public async Task RefreshUsageAsync()
    {
        if (_store == null)
        {
            await _ui.InvokeAsync(() => { Usage = ""; HasEntries = false; }).ConfigureAwait(true);
            return;
        }

        try
        {
            var usage = await Task.Run(_store.MeasureUsage).ConfigureAwait(true);
            var text = usage.Songs == 0
                ? _localization["Settings_StemModels_CacheEmpty"]
                : _localization.Plural("Settings_StemModels_CacheUsageOne", "Settings_StemModels_CacheUsage",
                    usage.Songs, ByteSizeText.Format(usage.Bytes));
            await _ui.InvokeAsync(() =>
            {
                Usage = text;
                HasEntries = usage.Songs > 0;
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, $"Stem cache measure failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private Task Refresh() => RefreshUsageAsync();

    [RelayCommand]
    private async Task ClearAsync()
    {
        if (_store == null) return;
        try
        {
            var deleted = await Task.Run(_store.Clear).ConfigureAwait(true);
            Detail = _localization.Plural("Settings_StemModels_CacheClearedOne", "Settings_StemModels_CacheCleared", deleted);
        }
        catch (Exception ex)
        {
            _logger?.Error(AppLogCategory.Audio, $"Stem cache clear failed: {ex.Message}", ex);
            Detail = ex.Message;
        }
        finally
        {
            await RefreshUsageAsync().ConfigureAwait(true);
        }
    }
}
