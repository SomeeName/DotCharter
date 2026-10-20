using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Infrastructure.Shell;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;
using DotCharter.Services.Update;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     "Update available": shows the release and drives download → verify →
///     stage → restart through <see cref="IAppUpdateService" />. One instance per open.
/// </summary>
public sealed class UpdateAvailableViewModel : DialogViewModel<bool>, IUpdateAvailableScreen,
    IDialogSetup<UpdateAvailableRequest>, ICancelableDialog
{
    private readonly IAppUpdateService _updates;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger _logger;
    private readonly IApplicationCloser? _closer;
    private CancellationTokenSource? _download;
    private UpdateInfo? _update;
    private string _versionText = "";
    private string _releaseNotes = "";
    private string _statusText = "";
    private bool _isDownloading;
    private double _downloadProgress;
    private bool _canInstall;

    public UpdateAvailableViewModel(IAppUpdateService updates, ILocalizationService localization, IAppLogger logger,
        IApplicationCloser? closer = null)
    {
        _updates = updates;
        _localization = localization;
        _logger = logger;
        _closer = closer;
        LaterCommand = new RelayCommand(() => TryCancel());
        DownloadAndInstallCommand = new AsyncRelayCommand(DownloadAndInstallAsync);
    }

    public string VersionText
    {
        get => _versionText;
        private set => SetProperty(ref _versionText, value);
    }

    public string ReleaseNotes
    {
        get => _releaseNotes;
        private set
        {
            if (SetProperty(ref _releaseNotes, value)) OnPropertyChanged(nameof(HasReleaseNotes));
        }
    }

    public bool HasReleaseNotes => _releaseNotes.Length > 0;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsDownloading
    {
        get => _isDownloading;
        private set => SetProperty(ref _isDownloading, value);
    }

    public double DownloadProgress
    {
        get => _downloadProgress;
        private set => SetProperty(ref _downloadProgress, value);
    }

    public bool CanInstall
    {
        get => _canInstall;
        private set => SetProperty(ref _canInstall, value);
    }

    public ICommand LaterCommand { get; }

    public ICommand DownloadAndInstallCommand { get; }

    public void Configure(UpdateAvailableRequest request)
    {
        _update = request.Update;
        VersionText = _localization.Format("Update_DialogVersionLine",
            request.Update.TagName, _updates.CurrentVersionText);
        ReleaseNotes = request.Update.ReleaseNotes.Trim();
        var block = _updates.GetApplyBlockReason();
        CanInstall = block == UpdateApplyBlockReason.None;
        StatusText = UpdateBlockReasonText.Describe(_localization, block);
    }

    /// <summary>Later, and Esc: stops a download in flight and closes.</summary>
    public bool TryCancel()
    {
        _download?.Cancel();
        Close(false);
        return true;
    }

    private async Task DownloadAndInstallAsync()
    {
        if (_update is not { } update || IsDownloading) return;

        var block = _updates.GetApplyBlockReason();
        if (block != UpdateApplyBlockReason.None)
        {
            CanInstall = false;
            StatusText = UpdateBlockReasonText.Describe(_localization, block);
            return;
        }

        _download?.Cancel();
        var cts = _download = new CancellationTokenSource();
        IsDownloading = true;
        DownloadProgress = 0;
        StatusText = _localization.Format("Update_Downloading", 0);
        try
        {
            // Progress captures the UI sync context, so the bound state moves on the UI thread.
            var progress = new Progress<double>(fraction =>
            {
                DownloadProgress = fraction * 100.0;
                StatusText = _localization.Format("Update_Downloading", (int)Math.Round(fraction * 100.0));
            });
            var zipPath = await _updates.DownloadUpdateAsync(update, progress, cts.Token);

            StatusText = _localization["Update_RestartingToApply"];
            // Closes the app only once the updater is confirmed started; on failure
            // the app stays up with the payload staged and says why.
            var failure = UpdateApplyLauncher.ApplyAndCloseOrReport(
                () => _updates.ApplyDownloadedUpdate(zipPath), CloseApplication, _localization, _logger,
                $"Update {update.TagName}");
            if (failure != null)
            {
                StatusText = failure;
                return;
            }

            _logger.Info(AppLogCategory.Application, $"Update {update.TagName} staged — closing for restart.");
        }
        catch (OperationCanceledException)
        {
            StatusText = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Application, "Update download/apply failed.", ex);
            StatusText = _localization.Format("Update_DownloadFailed", ex.Message);
        }
        finally
        {
            IsDownloading = false;
            if (_download == cts) _download = null;
            cts.Dispose();
        }
    }

    private void CloseApplication()
    {
        if (_closer is null)
            throw new InvalidOperationException("No application closer is wired; the staged update cannot be applied.");
        _closer.Close();
    }
}
