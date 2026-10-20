using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;
using DotCharter.Services.Parsing.Xml;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     The Rocksmith XML progress card. It reads the document OFF the UI thread
///     against a SNAPSHOT of the host beat map, and merges it ON it.
/// </summary>
public sealed class RsXmlImportViewModel : DialogViewModel<bool>, IRsXmlImportScreen,
    IDialogSetup<RsXmlImportRequest>, ICancelableDialog
{
    private readonly ProjectState _state;
    private readonly IChartEditorSurface _editor;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger _logger;
    private readonly RsXmlImporter _importer = new();
    private CancellationTokenSource? _cts;
    private string _fileName = "";
    private bool _isBusy;
    private string _errorMessage = "";

    public RsXmlImportViewModel(ProjectState state, IChartEditorSurface editor, ILocalizationService localization,
        IAppLogger logger)
    {
        _state = state;
        _editor = editor;
        _localization = localization;
        _logger = logger;
        CloseCommand = new RelayCommand(() => TryCancel());
    }

    public string FileName
    {
        get => _fileName;
        private set => SetProperty(ref _fileName, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public ICommand CloseCommand { get; }

    /// <summary>The merge this open started; a test awaits it.</summary>
    public Task Import { get; private set; } = Task.CompletedTask;

    public void Configure(RsXmlImportRequest request) => Import = ImportAsync(request.FilePath);

    /// <summary>During a run this only trips the token and stays open; the read ends by closing the card.</summary>
    public bool TryCancel()
    {
        if (IsBusy)
        {
            _logger.Info(AppLogCategory.Import, $"RS XML import cancellation requested. File='{FileName}'.");
            _cts?.Cancel();
            return false;
        }

        Close(false);
        return true;
    }

    private async Task ImportAsync(string filePath)
    {
        FileName = Path.GetFileName(filePath);
        var project = _state.ActiveProject;
        if (project == null)
        {
            _logger.Warning(AppLogCategory.Import, $"RS XML import requested but no active project. File='{filePath}'.");
            ErrorMessage = _localization["Status_RsXmlImportNoProject"];
            return;
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsBusy = true;
        try
        {
            var hostBeats = project.Beats.ToList();
            var plan = await Task.Run(() => RsXmlImporter.Prepare(filePath, hostBeats), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            Report(_importer.Apply(project, plan), filePath);
            _editor.RequestRedraw();
            Close(true);
        }
        catch (OperationCanceledException)
        {
            _logger.Info(AppLogCategory.Import, $"RS XML import cancelled before anything was merged. File='{filePath}'.");
            Close(false);
        }
        catch (Exception ex)
        {
            // The card stays up with the reason — it is the only surface this operation has.
            _logger.Error(AppLogCategory.Import, $"RS XML import failed. File='{filePath}'.", ex);
            ErrorMessage = _localization.Format("Status_RsXmlImportFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
            _cts = null;
        }
    }

    private void Report(RsXmlImportResult result, string filePath)
    {
        switch (result.Kind)
        {
            case RsXmlImportKind.Song:
                _logger.Info(AppLogCategory.Import,
                    $"Imported RS XML arrangement '{result.AddedArrangementName}' from '{filePath}'. ReplacedHostBeats={result.ReplacedHostBeats}.");
                _state.SetMessage(_localization.Format("Status_RsXmlImportedSong", result.AddedArrangementName ?? ""));
                if (_state.ActiveArrangement == null && _state.ActiveProject?.Arrangements.Count > 0)
                    _state.ActiveArrangement = _state.ActiveProject.Arrangements[^1];
                break;
            case RsXmlImportKind.Vocals:
                _logger.Info(AppLogCategory.Import,
                    $"Imported RS XML vocal track ({result.AddedVocalPhrases} phrases) from '{filePath}'.");
                _state.SetMessage(_localization.Plural("Status_RsXmlImportedVocalsOne", "Status_RsXmlImportedVocals",
                    result.AddedVocalPhrases));
                break;
            case RsXmlImportKind.Showlights:
                _logger.Info(AppLogCategory.Import,
                    $"Imported RS XML showlights ({result.AddedShowlights} entries) from '{filePath}'.");
                _state.SetMessage(_localization.Plural("Status_RsXmlImportedShowlightsOne",
                    "Status_RsXmlImportedShowlights", result.AddedShowlights));
                break;
            default:
                _logger.Warning(AppLogCategory.Import, $"RS XML import produced unknown result kind. File='{filePath}'.");
                break;
        }
    }
}
