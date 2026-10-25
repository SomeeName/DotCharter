using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Import.Eof;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.Logging;
using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     The Editor on Fire track picker. Import parses the ticked tracks OFF the
///     UI thread, then answers them with the audio found beside the file.
/// </summary>
public sealed class EofImportViewModel : DialogViewModel<EofImportSelection?>, IEofImportScreen,
    IDialogSetup<EofImportRequest>, ICancelableDialog
{
    private readonly IExportTargetWriter _files;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger _logger;
    private readonly EofImporter _importer = new();
    private readonly AsyncRelayCommand _import;
    private string _filePath = "";
    private IReadOnlyList<IEofTrackOption> _tracks = [];
    private bool _isBusy;
    private string _errorMessage = "";
    private CancellationTokenSource? _cts;

    public EofImportViewModel(IExportTargetWriter files, ILocalizationService localization, IAppLogger logger)
    {
        _files = files;
        _localization = localization;
        _logger = logger;
        _import = new AsyncRelayCommand(ImportAsync, () => !IsBusy);
        CancelCommand = new RelayCommand(() => TryCancel());
    }

    public IReadOnlyList<IEofTrackOption> AvailableTracks
    {
        get => _tracks;
        private set
        {
            if (SetProperty(ref _tracks, value)) OnPropertyChanged(nameof(HasNoTracks));
        }
    }

    public bool HasNoTracks => _tracks.Count == 0;

    public bool IsBusy
    {
        get => _isBusy;
        set
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

    /// <summary>Only tracks with notes are offered — EOF saves stub Pro Guitar tracks that would import empty.
    /// The vocal track counts lyrics, not notes.</summary>
    public void Configure(EofImportRequest request)
    {
        _filePath = request.FilePath;
        AvailableTracks = request.Tracks.Where(t => t.NoteCount > 0)
            .Select(IEofTrackOption (t) => new TrackOption(t.Id, t.IsVocals
                ? _localization.Plural("Dialog_EofImport_VocalTrack_One", "Dialog_EofImport_VocalTrack_Many", t.NoteCount, t.Name)
                : $"{t.Name} ({t.NoteCount} notes)"))
            .ToList();
        _logger.Info(AppLogCategory.Import,
            $"Editor on Fire track list loaded from '{_filePath}'. Tracks={AvailableTracks.Count}: " +
            string.Join(", ", AvailableTracks.Cast<TrackOption>().Select(t => $"{t.Id}:{t.Name}")) + ".");
    }

    /// <summary>During a parse this only trips the token and stays open; the parse then closes the dialog.</summary>
    public bool TryCancel()
    {
        if (IsBusy)
        {
            _logger.Info(AppLogCategory.Import, $"Editor on Fire import cancellation requested. File='{_filePath}'.");
            _cts?.Cancel();
            return false;
        }

        if (!IsClosed) _logger.Info(AppLogCategory.Import, $"Editor on Fire import canceled. File='{_filePath}'.");
        Close(null);
        return true;
    }

    private async Task ImportAsync()
    {
        if (IsBusy) return;
        ErrorMessage = "";
        var selectedIds = AvailableTracks.Cast<TrackOption>().Where(t => t.IsSelected).Select(t => t.Id).ToList();
        var filePath = _filePath;
        _logger.Info(AppLogCategory.Import,
            $"Editor on Fire project creation requested. File='{filePath}', selectedTrackIds={string.Join(",", selectedIds)}.");
        if (selectedIds.Count == 0)
        {
            _logger.Warning(AppLogCategory.Import,
                $"Editor on Fire project creation confirmed with no selected tracks. File='{filePath}'.");
            Close(null);
            return;
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        EofImportResult result;
        IsBusy = true;
        try
        {
            result = await Task.Run(() => _importer.ImportTracksWithMetadata(filePath, selectedIds), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            _logger.Info(AppLogCategory.Import,
                $"Editor on Fire import cancelled before anything was applied. File='{filePath}'.");
            Close(null);
            return;
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Import,
                $"Editor on Fire import failed. File='{filePath}', selectedTrackIds={string.Join(",", selectedIds)}.", ex);
            ErrorMessage = _localization.Format("Status_EofImportFailed", ex.Message);
            return;
        }
        finally
        {
            IsBusy = false;
            _cts = null;
        }

        var audioPath = EofProjectCreator.FindCompanionAudio(_files, filePath, result.Metadata);
        if (audioPath.Length == 0)
        {
            _logger.Warning(AppLogCategory.Import, $"EOF project creation aborted: no companion audio next to '{filePath}'.");
            ErrorMessage = _localization.Format("Status_EofMissingAudio", filePath);
            return;
        }

        Close(new EofImportSelection(filePath, result, audioPath));
    }

    private sealed class TrackOption(int id, string name) : ObservableObject, IEofTrackOption
    {
        private bool _isSelected = true;

        public int Id { get; } = id;

        public string Name { get; } = name;

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }
}
