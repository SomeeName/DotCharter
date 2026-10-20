using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Model;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Shell;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     The "Match model" block of Settings ▸ AI: install, describe and remove
///     the trained rig estimator and its pre-rendered tone library. A part
///     rather than a screen, owned by <c>AiSettingsViewModel</c>.
/// </summary>
public sealed partial class MatchModelViewModel : ObservableObject, IMatchModelPart, IDisposable
{
    private readonly IRigEstimatorModelDownloadService? _downloader;
    private readonly Func<IRigEstimator?>? _estimator;
    private readonly ILocalizationService _localization;
    private readonly IShellLauncher? _shell;
    private readonly IAppLogger? _logger;
    private readonly IUiDispatcher? _ui;
    private int _probe;
    private CancellationTokenSource? _modelCts;
    private CancellationTokenSource? _libraryCts;
    private bool _isModelRowVisible = true;
    private bool _isLibraryRowVisible = true;
    private bool _isSearchHidden;

    public MatchModelViewModel(
        ILocalizationService localization,
        IRigEstimatorModelDownloadService? downloader = null,
        Func<IRigEstimator?>? estimator = null,
        IShellLauncher? shell = null,
        IAppLogger? logger = null,
        IUiDispatcher? ui = null)
    {
        _localization = localization;
        _downloader = downloader;
        _estimator = estimator;
        _shell = shell;
        _logger = logger;
        _ui = ui;
    }

    /// <summary>The loadability probe in flight, for a test to wait on.</summary>
    internal Task? PendingProbe { get; private set; }

    public bool IsGroupVisible => _downloader != null && !_isSearchHidden;

    public bool IsModelRowVisible { get => _isModelRowVisible; private set => SetProperty(ref _isModelRowVisible, value); }

    public bool IsLibraryRowVisible { get => _isLibraryRowVisible; private set => SetProperty(ref _isLibraryRowVisible, value); }

    [ObservableProperty] private string _modelStatus = "";

    [ObservableProperty] private bool _modelIsError;

    [ObservableProperty] private string _libraryStatus = "";

    [ObservableProperty] private bool _libraryIsError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadModel))]
    private bool _modelInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadLibrary))]
    private bool _libraryInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadModel))]
    [NotifyCanExecuteChangedFor(nameof(DownloadModelCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelModelDownloadCommand))]
    private bool _isModelDownloading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadLibrary))]
    [NotifyCanExecuteChangedFor(nameof(DownloadLibraryCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelLibraryDownloadCommand))]
    private bool _isLibraryDownloading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModelDetail))]
    private string _modelDetail = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLibraryDetail))]
    private string _libraryDetail = "";

    public bool HasModelDetail => ModelDetail.Length > 0;

    public bool HasLibraryDetail => LibraryDetail.Length > 0;

    public bool CanDownloadModel => _downloader != null && !ModelInstalled && !IsModelDownloading;

    public bool CanDownloadLibrary => _downloader != null && !LibraryInstalled && !IsLibraryDownloading;

    public string ModelDirectory => _downloader?.ModelDirectory ?? "";

    public int ModelMegabytes => _downloader?.ApproximateMegabytes ?? 0;

    public int LibraryMegabytes => _downloader?.LibraryApproximateMegabytes ?? 0;

    ICommand IMatchModelPart.DownloadModelCommand => DownloadModelCommand;

    ICommand IMatchModelPart.CancelModelDownloadCommand => CancelModelDownloadCommand;

    ICommand IMatchModelPart.DownloadLibraryCommand => DownloadLibraryCommand;

    ICommand IMatchModelPart.CancelLibraryDownloadCommand => CancelLibraryDownloadCommand;

    ICommand IMatchModelPart.OpenFolderCommand => OpenFolderCommand;

    ICommand IMatchModelPart.RefreshCommand => RefreshStatusCommand;

    /// <summary>
    ///     Recomputes both rows. Installed and USABLE are different questions:
    ///     file presence answers the first, and the estimator answers the second
    ///     only when the files are already there — asking it builds an ONNX
    ///     session, which is exactly the work the feature needs anyway by then.
    /// </summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(IsGroupVisible));
        OnPropertyChanged(nameof(ModelDirectory));
        OnPropertyChanged(nameof(ModelMegabytes));
        RefreshLibrary();

        if (_downloader is null)
        {
            ModelInstalled = false;
            ModelStatus = "";
            ModelIsError = false;
            return;
        }

        ModelInstalled = _downloader.IsInstalled;
        if (!ModelInstalled)
        {
            ModelIsError = false;
            ModelStatus = _localization.Format("Settings_Ai_MatchModel_StateMissing", ModelMegabytes);
            return;
        }

        var info = _downloader.InstalledModel;
        if (ProbeLoadable()) return;

        ModelIsError = false;
        ModelStatus = info is null
            ? _localization["Settings_Ai_MatchModel_StateInstalled"]
            : _localization.Format("Settings_Ai_MatchModel_StateInstalledDetail",
                info.ModelId, info.CheckpointEpoch, info.BytesOnDisk / (1024 * 1024));

        // Surfaced rather than hidden: an export off the synthetic set has knob
        // labels that carry no signal by construction, and a user whose guesses
        // are nonsense should be able to see why without reading a log.
        if (info?.TrainedOnCrudeSmokeSet == true)
            ModelStatus += " " + _localization["Settings_Ai_MatchModel_SmokeSetWarning"];
    }

    /// <summary>
    ///     Whether the installed model actually loads. Asking the estimator builds its ONNX
    ///     session, several seconds the first time, so with a dispatcher the question goes to a
    ///     pool thread and only a NO comes back to the row; without one (a test) it is asked here.
    /// </summary>
    /// <returns>True when the answer is already in, and it is NO.</returns>
    private bool ProbeLoadable()
    {
        if (_estimator is null) return false;
        if (_ui is null)
        {
            if (_estimator()?.IsAvailable != false) return false;
            MarkUnloadable();
            return true;
        }

        var probe = ++_probe;
        PendingProbe = Task.Run(() => _estimator()?.IsAvailable).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && t.Result == false)
            {
                _ui.Post(() =>
                {
                    if (probe == _probe && ModelInstalled) MarkUnloadable();
                });
            }
        }, TaskScheduler.Default);
        return false;
    }

    private void MarkUnloadable()
    {
        ModelIsError = true;
        ModelStatus = _localization["Settings_Ai_MatchModel_StateUnloadable"];
    }

    /// <summary>File presence only — whether it is the RIGHT library for the DI in hand is the matcher's header check.</summary>
    public void RefreshLibrary()
    {
        OnPropertyChanged(nameof(LibraryMegabytes));
        if (_downloader is null)
        {
            LibraryInstalled = false;
            LibraryStatus = "";
            return;
        }

        LibraryInstalled = _downloader.IsLibraryInstalled;
        if (!LibraryInstalled)
        {
            LibraryStatus = _localization.Format("Settings_Ai_MatchLibrary_StateMissing", LibraryMegabytes);
            return;
        }

        LibraryStatus = _localization.Format("Settings_Ai_MatchLibrary_StateInstalled",
            _downloader.InstalledLibraryMegabytes);
    }

    public void SetRowVisibility(bool model, bool library, bool group)
    {
        IsModelRowVisible = model;
        IsLibraryRowVisible = library;
        if (_isSearchHidden == !group) return;
        _isSearchHidden = !group;
        OnPropertyChanged(nameof(IsGroupVisible));
    }

    public void Dispose()
    {
        _modelCts?.Dispose();
        _libraryCts?.Dispose();
    }

    [RelayCommand(CanExecute = nameof(CanDownloadModel))]
    private async Task DownloadModelAsync()
    {
        if (_downloader is null || IsModelDownloading) return;
        _modelCts?.Dispose();
        _modelCts = new CancellationTokenSource();

        IsModelDownloading = true;
        ModelIsError = false;
        ModelDetail = _localization["Settings_Ai_MatchModel_Connecting"];

        var outcome = await RunAsync(
            progress => _downloader.EnsureInstalledAsync(progress, _modelCts.Token),
            step => ModelDetail = step).ConfigureAwait(true);

        IsModelDownloading = false;

        // The refresh runs FIRST and the outcome is written over it: the other
        // order clobbers, because the not-installed branch clears the error flag
        // and a failed download then reports itself as a plain "not installed".
        Refresh();
        Report(outcome, detail => ModelDetail = detail, error => ModelIsError = error);
    }

    [RelayCommand(CanExecute = nameof(CanDownloadLibrary))]
    private async Task DownloadLibraryAsync()
    {
        if (_downloader is null || IsLibraryDownloading) return;
        _libraryCts?.Dispose();
        _libraryCts = new CancellationTokenSource();

        IsLibraryDownloading = true;
        LibraryIsError = false;
        LibraryDetail = _localization["Settings_Ai_MatchModel_Connecting"];

        var outcome = await RunAsync(
            progress => _downloader.EnsureLibraryInstalledAsync(progress, _libraryCts.Token),
            step => LibraryDetail = step).ConfigureAwait(true);

        IsLibraryDownloading = false;
        RefreshLibrary();
        Report(outcome, detail => LibraryDetail = detail, error => LibraryIsError = error);
    }

    private async Task<(bool Cancelled, string? Failure)> RunAsync(
        Func<IProgress<string>, Task<RigEstimatorModelInstallResult>> install, Action<string> onStep)
    {
        try
        {
            var result = await install(new SynchronousProgress(onStep)).ConfigureAwait(true);
            return result.Installed
                ? (false, null)
                : (false, _localization.Format("Settings_Ai_MatchModel_Failed", result.Reason ?? ""));
        }
        catch (OperationCanceledException)
        {
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger?.Error(AppLogCategory.Audio, "Match-model download failed.", ex);
            return (false, _localization.Format("Settings_Ai_MatchModel_Failed", ex.Message));
        }
    }

    private void Report((bool Cancelled, string? Failure) outcome, Action<string> setDetail, Action<bool> setError)
    {
        if (outcome.Cancelled)
        {
            setDetail(_localization["Settings_Ai_MatchModel_Cancelled"]);
            setError(false);
        }
        else if (outcome.Failure is not null)
        {
            setDetail(outcome.Failure);
            setError(true);
        }
        else
        {
            setDetail("");
        }
    }

    private bool CanCancelModelDownload => IsModelDownloading;

    [RelayCommand(CanExecute = nameof(CanCancelModelDownload))]
    private void CancelModelDownload() => Cancel(_modelCts);

    private bool CanCancelLibraryDownload => IsLibraryDownloading;

    [RelayCommand(CanExecute = nameof(CanCancelLibraryDownload))]
    private void CancelLibraryDownload() => Cancel(_libraryCts);

    private static void Cancel(CancellationTokenSource? cts)
    {
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { /* already finished */ }
    }

    [RelayCommand]
    private void RefreshStatus() => Refresh();

    /// <summary>
    ///     Opens the model folder, creating it first. Creating it is the point:
    ///     it does not exist before something is installed, and the user this
    ///     link is for is the one placing an export by hand.
    /// </summary>
    [RelayCommand]
    private void OpenFolder()
    {
        var directory = ModelDirectory;
        if (directory.Length == 0) return;
        try
        {
            _shell?.OpenFolder(directory, create: true);
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, $"Could not open '{directory}': {ex.Message}");
            ModelDetail = ex.Message;
            ModelIsError = true;
        }
    }

    /// <summary>Reports on the calling thread, so the caller's own write always lands last.</summary>
    private sealed class SynchronousProgress(Action<string> handler) : IProgress<string>
    {
        public void Report(string value) => handler(value);
    }
}
