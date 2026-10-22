using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Diagnostics;
using DotCharter.Services.Editing.Linting;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     Tools ▸ Batch lint. One instance per open; the folder and the auto-fix
///     choice carry over to the next open, the overwrite opt-in does not. One
///     file's failure never aborts the batch — it becomes a failed row.
/// </summary>
public sealed class BatchLintViewModel : DialogViewModel<bool>, IBatchLintScreen, ICancelableDialog
{
    private readonly IBatchChartLintService _lint;
    private readonly IFileStorageService _storage;
    private readonly IConfigurationService _config;
    private readonly IExportTargetWriter _files;
    private readonly IMessagePrompt _prompt;
    private readonly DialogMemory _memory;
    private readonly IAppLogger _logger;
    private readonly ILocalizationService _loc;
    private CancellationTokenSource? _cts;
    private IReadOnlyList<ILintResultRow> _results = [];
    private string _folderPath;
    private bool _applyAutoFixes;
    private bool _saveFixedProjects;
    private bool _isBusy;
    private double _progress;
    private string _currentFile = "";
    private string _status = "";
    private bool _hasRun;

    public BatchLintViewModel(IBatchChartLintService lint, IFileStorageService storage, IConfigurationService config,
        IExportTargetWriter files, IMessagePrompt prompt, DialogMemory memory, DialogBasics basics)
    {
        _loc = basics.Localization;
        _lint = lint;
        _storage = storage;
        _config = config;
        _files = files;
        _prompt = prompt;
        _memory = memory;
        _logger = basics.Logger;
        _folderPath = memory.LintFolderPath.Length > 0 ? memory.LintFolderPath : config.LoadAppConfig().LastLintFolderDir;
        _applyAutoFixes = memory.LintApplyAutoFixes;
        BrowseFolderCommand = new AsyncRelayCommand(BrowseFolderAsync);
        RunCommand = new AsyncRelayCommand(RunAsync);
        CancelCommand = new RelayCommand(() => TryCancel());
    }

    public string FolderPath
    {
        get => _folderPath;
        private set
        {
            if (SetProperty(ref _folderPath, value)) _memory.LintFolderPath = value;
        }
    }

    public ICommand BrowseFolderCommand { get; }

    public bool ApplyAutoFixes
    {
        get => _applyAutoFixes;
        set
        {
            if (!SetProperty(ref _applyAutoFixes, value)) return;
            _memory.LintApplyAutoFixes = value;
            if (!value) SaveFixedProjects = false;
            OnPropertyChanged(nameof(CanSaveFixedProjects));
        }
    }

    public bool SaveFixedProjects
    {
        get => _saveFixedProjects;
        set => SetProperty(ref _saveFixedProjects, value && _applyAutoFixes);
    }

    public bool CanSaveFixedProjects => _applyAutoFixes;

    public bool HasResults => _results.Count > 0;

    public IReadOnlyList<ILintResultRow> Results => _results;

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string CurrentFile
    {
        get => _currentFile;
        private set => SetProperty(ref _currentFile, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public ICommand RunCommand { get; }

    public ICommand CancelCommand { get; }

    /// <summary>
    ///     While a run is in flight this cancels the RUN and keeps the dialog
    ///     open, so Close and Esc stay live for the whole sweep and the partial
    ///     result is still reported.
    /// </summary>
    public bool TryCancel()
    {
        if (!IsBusy)
        {
            Close(_hasRun);
            return true;
        }

        if (_cts is { IsCancellationRequested: false })
        {
            _cts.Cancel();
            Status = _loc["BatchLint_Canceling"];
        }

        return false;
    }

    /// <summary>The post-run report of a bulk overwrite: how many files were rewritten, and by which fixers.</summary>
    public string DescribeOverwrite(ChartLintBatchResult result)
    {
        var headline = _loc.Plural("BatchLint_OverwroteOne", "BatchLint_OverwroteFmt", result.SavedCount);
        var classes = result.FixClasses
            .Where(pair => pair.Value > 0)
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key} × {pair.Value}")
            .ToList();
        return classes.Count == 0
            ? headline
            : headline + "\n" + _loc.Format("BatchLint_OverwroteFixes", string.Join(", ", classes));
    }

    private async Task BrowseFolderAsync()
    {
        var folder = await _storage.OpenFolderPickerAsync(_loc["Picker_SelectLintFolder"],
            string.IsNullOrEmpty(FolderPath) ? null : FolderPath);
        if (string.IsNullOrEmpty(folder)) return;
        FolderPath = folder;
        _config.RememberPickedFolder(folder, (c, d) => c.LastLintFolderDir = d);
    }

    private async Task RunAsync()
    {
        if (IsBusy) return;
        var files = FindProjects();
        if (files == null) return;

        var options = new ChartLintOptions
        {
            ApplyAutoFixes = ApplyAutoFixes,
            SaveFixedProjects = ApplyAutoFixes && SaveFixedProjects
        };
        // The count is named BEFORE the first byte is written, and a "no" writes nothing.
        if (options.SaveFixedProjects && !await ConfirmOverwriteAsync(files.Length)) return;

        IsBusy = true;
        SetResults([]);
        Progress = 0;
        CurrentFile = "";
        Status = _loc.Plural("BatchLint_RunningOne", "BatchLint_Running", files.Length);
        using var cts = new CancellationTokenSource();
        _cts = cts;
        // Progress<T> posts back to the UI context captured here; the worker never raises PropertyChanged.
        var progress = new Progress<ChartLintProgress>(step =>
        {
            Progress = step.Fraction;
            CurrentFile = step.FilePath.Length == 0 ? "" : Path.GetFileName(step.FilePath);
        });

        try
        {
            var result = await Task.Run(() => _lint.Run(files, options, progress, cts.Token));
            SetResults([.. result.Charts.Select(ToRow)]);
            _hasRun = true;
            var summary = _loc.Format("BatchLint_Summary", result.Charts.Count, result.TotalIssues, result.TotalFixes,
                result.SavedCount, result.FailedCount);
            // A cancelled run walked PART of the folder, and must not read as "your library is clean".
            Status = result.Canceled ? _loc.Format("BatchLint_SummaryCanceled", summary, files.Length) : summary;
            _logger.Info(AppLogCategory.Editing,
                $"Batch lint done. Folder='{FolderPath}', files={result.Charts.Count}/{files.Length}, issues={result.TotalIssues}, fixes={result.TotalFixes}, saved={result.SavedCount}, failed={result.FailedCount}, canceled={result.Canceled}, applyFixes={options.ApplyAutoFixes}, saveFixed={options.SaveFixedProjects}.");
            if (options.SaveFixedProjects)
                await _prompt.ShowAsync(_loc["BatchLint_OverwroteTitle"], DescribeOverwrite(result));
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Editing, $"Batch lint failed. Folder='{FolderPath}'.", ex);
            Status = _loc.Format("BatchLint_Failed", ex.Message);
        }
        finally
        {
            _cts = null;
            IsBusy = false;
            CurrentFile = "";
            Progress = 0;
        }
    }

    /// <summary>The folder's project files, or null with the reason in <see cref="Status" />.</summary>
    private string[]? FindProjects()
    {
        if (string.IsNullOrWhiteSpace(FolderPath) || !_files.DirectoryExists(FolderPath))
        {
            Status = _loc["BatchLint_NoFolder"];
            return null;
        }

        string[] files;
        try
        {
            // A project's Charter companion is never linted in its place: rewriting it
            // would put DotCharter's XML into the file Charter opens.
            files = [.. ProjectFiles.WithoutShadowedCompanions(
                ProjectFiles.OpenablePatterns.SelectMany(pattern => _files.FindFiles(FolderPath, pattern)))];
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Editing, $"Batch lint could not enumerate '{FolderPath}'.", ex);
            Status = _loc.Format("BatchLint_Failed", ex.Message);
            return null;
        }

        if (files.Length > 0) return files;
        Status = _loc["BatchLint_NoProjects"];
        SetResults([]);
        return null;
    }

    private async Task<bool> ConfirmOverwriteAsync(int count)
    {
        var confirmed = await _prompt.ShowAsync(_loc["BatchLint_ConfirmOverwriteTitle"],
            _loc.Plural("BatchLint_ConfirmOverwriteOne", "BatchLint_ConfirmOverwriteFmt", count, FolderPath),
            MessageDialogKind.Warning, confirmation: true);
        if (confirmed) return true;
        Status = _loc["BatchLint_OverwriteDeclined"];
        _logger.Info(AppLogCategory.Editing,
            $"Batch lint overwrite declined before any write. Folder='{FolderPath}', candidates={count}.");
        return false;
    }

    private void SetResults(IReadOnlyList<ILintResultRow> rows)
    {
        _results = rows;
        OnPropertyChanged(nameof(Results));
        OnPropertyChanged(nameof(HasResults));
    }

    private ILintResultRow ToRow(ChartLintReport report)
    {
        var fileName = Path.GetFileName(report.FilePath);
        var folder = Path.GetDirectoryName(report.FilePath) ?? "";
        if (report.Failed) return new Row(fileName, folder, report.Error ?? "", true, false);
        var detail = report.TotalFixes > 0
            ? _loc.Format("BatchLint_RowFixed", report.TotalIssues, report.TotalFixes)
            : _loc.Plural("BatchLint_RowIssuesOne", "BatchLint_RowIssues", report.TotalIssues);
        return new Row(fileName, folder, detail, false, report.Saved);
    }

    private sealed record Row(string FileName, string Folder, string Detail, bool Failed, bool Saved) : ILintResultRow;
}
