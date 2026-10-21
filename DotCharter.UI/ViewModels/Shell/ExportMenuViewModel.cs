// File exports: Guitar Pro, Editor on Fire, the sheet-music PDF and the
// one-key highway screenshot.
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Sheet;
using DotCharter.Services.Export.Validation;
using DotCharter.Services.Parsing.Eof;
using DotCharter.Tone.UI.ViewModels.Shell;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.UI.ViewModels.Shell;

/// <summary>
///     File ▸ Export (Guitar Pro, Editor on Fire, sheet-music PDF) and View ▸
///     Screenshot, exposed as <c>MainWindowViewModel.Export</c>. Each export
///     snapshots the chart on the UI thread and serializes it on a worker; the
///     save picker and the screenshot file go through the same two storage
///     services File ▸ Open Recent and Attach audio use
///     (<see cref="IFileStorageService" />, <see cref="IExportTargetWriter" />).
/// </summary>
public sealed partial class ExportMenuViewModel : ViewModelBase
{
    private readonly ProjectState _projectState;
    private readonly SheetMusicPaneViewModel _sheetMusicPane;
    private readonly IAppLogger _logger;
    private readonly ILocalizationService _localization;
    private readonly IFileStorageService? _fileStorage;
    private readonly IExportTargetWriter _exportFiles;
    private readonly IDialogs? _dialogs;

    public ExportMenuViewModel(
        ProjectState projectState,
        SheetMusicPaneViewModel sheetMusicPane,
        IAppLogger logger,
        ILocalizationService localization,
        IFileStorageService? fileStorage = null,
        IExportTargetWriter? exportFiles = null,
        IDialogs? dialogs = null)
    {
        _projectState = projectState;
        _sheetMusicPane = sheetMusicPane;
        _logger = logger;
        _localization = localization;
        _fileStorage = fileStorage;
        _exportFiles = exportFiles ?? new ExportTargetWriter();
        _dialogs = dialogs;
    }

    /// <summary>Set by the window: PrintWindow capture of the 3D highway
    /// slot (the pedalboard freeze-frame machinery — the only way to read
    /// back the native swapchain). Null when the window isn't ready.</summary>
    public Func<Avalonia.Media.Imaging.WriteableBitmap?>? HighwayScreenshotProvider { get; set; }

    /// <summary>
    /// Play-only content is never exported: refused before the save picker opens,
    ///     with the guard's localized message. Each exporter refuses again on its own.
    /// </summary>
    private bool RefusedAsPlayOnly()
    {
        var playOnly = PlayOnlyExportGuard.IsPlayOnly(_projectState.ActiveProject) ||
                       PlayOnlyExportGuard.IsPlayOnly(_projectState.ActiveArrangement);
        if (!playOnly) return false;
        _projectState.SetMessage(_localization[PlayOnlyExportGuard.MessageKey], StatusSeverity.Error);
        _logger.Info(AppLogCategory.Project, "Export refused: the chart is play-only.");
        return true;
    }

    /// <summary>Exports the active arrangement level as a Guitar Pro .gp file.</summary>
    [RelayCommand]
    private async Task ExportGuitarPro()
    {
        if (RefusedAsPlayOnly()) return;
        var arrangement = _projectState.ActiveArrangement;
        var level = _projectState.ActiveLevel;
        if (_fileStorage == null || arrangement == null || level == null || _projectState.Beats.Count == 0)
        {
            _projectState.SetMessage(_localization["GpExport_NoChart"]);
            return;
        }

        // The pre-export check the Rocksmith export runs, limited to what a .gp needs (a sound on
        // a string the tuning has): a crafted chart is refused before the picker opens, with the
        // finding the charter can act on.
        var blocking = new ChartExportValidator().ValidateGuitarPro(arrangement, level, _projectState.Beats)
            .FirstOrDefault(f => f.Severity == ExportFindingSeverity.Error);
        if (blocking != null)
        {
            _projectState.SetMessage(_localization.Format(
                "GpExport_Failed", ExportFindingFormatter.FormatMessage(blocking, _localization)), StatusSeverity.Error);
            _logger.Warning(AppLogCategory.Project, $"Guitar Pro export refused: {blocking.MessageKey} ({blocking.Detail}).");
            return;
        }

        var chart = _projectState.ActiveProject?.Chart;
        var baseName = SafeFileName.Make(
            chart?.SongName is { Length: > 0 } song ? $"{song} - {arrangement.Name}" : arrangement.Name);

        var path = await _fileStorage.SaveFilePickerAsync(
            _localization["GpExport_Title"],
            baseName + ".gp",
            new[] { new FilePickerFilter("Guitar Pro", new[] { "*.gp" }) });
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            // Capture-then-worker, the same ordering contract the save path
            // uses: GpExporter.Export serializes the whole arrangement and
            // writes the file synchronously, so running it inline froze the UI
            // thread for the duration. Running it on a worker over the LIVE
            // collections would instead race the editor, so snapshot first —
            // ChartProjectCloner is the single source of truth for that.
            var (arrangementSnapshot, levelSnapshot) = SnapshotForExport(arrangement, level);
            var beats = _projectState.Beats.ToList();
            var songName = chart?.SongName;
            var artist = chart?.Artist;
            await Task.Run(() => DotCharter.Services.Export.GuitarPro.GpExporter.Export(
                arrangementSnapshot, levelSnapshot, beats, path, songName, artist));
            _projectState.SetMessage(_localization.Format(
                "GpExport_Done", Path.GetFileName(path)), StatusSeverity.Success);
            _logger.Info(AppLogCategory.Project, $"Guitar Pro export written. Path='{path}'.");
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Project, $"Guitar Pro export failed. Path='{path}'.", ex);
            _projectState.SetMessage(_localization.Format(
                "GpExport_Failed", ex.Message), StatusSeverity.Error);
        }
    }

    /// <summary>Exports the whole project as an Editor on Fire (.eof) project.</summary>
    [RelayCommand]
    private async Task ExportEditorOnFire()
    {
        if (RefusedAsPlayOnly()) return;
        var project = _projectState.ActiveProject;
        if (_fileStorage == null || project == null || _projectState.Beats.Count == 0)
        {
            _projectState.SetMessage(_localization["EofExport_NoChart"]);
            return;
        }

        var baseName = SafeFileName.Make(project.Chart.SongName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "chart";
        }

        var path = await _fileStorage.SaveFilePickerAsync(
            _localization["EofExport_Title"],
            baseName + ".eof",
            new[] { new FilePickerFilter("Editor on Fire", new[] { "*.eof" }) });
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            // Snapshot, then serialize + write on a worker — see ExportGuitarPro.
            var snapshot = ChartProjectCloner.Clone(project);
            var result = await Task.Run(
                () => DotCharter.Services.Parsing.Eof.EofExporter.ExportToFile(snapshot, path));
            var losses = EofLossSummary.From(result.Report);
            ReportEofExport(project, Path.GetFileName(path), losses);
            _logger.Info(AppLogCategory.Project,
                $"EOF export written. Path='{path}', approximated={losses.Approximated}, dropped={losses.Dropped}" +
                (losses.IsLossless ? "." : $" ({losses.LogLine()})."));
            foreach (var note in result.Report.Notes)
            {
                _logger.Info(AppLogCategory.Project, $"EOF export {note.Kind}: {note.Subject} — {note.Message}");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Project, $"EOF export failed. Path='{path}'.", ex);
            _projectState.SetMessage(_localization.Format(
                "EofExport_Failed", ex.Message), StatusSeverity.Error);
        }
    }

    /// <summary>
    ///     Lossy is a Warning: the file landed, but not everything in it did. The rail says how
    ///     many places, and its Show opens the Chart quality report on the Editor on Fire round
    /// trip, which names each one. Counted in places, as that list counts them. Either
    ///     way this charter uses Editor on Fire, so the report opens with that section expanded for
    /// this project from now on.
    /// </summary>
    internal void ReportEofExport(ChartProject project, string fileName, EofLossSummary losses)
    {
        _projectState.Document.EofRoundTripProject = project;
        if (losses.IsLossless)
        {
            _projectState.SetMessage(_localization.Format("EofExport_Done", fileName), StatusSeverity.Success);
            return;
        }

        var message = _localization.PluralWith("EofExport_DoneLossyOne", "EofExport_DoneLossy",
            losses.Total, fileName, losses.Total);
        if (_dialogs is not { } dialogs)
        {
            _projectState.SetMessage(message, StatusSeverity.Warning);
            return;
        }

        _projectState.SetMessage(message, StatusSeverity.Warning, new StatusMessageAction(
            _localization["EofExport_ShowLosses"],
            () => DialogLaunch.Run(_logger,
                () => dialogs.ShowAsync<IChartQualityReportScreen, bool>(screen => screen.FocusEofRoundTrip = true),
                "chart quality report (EOF round trip)")));
    }

    /// <summary>Exports the active arrangement level as a printable sheet-music PDF (A4/Letter).</summary>
    [RelayCommand]
    private async Task ExportSheetPdf()
    {
        if (RefusedAsPlayOnly()) return;
        var arrangement = _projectState.ActiveArrangement;
        var level = _projectState.ActiveLevel;
        if (_fileStorage == null || arrangement == null || level == null || _projectState.Beats.Count == 0)
        {
            _projectState.SetMessage(_localization["SheetPdf_NoChart"]);
            return;
        }

        var chart = _projectState.ActiveProject?.Chart;
        var baseName = SafeFileName.Make(
            chart?.SongName is { Length: > 0 } song ? $"{song} - {arrangement.Name}" : arrangement.Name);

        var path = await _fileStorage.SaveFilePickerAsync(
            _localization["SheetPdf_Title"],
            baseName + ".pdf",
            new[] { new FilePickerFilter("PDF", new[] { "*.pdf" }) });
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var format = DefaultPageFormat();
            var staves = Math.Clamp(_sheetMusicPane.SheetStaves, 0, 2);
            // Capture-then-worker, the same ordering contract ExportGuitarPro
            // and ExportEditorOnFire use. The alphaTex build inside the exporter
            // now runs on a worker, so handing it the LIVE arrangement and the
            // live Beats collection would let it walk them while the editor
            // mutates them — an ObservableCollection enumerated off-thread
            // throws, and the beat map it reads is the one the whole export is
            // positioned against. ChartProjectCloner is the single source of
            // truth for the snapshot.
            var (arrangementSnapshot, levelSnapshot) = SnapshotForExport(arrangement, level);
            var projectSnapshot = _projectState.ActiveProject is { } live
                ? ChartProjectCloner.Clone(live)
                : null;
            var beatsSnapshot = _projectState.Beats.ToList();
            // ExportToFileAsync owns the stream: it renders into a sibling .tmp
            // and only swaps a COMPLETE document into place. Opening `path`
            // here with FileMode.Create truncated the user's existing PDF the
            // moment the export started, so any failure downstream left a
            // zero-byte file where a good one had been.
            await new SheetPdfExporter(_localization).ExportToFileAsync(
                projectSnapshot, arrangementSnapshot, levelSnapshot, beatsSnapshot, staves, format, path);
            _projectState.SetMessage(_localization.Format(
                "SheetPdf_Done", Path.GetFileName(path)), StatusSeverity.Success);
            _logger.Info(AppLogCategory.Project, $"Sheet PDF export written. Path='{path}'.");
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Project, $"Sheet PDF export failed. Path='{path}'.", ex);
            _projectState.SetMessage(_localization.Format(
                "SheetPdf_Failed", ex.Message), StatusSeverity.Error);
        }
    }

    /// <summary>
    ///     Deep-copies an arrangement + the level being exported so a worker can
    ///     serialize them while the user keeps editing.
    ///     <c>ChartProjectCloner</c> is the single source of truth for
    ///     snapshotting — never hand-roll one. The level is taken BY INDEX out
    ///     of the cloned arrangement rather than cloned separately, so the two
    ///     stay the same object graph.
    /// </summary>
    private static (Arrangement Arrangement, ChartLevel Level) SnapshotForExport(
        Arrangement arrangement, ChartLevel level)
    {
        var levelIndex = arrangement.Levels.IndexOf(level);
        var clonedArrangement = ChartProjectCloner.CloneArrangement(arrangement);
        var clonedLevel = levelIndex >= 0 && levelIndex < clonedArrangement.Levels.Count
            ? clonedArrangement.Levels[levelIndex]
            : ChartProjectCloner.CloneLevel(level);
        return (clonedArrangement, clonedLevel);
    }

    /// <summary>A4 everywhere except the US/Canada Letter regions — a sensible default that
    /// still satisfies "tuned for A4/Letter" without an extra dialog.</summary>
    private static SheetPageFormat DefaultPageFormat()
    {
        try
        {
            var region = System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName;
            return region is "US" or "CA"
                ? SheetPageFormat.Letter
                : SheetPageFormat.A4;
        }
        catch
        {
            return SheetPageFormat.A4;
        }
    }

    /// <summary>v0.32 one-key screenshot: captures the current highway view
    /// to <c>ConfigRoot/screenshots/</c> and confirms via the OSD status
    /// channel. Never throws — a failed capture only costs the screenshot.
    /// <para>
    /// Split across the dispatcher on purpose. The
    /// <see cref="HighwayScreenshotProvider" /> call is a <c>PrintWindow</c>
    /// read-back of the native swapchain and MUST stay on the UI thread; the
    /// PNG encode + file write of a full-window bitmap must not — it was
    /// hundreds of milliseconds of frozen window between the keypress and the
    /// OSD confirmation, all of it deflate and fsync. Both
    /// <c>SetMessage</c> calls stay on the dispatcher (the OSD channel is
    /// UI-thread state), which is what the <c>await</c> in the middle buys.
    /// The generated command is still <c>CaptureHighwayScreenshotCommand</c>:
    /// the toolkit strips the <c>Async</c> suffix, and three call sites name it
    /// (the keybind registrar, the command palette and the View menu).
    /// </para></summary>
    [RelayCommand]
    private async Task CaptureHighwayScreenshotAsync()
    {
        try
        {
            // UI thread only: this reads back the native swapchain.
            var shot = HighwayScreenshotProvider?.Invoke();
            if (shot == null)
            {
                _projectState.SetMessage(_localization["Status_ScreenshotFailed"], StatusSeverity.Error);
                return;
            }

            var dir = Path.Combine(
                DotCharter.Tone.Infrastructure.Storage.WritablePaths.ConfigRoot, "screenshots");
            var path = Path.Combine(dir, $"dotcharter-{DateTime.Now:yyyyMMdd-HHmmss}.png");

            await Task.Run(() =>
            {
                using (shot)
                using (var stream = _exportFiles.Create(path))
                {
                    shot.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }
            });

            _projectState.SetMessage(_localization.Format("Status_ScreenshotSaved", Path.GetFileName(path)), StatusSeverity.Success);
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Application, $"Screenshot failed: {ex.Message}");
            _projectState.SetMessage(_localization["Status_ScreenshotFailed"], StatusSeverity.Error);
        }
    }
}
