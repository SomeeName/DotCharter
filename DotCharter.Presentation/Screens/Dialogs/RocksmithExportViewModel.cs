using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Project;
using DotCharter.Core.Utilities;
using DotCharter.Localization;
using DotCharter.Services.Export.Audio;
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
    IExportTargetWriter Files, IShellLauncher Shell, IMessagePrompt Prompt, IRocksmithGameProbe Game)
{
    internal RocksmithVolumeMeasure? VolumeMeasure { get; init; }
    internal Func<string, string>? VolumeSignature { get; init; }
    internal Func<string?, string, string, RocksmithTestPackageOwnership>? TestOwnershipFactory { get; init; }
}

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
    private readonly ChartIssuesDoor _issuesDoor;
    private readonly DialogMemory _memory;
    private readonly ChartProject? _project;
    private readonly RocksmithFixes _fixes;
    private readonly AsyncRelayCommand _export;
    private readonly RelayCommand _cancelBuild;
    private readonly RelayCommand _showInIssues;
    private readonly AsyncRelayCommand _testExport;
    private readonly AsyncRelayCommand _autoVolume;
    private readonly AsyncRelayCommand _autoPreviewVolume;
    private readonly RocksmithExportPickers _pickers;
    private readonly RocksmithExportDlcBuilder _dlcBuilder;
    private readonly RocksmithExportSongNames _names;
    private string _defaultTestFolder = "";
    private readonly AsyncRelayCommand _browseOutput;
    private IReadOnlyList<ExportValidationFinding> _findings = [];
    internal bool IsDisposed { get; private set; }

    [ObservableProperty][NotifyPropertyChangedFor(nameof(TestFolderWatermark))] private string _outputPath = "";
    [ObservableProperty] private string _dlcKey = "";
    [ObservableProperty] private string _version = "1";
    [ObservableProperty] private string _testFolder = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasVolumeMessage))] private string _volumeMessage = "";
    [ObservableProperty] private bool _isMeasuringVolume;
    [ObservableProperty] private string _appId = "248750";
    [ObservableProperty] private string _audioPath = "";
    [ObservableProperty] private string _previewAudioPath = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasInvalidPreviewStart))] private string _previewStartText = "";
    [ObservableProperty] private string _albumArtPath = "";
    [ObservableProperty] private double _audioVolume = -8.0;
    [ObservableProperty] private double _previewVolume = -7.0;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(VocalsTicked))] private bool _includeVocals = true;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(VocalsTicked))] private bool _hasVocals;
    [ObservableProperty] private string _dlcKeyWatermark = "";

    public RocksmithExportViewModel(ChartEditDialogServices edit, RocksmithExportServices io, IDialogs dialogs, DialogMemory memory,
        IChartMaintenanceService maintenance, IDlcBuilderProjectWriter dlcBuilderWriter, ChartIssuesDoor issuesDoor)
    {
        _issuesDoor = issuesDoor;
        _edit = edit;
        WeakLanguageSubscription.Subscribe(edit.Localization, this,
            static screen => screen.OnPropertyChanged(nameof(MainAudioWatermark)));
        _fixes = new RocksmithFixes(maintenance, edit.Editor, edit.State, dialogs, edit.Localization, edit.Logger);
        _io = io;
        _dialogs = dialogs;
        _memory = memory;
        Target = new ExportTargetViewModel(edit.Localization);
        RocksmithExportSupport.FollowTargetName(this, UpdateOutputName, OnGateChanged);
        _names = new RocksmithExportSongNames(edit.Localization);
        _export = new AsyncRelayCommand(ExportAsync, () => Gate.CanExport);
        _testExport = new AsyncRelayCommand(TestExportAsync, () => Gate.CanTestExport(AudioPath));
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
        BrowseTestFolderCommand = new AsyncRelayCommand(() => _pickers.BrowseTestFolderAsync(this));
        _cancelBuild = new RelayCommand(() => _buildCts?.Cancel(), () => IsBuilding);
        OpenToneDesignerCommand = new RelayCommand(OpenToneDesigner);
        OpenSongMetadataCommand = new RelayCommand(() => OpenSongMetadataAsync().Observe(edit.Logger, "song metadata"), () => !IsBuilding);
        _showInIssues = new RelayCommand(ShowInIssues, () => !IsBuilding && _issuesDoor.CanOpen);
        ShowInIssuesCommand = _showInIssues;
        BrowseAudioCommand = new AsyncRelayCommand(async () => AudioPath = await _pickers.AudioAsync(AudioPath) ?? AudioPath);
        BrowsePreviewAudioCommand = new AsyncRelayCommand(
            async () => PreviewAudioPath = await _pickers.AudioAsync(PreviewAudioPath) ?? PreviewAudioPath);
        BrowseAlbumArtCommand = new AsyncRelayCommand(async () => AlbumArtPath = await _pickers.AlbumArtAsync(AlbumArtPath) ?? AlbumArtPath);
        _browseOutput = new AsyncRelayCommand(() => _pickers.BrowseOutputAsync(this, RocksmithExportSupport.FileNameTag(Target, HasPsxrSelection)));
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
        CloseCommand = new RelayCommand(() => TryCancel());
        Arrangements.CollectionChanged += (_, _) => OnGateChanged();

        _project = edit.State.ActiveProject;
        if (_project is null) return;
        Assemble(_project);
        var sameProject = ReferenceEquals(_memory.ExportDraftProject, _project) && _memory.ExportDraft is not null;
        RefreshArrangements(_project, sameProject ? _memory.ExportDraft!.Ticks : null);
        Prefill(_project, sameProject ? _memory.ExportDraft : null);
        SetOutputName(ExportPathDefaults.WithPlatformSuffix(OutputPath, Target.IsMacOnly));
        UpdateOutputName();
        Revalidate();
        edit.Logger.Info(AppLogCategory.Project,
            $"Rocksmith export dialog opened. Song='{_project.Chart.SongName}', arrangements={Arrangements.Count}, errors={ErrorCount}, warnings={WarningCount}.");
    }

    /// <summary>The PC package name the song's title, artist and version derive now; an output file named so follows them.</summary>
    internal string DerivedPackageName => _names.PackageName;

    public bool VocalsTicked
    {
        get => IncludeVocals && HasVocals;
        set => IncludeVocals = value;
    }

    public ICommand OpenSongMetadataCommand { get; }

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
    public bool HasBlockingErrors => _findings.Any(f => RocksmithExportGate.Blocks(f, Arrangements, IncludeVocals));

    /// <summary>A typed key the build would refuse: under 6 ASCII letters/digits, or any non-ASCII character.</summary>
    public bool HasInvalidDlcKey => RocksmithDlcKey.IsInvalidTypedKey(DlcKey);

    public bool HasInvalidPreviewStart => !PreviewStartTime.TryParse(PreviewStartText, out _);
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

    /// <summary>Why Mac is unavailable for accepted content: missing encryption or a Windows-only format.</summary>
    public RocksmithMacBlock? MacBlock => Target.MacUnavailableReason(HasPsxrSelection);

    /// <summary>What the platform choice will write, under the picker, or why the Mac package is ruled out.</summary>
    public string PlatformNote =>
        RocksmithExportSupport.PlatformNote(_edit.Localization, Target, MacBlock, OutputPath, PackagePath);

    public bool HasPlatformNote => PlatformNote.Length > 0;

    public ICommand OpenToneDesignerCommand { get; }
    public ICommand ShowInIssuesCommand { get; }
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
        IsDisposed = true;
        _buildCts?.Cancel();
        EndOpenWork();
        CommitVersion();
        SaveDraft();
    }

    /// <summary>
    ///     Why Export cannot run right now: every failing gate clause, one per line. Empty when Export can run
    ///     and while a build runs. Shown above the action bar and as the button's tooltip (while
    ///     <see cref="IsExportBlocked" />), and nowhere else, so a grey Export says why once.
    /// </summary>
    public string ExportBlockedReason => IsBuilding || Gate.CanExport ? "" : string.Join("\n", BlockingReasons());

    /// <summary>Export is greyed for a reason <see cref="ExportBlockedReason" /> names.</summary>
    public bool IsExportBlocked => ExportBlockedReason.Length > 0;

    /// <summary>Every gate clause that is false, in gate order, as the sentence the dialog prints for it.</summary>
    internal IReadOnlyList<string> BlockingReasons() => Gate.Reasons(_edit.Localization, MacBlock);

    private RocksmithExportGate Gate => new(IsBuilding, MacOnlyBlocked, HasBlockingErrors, HasInvalidDlcKey, HasInvalidPreviewStart, MissingPaths,
        NothingSelectedToExport);

    /// <summary>The version and the preview start typed in the dialog become the song's, as one undo step each.</summary>
    private void CommitVersion() => RocksmithExportSupport.CommitVersion(_edit.History, _project?.Chart, PackageVersion, PreviewStartText);

    /// <summary>The target is Mac alone and no Mac package can be written.</summary>
    private bool MacOnlyBlocked => Target.IsMacOnly && MacBlock is not null;

    /// <summary>The platform the build is asked for; PC + Mac falls back to PC when the Mac package is ruled out.</summary>
    private ExportPlatform BuildPlatform => RocksmithExportSupport.BuildPlatform(Target, MacBlock);

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
            var row = RocksmithExportSupport.ArrangementRow(a, ticks);
            row.PropertyChanged += OnRowChanged;
            Arrangements.Add(row);
        }

        HasVocals = project.VocalTracks.Any(t => t.Phrases.Any(p => p.Lyrics.Count > 0));
        OnGateChanged();
    }

    private void Prefill(ChartProject project, ExportDraft? draft)
    {
        var config = _edit.Config.LoadAppConfig();
        _defaultTestFolder = config.ExportTestDefaultFolder?.Trim() ?? "";
        TestFolder = draft?.TestFolderOverride ?? _defaultTestFolder;
        _names.RefreshKey(project);
        DlcKeyWatermark = _names.KeyWatermark;
        var projectPath = _edit.State.CurrentProjectPath;

        var (keepAudio, keepPreview) = RocksmithExportSupport.RestoreDraft(this, project.Chart, config, draft);
        _names.Advance(project, Version);
        // A remembered name derived from the title and artist of that day follows today's; a typed one stays.
        OutputPath = !string.IsNullOrWhiteSpace(draft?.OutputPath)
            ? RocksmithReleaseName.Retarget(draft.OutputPath, draft.DerivedPackageName, _names.PackageName) ?? draft.OutputPath
            : Path.Combine(ExportPathDefaults.ExportDirectory(config, projectPath, _io.Files),
                ExportPathDefaults.PackageFileName(project));
        AudioPath = !string.IsNullOrWhiteSpace(draft?.AudioPath) ? draft.AudioPath
            : ExportPathDefaults.ProjectAudio(project, _edit.State.CurrentAudioFileName, projectPath, _io.Files) ?? "";
        if (string.IsNullOrWhiteSpace(AlbumArtPath)) AlbumArtPath = ExportPathDefaults.CoverArt(project, projectPath, _io.Files);
        _volumeMeasurement = EnsureProjectVolumesAsync(project, keepAudio, keepPreview);
    }

    private void SaveDraft() => RocksmithExportSupport.SaveDraft(this, _project, _memory, _defaultTestFolder);

    public string MainAudioWatermark => _edit.Localization.Format("Dialog_Export_MainAudioRequired", ExportAudioFormats.DisplayText);

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        RocksmithExportSupport.RowChanged(e, UpdateOutputName, OnGateChanged);

    private bool HasPsxrSelection => RocksmithExportSupport.IsPsxr(_project, Arrangements);

    private void UpdateOutputName() => RocksmithExportSupport.UpdateOutputName(this, HasPsxrSelection, OnGateChanged);

    private void OnGateChanged()
    {
        Target.SetExtendedContent(HasPsxrSelection);
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

    internal void SetOutputName(string path) => _pickers.SetOutputName(this, path);

    partial void OnOutputPathChanged(string value) => _pickers.OutputEdited(UpdateOutputName);
    partial void OnAudioPathChanged(string value) => OnGateChanged();
    partial void OnPreviewAudioPathChanged(string value) => OnGateChanged();
    partial void OnDlcKeyChanged(string value) => OnGateChanged();
    partial void OnPreviewStartTextChanged(string value) => OnGateChanged();

    /// <summary>The song's names changed while the dialog is open: a derived output name follows them.</summary>
    private void FollowSongNames()
    {
        if (_project is null) return;
        var renamed = _names.Follow(_project, Version, OutputPath);
        DlcKeyWatermark = _names.KeyWatermark;
        if (renamed is not null) SetOutputName(renamed);
    }

    partial void OnVersionChanged(string? oldValue, string newValue)
    {
        if (_project is null) return;
        _names.Advance(_project, newValue);
        var renamed = RocksmithReleaseName.Rename(OutputPath, _project, oldValue ?? "1", newValue, Target.IsMacOnly);
        if (renamed != null) SetOutputName(renamed);
    }

    partial void OnIsMeasuringVolumeChanged(bool value)
    {
        foreach (var command in new[] { _autoVolume, _autoPreviewVolume }) command.NotifyCanExecuteChanged();
    }
    partial void OnIncludeVocalsChanged(bool value) => OnGateChanged();
}
