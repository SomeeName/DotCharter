using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
/// File ▸ Audio from YouTube: paste a video link, accept the one-time rights
/// notice, download with named progress that Cancel really stops.
///     <see cref="IDialogScreen{TResult}.Result" /> is the downloaded mp3 for the launcher to
///     attach to the open project; null for a cancel.
/// </summary>
public interface IYoutubeAudioScreen : IDialogScreen<string?>
{
    string Url { get; set; }

    /// <summary>yt-dlp and ffmpeg are both missing or one of them is: say how to get them.</summary>
    bool ShowToolsMissing { get; }

    /// <summary>
    /// The tools are there but the one-time rights notice is unanswered: show it;
    ///     nothing can be downloaded before <see cref="AcceptRightsCommand" /> runs.
    /// </summary>
    bool ShowRightsNotice { get; }

    /// <summary>Records the acceptance in the app config, once and for good.</summary>
    ICommand AcceptRightsCommand { get; }

    /// <summary>A failure: a link that is not a YouTube video, or a download that did not finish.</summary>
    string ErrorText { get; }

    bool IsBusy { get; }

    /// <summary>0..1, meaningful while <see cref="IsProgressDeterminate" />: yt-dlp reports a percentage once the download starts.</summary>
    double Progress { get; }

    bool IsProgressDeterminate { get; }

    /// <summary>What the run is doing now.</summary>
    string ProgressText { get; }

    ICommand DownloadCommand { get; }

    /// <summary>Cancels the download while one is in flight and closes once it has unwound; closes at once otherwise.</summary>
    ICommand CancelCommand { get; }
}
