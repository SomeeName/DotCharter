using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Import.Youtube;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     File ▸ Audio from YouTube, one instance per open: a link, the one-time rights notice,
///     then the download. The answer is the mp3 on disk; <c>YoutubeAudioLauncher</c> attaches it
///     once the dialog is gone.
/// </summary>
public sealed partial class YoutubeAudioViewModel : DialogViewModel<string?>, IYoutubeAudioScreen, ICancelableDialog, IDisposable
{
    private readonly YoutubeAudioDownload _download;
    private readonly IConfigurationService _config;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger _logger;
    private readonly AsyncRelayCommand _downloadCommand;
    private CancellationTokenSource? _cts;
    private bool _rightsAccepted;
    private bool _isBusy;

    [ObservableProperty] private string _url = "";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressDeterminate;
    [ObservableProperty] private string _progressText = "";

    public YoutubeAudioViewModel(YoutubeAudioDownload download, IConfigurationService config, ILocalizationService localization,
        IAppLogger logger)
    {
        _download = download;
        _config = config;
        _localization = localization;
        _logger = logger;
        _rightsAccepted = config.LoadAppConfig().YoutubeRightsAccepted;
        _downloadCommand = new AsyncRelayCommand(DownloadAsync, CanDownload);
        AcceptRightsCommand = new RelayCommand(AcceptRights);
        CancelCommand = new RelayCommand(() => TryCancel());
    }

    public IAsyncRelayCommand DownloadCommand => _downloadCommand;
    ICommand IYoutubeAudioScreen.DownloadCommand => _downloadCommand;
    public ICommand AcceptRightsCommand { get; }
    public ICommand CancelCommand { get; }

    /// <summary>The one-time rights notice was accepted; persisted in <c>AppConfig.YoutubeRightsAccepted</c>.</summary>
    public bool RightsAccepted => _rightsAccepted;

    public bool ShowToolsMissing => !_download.IsAvailable;
    public bool ShowRightsNotice => _download.IsAvailable && !_rightsAccepted;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) _downloadCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Cancels a download in flight and keeps the dialog open while it unwinds; closes at once otherwise.</summary>
    public bool TryCancel()
    {
        if (!IsBusy)
        {
            Close(null);
            return true;
        }

        if (_cts is { IsCancellationRequested: false } cts)
        {
            _logger.Info(AppLogCategory.Import, "YouTube audio download cancel requested.");
            cts.Cancel();
            ProgressText = _localization["Dialog_YoutubeAudio_Canceling"];
        }

        return false;
    }

    public void Dispose() => _cts?.Cancel();

    private bool CanDownload() => !IsBusy && _rightsAccepted && _download.IsAvailable && !string.IsNullOrWhiteSpace(Url);

    private void AcceptRights()
    {
        _config.Update(config => config.YoutubeRightsAccepted = true);
        _rightsAccepted = true;
        _logger.Info(AppLogCategory.Import, "YouTube rights notice accepted.");
        OnPropertyChanged(nameof(RightsAccepted));
        OnPropertyChanged(nameof(ShowRightsNotice));
        _downloadCommand.NotifyCanExecuteChanged();
    }

    private async Task DownloadAsync()
    {
        if (!CanDownload()) return;
        if (YoutubeUrl.TryNormalize(Url) is not { } url)
        {
            ErrorText = _localization["Dialog_YoutubeAudio_InvalidUrl"];
            return;
        }

        var cts = _cts = new CancellationTokenSource();
        IsBusy = true;
        ErrorText = "";
        Progress = 0;
        IsProgressDeterminate = false;
        ProgressText = _localization["Dialog_YoutubeAudio_Downloading"];
        _logger.Info(AppLogCategory.Import, $"YouTube audio download requested. Url='{url}'.");
        try
        {
            var result = await _download.DownloadAsync(url, new Progress<double>(ShowProgress), cts.Token);
            if (result is { Success: true, FilePath: { } path }) Close(path);
            else ErrorText = _localization.Format("Dialog_YoutubeAudio_Failed", result.Error ?? "");
        }
        catch (OperationCanceledException)
        {
            _logger.Info(AppLogCategory.Import, "YouTube audio download canceled by the user.");
            Close(null);
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Import, "YouTube audio download failed.", ex);
            ErrorText = _localization.Format("Dialog_YoutubeAudio_Failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
            IsProgressDeterminate = false;
            ProgressText = "";
            _cts = null;
            cts.Dispose();
        }
    }

    // yt-dlp reports on a pool thread; Progress<T> posts each report back to the thread that started the run (the UI thread).
    private void ShowProgress(double fraction)
    {
        if (IsClosed || _cts is not { IsCancellationRequested: false }) return;
        IsProgressDeterminate = true;
        Progress = fraction;
    }

    partial void OnUrlChanged(string value)
    {
        ErrorText = "";
        _downloadCommand.NotifyCanExecuteChanged();
    }
}
