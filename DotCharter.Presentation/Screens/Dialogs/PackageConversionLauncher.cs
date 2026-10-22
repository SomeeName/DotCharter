using DotCharter.Localization;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Services.Plugins;
using DotCharter.Services.State;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     File ▸ Import ▸ Convert PSARC to DotCharter song: pick one or more packages, choose where the
///     <c>.dcsong</c> files go (beside each package, or one folder), convert under a status-bar pill
///     with a cancel, then say what happened. The conversion itself is the plugin's
///     <see cref="IPackageSongConverter" />; a build without one never reaches this class (the menu
///     row and the palette entry are absent) and a stale call is logged and refused.
/// </summary>
public sealed class PackageConversionLauncher(
    IDialogs dialogs,
    IFileStorageService storage,
    IConfigurationService config,
    IStatusBarService statusBar,
    ProjectState state,
    DialogBasics basics,
    IEnumerable<IPackageSongConverter> converters)
{
    public const string TaskId = "import.packages.convert";

    /// <summary>How many failures the summary spells out before it says "and N more".</summary>
    internal const int FailuresListed = 8;

    private readonly ILocalizationService _localization = basics.Localization;
    private readonly IAppLogger _logger = basics.Logger;

    /// <summary>The picker, then <see cref="ConvertAsync" />. Null when nothing was converted (no converter, no files, cancelled).</summary>
    public async Task<PackageConversionReport?> PickAsync()
    {
        var converter = converters.FirstOrDefault();
        if (converter == null)
        {
            _logger.Warning(AppLogCategory.Import, "Package conversion requested but no converter is registered.");
            state.SetMessage(_localization["Status_ConvertUnavailable"], StatusSeverity.Warning);
            return null;
        }

        var appConfig = config.LoadAppConfig();
        var files = await storage.OpenFilesPickerAsync(_localization["Picker_ConvertPackages"],
            [new FilePickerFilter(_localization["Picker_GamePackages"], converter.FilePatterns), FilePickerFilters.AllFiles],
            string.IsNullOrEmpty(appConfig.PsarcLibraryPath) ? appConfig.LastRscpDir : appConfig.PsarcLibraryPath);
        if (files.Count == 0)
        {
            _logger.Info(AppLogCategory.Import, "Package conversion file picker canceled.");
            return null;
        }

        return await ConvertAsync(converter, files);
    }

    /// <summary>
    ///     Asks where the songs go, converts <paramref name="files" /> and reports. Null when the user backed
    ///     out before anything ran or cancelled the run.
    /// </summary>
    public async Task<PackageConversionReport?> ConvertAsync(IPackageSongConverter converter, IReadOnlyList<string> files)
    {
        var beside = await dialogs.MessageAsync(
            _localization["Convert_Prompt_Title"],
            _localization.Format("Convert_Prompt_Message", files.Count),
            isConfirmation: true);
        string? outputDirectory = null;
        if (!beside)
        {
            outputDirectory = await storage.OpenFolderPickerAsync(
                _localization["Picker_ConvertOutputFolder"], Path.GetDirectoryName(files[0]));
            if (string.IsNullOrEmpty(outputDirectory))
            {
                _logger.Info(AppLogCategory.Import, "Package conversion output folder picker canceled.");
                return null;
            }
        }

        PackageConversionReport report;
        using var cancel = new CancellationTokenSource();
        var done = 0;
        statusBar.BeginTask(TaskId, _localization["Status_ConvertingPackages"], indeterminate: true, cancel: cancel.Cancel);
        try
        {
            report = await converter.ConvertAsync(files, outputDirectory, progress: new InlineProgress(item =>
            {
                done++;
                statusBar.BeginTask(TaskId,
                    _localization.Format("Status_ConvertingPackage", Path.GetFileName(item.PackagePath), done),
                    indeterminate: true, cancel: cancel.Cancel);
            }), cancellationToken: cancel.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.Info(AppLogCategory.Import, "Package conversion cancelled.");
            state.SetMessage(_localization["Status_ConvertCancelled"], StatusSeverity.Warning);
            return null;
        }
        finally
        {
            statusBar.CompleteTask(TaskId);
        }

        _logger.Info(AppLogCategory.Import,
            $"Package conversion finished: {report.Packages} package(s), {report.Converted} converted, {report.Skipped} skipped, {report.Failed} failed.");
        await dialogs.MessageAsync(
            _localization[report.Failed == 0 ? "Convert_Summary_Title" : "Convert_Summary_TitleProblems"],
            Summarize(report));
        return report;
    }

    /// <summary>Runs each report where it is made. The status bar posts to the UI itself, and a posting
    /// <see cref="Progress{T}" /> could land a label after the task was completed.</summary>
    private sealed class InlineProgress(Action<PackageConversionItem> report) : IProgress<PackageConversionItem>
    {
        public void Report(PackageConversionItem value) => report(value);
    }

    /// <summary>The summary body: the counts, where the songs went, then the failures with their reasons.</summary>
    internal string Summarize(PackageConversionReport report)
    {
        if (report.Packages == 0 && report.Failed == 0) return _localization["Convert_Summary_NoPackages"];

        var lines = new List<string> { _localization.Format("Convert_Summary_Counts", report.Converted, report.Skipped, report.Failed) };
        var folders = report.Songs
            .Where(s => s.Status == PackageConversionStatus.Converted && s.OutputPath != null)
            .Select(s => Path.GetDirectoryName(s.OutputPath)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (folders.Count == 1) lines.Add(_localization.Format("Convert_Summary_Where", folders[0]));

        var failed = report.Songs.Where(s => s.Status == PackageConversionStatus.Failed).ToList();
        lines.AddRange(failed.Take(FailuresListed).Select(f =>
            _localization.Format("Convert_Summary_FailedLine", Path.GetFileName(f.PackagePath), f.Error ?? "")));
        if (failed.Count > FailuresListed) lines.Add(_localization.Format("Convert_Summary_More", failed.Count - FailuresListed));
        return string.Join('\n', lines);
    }
}
