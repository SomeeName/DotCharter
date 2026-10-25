using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Services.Audio.Lyrics;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>One lyric model on Settings ▸ AI &amp; models: its state and its three verbs.</summary>
public sealed partial class LyricModelRowViewModel : ObservableObject, ILyricModelRow
{
    private readonly string _nameKey;
    private readonly ILyricModelDownloadService _models;
    private readonly ILocalizationService _localization;
    private readonly IUiDispatcher _ui;
    private readonly AsyncRelayCommand _download;
    private readonly AsyncRelayCommand _repair;
    private readonly RelayCommand _retryGpu;
    private LyricModelStatus? _status;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private bool _isShown = true;

    public LyricModelRowViewModel(string key, string id, string nameKey, ILyricModelDownloadService models,
        ILocalizationService localization, IUiDispatcher ui)
    {
        Key = key;
        Id = id;
        _nameKey = nameKey;
        _models = models;
        _localization = localization;
        _ui = ui;
        _download = new AsyncRelayCommand(() => InstallAsync(repair: false), () => !IsBusy && !IsInstalled);
        _repair = new AsyncRelayCommand(() => InstallAsync(repair: true), () => !IsBusy && IsInstalled);
        _retryGpu = new RelayCommand(RetryGpu, () => IsCpuFallback);
    }

    public string Key { get; }

    public string Id { get; }

    public string Name => _localization[_nameKey];

    public string Description => _localization[_nameKey + "_Desc"];

    public bool IsInstalled => _status?.IsInstalled == true;

    public string StatusText => IsInstalled
        ? _localization["Settings_LyricModels_Installed"]
        : _localization.Format("Settings_LyricModels_Missing", _status?.Megabytes ?? 0);

    public string ProviderText => (_status?.Runner ?? LyricModelRunner.NotRunYet) switch
    {
        LyricModelRunner.Gpu => _localization.Format("Settings_LyricModels_RunsOnGpu", _status?.ProviderName ?? "GPU"),
        LyricModelRunner.Cpu => _localization["Settings_LyricModels_RunsOnCpu"],
        LyricModelRunner.CpuFallback => _localization["Settings_LyricModels_CpuFallback"],
        _ => _localization["Settings_LyricModels_NotRunYet"]
    };

    public bool IsCpuFallback => _status?.Runner == LyricModelRunner.CpuFallback;

    public ICommand DownloadCommand => _download;

    public ICommand RepairCommand => _repair;

    public ICommand RetryGpuCommand => _retryGpu;

    /// <summary>Takes a fresh state from the service and raises everything that reads it.</summary>
    public void Apply(LyricModelStatus? status)
    {
        _status = status;
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ProviderText));
        OnPropertyChanged(nameof(IsCpuFallback));
        NotifyCommands();
    }

    partial void OnIsBusyChanged(bool value) => NotifyCommands();

    private void NotifyCommands()
    {
        _download.NotifyCanExecuteChanged();
        _repair.NotifyCanExecuteChanged();
        _retryGpu.NotifyCanExecuteChanged();
    }

    private async Task InstallAsync(bool repair)
    {
        IsBusy = true;
        ProgressText = "";
        var progress = new Progress<string>(line => _ui.Post(() => ProgressText = line));
        var installed = await _models.InstallAsync(Id, repair, progress).ConfigureAwait(true);
        IsBusy = false;
        ProgressText = installed ? "" : _localization["Settings_LyricModels_Failed"];
        Refresh();
    }

    private void RetryGpu()
    {
        _models.RetryGpu(Id);
        Refresh();
    }

    private void Refresh() => Apply(_models.Models.FirstOrDefault(s => s.Id == Id));
}
