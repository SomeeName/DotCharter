using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IYoutubeAudioScreen" />. Download answers a made-up mp3 path; Cancel answers null.</summary>
public sealed class FakeYoutubeAudioScreen : FakeDialog<string?>, IYoutubeAudioScreen
{
    private FakeYoutubeAudioScreen(string url, bool busy = false)
    {
        Url = url;
        DownloadCommand = string.IsNullOrWhiteSpace(url) || busy ? Disabled() : Answer("youtube-20261014-120000.mp3");
        CancelCommand = Answer(null);
    }

    public static IReadOnlyList<FakeVariant<IYoutubeAudioScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new(FakeVariantNames.Busy, Busy)
    ];

    /// <summary>A pasted link, the rights notice already accepted.</summary>
    public static FakeYoutubeAudioScreen Default() => new("https://www.youtube.com/watch?v=dQw4w9WgXcQ");

    /// <summary>First open: no link, the one-time rights notice still unanswered.</summary>
    public static FakeYoutubeAudioScreen Empty() => new("") { ShowRightsNotice = true };

    /// <summary>A very long link and a long failure strip.</summary>
    public static FakeYoutubeAudioScreen LongText() =>
        new("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PLx0sYbCqOb8TBPRdmBHs5Iftvv9TPboYG&index=12&t=95s&pp=iAQB8AUB")
        {
            ErrorText = "Der Download ist fehlgeschlagen: yt-dlp failed (exit 1). ERROR: [youtube] dQw4w9WgXcQ: Video unavailable. " +
                        "This video is not available in your country because of a copyright claim."
        };

    /// <summary>Every strip at once: tools missing, the rights notice and an error.</summary>
    public static FakeYoutubeAudioScreen Full() => new("youtube.com/shorts/abc")
    {
        ShowToolsMissing = true,
        ShowRightsNotice = true,
        ErrorText = "That is not a link to a YouTube video."
    };

    /// <summary>A download at 42 %.</summary>
    public static FakeYoutubeAudioScreen Busy() => new("https://youtu.be/dQw4w9WgXcQ", busy: true)
    {
        IsBusy = true,
        IsProgressDeterminate = true,
        Progress = 0.42,
        ProgressText = "Downloading YouTube audio…"
    };

    public string Url { get; set; }
    public bool ShowToolsMissing { get; init; }
    public bool ShowRightsNotice { get; init; }
    public ICommand AcceptRightsCommand { get; } = new RelayCommand(() => { });
    public string ErrorText { get; init; } = "";
    public bool IsBusy { get; init; }
    public double Progress { get; init; }
    public bool IsProgressDeterminate { get; init; }
    public string ProgressText { get; init; } = "";
    public ICommand DownloadCommand { get; }
    public ICommand CancelCommand { get; }
}
