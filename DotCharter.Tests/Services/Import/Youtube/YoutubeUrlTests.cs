using DotCharter.Services.Import.Youtube;

namespace DotCharter.Tests.Services.Import.Youtube;

/// <summary>What File ▸ Audio from YouTube accepts as a link (#820), and where its download lands.</summary>
public sealed class YoutubeUrlTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("  https://youtu.be/dQw4w9WgXcQ  ", "https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("youtube.com/watch?v=dQw4w9WgXcQ&t=42s", "https://youtube.com/watch?v=dQw4w9WgXcQ&t=42s")]
    [InlineData("https://music.youtube.com/watch?v=abc", "https://music.youtube.com/watch?v=abc")]
    [InlineData("https://m.youtube.com/watch?v=abc", "https://m.youtube.com/watch?v=abc")]
    [InlineData("https://www.youtube.com/shorts/abc", "https://www.youtube.com/shorts/abc")]
    public void A_video_link_is_accepted(string text, string expected) => Assert.Equal(expected, YoutubeUrl.TryNormalize(text));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("https://example.com/watch?v=abc")]
    [InlineData("https://youtube.com.evil.example/watch?v=abc")]
    [InlineData("https://www.youtube.com/")]
    [InlineData("https://www.youtube.com/@channel")]
    [InlineData("https://youtu.be/")]
    [InlineData("ftp://www.youtube.com/watch?v=abc")]
    [InlineData("file:///C:/song.mp3")]
    [InlineData("--exec calc")]
    public void Anything_else_is_refused(string? text) => Assert.Null(YoutubeUrl.TryNormalize(text));

    private sealed class NoTools : IYtDlpAudioDownloader
    {
        public bool IsAvailable => false;
        public string? YtDlpPath => null;
        public string? FfmpegPath => null;

        public Task<YtDlpDownloadResult> DownloadMp3Async(string videoUrl, string outputBasePathNoExtension,
            IProgress<double>? progress = null, CancellationToken cancellationToken = default, TimeSpan? timeout = null) =>
            throw new NotSupportedException();
    }

    [Fact]
    public void A_saved_project_downloads_into_its_own_folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dc-yt-out");
        var state = new ProjectState { CurrentProjectPath = Path.Combine(folder, "project.rscp") };

        Assert.Equal(Path.GetFullPath(folder), new YoutubeAudioDownload(new NoTools(), state).OutputDirectory());
    }

    [Fact]
    public void An_unsaved_project_downloads_into_the_per_user_folder()
    {
        var download = new YoutubeAudioDownload(new NoTools(), new ProjectState());

        Assert.Equal(Path.Combine(DotCharter.Tone.Infrastructure.Storage.UserDataFolders.LocalAppData, "YouTube"),
            download.OutputDirectory());
    }
}
