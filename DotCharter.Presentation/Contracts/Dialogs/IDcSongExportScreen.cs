using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
/// File ▸ Export ▸ DotCharter song: writes the open chart as one <c>.dcsong</c>
///     package — the chart, the song audio (Opus 192 kb/s, or lossless FLAC), a preview clip,
///     the cover and banner, and optionally the stems already separated for the song. The
///     dialog says what the package will hold and about how big it will be, writes it with
///     progress and a cancel, and says where it went. Export is greyed with its reason on
///     screen while anything blocks it — no ffmpeg above all, which the writer needs for
///     every package. <see cref="IDialogScreen{TResult}.Result" /> is true when a package was
///     written.
/// </summary>
public interface IDcSongExportScreen : IDialogScreen<bool>
{
    /// <summary>"Artist — Title", the song being packaged.</summary>
    string SongLine { get; }

    /// <summary>The two audio choices, Opus first: "Opus 192 kb/s", "FLAC (lossless)".</summary>
    IReadOnlyList<string> AudioQualityOptions { get; }

    /// <summary>0 is Opus (the default), 1 is FLAC.</summary>
    int AudioQualityIndex { get; set; }

    /// <summary>What the chosen audio is for: Opus plays, FLAC is for charting.</summary>
    string AudioQualityNote { get; }

    /// <summary>Every arrangement of the chart, ticked by default.</summary>
    IReadOnlyList<IDcSongArrangementRow> Arrangements { get; }

    /// <summary>Stems go in. Off by default; only settable when <see cref="CanIncludeStems" />.</summary>
    bool IncludeStems { get; set; }

    /// <summary>Stems separated for this song exist on disk.</summary>
    bool CanIncludeStems { get; }

    /// <summary>Which stems and what they add ("4 stems: bass, drums, other, vocals · about 20.1 MB"),
    /// or why there are none to offer.</summary>
    string StemsNote { get; }

    /// <summary>A PNG or JPEG chosen for the package; empty uses the chart's own cover art.</summary>
    string CoverPath { get; set; }

    /// <summary>What an empty cover box means for this chart: its own cover art, or none.</summary>
    string CoverNote { get; }

    /// <summary>A PNG or JPEG banner; empty writes none.</summary>
    string BannerPath { get; set; }

    string OutputPath { get; set; }

    /// <summary>Whether the output sits in the Library folder, or that no Library folder is set.</summary>
    string OutputNote { get; }

    /// <summary>"Estimated size: about 4.7 MB" (audio, preview and stems), or a dash while the
    /// song's length or the check is not known yet.</summary>
    string EstimatedSize { get; }

    /// <summary>
    ///     Why Export is greyed: every failing clause (still checking, no ffmpeg, no song audio,
    ///     nothing ticked, no output file), one sentence per line. Empty when it can run, and
    ///     while it runs. It is the one place the dialog says any of them.
    /// </summary>
    string ExportBlockedReason { get; }

    bool IsExportBlocked { get; }

    bool IsWriting { get; }

    double ProgressFraction { get; }

    /// <summary>"Step 2 of 5: Encoding the song audio".</summary>
    string ProgressMessage { get; }

    bool HasResult { get; }

    /// <summary>"Saved D:\Songs\Artist - Title.dcsong (4.6 MB)".</summary>
    string ResultMessage { get; }

    /// <summary>What the writer did that the user should know about (a skipped cover, a lossy source).</summary>
    IReadOnlyList<string> ResultWarnings { get; }

    bool HasError { get; }

    /// <summary>Why the last write failed, in the user's language.</summary>
    string ErrorMessage { get; }

    ICommand BrowseCoverCommand { get; }

    ICommand BrowseBannerCommand { get; }

    ICommand BrowseOutputCommand { get; }

    ICommand ExportCommand { get; }

    ICommand CancelWriteCommand { get; }

    ICommand OpenOutputFolderCommand { get; }

    /// <summary>Closes; while a write runs it cancels the write instead.</summary>
    ICommand CloseCommand { get; }
}

/// <summary>One arrangement the package can carry.</summary>
public interface IDcSongArrangementRow : System.ComponentModel.INotifyPropertyChanged
{
    string Name { get; }

    /// <summary>The arrangement's type beside its name ("Lead"), or "" when the name already says it.</summary>
    string TypeLabel { get; }

    bool IsSelected { get; set; }
}
