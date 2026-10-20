namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     #461: every product ffmpeg vector that encodes audio (<c>"-c:a"</c>) from a file that may
///     carry cover art maps the audio stream only (<c>FfmpegAudioOnly.Args</c>). Without it the
///     picture is mapped into the output, and the shipped minimal ffmpeg has no video encoder for it.
/// </summary>
public sealed class FfmpegAudioOnlyLintTests
{
    /// <summary>
    ///     Files whose encodes read only audio the app wrote itself, so there is no picture.
    ///     Empty since #706: <c>FfmpegStemEncoder</c> was the one entry, and the <c>.dcsong</c>
    ///     writer's lossless mode now hands it the user's own FLAC/WAV, so it maps the audio
    ///     stream like every other encoder.
    /// </summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Every_audio_encode_from_a_user_file_maps_the_audio_stream_only()
    {
        var root = TestPaths.RepoRoot();
        var encoders = Directory.EnumerateFiles(Path.Combine(root, "DotCharter.Services"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => (Path: Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'), Text: File.ReadAllText(f)))
            .Where(f => f.Text.Contains("\"-c:a\"", StringComparison.Ordinal))
            .ToList();

        Assert.True(encoders.Count >= 5, $"the scan found only {encoders.Count} audio-encoding files; it has stopped seeing the code.");
        var missing = encoders
            .Where(f => !Exempt.ContainsKey(f.Path) && !f.Text.Contains("FfmpegAudioOnly.Args", StringComparison.Ordinal))
            .Select(f => f.Path)
            .ToList();
        Assert.True(missing.Count == 0, LintReport.Of(
            "These files encode audio with ffmpeg (\"-c:a\") but do not map the audio stream only, so a file with cover art " +
            "maps the picture too and the shipped audio-only ffmpeg has no encoder for it (#461):",
            missing,
            "add FfmpegAudioOnly.Args (DotCharter.Services/Export/Audio/FfmpegAudioOnly.cs) to the argument list after the input.",
            "docs/agents/services/dcsong-format.md (FfmpegAudioOnly.Args)"));
        var stale = Exempt.Keys.Where(path => !encoders.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase))).ToList();
        Assert.True(stale.Count == 0, LintReport.Of(
            "These exemptions name a file that no longer encodes audio:", stale, "delete the entry from FfmpegAudioOnlyLintTests.Exempt."));
    }
}
