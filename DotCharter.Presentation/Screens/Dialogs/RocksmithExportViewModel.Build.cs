// The export's pickers, validation, build and result.
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Project;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Shared;
using DotCharter.Services.Diagnostics;
using DotCharter.Services.Export;
using DotCharter.Services.Export.DynamicDifficulty;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Export.Validation;
using DotCharter.Core.Utilities;
using DotCharter.Services.State;
using DotCharter.Tone.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Dialogs;

public sealed partial class RocksmithExportViewModel
{
    private CancellationTokenSource? _buildCts;
    private ClosableProgress<RocksmithBuildProgress>? _buildProgress;

    [ObservableProperty] private bool _isBuilding;
    [ObservableProperty] private double _progressFraction;
    [ObservableProperty] private string _progressMessage = "";
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string _resultPath = "";

    /// <summary>The package Open folder reveals: the first one written (a build can write two).</summary>
    private string _revealPath = "";

    public ObservableCollection<string> ResultWarnings { get; } = [];
    IReadOnlyList<string> IRocksmithExportScreen.ResultWarnings => ResultWarnings;

    /// <summary>Runs the validator into the list: on open, and again right before a build, so the user never presses "re-check".</summary>
    private void Revalidate()
    {
        if (_project is null) return;
        _findings = _validator.Validate(_project);
        ValidationItems.Clear();
        foreach (var finding in _findings.OrderByDescending(f => f.Severity).ThenBy(f => f.TimeMs ?? double.MaxValue))
        {
            var time = finding.TimeMs;
            ValidationItems.Add(new ExportFindingRow(finding.Severity == ExportFindingSeverity.Error,
                ExportFindingFormatter.FormatLocation(finding), ExportFindingFormatter.FormatMessage(finding, _edit.Localization),
                time.HasValue, new RelayCommand(() => _edit.Editor.ExecuteSeek(time!.Value), () => time.HasValue),
                new ExportFindingFix(_fixes.CanFix(finding), _fixes.Describe(finding), new AsyncRelayCommand(() => FixAsync(finding)))));
        }

        OnPropertyChanged(nameof(ValidationItems));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(ValidationSummary));
        OnGateChanged();
    }

    private async Task FixAsync(ExportValidationFinding finding)
    {
        await _fixes.FixAsync(finding);
        Revalidate();
    }

    /// <summary>The full report over the export; whatever its fixes changed, the
    /// export's own list is re-checked when it closes.</summary>
    private async Task ReviewReportAsync()
    {
        if (_project is null || IsBuilding) return;
        try
        {
            await _dialogs.ShowAsync<IChartQualityReportScreen, bool>();
        }
        catch (InvalidOperationException ex)
        {
            _edit.Logger.Error(AppLogCategory.Project, "The chart quality report could not open.", ex);
        }

        Revalidate();
    }

    private void OpenToneDesigner() => RocksmithExportSupport.OpenToneDesigner(_dialogs, _edit.Logger, _project);

    private Task ExportAsync() => RunExportAsync(test: false);

    private Task TestExportAsync() => RunExportAsync(test: true);

    /// <summary>
    ///     Release (the output field, the song's version) or Test (DLC Builder's test build: the test folder,
    ///     version "test", and a new <c>_vN</c> package while the game runs).
    /// </summary>
    private async Task RunExportAsync(bool test)
    {
        if (_project is not { } project) return;
        // Re-assemble, then refresh the ROWS with the findings: they are the arrangement filter AND the lookup
        // Blocks uses, so an arrangement renamed while the dialog was open would otherwise leave the package.
        Assemble(project);
        RefreshArrangements(project, Arrangements.GroupBy(a => a.Name).ToDictionary(g => g.Key, g => g.Last().IsSelected));
        Revalidate();
        if (test ? !CanTestExport() : !CanExport())
        {
            // The button was live, which is how this ran: say what the re-check just found.
            _edit.State.SetMessage(string.Join(" ", BlockingReasons()), StatusSeverity.Warning);
            return;
        }

        if (!test) CommitVersion();
        var buildProject = ChartProjectCloner.Clone(project);
        var plan = test
            ? RocksmithTestBuild.Plan(_io.Files, _io.Game, ResolvedTestFolder, DlcKey.Trim().Length > 0 ? DlcKey.Trim() : _derivedKey)
            : null;
        var options = plan is null
            ? BuildOptions(project)
            : BuildOptions(buildProject, RocksmithTestBuild.Prepare(buildProject, plan, ResolvedTestFolder, Target.IsMacOnly),
                Target.IsMacOnly ? ExportPlatform.Mac : ExportPlatform.Pc, plan.DlcKey, RocksmithTestExport.Version);
        ClearResult();
        ProgressFraction = 0;
        ProgressMessage = "";
        IsBuilding = true;
        _buildCts = new CancellationTokenSource();
        var loc = _edit.Localization;
        var buildProgress = new ClosableProgress<RocksmithBuildProgress>(p =>
        {
            ProgressFraction = p.Fraction;
            ProgressMessage = loc.Format("Dialog_Export_ProgressStep", p.Step, p.TotalSteps, loc[p.ResourceKey]);
        });
        _buildProgress = buildProgress;
        var progress = new Progress<RocksmithBuildProgress>(buildProgress.Report);
        _edit.Logger.Info(AppLogCategory.Project,
            $"Rocksmith export started. Output='{options.OutputPath}', audio='{options.AudioPath}', " +
            $"arrangements=[{string.Join(", ", options.ArrangementFilter ?? [])}], vocals={options.IncludeVocals}.");

        try
        {
            // DLC Builder deletes the earlier test builds BEFORE it builds the new one.
            if (plan is not null)
            {
                foreach (var superseded in plan.Superseded) _io.Files.DeleteIfExists(superseded);
            }

            // A deep snapshot: the build runs on a worker and the live project stays editable.
            var result = await new RocksmithPackageBuilder(_edit.Logger)
                .BuildPsarcAsync(buildProject, options, progress, _buildCts.Token);
            var paths = result.PackagePaths;
            ResultPath = string.Join("\n", paths);
            _revealPath = paths[0];
            foreach (var warning in result.Warnings) ResultWarnings.Add(warning);
            HasResult = true;
            _edit.State.SetMessage(paths.Count > 1
                ? loc.Format("Status_Export_SuccessBoth", paths[0], paths[1])
                : loc.Format("Status_Export_Success", paths[0]), StatusSeverity.Success);
            RocksmithExportSupport.Remember(_edit, result.PsarcPath, test, AppId, TestFolder, AudioVolume, PreviewVolume, IncludeVocals);
        }
        catch (OperationCanceledException)
        {
            _edit.State.SetMessage(loc["Dialog_Export_Canceled"], StatusSeverity.Warning);
            _edit.Logger.Info(AppLogCategory.Project, "Rocksmith export canceled by user.");
        }
        catch (PlayOnlyContentException ex)
        {
            // Play-only content is never exported.
            _edit.Logger.Info(AppLogCategory.Project, ex.Message);
            EndBuild();
            await _io.Prompt.ShowAsync(loc["Dialog_Export_Title"], loc[PlayOnlyExportGuard.MessageKey],
                MessageDialogKind.Error, ex.Message);
        }
        catch (Exception ex)
        {
            _edit.Logger.Error(AppLogCategory.Project, $"Rocksmith export failed. Output='{options.OutputPath}'.", ex);
            // The build is over before the modal opens, or Close would read it as running and refuse.
            EndBuild();
            await _io.Prompt.ShowAsync(loc["Dialog_Export_Title"], loc.Format("Status_Export_Failed", ex.Message),
                MessageDialogKind.Error, ex.ToString());
        }
        finally
        {
            EndBuild();
        }
    }

    internal RocksmithBuildOptions BuildOptions(ChartProject project) =>
        BuildOptions(project, OutputPath, BuildPlatform, DlcKey.Trim(), PackageVersion);

    /// <summary>The version the package carries: the field, "1" when blank.</summary>
    private string PackageVersion => string.IsNullOrWhiteSpace(Version) ? "1" : Version.Trim();

    private RocksmithBuildOptions BuildOptions(ChartProject project, string outputPath, ExportPlatform platform, string dlcKey,
        string version)
    {
        var config = _edit.Config.LoadAppConfig();
        return new RocksmithBuildOptions
        {
            OutputPath = outputPath,
            Platform = platform,
            // Mac is encrypted-only; for the PC package Plain is the explicit choice.
            PlainPackage = Target.WritesPlain && !Target.IsMacOnly,
            AudioPath = AudioPath,
            DlcKey = dlcKey,
            Version = version,
            AppId = string.IsNullOrWhiteSpace(AppId) ? "248750" : AppId.Trim(),
            PreviewAudioPath = PreviewAudioPath,
            AlbumArtPath = AlbumArtPath,
            AudioVolume = AudioVolume,
            PreviewVolume = PreviewVolume,
            IncludeVocals = IncludeVocals && HasVocals,
            Rs7ChordMode = SplitExtendedStringChords ? Rs7ChordMode.Split : Rs7ChordMode.Projection,
            SongLengthMs = _edit.State.TotalAudioLengthMs,
            ArrangementFilter = Arrangements.Where(a => a.IsSelected).Select(a => a.Name).ToList(),
            // The Tone designer commits its slot rows to the arrangements on close; what it imported is session-only.
            ToneDefinitions = _memory.ImportedToneBodiesFor(project),
            FallbackCharter = config.DefaultCharterName,
            DlcBuilderSteps = DlcBuilderSteps.FromConfig(config)
        };
    }

    /// <summary>Where a blank test folder points: a "test" folder beside the output file.</summary>
    public string TestFolderWatermark => RocksmithTestBuild.DefaultFolder(OutputPath, _io.Files.DocumentsFolder);

    private string ResolvedTestFolder => string.IsNullOrWhiteSpace(TestFolder) ? TestFolderWatermark : TestFolder.Trim();

    /// <summary>Auto volume: DLC Builder's loudness measurement of the main or preview audio.</summary>
    private async Task MeasureVolumeAsync(bool preview)
    {
        VolumeMessage = "";
        IsMeasuringVolume = true;
        var (volume, error) = await RocksmithExportSupport.MeasureVolumeAsync((preview ? PreviewAudioPath : AudioPath).Trim(), _edit.Logger);
        IsMeasuringVolume = false;
        if (volume is { } measured)
        {
            if (preview) PreviewVolume = measured;
            else AudioVolume = measured;
        }
        else
        {
            VolumeMessage = _edit.Localization.Format("Dialog_Export_AutoVolumeFailed", error ?? "");
        }
    }

    /// <summary>Idempotent: the failure path runs it before its modal and <c>finally</c> runs it again.</summary>
    private void EndBuild()
    {
        _buildProgress?.Close();
        _buildProgress = null;
        IsBuilding = false;
        ProgressMessage = "";
        ProgressFraction = 0;
        _buildCts?.Dispose();
        _buildCts = null;
    }

    private void OpenOutputFolder() => RocksmithExportSupport.Reveal(_io.Shell, _edit.Logger, _revealPath);

    private void ClearResult()
    {
        HasResult = false;
        ResultPath = "";
        _revealPath = "";
        ResultWarnings.Clear();
    }

    partial void OnIsBuildingChanged(bool value)
    {
        _export.NotifyCanExecuteChanged();
        _cancelBuild.NotifyCanExecuteChanged();
        RaiseExportBlocked();
    }
}
