namespace DotCharter.Services.Import.Youtube;

/// <summary>
///     What File ▸ Audio from YouTube accepts: an http(s) link to a single video on
///     youtube.com, music.youtube.com, m.youtube.com or youtu.be. Anything else is
///     refused before yt-dlp starts, so the field cannot fetch from an
///     arbitrary site (yt-dlp would happily try) and a pasted playlist link still
///     names one video (the downloader passes <c>--no-playlist</c>).
/// </summary>
public static class YoutubeUrl
{
    private static readonly string[] Hosts = ["youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "youtu.be"];

    /// <summary>The trimmed absolute URL when <paramref name="text" /> is a YouTube video link; null otherwise.</summary>
    public static string? TryNormalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal)) trimmed = "https://" + trimmed;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;
        if (!Hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)) return null;
        var hasVideo = uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)
            ? uri.AbsolutePath.Trim('/').Length > 0
            : uri.Query.Contains("v=", StringComparison.Ordinal) || uri.AbsolutePath.StartsWith("/shorts/", StringComparison.OrdinalIgnoreCase)
                                                                 || uri.AbsolutePath.StartsWith("/live/", StringComparison.OrdinalIgnoreCase);
        return hasVideo ? uri.AbsoluteUri : null;
    }
}
