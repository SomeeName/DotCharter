namespace DotCharter.Services.Export.Audio;

/// <summary>
///     The ffmpeg output options that keep an audio transcode audio-only.
/// </summary>
/// <remarks>
///     A FLAC, MP3 or M4A with cover art carries the picture as an attached-picture
///     VIDEO stream. Without a mapping ffmpeg copies it into the output and picks
///     that format's default video encoder (Theora for ogg); the minimal ffmpeg the
///     app ships has none, so the whole command fails with "Encoder not found".
/// <c>-map 0:a:0</c> takes the first audio stream of the first input,
///     and <c>-vn -sn -dn</c> drop video, subtitle and data streams. Place it after
///     the <c>-i</c> and before the output path.
/// </remarks>
public static class FfmpegAudioOnly
{
    /// <summary>The options, in order.</summary>
    public static IReadOnlyList<string> Args { get; } = ["-map", "0:a:0", "-vn", "-sn", "-dn"];
}
