using DotCharter.Services.State;

namespace DotCharter.Services.Import.Youtube;

/// <summary>
/// The file half of File ▸ Audio from YouTube: where the mp3 lands and how a
///     download the user did not keep goes away. A saved project downloads straight into
///     its own folder, so attaching references the file in place; an unsaved one into
///     <c>%LOCALAPPDATA%\DotCharter\YouTube</c>, which outlives the session the way the
///     absolute path the attach then records has to.
/// </summary>
public sealed class YoutubeAudioDownload(IYtDlpAudioDownloader ytDlp, ProjectState state)
{
    /// <summary>yt-dlp and ffmpeg are both resolvable.</summary>
    public bool IsAvailable => ytDlp.IsAvailable;

    /// <summary>The open project's folder, or the per-user download folder when the project has never been saved.</summary>
    public string OutputDirectory()
    {
        var projectPath = state.CurrentProjectPath;
        if (!string.IsNullOrWhiteSpace(projectPath) && Path.GetDirectoryName(Path.GetFullPath(projectPath)) is { Length: > 0 } folder)
            return folder;
        return Path.Combine(UserDataFolders.LocalAppData, "YouTube");
    }

    /// <summary>Downloads <paramref name="url" /> to <c>youtube-&lt;stamp&gt;.mp3</c> in <see cref="OutputDirectory" />.</summary>
    public Task<YtDlpDownloadResult> DownloadAsync(string url, IProgress<double>? progress, CancellationToken ct)
    {
        var folder = OutputDirectory();
        Directory.CreateDirectory(folder);
        var basePath = Path.Combine(folder, $"youtube-{DateTime.Now:yyyyMMdd-HHmmss}");
        return ytDlp.DownloadMp3Async(url, basePath, progress, ct);
    }

    /// <summary>Removes a download the attach did not keep (the user declined to replace the song audio, or the attach failed).</summary>
    public static void Discard(string path) => BestEffortDelete.File(path);
}
