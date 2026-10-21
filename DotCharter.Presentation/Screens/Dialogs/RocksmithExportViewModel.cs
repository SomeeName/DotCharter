using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Project;
using DotCharter.Core.Utilities;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Shared;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Diagnostics;
using DotCharter.Services.Export;
using DotCharter.Services.Export.DlcBuilder;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Export.Validation;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Shell;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>What the export dialog reads, picks and writes through, in one part so it stays inside seven dependencies.</summary>
public sealed record RocksmithExportServices(
    IEditorTransport Transport, IProjectDocumentService Documents, IFileStorageService Storage,
    IExportTargetWriter Files, IShellLauncher Shell, IMessagePrompt Prompt, IRocksmithGameProbe Game);

/// <summary>
///     The Rocksmith package export, one instance per open. It re-assembles the project the way a
///     save does, validates it, and gates Export on every clause that would make the build fail or
///     lie (a Mac-only target with no Mac package possible, a blocking finding, a bad DLC key,
/// missing paths, nothing ticked), each with its reason on screen. PC + Mac writes both
///     packages, or the PC one alone with the reason printed under the platform picker. The
///     fields persist per project through <see cref="DialogMemory" />; the build is in <c>.Build.cs</c>.
/// </summary>
public sealed partial class RocksmithExportViewModel : DialogViewModel<bool>, IRocksmithExportScreen, ICancelableDialog,
    IDisposable
{
    private readonly ChartEditDialogServices _edit;
    private readonly RocksmithExportServices _io;
    private readonly IDialogs _dialogs;
    private readonly DialogMemory _memory;
    private readonly ChartProject? _project;
    private readonly ChartExportValidator _validator = new();
    private readonly RocksmithFixes _fixes;
    private readonly AsyncRelayCommand _export;
    private readonly RelayCommand _cancelBuild;
    private readonly AsyncRelayCommand _testExport;
    private readonly AsyncRelayCommand _autoVolume;
    private readonly AsyncRelayCommand _autoPreviewVolume;
    private readonly RocksmithExportPickers _pickers;
    private readonly RocksmithExportDlcBuilder _dlcBuilder;
    private string _derivedKey = "";
    private readonly AsyncRelayCommand _browseOutput;
    private IReadOnlyList<ExportValidationFinding> _findings = [];

    [ObservableProperty] private string _outputPath = "";
    [ObservableProperty] private string _dlcKey = "";
    [ObservableProperty] private string _version = "1";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(TestFolderWatermark))] private string _testFolder = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasVolumeMessage))] private string _volumeMessage = "";
    [ObservableProperty] private bool _isMeasuringVolume;
    [ObservableProperty] private string _appId = "248750";
    [ObservableProperty] private string _audioPath = "";
    [ObservableProperty] private string _previewAudioPath = "";
    [ObservableProperty] private string _albumArtPath = "";
    [ObservableProperty] private double _audioVolume = -8.0;
    [ObservableProperty] private double _previewVolume = -7.0;
    [ObservableProperty] private bool _includeVocals = true;
    [ObservableProperty] private bool _hasVocals;
    [ObservableProperty] private bool _splitExtendedStringChords;
    [ObservableProperty] private bool _hasExtendedStringGuitar;
    [ObservableProperty] private string _dlcKeyWatermark = "";

    public RocksmithExportViewModel(ChartEditDialogServices edit, RocksmithExportServices io, IDialogs dialogs, DialogMemory memory,
        IChartMaintenanceService maintenance, IDlcBuilderProjectWriter dlcBuilderWriter)
    {
        _edit = edit;
        _fixes = new RocksmithFixes(maintenance, edit.Editor, edit.State, dialogs, edit.Localization, edit.Logger);
        _io = io;
        _dialogs = dialogs;
        _memory = memory;
        Target = new ExportTargetViewModel(edit.Localization);
        Target.TargetChanged += (_, _) =>
        {
            OutputPath = ExportPathDefaults.WithPlatformSuffix(OutputPath, Target.IsMacOnly);
            OnGateChanged();
        };
        _export = new AsyncRelayCommand(ExportAsync, CanExport);
        _testExport = new AsyncRelayCommand(TestExportAsync, CanTestExport);
        _autoVolume = new AsyncRelayCommand(() => MeasureVolumeAsync(preview: false),
            () => !IsMeasuringVolume && !string.IsNullOrWhiteSpace(AudioPath));
        _autoPreviewVolume = new AsyncRelayCommand(() => MeasureVolumeAsync(preview: true),
            () => !IsMeasuringVolume && !string.IsNullOrWhiteSpace(PreviewAudioPath));
        _pickers = new RocksmithExportPickers(edit, io);
        _dlcBuilder = new RocksmithExportDlcBuilder(edit, io, dlcBuilderWriter, _pickers, DlcBuilderInput);
        _dlcBuilder.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(DlcBuilderProjectMessage));
            OnPropertyChanged(nameof(HasDlcBuilderProjectMessage));
        };
        BrowseTestFolderCommand = new AsyncRelayCommand(async () => TestFolder = await _pickers.FolderAsync(ResolvedTestFolder) ?? TestFolder);
        _cancelBuild = new RelayCommand(() => _buildCts?.Cancel(), () => IsBuilding);
        OpenToneDesignerCommand = new RelayCommand(OpenToneDesigner);
        ReviewReportCommand = new AsyncRelayCommand(ReviewReportAsync);
        BrowseAudioCommand = new AsyncRelayCommand(async () => AudioPath = await _pickers.AudioAsync(AudioPath) ?? AudioPath);
        BrowsePreviewAudioCommand = new AsyncRelayCommand(
            async () => PreviewAudioPath = await _pickers.AudioAsync(PreviewAudioPath) ?? PreviewAudioPath);
        BrowseAlbumArtCommand = new AsyncRelayCommand(async () => AlbumArtPath = await _pickers.AlbumArtAsync(AlbumArtPath) ?? AlbumArtPath);
        _browseOutput = new AsyncRelayCommand(async () => OutputPath = await _pickers.OutputAsync(OutputPath) ?? OutputPath);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
        CloseCommand = new RelayCommand(() => TryCancel());
        Arrangements.CollectionChanged += (_, _) => OnGateChanged();

        _project = edit.State.ActiveProject;
        if (_project is null) return;
        Assemble(_project);
        var sameProject = ReferenceEquals(_memory.ExportDraftProject, _project) && _memory.ExportDraft is not null;
        RefreshArrangements(_project, sameProject ? _memory.ExportDraft!.Ticks : null);
        Prefill(_project, sameProject ? _memory.ExportDraft : null);
        OutputPath = ExportPathDefaults.WithPlatformSuffix(OutputPath, Target.IsMacOnly);
        Revalidate();
        edit.Logger.Info(AppLogCategory.Project,
            $"Rocksmith export dialog opened. Song='{_project.Chart.SongName}', arrangements={Arrangements.Count}, errors={ErrorCount}, warnings={WarningCount}.");
    }

    public ExportTargetViewModel Target { get; }
    public ObservableCollection<ExportArrangementRow> Arrangements { get; } = [];
    public ObservableCollection<ExportFindingRow> ValidationItems { get; } = [];
    IExportTarget IRocksmithExportScreen.Target => Target;
    IReadOnlyList<IExportArrangementRow> IRocksmithExportScreen.Arrangements => Arrangements;
    IReadOnlyList<IExportFinding> IRocksmithExportScreen.ValidationItems => ValidationItems;

    public int ErrorCount => _findings.Count(f => f.Severity == ExportFindingSeverity.Error);
    public int WarningCount => _findings.Count(f => f.Severity == ExportFindingSeverity.Warning);
    public bool HasFindings => _findings.Count > 0;

    public string ValidationSummary => HasFindings
        ? _edit.Localization.Format("Dialog_Export_ValidationSummary", ErrorCount, WarningCount)
        : _edit.Localization["Dialog_Export_ValidationOk"];

    /// <summary>An error blocks when it is global, on a ticked arrangement, or on a vocal track while vocals are included.</summary>
    public bool HasBlockingErrors => _findings.Any(Blocks);

    /// <summary>A typed key the build would refuse: under 6 ASCII letters/digits, or any non-ASCII character.</summary>
    public bool HasInvalidDlcKey => RocksmithDlcKey.IsInvalidTypedKey(DlcKey);

    public bool MissingPaths => string.IsNullOrWhiteSpace(OutputPath) || string.IsNullOrWhiteSpace(AudioPath);

    public bool NothingSelectedToExport => !Arrangements.Any(a => a.IsSelected) && !(IncludeVocals && HasVocals);

    public ICommand ExportDlcBuilderProjectCommand => _dlcBuilder.Command;
    public string DlcBuilderProjectMessage => _dlcBuilder.Message;
    public bool HasDlcBuilderProjectMessage => _dlcBuilder.HasMessage;

    /// <summary>The chart as the export sees it now, on a private copy the writer may refresh.</summary>
    private DlcBuilderProjectInput? DlcBuilderInput()
    {
        if (_project is not { } project) return null;
        Assemble(project);
        return new DlcBuilderProjectInput(ChartProjectCloner.Clone(project), AudioPath, AlbumArtPath, OutputPath);
    }

    /// <summary>Why no Mac package can be written right now: a ticked 7-/8-string
    /// guitar, or no Mac encryption. Null when it can.</summary>
    public RocksmithMacBlock? MacBlock =>
        RocksmithPlatformTargets.MacUnavailableReason(Arrangements.Any(a => a.IsSelected && a.IsExtendedString));

    /// <summary>What the platform choice will write, under the picker, or why the Mac package is ruled out.</summary>
    public string PlatformNote =>
        RocksmithExportSupport.PlatformNote(_edit.Localization, Target, MacBlock, OutputPath, PackagePath);

    public bool HasPlatformNote => PlatformNote.Length > 0;

    public ICommand OpenToneDesignerCommand { get; }
    public ICommand ReviewReportCommand { get; }
    public ICommand BrowseAudioCommand { get; }
    public ICommand BrowseTestFolderCommand { get; }
    public ICommand TestExportCommand => _testExport;
    public ICommand AutoVolumeCommand => _autoVolume;
    public ICommand AutoPreviewVolumeCommand => _autoPreviewVolume;
    public bool HasVolumeMessage => VolumeMessage.Length > 0;
    public ICommand BrowsePreviewAudioCommand { get; }
    public ICommand BrowseAlbumArtCommand { get; }
    public IAsyncRelayCommand BrowseOutputCommand => _browseOutput;
    public IAsyncRelayCommand ExportCommand => _export;
    ICommand IRocksmithExportScreen.BrowseOutputCommand => _browseOutput;
    ICommand IRocksmithExportScreen.ExportCommand => _export;
    public ICommand CancelBuildCommand => _cancelBuild;
    public ICommand OpenOutputFolderCommand { get; }
    public ICommand CloseCommand { get; }

    /// <summary>While a build runs, Esc and Close cancel the build and the dialog stays open.</summary>
    public bool TryCancel()
    {
        if (IsBuilding)
        {
            _buildCts?.Cancel();
            return false;
        }

        CommitVersion();
        SaveDraft();
        Close(HasResult);
        return true;
    }

    public void Dispose()
    {
        _buildCts?.Cancel();
        CommitVersion();
        SaveDraft();
    }

    /// <summary>
    /// Why Export cannot run right now: every failing gate clause, one per line. Empty
    ///     when Export can run, and while a build runs (the progress block says what is happening
    ///     then). Shown above the action bar and as the button's tooltip (enabled only while
    /// <see cref="IsExportBlocked" />), and nowhere else, so a grey Export says why once.
    /// </summary>
    public string ExportBlockedReason => IsBuilding || CanExport() ? "" : string.Join("\n", BlockingReasons());

    /// <summary>Export is greyed for a reason <see cref="ExportBlockedReason" /> names.</summary>
    public bool IsExportBlocked => ExportBlockedReason.Length > 0;

    /// <summary>Every gate clause that is false, in gate order, as the sentence the dialog prints for it.</summary>
    internal IReadOnlyList<string> BlockingReasons()
    {
        var loc = _edit.Localization;
        var reasons = new List<string>(5);
        if (MacOnlyBlocked) reasons.Add(loc.Format("Dialog_Export_MacUnavailable", RocksmithExportSupport.MacBlockText(loc, MacBlock!.Value)));
        if (HasBlockingErrors) reasons.Add(loc["Dialog_Export_ErrorsBlockExport"]);
        if (HasInvalidDlcKey) reasons.Add(loc["Dialog_Export_DlcKeyInvalid"]);
        if (MissingPaths) reasons.Add(loc["Dialog_Export_MissingPathsHint"]);
        if (NothingSelectedToExport) reasons.Add(loc["Dialog_Export_NothingSelectedHint"]);
        return reasons;
    }

    /// <summary>The version typed in the dialog becomes the song's, as one undo step.</summary>
    private void CommitVersion() => RocksmithExportSupport.CommitVersion(_edit.History, _project?.Chart, PackageVersion);

    private bool CanExport() =>
        !IsBuilding && !MacOnlyBlocked && !HasBlockingErrors && !HasInvalidDlcKey && !MissingPaths && !NothingSelectedToExport;

    /// <summary>A test export needs everything Export does except the output file: it writes to the test folder.</summary>
    private bool CanTestExport() =>
        !IsBuilding && !MacOnlyBlocked && !HasBlockingErrors && !HasInvalidDlcKey && !string.IsNullOrWhiteSpace(AudioPath) &&
        !NothingSelectedToExport;

    private bool Blocks(ExportValidationFinding finding)
    {
        if (finding.Severity != ExportFindingSeverity.Error) return false;
        if (finding.ArrangementName.Length == 0) return true;
        return Arrangements.FirstOrDefault(a => a.Name == finding.ArrangementName) is { } row ? row.IsSelected : IncludeVocals;
    }

    /// <summary>The target is Mac alone and no Mac package can be written.</summary>
    private bool MacOnlyBlocked => Target.IsMacOnly && MacBlock is not null;

    /// <summary>The platform the build is asked for: PC + Mac falls back to PC when the Mac
    /// package is ruled out, so the build never has to drop it on its own.</summary>
    private ExportPlatform BuildPlatform =>
        Target.SelectedPlatformOption.Value == ExportPlatform.PcAndMac && MacBlock is not null
            ? ExportPlatform.Pc
            : Target.SelectedPlatformOption.Value;

    /// <summary>One platform's package path for the current output field.</summary>
    private string PackagePath(bool mac) => RocksmithPlatformTargets.OutputPathFor(OutputPath, BuildPlatform, mac);

    /// <summary>The save's own assembly step, so the export sees the current lyrics, BPM and audio metadata.</summary>
    private void Assemble(ChartProject project) =>
        _io.Documents.AssembleProject(project, _io.Transport.Bpm, _edit.State.CurrentAudioFileName, _edit.Editor.LyricPhrases);

    /// <summary>One row per instrumental arrangement; <paramref name="ticks" /> keeps the user's choices, by name (names are not unique).</summary>
    private void RefreshArrangements(ChartProject project, IReadOnlyDictionary<string, bool>? ticks)
    {
        foreach (var row in Arrangements) row.PropertyChanged -= OnRowChanged;
        Arrangements.Clear();
        foreach (var a in project.Arrangements.Where(a => a.ArrangementType != ArrangementType.Vocal))
        {
            var row = new ExportArrangementRow
            {
                Name = a.Name, TypeLabel = ArrangementTypeLabel.For(a.Name, a.ArrangementType.ToString()),
                IsSelected = ticks is null || !ticks.TryGetValue(a.Name, out var was) || was,
                IsExtendedString = RocksmithPackageBuilder.IsExtendedStringGuitar(a)
            };
            row.PropertyChanged += OnRowChanged;
            Arrangements.Add(row);
        }

        HasVocals = project.VocalTracks.Any(t => t.Phrases.Any(p => p.Lyrics.Count > 0));
        HasExtendedStringGuitar = project.Arrangements.Any(RocksmithPackageBuilder.IsExtendedStringGuitar);
        OnGateChanged();
    }

    private void Prefill(ChartProject project, ExportDraft? draft)
    {
        var config = _edit.Config.LoadAppConfig();
        var title = string.IsNullOrWhiteSpace(project.Chart.SongName) ? "Untitled" : project.Chart.SongName.Trim();
        var artist = string.IsNullOrWhiteSpace(project.Chart.Artist) ? "Unknown Artist" : project.Chart.Artist.Trim();
        var derivedKey = RocksmithDlcKey.Create(artist, title);
        _derivedKey = derivedKey;
        DlcKeyWatermark = _edit.Localization["Dialog_Export_DlcKeyAuto"] + " — " + derivedKey;
        var projectPath = _edit.State.CurrentProjectPath;

        DlcKey = draft?.DlcKey ?? "";
        Version = string.IsNullOrWhiteSpace(project.Chart.Version) ? "1" : project.Chart.Version;
        TestFolder = config.ExportTestFolder ?? "";
        PreviewAudioPath = draft?.PreviewAudioPath ?? "";
        AlbumArtPath = draft?.AlbumArtPath ?? "";
        AppId = draft?.AppId ?? (string.IsNullOrWhiteSpace(config.LastExportAppId) ? "248750" : config.LastExportAppId);
        AudioVolume = draft?.AudioVolume ?? config.ExportAudioVolume;
        PreviewVolume = draft?.PreviewVolume ?? config.ExportPreviewVolume;
        IncludeVocals = draft?.IncludeVocals ?? config.ExportIncludeVocals;
        SplitExtendedStringChords = draft?.SplitExtendedStringChords ?? false;
        OutputPath = !string.IsNullOrWhiteSpace(draft?.OutputPath) ? draft.OutputPath
            : Path.Combine(ExportPathDefaults.ExportDirectory(config, projectPath, _io.Files),
                $"{RocksmithReleaseName.For(project, Version)}_p.psarc");
        AudioPath = !string.IsNullOrWhiteSpace(draft?.AudioPath) ? draft.AudioPath
            : ExportPathDefaults.ProjectAudio(_edit.State.CurrentAudioFileName, projectPath, _io.Files) ?? "";
        if (string.IsNullOrWhiteSpace(AlbumArtPath)) AlbumArtPath = ExportPathDefaults.CoverArt(project, projectPath, _io.Files);
    }

    private void SaveDraft()
    {
        if (_project is null) return;
        _memory.ExportDraftProject = _project;
        _memory.ExportDraft = new ExportDraft
        {
            DlcKey = DlcKey, AppId = AppId, AudioPath = AudioPath, PreviewAudioPath = PreviewAudioPath,
            AlbumArtPath = AlbumArtPath, OutputPath = OutputPath, AudioVolume = AudioVolume, PreviewVolume = PreviewVolume,
            IncludeVocals = IncludeVocals,
            SplitExtendedStringChords = SplitExtendedStringChords,
            Ticks = Arrangements.GroupBy(a => a.Name).ToDictionary(g => g.Key, g => g.Last().IsSelected, StringComparer.Ordinal)
        };
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExportArrangementRow.IsSelected)) OnGateChanged();
    }

    private void OnGateChanged()
    {
        OnPropertyChanged(nameof(HasBlockingErrors));
        OnPropertyChanged(nameof(NothingSelectedToExport));
        OnPropertyChanged(nameof(MissingPaths));
        OnPropertyChanged(nameof(HasInvalidDlcKey));
        OnPropertyChanged(nameof(MacBlock));
        OnPropertyChanged(nameof(PlatformNote));
        OnPropertyChanged(nameof(HasPlatformNote));
        foreach (var command in new[] { _export, _testExport, _autoVolume, _autoPreviewVolume }) command.NotifyCanExecuteChanged();
        RaiseExportBlocked();
    }

    private void RaiseExportBlocked()
    {
        OnPropertyChanged(nameof(ExportBlockedReason));
        OnPropertyChanged(nameof(IsExportBlocked));
    }

    partial void OnOutputPathChanged(string value) => OnGateChanged();
    partial void OnAudioPathChanged(string value) => OnGateChanged();
    partial void OnPreviewAudioPathChanged(string value) => OnGateChanged();
    partial void OnDlcKeyChanged(string value) => OnGateChanged();

    /// <summary>
    ///     The release file name follows the version while it still is the one the dialog derived:
    ///     a path the user typed or picked keeps its name.
    /// </summary>
    partial void OnVersionChanged(string? oldValue, string newValue) => OutputPath = _project is null
        ? OutputPath
        : RocksmithReleaseName.Rename(OutputPath, _project, oldValue ?? "1", newValue, Target.IsMacOnly) ?? OutputPath;

    partial void OnIsMeasuringVolumeChanged(bool value)
    {
        foreach (var command in new[] { _autoVolume, _autoPreviewVolume }) command.NotifyCanExecuteChanged();
    }
    partial void OnIncludeVocalsChanged(bool value) => OnGateChanged();
}
