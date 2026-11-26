// The export's pickers, validation, build and result.
using DotCharter.Services.Export.Audio;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Core.Domain.Project;
using DotCharter.Core.Utilities;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Shared;
using DotCharter.Services.Export;
using DotCharter.Services.Export.DynamicDifficulty;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Export.Validation;
using DotCharter.Services.Infrastructure.Storage;
using DotCharter.Services.State;
using DotCharter.Tone.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Dialogs;

public sealed partial class RocksmithExportViewModel
{
    private Task _volumeMeasurement = Task.CompletedTask;
    private RocksmithExportVolumeInput? _volumeInput;
    private CancellationTokenSource? _buildCts;
    private ClosableProgress<RocksmithBuildProgress>? _buildProgress;

    internal int? AudioAutoTargetLufs { get; set; }
    internal int? PreviewAutoTargetLufs { get; set; }

    [ObservableProperty] private bool _isBuilding;
    [ObservableProperty] private double _progressFraction;
    [ObservableProperty] private string _progressMessage = "";
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string _resultPath = "";

    /// <summary>The package Open folder reveals: the first one written (a build can write two).</summary>
    private string _revealPath = "";

    public ObservableCollection<string> ResultWarnings { get; } = [];
    IReadOnlyList<string> IRocksmithExportScreen.ResultWarnings => ResultWarnings;

    /// <summary>Re-checks the chart off the UI thread; Export stays greyed with the "checking" reason while it runs.</summary>
    internal void Revalidate() => _check.Start(ValidationWork, ApplyFindings);

    /// <summary>The same check on the calling thread: the Export click only, so the gate decides on current findings.</summary>
    private void RevalidateNow() => _check.RunNow(ValidationWork, ApplyFindings);

    private Func<IReadOnlyList<ExportValidationFinding>>? ValidationWork() => _project is not { } project ? null : RocksmithExportFindingsCheck.Work(
        project, _edit.Config.LoadAppConfig(), _io.Library, RocksmithExportOwnPackages.For(OutputPath, BuildPlatform, HasPsxrSelection), _edit.State.TotalAudioLengthMs);

    private void ApplyFindings(IReadOnlyList<ExportValidationFinding> found)
    {
        _findings = found;
        ValidationItems.Clear();
        foreach (var row in RocksmithExportFindingsCheck.Rows(found, _edit, _fixes, FixAsync, OpenSongMetadataCommand)) ValidationItems.Add(row);

        OnPropertyChanged(nameof(ValidationItems));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(ValidationSummary));
        OnGateChanged();
    }

    /// <summary>Opens Song metadata over the export (title and original-script field), then re-checks: a typed title clears the warning and the file name follows.</summary>
    private async Task OpenSongMetadataAsync()
    {
        if (IsBuilding) return;
        await _dialogs.ShowAsync<ISongMetadataScreen, bool>();
        FollowSongNames();
        Revalidate();
    }

    private async Task FixAsync(ExportValidationFinding finding)
    {
        await _fixes.FixAsync(finding);
        Revalidate();
    }

    /// <summary>Closes the export FIRST (so the tab is not hidden behind the overlay), then opens the Inspector's Issues tab: the whole-project report.</summary>
    private void ShowInIssues()
    {
        if (IsBuilding || !_issuesDoor.CanOpen) return;
        Close(HasResult);
        _issuesDoor.TryOpen();
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
        if (PrepareExport(test) is not { } project) return;

        var run = new CancellationTokenSource();
        _buildCts = run;
        var continuation = new RocksmithExportContinuation(this, run.Token, () => ReferenceEquals(_buildCts, run));
        IsBuilding = true;
        var loc = _edit.Localization;
        RocksmithBuildOptions? options = null;
        try
        {
            if (!continuation.IsCurrent) return;
            RocksmithExportSupport.CommitPreviewStart(_edit.History, project.Chart, PreviewStartText, PreviewVolume, PreviewAudioPath);
            var volumeInput = (AudioPath, PreviewAudioPath, PreviewStartText);
            var (keepAudio, keepPreview) = RocksmithExportSupport.VolumesToKeep(this, project.Chart, continuation.Shown);
            await EnsureProjectVolumesAsync(project, keepAudio, keepPreview, continuation).WaitAsync(continuation.Token);
            if (!continuation.IsCurrent || !ReferenceEquals(_edit.State.ActiveProject, project)) return;
            if (volumeInput != (AudioPath, PreviewAudioPath, PreviewStartText)) return;
            if (!test) RocksmithExportSupport.CommitVersion(_edit.History, _project?.Chart, PackageVersion, PreviewStartText);
            var buildProject = ChartProjectCloner.Clone(project);
            var testBuild = test ? _pickers.PlanTestBuild(this, project, buildProject, _names.Key) : null;
            RocksmithExportSupport.PrepareOutputName(this, HasPsxrSelection);
            options = testBuild is null
                ? BuildOptions(project)
                : BuildOptions(project, RocksmithTestBuild.Prepare(buildProject, testBuild.Plan, testBuild.Folder, Target.IsMacOnly, HasPsxrSelection),
                    Target.IsMacOnly ? ExportPlatform.Mac : ExportPlatform.Pc, testBuild.Plan.DlcKey, RocksmithTestExport.Version, test: true);
            ClearResult();
            ProgressFraction = 0;
            ProgressMessage = "";
            var buildProgress = new ClosableProgress<RocksmithBuildProgress>(p =>
            {
                if (!continuation.IsCurrent) return;
                ProgressFraction = p.Fraction;
                ProgressMessage = loc.Format("Dialog_Export_ProgressStep", p.Step, p.TotalSteps, loc[p.ResourceKey]);
            });
            _buildProgress = buildProgress;
            var progress = new Progress<RocksmithBuildProgress>(buildProgress.Report);
            _edit.Logger.Info(AppLogCategory.Project,
                $"Rocksmith export started. Output='{options.OutputPath}', audio='{options.AudioPath}', " +
                $"arrangements=[{string.Join(", ", options.ArrangementFilter ?? [])}], vocals={options.IncludeVocals}, previewVolume={options.PreviewVolume}.");

            // A deep snapshot: the build runs on a worker and the live project stays editable.
            var beforePublish = testBuild is null
                ? _pickers.BeforePublish(false, options.WriteStandardPackage) : testBuild.Ownership.BeforePublishAsync;
            Task<RocksmithBuildResult> Build() => new RocksmithPackageBuilder(_edit.Logger)
                .BuildPsarcAsync(buildProject, options, progress, continuation.Token, beforePublish);
            var result = testBuild is null ? await CompanionOwnership.BuildAndRecordReleaseAsync(Build, options.ProjectDirectory, buildProject)
                : await testBuild.Ownership.BuildAndCleanUpAsync(Build, testBuild.Plan.Superseded);
            if (!continuation.IsCurrent) return;
            var paths = result.PackagePaths;
            _pickers.PublishedOutput(result.PsarcPath, test, options.Platform);
            ResultPath = string.Join("\n", paths);
            _revealPath = paths[0];
            foreach (var warning in result.Warnings) ResultWarnings.Add(warning);
            HasResult = true;
            _edit.State.SetMessage(paths.Count > 1
                ? loc.Format("Status_Export_SuccessBoth", paths[0], paths[1])
                : loc.Format("Status_Export_Success", paths[0]), StatusSeverity.Success);
            RocksmithExportSupport.Remember(_edit, result.PsarcPath, test, AppId, IncludeVocals, FixLowBassTuning);
            if (ReferenceEquals(_edit.State.ActiveProject, project))
            {
                RocksmithExportSupport.CommitVolumes(_edit.History, project.Chart, AudioVolume, PreviewVolume,
                    AudioAutoTargetLufs is not null, PreviewAutoTargetLufs is not null);
            }
        }
        catch (OperationCanceledException)
        {
            if (!continuation.IsOwner) return;
            _edit.State.SetMessage(loc["Dialog_Export_Canceled"], StatusSeverity.Warning);
            _edit.Logger.Info(AppLogCategory.Project, "Rocksmith export canceled by user.");
        }
        catch (Exception ex)
        {
            if (!continuation.IsCurrent) return;
            // The build is over before the modal opens, or Close would read it as running and refuse.
            EndBuild(run);
            await _pickers.ShowFailureAsync(ex, options?.OutputPath ?? OutputPath);
        }
        finally
        {
            EndBuild(run);
        }
    }

    private ChartProject? PrepareExport(bool test)
    {
        if (_project is not { } project || IsDisposed || Result.IsCompleted) return null;
        // Re-assemble, then refresh the ROWS with the findings: they are the arrangement filter AND the lookup
        // Blocks uses, so an arrangement renamed while the dialog was open would otherwise leave the package.
        Assemble(project);
        RefreshArrangements(project, Arrangements.GroupBy(a => a.Name).ToDictionary(g => g.Key, g => g.Last().IsSelected));
        UpdateOutputName();
        RevalidateNow();
        if (test ? !Gate.CanTestExport(AudioPath) : !Gate.CanExport)
        {
            // The button was live, which is how this ran: say what the re-check just found.
            _edit.State.SetMessage(string.Join(" ", BlockingReasons()), StatusSeverity.Warning);
            return null;
        }
        return project;
    }

    internal RocksmithBuildOptions BuildOptions(ChartProject project) =>
        BuildOptions(project, OutputPath, BuildPlatform, DlcKey.Trim(), PackageVersion);

    /// <summary>The version the package carries: the field, "1" when blank.</summary>
    private string PackageVersion => string.IsNullOrWhiteSpace(Version) ? "1" : Version.Trim();

    private RocksmithBuildOptions BuildOptions(ChartProject project, string outputPath, ExportPlatform platform, string dlcKey,
        string version, bool test = false)
    {
        var config = _edit.Config.LoadAppConfig();
        return new RocksmithBuildOptions
        {
            OutputPath = outputPath,
            CleanUpChordSidecars = !test,
            ProjectDirectory = string.IsNullOrWhiteSpace(_edit.State.CurrentProjectPath)
                ? null : ProjectFolderLayout.ProjectDirectory(_edit.State.CurrentProjectPath),
            Platform = platform,
            PlainPackage = Target.WritesPlain && !Target.IsMacOnly,
            WriteStandardPackage = WriteStandardPackage && OffersStandardPackage,
            FixLowBassTuning = FixLowBassTuning,
            StandardPlainPackage = Target.StockWritesPlain,
            AudioPath = AudioPath,
            DlcKey = dlcKey,
            Version = version,
            AppId = string.IsNullOrWhiteSpace(AppId) ? "248750" : AppId.Trim(),
            PreviewAudioPath = PreviewAudioPath,
            PreviewStartMs = PreviewStartTime.TryParse(PreviewStartText, out var previewStart) ? previewStart : null,
            AlbumArtPath = AlbumArtPath,
            AudioVolume = AudioVolume,
            PreviewVolume = PreviewVolume,
            IncludeVocals = IncludeVocals && HasVocals,
            SongLengthMs = _edit.State.TotalAudioLengthMs,
            ArrangementFilter = Arrangements.Where(a => a.IsSelected).Select(a => a.Name).ToList(),
            // The Tone designer commits its slot rows to the arrangements on close; what it imported is session-only.
            ToneDefinitions = _memory.ImportedToneBodiesFor(project),
            FallbackCharter = config.DefaultCharterName,
            DlcBuilderSteps = DlcBuilderSteps.FromConfig(config),
            ChordNameOptions = DotCharter.Services.Editing.ChordShapes.NewChordNames.OptionsFor(config)
        };
    }

    /// <summary>Where a blank test folder points: the folder of the normal output.</summary>
    public string TestFolderWatermark => RocksmithTestBuild.DefaultFolder(OutputPath, _io.Files.DocumentsFolder);

    /// <summary>Measures this song's missing or stale volumes off the UI thread and shows them, unless one was already set this session.</summary>
    private Task EnsureProjectVolumesAsync(ChartProject project, bool keepAudio, bool keepPreview, RocksmithExportContinuation? continuation = null) =>
        _volumeMeasurement = RocksmithExportSupport.JoinProjectVolumesAsync(this, project, _edit, keepAudio, keepPreview, _io.VolumeMeasure,
            _volumeMeasurement, _volumeInput, input => _volumeInput = input, _io.VolumeSignature, continuation ?? new RocksmithExportContinuation(this, OpenToken));

    /// <summary>Measures the main or preview audio against the saved Auto volume target.</summary>
    private Task MeasureVolumeAsync(bool preview) => RocksmithExportSupport.MeasureVolumeAsync(this, preview, _edit);

    partial void OnAudioVolumeChanged(double value) => AudioAutoTargetLufs = null;
    partial void OnPreviewVolumeChanged(double value) => PreviewAutoTargetLufs = null;

    /// <summary>Idempotent: the failure path runs it before its modal and <c>finally</c> runs it again.</summary>
    private void EndBuild(CancellationTokenSource run)
    {
        if (!ReferenceEquals(_buildCts, run)) return;
        _buildCts = null;
        _buildProgress?.Close();
        _buildProgress = null;
        run.Dispose();
        ProgressMessage = "";
        ProgressFraction = 0;
        IsBuilding = false;
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
        _showInIssues.NotifyCanExecuteChanged();
        RaiseExportBlocked();
    }
}
