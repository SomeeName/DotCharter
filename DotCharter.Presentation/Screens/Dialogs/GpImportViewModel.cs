using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Project;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Import.GuitarPro;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.Logging;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     File ▸ Import ▸ Guitar Pro. One instance per open, on the track list the
///     launcher read. Parsing the score is seconds of work, so it runs off the
///     UI thread; applying it touches the live project, so <see cref="GpImportApplier" />
///     does that on it.
/// </summary>
public sealed class GpImportViewModel : DialogViewModel<bool>, IGpImportScreen, IDialogSetup<GpImportRequest>,
    ICancelableDialog
{
    private readonly GpImportApplier _applier;
    private readonly IFileStorageService _storage;
    private readonly IConfigurationService _config;
    private readonly ProjectState _state;
    private readonly IAppLogger _logger;
    private readonly ILocalizationService _localization;
    private readonly AsyncRelayCommand _import;
    private readonly Func<string, List<int>, GpImportSettings, GpImportResult> _parse;
    private IReadOnlyList<TrackRow> _tracks = [];
    private IReadOnlyList<Arrangement> _arrangements = [];
    private string _filePath = "";
    private GpImportMode _mode = GpImportMode.AddNew;
    private string _oldFilePath = "";
    private string _errorMessage = "";
    private bool _isBusy;
    private CancellationTokenSource? _cts;

    /// <summary>The <c>parse</c> is replaceable so a test can hold a parse in flight or make one throw.</summary>
    public GpImportViewModel(GpImportApplier applier, IFileStorageService storage, IConfigurationService config,
        ProjectState state, IAppLogger logger, ILocalizationService localization, Func<string, List<int>, GpImportSettings, GpImportResult>? parse = null)
    {
        _parse = parse ?? new GpImporter().ImportTracksWithMetadata;
        _applier = applier;
        _storage = storage;
        _config = config;
        _state = state;
        _logger = logger;
        _localization = localization;
        _import = new AsyncRelayCommand(ImportAsync, () => !IsBusy && _tracks.Any(t => t.IsSelected));
        PickOldFileCommand = new AsyncRelayCommand(PickOldFileAsync);
        CancelCommand = new RelayCommand(() => TryCancel());
    }

    public bool IsAddNewMode
    {
        get => _mode == GpImportMode.AddNew;
        set => SetMode(GpImportMode.AddNew, value);
    }

    public bool IsTechniqueOnlyMode
    {
        get => _mode == GpImportMode.TechniqueOnly;
        set => SetMode(GpImportMode.TechniqueOnly, value);
    }

    public bool IsDiffCompareMode
    {
        get => _mode == GpImportMode.DiffCompare;
        set => SetMode(GpImportMode.DiffCompare, value);
    }

    public bool RequiresOldFile => _mode == GpImportMode.DiffCompare;

    public string OldFilePath
    {
        get => _oldFilePath;
        private set => SetProperty(ref _oldFilePath, value);
    }

    public ICommand PickOldFileCommand { get; }

    public bool RequiresTargetSelection => _mode != GpImportMode.AddNew;

    public bool HasTracks => _tracks.Count > 0;

    public IReadOnlyList<IGpTrackRow> Tracks => _tracks;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) _import.NotifyCanExecuteChanged();
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public ICommand ImportCommand => _import;

    public ICommand CancelCommand { get; }

    internal GpImportMode Mode => _mode;

    public void Configure(GpImportRequest request)
    {
        _filePath = request.FilePath;
        _arrangements = [.. _state.ActiveProject?.Arrangements ?? []];
        var names = _arrangements.Select(a => a.Name).ToList();
        _tracks = [.. request.Tracks.Select(t => new TrackRow(t.Id, t.Name, names, OnTrackChanged))];
        OnPropertyChanged(nameof(Tracks));
        OnPropertyChanged(nameof(HasTracks));
        _import.NotifyCanExecuteChanged();
        _logger.Info(AppLogCategory.Import,
            $"Guitar Pro track list loaded from '{_filePath}'. Tracks={_tracks.Count}: {string.Join(", ", _tracks.Select(t => $"{t.Id}:{t.Name}"))}.");
    }

    /// <summary>A parse in flight is cancelled instead: it cannot be interrupted, but nothing of it is applied.</summary>
    public bool TryCancel()
    {
        if (IsBusy)
        {
            _logger.Info(AppLogCategory.Import, $"Guitar Pro import cancellation requested. File='{_filePath}'.");
            _cts?.Cancel();
            return false;
        }

        if (!IsClosed) _logger.Info(AppLogCategory.Import, $"Guitar Pro import canceled. File='{_filePath}'.");
        Close(false);
        return true;
    }

    private void SetMode(GpImportMode mode, bool on)
    {
        if (!on || _mode == mode) return;
        _mode = mode;
        OnPropertyChanged(nameof(IsAddNewMode));
        OnPropertyChanged(nameof(IsTechniqueOnlyMode));
        OnPropertyChanged(nameof(IsDiffCompareMode));
        OnPropertyChanged(nameof(RequiresOldFile));
        OnPropertyChanged(nameof(RequiresTargetSelection));
    }

    private void OnTrackChanged() => _import.NotifyCanExecuteChanged();

    private async Task PickOldFileAsync()
    {
        var path = await _storage.OpenFilePickerAsync(_localization["Picker_SelectGpFile"],
            [FilePickerFilters.GuitarPro, FilePickerFilters.AllFiles], _config.LoadAppConfig().LastGpDir);
        if (string.IsNullOrEmpty(path)) return;
        OldFilePath = path;
        _config.RememberDirectory(path, (c, d) => c.LastGpDir = d);
    }

    private async Task ImportAsync()
    {
        if (IsBusy) return;
        ErrorMessage = "";
        var selected = _tracks.Where(t => t.IsSelected).ToList();
        var ids = selected.Select(t => t.Id).ToList();
        var (mode, filePath, oldFilePath) = (_mode, _filePath, _oldFilePath);
        _logger.Info(AppLogCategory.Import,
            $"Guitar Pro import requested. Mode={mode}, file='{filePath}', oldFile='{oldFilePath}', selectedTrackIds={string.Join(",", ids)}.");
        if (mode == GpImportMode.DiffCompare && string.IsNullOrEmpty(oldFilePath))
        {
            ErrorMessage = _localization["Status_GpImport_DiffNeedsOldFile"];
            return;
        }

        var settings = GpImportSettings.FromConfig(_config.LoadAppConfig());
        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsBusy = true;
        try
        {
            var parsed = await Task.Run(() =>
            {
                var newer = _parse(filePath, ids, settings);
                var older = mode == GpImportMode.DiffCompare ? _parse(oldFilePath, ids, settings) : null;
                return (Newer: newer, Older: older);
            }, cts.Token);
            cts.Token.ThrowIfCancellationRequested();

            _applier.Apply(mode, filePath, [.. selected.Select(t => new GpTrackTarget(t.Name, TargetOf(t)))],
                parsed.Newer, parsed.Older);
        }
        catch (OperationCanceledException)
        {
            _logger.Info(AppLogCategory.Import,
                $"Guitar Pro import cancelled before anything was applied. File='{filePath}'.");
            Close(false);
            return;
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Import,
                $"Guitar Pro import failed. Mode={mode}, file='{filePath}', selectedTrackIds={string.Join(",", ids)}.", ex);
            ErrorMessage = _localization.Format("Status_GpImportFailed", ex.Message);
            return;
        }
        finally
        {
            IsBusy = false;
            _cts = null;
        }

        Close(true);
    }

    private Arrangement? TargetOf(TrackRow track) =>
        track.TargetIndex >= 0 && track.TargetIndex < _arrangements.Count ? _arrangements[track.TargetIndex] : null;

    /// <summary>One track of the score; chosen by default, merging into its same-named arrangement when there is one.</summary>
    internal sealed class TrackRow : ObservableObject, IGpTrackRow
    {
        private readonly Action _changed;
        private bool _isSelected = true;
        private int _targetIndex;

        public TrackRow(int id, string name, IReadOnlyList<string> targets, Action changed)
        {
            Id = id;
            Name = name;
            Targets = targets;
            _changed = changed;
            var sameName = targets.ToList().FindIndex(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));
            _targetIndex = sameName >= 0 ? sameName : targets.Count > 0 ? 0 : -1;
        }

        public int Id { get; }

        public string Name { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (SetProperty(ref _isSelected, value)) _changed();
            }
        }

        public IReadOnlyList<string> Targets { get; }

        public int TargetIndex
        {
            get => _targetIndex;
            set => SetProperty(ref _targetIndex, value);
        }
    }
}
