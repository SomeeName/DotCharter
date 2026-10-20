using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Project;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Export;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Package.DcSong;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Shell;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>What the song-package export reads, picks and writes through, in one part so it stays inside seven dependencies.</summary>
public sealed record DcSongExportServices(
    IEditorTransport Transport, IProjectDocumentService Documents, IFileStorageService Storage,
    IExportTargetWriter Files, IShellLauncher Shell, IDcSongExporter Exporter);

/// <summary>One arrangement row; <see cref="Index" /> is its place in the project's list.</summary>
public sealed partial class DcSongArrangementRow : ObservableObject, IDcSongArrangementRow
{
    [ObservableProperty] private bool _isSelected = true;

    public required int Index { get; init; }

    public required string Name { get; init; }

    public required string TypeLabel { get; init; }

    public override string ToString() => Name;
}

/// <summary>
/// File ▸ Export ▸ DotCharter song, one instance per open. It assembles the project
///     the way a save does, asks the exporter off the UI thread whether ffmpeg is there and which
///     stems exist, and gates Export on every clause that would make the write fail, each with
///     its reason on screen. The pickers, the write and its outcome are in
///     <c>DcSongExportViewModel.Write.cs</c>.
/// </summary>
public sealed partial class DcSongExportViewModel : DialogViewModel<bool>, IDcSongExportScreen, ICancelableDialog, IDisposable
{
    private const int FlacIndex = 1;

    private readonly ChartEditDialogServices _edit;
    private readonly DcSongExportServices _io;
    private readonly ChartProject? _project;
    private readonly string _audioPath = "";
    private readonly string _projectDirectory = "";
    private readonly string _libraryFolder = "";
    private readonly CancellationTokenSource _probeCts = new();
    private readonly AsyncRelayCommand _export;
    private readonly RelayCommand _cancelWrite;
    private DcSongExportProbe? _probe;
    private bool _probeFailed;

    [ObservableProperty] private int _audioQualityIndex;
    private bool _includeStems;
    [ObservableProperty] private string _coverPath = "";
    [ObservableProperty] private string _bannerPath = "";
    [ObservableProperty] private string _outputPath = "";

    public DcSongExportViewModel(ChartEditDialogServices edit, DcSongExportServices io)
    {
        _edit = edit;
        _io = io;
        var loc = edit.Localization;
        AudioQualityOptions = [loc["Dialog_DcSong_Quality_Opus"], loc["Dialog_DcSong_Quality_Flac"]];
        _export = new AsyncRelayCommand(ExportAsync, CanExport);
        _cancelWrite = new RelayCommand(() => _writeCts?.Cancel(), () => IsWriting);
        BrowseCoverCommand = new AsyncRelayCommand(async () => CoverPath = await PickImageAsync("Picker_SelectAlbumArt", CoverPath) ?? CoverPath);
        BrowseBannerCommand = new AsyncRelayCommand(async () => BannerPath = await PickImageAsync("Picker_DcSong_Banner", BannerPath) ?? BannerPath);
        BrowseOutputCommand = new AsyncRelayCommand(BrowseOutputAsync);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
        CloseCommand = new RelayCommand(() => TryCancel());

        _project = edit.State.ActiveProject;
        if (_project is null)
        {
            SongLine = "";
            CoverNote = "";
            ProbeTask = Task.CompletedTask;
            return;
        }

        Assemble(_project);
        var chart = _project.Chart;
        SongLine = string.Join(" — ", new[] { chart.Artist, chart.SongName }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
        CoverNote = loc[string.IsNullOrWhiteSpace(chart.CoverArtPath) ? "Dialog_DcSong_Cover_NoneDesc" : "Dialog_DcSong_Cover_OwnDesc"];
        for (var i = 0; i < _project.Arrangements.Count; i++)
        {
            var a = _project.Arrangements[i];
            var row = new DcSongArrangementRow
            {
                Index = i, Name = a.Name, TypeLabel = ArrangementTypeLabel.For(a.Name, a.ArrangementType.ToString())
            };
            row.PropertyChanged += (_, _) => OnGateChanged();
            Arrangements.Add(row);
        }

        var projectPath = edit.State.CurrentProjectPath;
        _projectDirectory = string.IsNullOrWhiteSpace(projectPath) ? "" : Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? "";
        _audioPath = ResolveAudio(edit.State.CurrentAudioFileName, _projectDirectory);
        var config = edit.Config.LoadAppConfig();
        _libraryFolder = !string.IsNullOrWhiteSpace(config.PsarcLibraryPath) && io.Files.DirectoryExists(config.PsarcLibraryPath)
            ? config.PsarcLibraryPath
            : "";
        var folder = _libraryFolder.Length > 0 ? _libraryFolder : ExportPathDefaults.ExportDirectory(config, projectPath, io.Files);
        OutputPath = Path.Combine(folder, DefaultFileName(chart.Artist, chart.SongName));
        ProbeTask = ProbeAsync(_project);
        edit.Logger.Info(AppLogCategory.Project,
            $".dcsong export dialog opened. Song='{chart.SongName}', arrangements={Arrangements.Count}, audio='{_audioPath}', output='{OutputPath}'.");
    }

    public string SongLine { get; }

    public IReadOnlyList<string> AudioQualityOptions { get; }

    public ObservableCollection<DcSongArrangementRow> Arrangements { get; } = [];
    IReadOnlyList<IDcSongArrangementRow> IDcSongExportScreen.Arrangements => Arrangements;

    public bool IsLossless => AudioQualityIndex == FlacIndex;

    public string AudioQualityNote => _edit.Localization[IsLossless ? "Dialog_DcSong_Quality_FlacDesc" : "Dialog_DcSong_Quality_OpusDesc"];

    public bool CanIncludeStems => _probe is { Stems.Count: > 0 };

    /// <summary>Off by default; stays off while there is nothing to include.</summary>
    public bool IncludeStems
    {
        get => _includeStems;
        set
        {
            if (SetProperty(ref _includeStems, value && CanIncludeStems)) OnPropertyChanged(nameof(EstimatedSize));
        }
    }

    public string StemsNote
    {
        get
        {
            var loc = _edit.Localization;
            if (_probe is null) return _probeFailed ? "" : loc["Dialog_DcSong_Checking"];
            if (_probe.Stems.Count == 0) return loc["Dialog_DcSong_Stems_NoneDesc"];
            var names = string.Join(", ", _probe.Stems.Select(s => s.Name));
            var bytes = DcSongSizeEstimate.StemBytes(_edit.State.TotalAudioLengthMs, _probe.Stems);
            return loc.Plural("Dialog_DcSong_Stems_DescOne", "Dialog_DcSong_Stems_Desc", _probe.Stems.Count, names, Megabytes(bytes));
        }
    }

    public string CoverNote { get; }

    public string OutputNote
    {
        get
        {
            if (_libraryFolder.Length == 0) return _edit.Localization["Dialog_DcSong_Output_NoLibraryDesc"];
            return _edit.Localization[IsInLibrary(OutputPath) ? "Dialog_DcSong_Output_InLibraryDesc" : "Dialog_DcSong_Output_OutsideLibraryDesc"];
        }
    }

    public string EstimatedSize
    {
        get
        {
            var loc = _edit.Localization;
            var lengthMs = _edit.State.TotalAudioLengthMs;
            if (_probe is null || lengthMs <= 0) return loc.Format("Dialog_DcSong_SizeMeta", "—");
            var bytes = DcSongSizeEstimate.Bytes(lengthMs, IsLossless, _audioPath, _probe.AudioBytes,
                IncludeStems ? _probe.Stems : []);
            return loc.Format("Dialog_DcSong_SizeMeta", loc.Format("Dialog_DcSong_SizeValue", Megabytes(bytes)));
        }
    }

    /// <summary>Completes when the off-thread probe has landed (tests await it).</summary>
    internal Task ProbeTask { get; }

    public ICommand BrowseCoverCommand { get; }
    public ICommand BrowseBannerCommand { get; }
    public ICommand BrowseOutputCommand { get; }
    public IAsyncRelayCommand ExportCommand => _export;
    ICommand IDcSongExportScreen.ExportCommand => _export;
    public ICommand CancelWriteCommand => _cancelWrite;
    public ICommand OpenOutputFolderCommand { get; }
    public ICommand CloseCommand { get; }

    public string ExportBlockedReason => IsWriting ? "" : string.Join("\n", BlockingReasons());

    public bool IsExportBlocked => ExportBlockedReason.Length > 0;

    /// <summary>While a write runs, Esc and Close cancel the write and the dialog stays open.</summary>
    public bool TryCancel()
    {
        if (IsWriting)
        {
            _writeCts?.Cancel();
            return false;
        }

        Close(HasResult);
        return true;
    }

    public void Dispose()
    {
        _probeCts.Cancel();
        _writeCts?.Cancel();
    }

    /// <summary>Every clause that keeps Export grey, in the order the dialog prints them.</summary>
    internal IReadOnlyList<string> BlockingReasons()
    {
        var loc = _edit.Localization;
        if (_project is null) return [loc["Status_Export_NoProject"]];
        var reasons = new List<string>(4);
        if (_probeFailed)
        {
            reasons.Add(loc["Dialog_DcSong_ProbeFailed"]);
        }
        else if (_probe is null)
        {
            reasons.Add(loc["Dialog_DcSong_Checking"]);
        }
        else
        {
            if (!_probe.EncoderAvailable) reasons.Add(loc["Dialog_DcSong_FfmpegMissing"]);
            if (!_probe.AudioExists) reasons.Add(loc["Dialog_DcSong_AudioMissing"]);
        }

        if (!Arrangements.Any(a => a.IsSelected)) reasons.Add(loc["Dialog_DcSong_NothingSelected"]);
        if (string.IsNullOrWhiteSpace(OutputPath)) reasons.Add(loc["Dialog_DcSong_NoOutput"]);
        return reasons;
    }

    private bool CanExport() => !IsWriting && BlockingReasons().Count == 0;

    private async Task ProbeAsync(ChartProject project)
    {
        try
        {
            _probe = await _io.Exporter.ProbeAsync(new DcSongProbeRequest(_audioPath, _projectDirectory, project.Stems.ToList()),
                _probeCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _edit.Logger.Error(AppLogCategory.Project, ".dcsong export: the ffmpeg and stem check failed.", ex);
            _probeFailed = true;
        }

        if (!CanIncludeStems) IncludeStems = false;
        OnPropertyChanged(nameof(CanIncludeStems));
        OnPropertyChanged(nameof(StemsNote));
        OnPropertyChanged(nameof(EstimatedSize));
        OnGateChanged();
    }

    private void Assemble(ChartProject project) =>
        _io.Documents.AssembleProject(project, _io.Transport.Bpm, _edit.State.CurrentAudioFileName, _edit.Editor.LyricPhrases);

    private bool IsInLibrary(string path)
    {
        if (_libraryFolder.Length == 0 || string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_libraryFolder)) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private string Megabytes(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.CurrentCulture);

    private static string ResolveAudio(string audioFileName, string projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(audioFileName)) return "";
        return Path.IsPathRooted(audioFileName) || projectDirectory.Length == 0
            ? audioFileName
            : Path.Combine(projectDirectory, audioFileName);
    }

    /// <summary>"Artist - Title.dcsong", with every character a file name cannot hold replaced.</summary>
    internal static string DefaultFileName(string artist, string title)
    {
        var parts = new[] { artist, title }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        var name = parts.Count == 0 ? "song" : string.Join(" - ", parts);
        var invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return (name.Length == 0 ? "song" : name) + DcSongFormat.Extension;
    }

    private void OnGateChanged()
    {
        _export.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ExportBlockedReason));
        OnPropertyChanged(nameof(IsExportBlocked));
    }

    partial void OnAudioQualityIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsLossless));
        OnPropertyChanged(nameof(AudioQualityNote));
        OnPropertyChanged(nameof(EstimatedSize));
    }

    partial void OnOutputPathChanged(string value)
    {
        OnPropertyChanged(nameof(OutputNote));
        OnGateChanged();
    }
}
