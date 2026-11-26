using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Project;
using DotCharter.Presentation.Shared;
using DotCharter.Services.Export.Validation;
using DotCharter.Services.Library.Index;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     Runs the export dialog's findings check off the UI thread (the Library lookup and the whole validator are
///     too slow for the open path). A newer request makes an older result stale: it is dropped, never applied.
/// </summary>
internal sealed class RocksmithExportFindingsCheck(
    RocksmithExportServices io, IAppLogger logger, Func<bool> isDisposed, Action checkingChanged)
{
    private int _request;

    public bool IsChecking { get; private set; }

    /// <summary>Completes when the latest request has landed (or was dropped as stale).</summary>
    public Task Ready { get; private set; } = Task.CompletedTask;

    /// <summary>Reads nothing on the worker: every input is captured here, on the calling thread (config is cached).</summary>
    public static Func<IReadOnlyList<ExportValidationFinding>> Work(ChartProject project, AppConfig config, ILibraryIndex library,
        IReadOnlyList<string> ownPackagePaths, double? audioLengthMs)
    {
        var validator = new ChartExportValidator
        {
            SixStringProjection = config.RsXmlSixStringProjection, LibraryIndex = library, OwnPackagePaths = ownPackagePaths
        };
        return () => validator.Validate(project, audioLengthMs);
    }

    /// <summary>One list row per finding, worst first then by time.</summary>
    public static IEnumerable<ExportFindingRow> Rows(IReadOnlyList<ExportValidationFinding> found, ChartEditDialogServices edit,
        RocksmithFixes fixes, Func<ExportValidationFinding, Task> fix, ICommand openMetadata)
    {
        foreach (var finding in found.OrderByDescending(f => f.Severity).ThenBy(f => f.TimeMs ?? double.MaxValue))
        {
            var time = finding.TimeMs;
            yield return new ExportFindingRow(finding.Severity == ExportFindingSeverity.Error,
                ExportFindingFormatter.FormatLocation(finding), ExportFindingFormatter.FormatMessage(finding, edit.Localization),
                time.HasValue, new RelayCommand(() => edit.Editor.ExecuteSeek(time!.Value), () => time.HasValue),
                new ExportFindingFix(fixes.CanFix(finding), fixes.Describe(finding), new AsyncRelayCommand(() => fix(finding))),
                new ExportFindingLink(ExportFindingFormatter.OpensSongMetadata(finding.MessageKey), openMetadata));
        }
    }

    public void Start(Func<Func<IReadOnlyList<ExportValidationFinding>>?> capture, Action<IReadOnlyList<ExportValidationFinding>> apply)
    {
        if (capture() is not { } work) return;
        var request = ++_request;
        Set(true);
        Ready = RunAsync(work, apply, request);
    }

    /// <summary>The same check on the calling thread: only the Export click uses it, so the gate decides on current findings.</summary>
    public void RunNow(Func<Func<IReadOnlyList<ExportValidationFinding>>?> capture, Action<IReadOnlyList<ExportValidationFinding>> apply)
    {
        if (capture() is not { } work) return;
        _request++;
        apply(work());
        Set(false);
    }

    private async Task RunAsync(Func<IReadOnlyList<ExportValidationFinding>> work, Action<IReadOnlyList<ExportValidationFinding>> apply, int request)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        IReadOnlyList<ExportValidationFinding> found;
        try
        {
            found = await (io.RunValidation?.Invoke(work) ?? Task.Run(work));
        }
        catch (Exception ex)
        {
            // The Export click re-checks on its own and surfaces the same failure; the last findings stay meanwhile.
            logger.Warning(AppLogCategory.Project, "Rocksmith export findings check failed.", ex);
            if (request == _request) Set(false);
            return;
        }

        if (request != _request || isDisposed()) return;
        apply(found);
        Set(false);
        logger.Info(AppLogCategory.Project,
            $"Rocksmith export findings ready in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms: {found.Count} findings.");
    }

    private void Set(bool value)
    {
        if (IsChecking == value) return;
        IsChecking = value;
        checkingChanged();
    }
}
