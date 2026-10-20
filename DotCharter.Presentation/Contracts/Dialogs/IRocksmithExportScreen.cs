using System.ComponentModel;
using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
///     File ▸ Export Rocksmith package: the target (platform, package form), the
///     arrangements to include, audio, package identity and version, the output file, the pre-export
///     findings, then the build with progress and a result. Export is greyed with its reason on
///     screen while anything blocks it. <see cref="IDialogScreen{TResult}.Result" /> is true when a
///     package was built.
/// </summary>
public interface IRocksmithExportScreen : IDialogScreen<bool>
{
    /// <summary>The export target: the platform and the package form.</summary>
    IExportTarget Target { get; }

    IReadOnlyList<IExportArrangementRow> Arrangements { get; }

    bool IncludeVocals { get; set; }

    /// <summary>A lyric track exists; without one the vocals box is greyed and says so.</summary>
    bool HasVocals { get; }

    /// <summary>
    ///     Write a 7-string guitar's chords on the 7th string as simultaneous single notes
    /// (rs7chart's "split") instead of one chord whose 7th string is in the SNG's
    /// RS7X block — for an rs7string build from before the block. Off by default.
    /// </summary>
    bool SplitExtendedStringChords { get; set; }

    /// <summary>An arrangement is a 7-/8-string guitar; the split option shows only then.</summary>
    bool HasExtendedStringGuitar { get; }

    string AudioPath { get; set; }

    string PreviewAudioPath { get; set; }

    double AudioVolume { get; set; }

    double PreviewVolume { get; set; }

    /// <summary>Measures the main audio and sets <see cref="AudioVolume" /> the way DLC Builder does.</summary>
    ICommand AutoVolumeCommand { get; }

    /// <summary>The same for the preview audio; greyed while there is none.</summary>
    ICommand AutoPreviewVolumeCommand { get; }

    /// <summary>A volume measurement is running (or the last one failed: <see cref="VolumeMessage" />).</summary>
    bool IsMeasuringVolume { get; }

    /// <summary>Why the last Auto volume produced nothing; empty otherwise.</summary>
    string VolumeMessage { get; }

    bool HasVolumeMessage { get; }

    /// <summary>No key file: every package is written unencrypted, and the
    /// dialog says so — neutrally, nothing about keys.</summary>
    bool WritesUnencrypted { get; }

    /// <summary>What the platform choice writes: both file names for PC + Mac, or why
    /// there is no Mac package (PC + Mac then writes PC alone). Empty for PC.</summary>
    string PlatformNote { get; }

    bool HasPlatformNote { get; }

    string DlcKey { get; set; }

    /// <summary>The song's package version ("1" by default): in the package and in the release file name.</summary>
    string Version { get; set; }

    /// <summary>Where Test export writes; blank uses a "test" folder beside the output file.</summary>
    string TestFolder { get; set; }

    /// <summary>The folder a blank <see cref="TestFolder" /> stands for.</summary>
    string TestFolderWatermark { get; }

    /// <summary>The key derived from the song when the field is left empty.</summary>
    string DlcKeyWatermark { get; }

    string AppId { get; set; }

    string AlbumArtPath { get; set; }

    string OutputPath { get; set; }

    /// <summary>"2 errors, 3 warnings", or that the chart passed.</summary>
    string ValidationSummary { get; }

    bool HasFindings { get; }

    IReadOnlyList<IExportFinding> ValidationItems { get; }

    /// <summary>
    /// Opens the Chart quality report over the export: the non-blocking findings too,
    ///     each with Go to and Fix; the export re-checks when it closes. The build itself still
    ///     only refuses on the blocking findings above.
    /// </summary>
    ICommand ReviewReportCommand { get; }


    /// <summary>
    ///     Why Export is greyed: every failing gate clause (Mac, an error on an included
    ///     arrangement, a short DLC key, missing paths, nothing ticked), one sentence per line, in
    /// that order. Empty when it can run. It is the one place the dialog says any of
    /// them.
    /// </summary>
    string ExportBlockedReason { get; }

    /// <summary>Export is greyed for the reason <see cref="ExportBlockedReason" /> names.</summary>
    bool IsExportBlocked { get; }

    bool IsBuilding { get; }

    double ProgressFraction { get; }

    /// <summary>"Step 3 of 7 — Writing SNG".</summary>
    string ProgressMessage { get; }

    bool HasResult { get; }

    string ResultPath { get; }

    IReadOnlyList<string> ResultWarnings { get; }

    /// <summary>Opens the Tone designer over this dialog.</summary>
    ICommand OpenToneDesignerCommand { get; }

    ICommand BrowseAudioCommand { get; }

    ICommand BrowsePreviewAudioCommand { get; }

    ICommand BrowseAlbumArtCommand { get; }

    ICommand BrowseOutputCommand { get; }

    ICommand ExportCommand { get; }

    ICommand BrowseTestFolderCommand { get; }

    /// <summary>
    ///     A test build like DLC Builder's: into the test folder, version "test"; while the game
    ///     runs it is a new <c>_vN</c> package, otherwise it replaces the earlier test builds.
    /// </summary>
    ICommand TestExportCommand { get; }

    ICommand CancelBuildCommand { get; }

    ICommand OpenOutputFolderCommand { get; }

    /// <summary>Closes; while a build runs it cancels the build instead.</summary>
    ICommand CloseCommand { get; }
}

/// <summary>The export target section: the platform and the package form.</summary>
public interface IExportTarget : INotifyPropertyChanged
{
    IReadOnlyList<IExportChoice> PlatformOptions { get; }

    IExportChoice SelectedPlatformOption { get; set; }

    /// <summary>Encrypted (the default) or Plain.</summary>
    IReadOnlyList<IExportChoice> PackageOptions { get; }

    IExportChoice SelectedPackageOption { get; set; }

    /// <summary>The picker is live; without keys it is not and Plain is the only form.</summary>
    bool CanChoosePackage { get; }
}

public interface IExportChoice
{
    string Label { get; }
}

public interface IExportArrangementRow : INotifyPropertyChanged
{
    string Name { get; }

    string TypeLabel { get; }

    bool IsSelected { get; set; }
}

/// <summary>One pre-export finding; one with a time can seek the editor behind the dialog.</summary>
public interface IExportFinding
{
    bool IsError { get; }

    /// <summary>"Rhythm · 1:23.456".</summary>
    string Location { get; }

    string Message { get; }

    bool HasTime { get; }

    ICommand JumpCommand { get; }

    /// <summary>An existing verb repairs this finding.</summary>
    bool HasFix { get; }

    /// <summary>What Fix will do, for its tooltip: "Auto-fix Lead: …".</summary>
    string FixDescription { get; }

    /// <summary>Runs the fix as ONE undo step and re-checks; a tone finding opens the Tone designer,
    /// except an undefined key with exactly one possible repair, which is renamed in place.</summary>
    ICommand FixCommand { get; }
}
