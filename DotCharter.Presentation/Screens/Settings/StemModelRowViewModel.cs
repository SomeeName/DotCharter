using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>One catalogue row: what the model is, whether it is on disk, and the four verbs over it.</summary>
public sealed partial class StemModelRowViewModel : ObservableObject, IStemModelRow
{
    private readonly StemModelManager _manager;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger? _logger;
    private CancellationTokenSource? _cts;

    public StemModelRowViewModel(
        StemModelManager manager,
        StemModelDescriptor descriptor,
        ILocalizationService localization,
        IAppLogger? logger = null)
    {
        _manager = manager;
        _localization = localization;
        _logger = logger;
        Descriptor = descriptor;
        RefreshState();
    }

    public StemModelDescriptor Descriptor { get; }

    public string DisplayName => Descriptor.DisplayName;

    public string Description => Descriptor.DescriptionKey is { } key ? _localization[key] : Descriptor.Description;

    public string SourceLabel => _localization.Format("Settings_StemModels_StemCount", Descriptor.SourceCount);

    public string SizeLabel => Descriptor.ApproxBytes <= 0
        ? "—"
        : $"{Descriptor.ApproxBytes / 1024.0 / 1024.0:0.#} MB";

    public string DownloadedSizeLabel
    {
        get
        {
            if (!IsInstalled) return "";
            var bytes = _manager.GetInstalledSize(Descriptor.Id);
            return bytes <= 0
                ? ""
                : _localization.Format("Settings_StemModels_OnDisk", $"{bytes / 1024.0 / 1024.0:0.#} MB");
        }
    }

    [ObservableProperty] private bool _isInstalled;

    [ObservableProperty] private bool _isActive;

    [ObservableProperty] private bool _isDownloading;

    [ObservableProperty] private double _progressFraction;

    [ObservableProperty] private string _progressText = "";

    /// <summary>Whether the catalogue row can be downloaded now: a model on the app's private
    /// releases needs the GitHub sign-in. Read again on every <see cref="RefreshState" />.</summary>
    [ObservableProperty] private StemModelDownloadBlock _downloadBlock;

    public bool CanDownload => !IsInstalled && !IsDownloading && DownloadBlock == StemModelDownloadBlock.None;

    public bool IsDownloadUnavailable => !IsInstalled && !IsDownloading && DownloadBlock != StemModelDownloadBlock.None;

    public string DownloadUnavailableReason => DownloadBlock switch
    {
        StemModelDownloadBlock.NeedsGitHubSignIn when IsDownloadUnavailable =>
            _localization["Settings_StemModels_NeedsGitHubSignIn"],
        _ => "",
    };

    public bool CanCancel => IsDownloading;

    public bool CanDelete => IsInstalled && !IsDownloading;

    public bool CanSetActive => IsInstalled;

    /// <summary>True for a model the user imported — only those can leave the catalogue.</summary>
    public bool IsCustom => !StemModelCatalog.BuiltIn.Any(
        b => string.Equals(b.Id, Descriptor.Id, StringComparison.OrdinalIgnoreCase));

    ICommand IStemModelRow.DownloadCommand => DownloadCommand;

    ICommand IStemModelRow.CancelCommand => CancelCommand;

    ICommand IStemModelRow.DeleteCommand => DeleteCommand;

    ICommand IStemModelRow.RemoveCustomCommand => RemoveCustomCommand;

    public void RefreshState()
    {
        IsInstalled = _manager.IsInstalled(Descriptor.Id);
        DownloadBlock = _manager.DownloadBlock(Descriptor.Id);
        OnPropertyChanged(nameof(DownloadedSizeLabel));
    }

    /// <summary>Clears the flag without re-entering the "one active model" reconciliation that set it.</summary>
    internal void SetActiveSilently(bool value)
    {
        if (IsActive == value) return;
#pragma warning disable MVVMTK0034
        SetProperty(ref _isActive, value, nameof(IsActive));
#pragma warning restore MVVMTK0034
    }

    partial void OnDownloadBlockChanged(StemModelDownloadBlock value)
    {
        _ = value;
        RaiseDownloadAvailability();
    }

    private void RaiseDownloadAvailability()
    {
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(IsDownloadUnavailable));
        OnPropertyChanged(nameof(DownloadUnavailableReason));
    }

    partial void OnIsInstalledChanged(bool value)
    {
        _ = value;
        RaiseDownloadAvailability();
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanSetActive));
        OnPropertyChanged(nameof(DownloadedSizeLabel));
    }

    partial void OnIsDownloadingChanged(bool value)
    {
        _ = value;
        RaiseDownloadAvailability();
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanDelete));
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (IsDownloading || IsInstalled || DownloadBlock != StemModelDownloadBlock.None) return;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsDownloading = true;
        ProgressFraction = 0;
        ProgressText = _localization["Settings_Ai_MatchModel_Connecting"];

        var progress = new Progress<ModelDownloadProgress>(p =>
        {
            ProgressFraction = p.Fraction;
            ProgressText = p.TotalBytes > 0
                ? $"{p.BytesRead / 1024.0 / 1024.0:0.#} / {p.TotalBytes / 1024.0 / 1024.0:0.#} MB ({p.Fraction * 100:0.#}%)"
                : $"{p.BytesRead / 1024.0 / 1024.0:0.#} MB";
        });
        try
        {
            await _manager.DownloadAsync(Descriptor.Id, progress, _cts.Token).ConfigureAwait(true);
            ProgressText = _localization["Settings_StemModels_StateInstalled"];
        }
        catch (OperationCanceledException)
        {
            ProgressText = _localization["Settings_Ai_MatchModel_Cancelled"];
        }
        catch (Exception ex)
        {
            _logger?.Error(AppLogCategory.Audio, $"Stem model download failed: {ex.Message}", ex);
            ProgressText = _localization.Format("Settings_Ai_MatchModel_Failed", ex.Message);
        }
        finally
        {
            IsDownloading = false;
            RefreshState();
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { /* the download's finally already disposed it */ }
    }

    [RelayCommand]
    private void Delete()
    {
        if (!IsInstalled || IsDownloading) return;
        _manager.Delete(Descriptor.Id);
        RefreshState();
        if (IsActive) SetActiveSilently(false);
    }

    [RelayCommand]
    private void RemoveCustom()
    {
        if (!IsCustom) return;
        _manager.RemoveCustom(Descriptor.Id);
    }
}
