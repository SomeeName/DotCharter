using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Import.Youtube;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     File ▸ Audio from YouTube (#820): the one-time rights notice (#821) gates every
///     download, the acceptance lands in <c>AppConfig.YoutubeRightsAccepted</c>, a link
///     that is not a YouTube video never reaches yt-dlp, and a finished download is the
///     dialog's answer.
/// </summary>
public sealed class YoutubeAudioViewModelTests
{
    private sealed class CountingYtDlp(bool available = true, bool succeed = false) : IYtDlpAudioDownloader
    {
        public List<string> Urls { get; } = [];
        public bool IsAvailable => available;
        public string? YtDlpPath => available ? "yt-dlp" : null;
        public string? FfmpegPath => available ? "ffmpeg" : null;

        public Task<YtDlpDownloadResult> DownloadMp3Async(string videoUrl, string outputBasePathNoExtension,
            IProgress<double>? progress = null, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
        {
            Urls.Add(videoUrl);
            return Task.FromResult(succeed
                ? new YtDlpDownloadResult(true, outputBasePathNoExtension + ".mp3", null)
                : new YtDlpDownloadResult(false, null, "test"));
        }
    }

    private sealed class Rig : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "dc-yt-vm-" + Guid.NewGuid().ToString("N"));

        public Rig(bool accepted = false, bool available = true, bool succeed = false)
        {
            Config.AppConfig.YoutubeRightsAccepted = accepted;
            YtDlp = new CountingYtDlp(available, succeed);
            // A saved project: the download lands in its folder, never in the real per-user one.
            State.ActiveProject = new ChartProject();
            State.CurrentProjectPath = Path.Combine(_folder, "project.rscp");
            Vm = new YoutubeAudioViewModel(new YoutubeAudioDownload(YtDlp, State), Config, FakeLocalization.English(), new FakeAppLogger());
        }

        public FakeConfigurationService Config { get; } = new();
        public ProjectState State { get; } = new();
        public CountingYtDlp YtDlp { get; }
        public YoutubeAudioViewModel Vm { get; }

        public void Dispose()
        {
            Vm.Dispose();
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }
    }

    private const string Link = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";

    [Fact]
    public async Task Nothing_is_downloaded_before_the_notice_is_accepted()
    {
        using var rig = new Rig();
        rig.Vm.Url = Link;

        Assert.True(rig.Vm.ShowRightsNotice);
        Assert.False(rig.Vm.DownloadCommand.CanExecute(null), "Download must be disabled while the rights notice is unanswered");
        await rig.Vm.DownloadCommand.ExecuteAsync(null);
        Assert.Empty(rig.YtDlp.Urls);
        Assert.False(rig.Config.AppConfig.YoutubeRightsAccepted);
    }

    [Fact]
    public void Accepting_the_notice_persists_and_unlocks_the_download()
    {
        using var rig = new Rig();
        rig.Vm.Url = Link;

        rig.Vm.AcceptRightsCommand.Execute(null);

        Assert.True(rig.Config.AppConfig.YoutubeRightsAccepted);
        Assert.False(rig.Vm.ShowRightsNotice);
        Assert.True(rig.Vm.DownloadCommand.CanExecute(null));
    }

    [Fact]
    public void A_previous_acceptance_is_honoured_from_the_config()
    {
        using var rig = new Rig(accepted: true);
        Assert.True(rig.Vm.RightsAccepted);
        Assert.False(rig.Vm.ShowRightsNotice);
    }

    [Fact]
    public void Without_the_tools_the_hint_shows_and_nothing_can_be_downloaded()
    {
        using var rig = new Rig(accepted: true, available: false);
        rig.Vm.Url = Link;

        Assert.True(rig.Vm.ShowToolsMissing);
        Assert.False(rig.Vm.ShowRightsNotice);
        Assert.False(rig.Vm.DownloadCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_link_that_is_not_a_YouTube_video_never_reaches_yt_dlp()
    {
        using var rig = new Rig(accepted: true);
        rig.Vm.Url = "https://example.com/watch?v=abc";

        await rig.Vm.DownloadCommand.ExecuteAsync(null);

        Assert.Empty(rig.YtDlp.Urls);
        Assert.Equal(FakeLocalization.English()["Dialog_YoutubeAudio_InvalidUrl"], rig.Vm.ErrorText);
        Assert.False(rig.Vm.Result.IsCompleted);
    }

    [Fact]
    public async Task A_finished_download_answers_its_mp3()
    {
        using var rig = new Rig(accepted: true, succeed: true);
        rig.Vm.Url = "  youtu.be/dQw4w9WgXcQ  ";

        await rig.Vm.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(["https://youtu.be/dQw4w9WgXcQ"], rig.YtDlp.Urls);
        var path = await rig.Vm.Result;
        Assert.NotNull(path);
        Assert.EndsWith(".mp3", path, StringComparison.Ordinal);
        Assert.False(rig.Vm.IsBusy);
    }

    [Fact]
    public async Task A_failed_download_stays_open_and_says_why()
    {
        using var rig = new Rig(accepted: true);
        rig.Vm.Url = Link;

        await rig.Vm.DownloadCommand.ExecuteAsync(null);

        Assert.Single(rig.YtDlp.Urls);
        Assert.Contains("test", rig.Vm.ErrorText, StringComparison.Ordinal);
        Assert.False(rig.Vm.Result.IsCompleted);
        Assert.False(rig.Vm.IsBusy);
    }

    [Fact]
    public async Task Cancel_when_idle_closes_with_no_answer()
    {
        using var rig = new Rig(accepted: true);

        Assert.True(rig.Vm.TryCancel());

        Assert.Null(await rig.Vm.Result);
    }
}
