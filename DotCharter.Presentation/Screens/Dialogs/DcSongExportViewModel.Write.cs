// The song-package export's pickers, the write and its outcome.
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Shared;
using DotCharter.Services.Export;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Services.Package.DcSong;
using DotCharter.Services.State;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Presentation.Screens.Dialogs;

public sealed partial class DcSongExportViewModel
{
    private CancellationTokenSource? _writeCts;
    private ClosableProgress<DcSongWriteProgress>? _writeProgress;
    private string _writtenPath = "";

    [ObservableProperty] private bool _isWriting;
    [ObservableProperty] private double _progressFraction;
    [ObservableProperty] private string _progressMessage = "";
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string _resultMessage = "";
    [ObservableProperty] private string _errorMessage = "";

    public ObservableCollection<string> ResultWarnings { get; } = [];
    IReadOnlyList<string> IDcSongExportScreen.ResultWarnings => ResultWarnings;

    public bool HasError => ErrorMessage.Length > 0;

    /// <summary>The request the dialog's choices make, or null when there is nothing to write.</summary>
    internal DcSongWriteRequest? BuildRequest()
    {
        if (_project is null || _probe is null) return null;
        var output = OutputPath.Trim();
        if (!output.EndsWith(DcSongFormat.Extension, StringComparison.OrdinalIgnoreCase)) output += DcSongFormat.Extension;
        return new DcSongWriteRequest
        {
            Project = _project,
            AudioPath = _audioPath,
            OutputPath = output,
            Lossless = IsLossless,
            CoverPath = string.IsNullOrWhiteSpace(CoverPath) ? null : CoverPath.Trim(),
            BannerPath = string.IsNullOrWhiteSpace(BannerPath) ? null : BannerPath.Trim(),
            ProjectDirectory = _projectDirectory.Length == 0 ? null : _projectDirectory,
            Stems = IncludeStems ? _probe.Stems.Select(s => new DcSongStemSource(s.Name, s.SourcePath)).ToList() : [],
            ArrangementIndexes = Arrangements.Where(a => a.IsSelected).Select(a => a.Index).ToList()
        };
    }

    private async Task ExportAsync()
    {
        if (_project is not { } project || !CanExport()) return;
        Assemble(project);
        if (BuildRequest() is not { } request) return;

        ClearOutcome();
        ProgressFraction = 0;
        ProgressMessage = "";
        IsWriting = true;
        _writeCts = new CancellationTokenSource();
        var loc = _edit.Localization;
        var target = new ClosableProgress<DcSongWriteProgress>(p =>
        {
            ProgressFraction = p.Fraction;
            ProgressMessage = loc.Format("Dialog_Export_ProgressStep", p.Step, p.TotalSteps, loc[p.ResourceKey]);
        });
        _writeProgress = target;
        _edit.Logger.Info(AppLogCategory.Project,
            $".dcsong export started. Output='{request.OutputPath}', lossless={request.Lossless}, " +
            $"arrangements=[{string.Join(", ", request.ArrangementIndexes ?? [])}], stems={request.Stems.Count}.");
        try
        {
            var result = await _io.Exporter.WriteAsync(request, new Progress<DcSongWriteProgress>(target.Report), _writeCts.Token);
            _writtenPath = result.OutputPath;
            ResultMessage = loc.Format("Dialog_DcSong_Saved", result.OutputPath, Megabytes(result.PackageBytes));
            foreach (var warning in result.Warnings) ResultWarnings.Add(loc[DcSongWriteWarnings.ResourceKey(warning)]);
            HasResult = true;
            _edit.State.SetMessage(loc.Format("Status_DcSong_Saved", result.OutputPath), StatusSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            _edit.Logger.Info(AppLogCategory.Project, ".dcsong export canceled by the user.");
            _edit.State.SetMessage(loc["Dialog_DcSong_Canceled"], StatusSeverity.Warning);
        }
        catch (DcSongWriteException ex)
        {
            _edit.Logger.Warning(AppLogCategory.Project, $".dcsong export refused ({ex.Reason}): {ex.Message}");
            ErrorMessage = loc[ex.ResourceKey];
        }
        catch (PlayOnlyContentException ex)
        {
            // Play-only content is never exported.
            _edit.Logger.Info(AppLogCategory.Project, ex.Message);
            ErrorMessage = loc[PlayOnlyExportGuard.MessageKey];
        }
        catch (Exception ex)
        {
            _edit.Logger.Error(AppLogCategory.Project, $".dcsong export failed. Output='{request.OutputPath}'.", ex);
            ErrorMessage = loc.Format("Dialog_DcSong_WriteFailed", ex.Message);
        }
        finally
        {
            EndWrite();
        }
    }

    private void EndWrite()
    {
        _writeProgress?.Close();
        _writeProgress = null;
        IsWriting = false;
        ProgressFraction = 0;
        ProgressMessage = "";
        _writeCts?.Dispose();
        _writeCts = null;
    }

    private void ClearOutcome()
    {
        HasResult = false;
        _writtenPath = "";
        ResultMessage = "";
        ResultWarnings.Clear();
        ErrorMessage = "";
    }

    private async Task<string?> PickImageAsync(string titleKey, string current)
    {
        var config = _edit.Config.LoadAppConfig();
        var path = await _io.Storage.OpenFilePickerAsync(_edit.Localization[titleKey],
            [new FilePickerFilter("PNG / JPEG", ["*.png", "*.jpg", "*.jpeg"]), FilePickerFilters.AllFiles],
            string.IsNullOrWhiteSpace(current) ? config.LastAlbumArtDir : current);
        if (string.IsNullOrEmpty(path)) return null;
        _edit.Config.RememberDirectory(path, (c, d) => c.LastAlbumArtDir = d);
        return path;
    }

    private async Task BrowseOutputAsync()
    {
        var suggested = string.IsNullOrWhiteSpace(OutputPath)
            ? DefaultFileName(_project?.Chart.Artist ?? "", _project?.Chart.SongName ?? "")
            : Path.GetFileName(OutputPath);
        var path = await _io.Storage.SaveFilePickerAsync(_edit.Localization["Picker_DcSong_Save"], suggested,
            [new FilePickerFilter("DotCharter song", ["*" + DcSongFormat.Extension])], OutputPath);
        if (string.IsNullOrEmpty(path)) return;
        OutputPath = path;
    }

    private void OpenOutputFolder()
    {
        if (_writtenPath.Length == 0) return;
        if (!_io.Shell.RevealInFileManager(_writtenPath))
            _edit.Logger.Warning(AppLogCategory.Project, $"Could not reveal the .dcsong '{_writtenPath}'.");
    }

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnIsWritingChanged(bool value)
    {
        _cancelWrite.NotifyCanExecuteChanged();
        OnGateChanged();
    }
}
