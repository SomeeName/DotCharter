using System.Text.RegularExpressions;
using DotCharter.Services.Export.Audio;
using DotCharter.Services.Import.Youtube;

namespace DotCharter.Tests.Services.Import.Youtube;

/// <summary>
///     Every yt-dlp command line the app builds, pinned, and checked against the
///     bundled yt-dlp's own <c>--help</c>: a stubbed process runner never runs the
///     line, so an option yt-dlp does not have would only fail for users.
/// </summary>
public sealed partial class YtDlpArgumentTests
{
    private const string FfmpegDir = @"C:\tools";
    private const string OutputTemplate = @"C:\out\song.%(ext)s";
    private const string VideoUrl = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";

    /// <summary>Overrides where the bundled yt-dlp is looked for.</summary>
    private const string YtDlpVariable = "DOTCHARTER_YTDLP";

    public static TheoryData<string> CommandLines() => ["download"];

    private static IReadOnlyList<string> CommandLine(string name) => name switch
    {
        "download" => YtDlpAudioDownloader.BuildArguments(FfmpegDir, OutputTemplate, VideoUrl),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null)
    };

    [Fact]
    public void DownloadArguments_ArePinned() =>
        Assert.Equal(
        [
            "-x",
            "--audio-format", "mp3",
            "--audio-quality", "0",
            "--no-playlist",
            "--newline",
            "--ffmpeg-location", FfmpegDir,
            "-o", OutputTemplate,
            "--", VideoUrl
        ], YtDlpAudioDownloader.BuildArguments(FfmpegDir, OutputTemplate, VideoUrl));

    [Theory]
    [MemberData(nameof(CommandLines))]
    public void EveryOption_IsListedByTheBundledYtDlpHelp(string name)
    {
        var ytDlp = BundledYtDlp();
        Assert.SkipWhen(ytDlp is null,
            $"no bundled yt-dlp (tools/redist/win-x64/yt-dlp.exe or %{YtDlpVariable}%) — nothing to check the options against");

        // --help only: it reads nothing from the network.
        var result = MediaProcess.Run(ytDlp!, ["--help"], TimeSpan.FromSeconds(60),
            TestContext.Current.CancellationToken);
        Assert.True(result.ExitCode == 0, $"yt-dlp --help exited {result.ExitCode}: {result.StandardError}");
        var help = result.StandardOutput;
        Assert.Contains("--dump-single-json", help);

        var missing = Options(CommandLine(name))
            .Where(option => !Regex.IsMatch(help, $@"(?<![\w-]){Regex.Escape(option)}(?![\w-])"))
            .ToList();
        Assert.True(missing.Count == 0,
            $"the {name} command line passes option(s) yt-dlp {ytDlp} does not list in --help: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Options_SkipValuesAndTheTerminator() =>
        Assert.Equal(["-x", "--audio-format", "--no-playlist", "-o"],
            Options(["-x", "--audio-format", "mp3", "--no-playlist", "-o", "out", "--", "-url"]));

    /// <summary>The switches in an argument list: stops at the <c>--</c>
    /// terminator (what follows is a positional), and skips option values.</summary>
    private static List<string> Options(IReadOnlyList<string> args) =>
        args.TakeWhile(arg => arg != "--").Where(arg => OptionToken().IsMatch(arg)).ToList();

    /// <summary><c>%DOTCHARTER_YTDLP%</c>, else the release script's pinned cache
    /// <c>tools/redist/win-x64/yt-dlp.exe</c> in the repo or any parent (an agent
    /// worktree under <c>.claude/worktrees/</c> finds the main checkout's).</summary>
    private static string? BundledYtDlp()
    {
        var configured = Environment.GetEnvironmentVariable(YtDlpVariable);
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        if (!OperatingSystem.IsWindows()) return null;

        for (var dir = new DirectoryInfo(TestPaths.RepoRoot()); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "redist", "win-x64", "yt-dlp.exe");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    [GeneratedRegex(@"^--?[A-Za-z][\w-]*$")]
    private static partial Regex OptionToken();
}
