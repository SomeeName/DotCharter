using System.Globalization;
using DotCharter.Services.Export.Audio;
using DotCharter.Services.Import.Youtube;

namespace DotCharter.Services.Export;

/// <summary>Materializes the portable media shared by all three editor companions.</summary>
internal sealed class ExportCompanionMedia
{
    private static readonly TimeSpan TranscodeTimeout = TimeSpan.FromMinutes(5);
    private readonly IAppLogger _logger;
    private readonly Action<string, string>? _link;
    private readonly Action<string, string>? _copy;
    private readonly Func<string, IReadOnlyList<string>, TimeSpan, MediaProcess.MediaProcessResult> _run;

    internal ExportCompanionMedia(IAppLogger logger,
        Func<string, IReadOnlyList<string>, TimeSpan, MediaProcess.MediaProcessResult>? run = null,
        Action<string, string>? link = null, Action<string, string>? copy = null)
    {
        _logger = logger;
        _link = link;
        _copy = copy;
        _run = run ?? ((executable, arguments, timeout) => MediaProcess.Run(executable, arguments, timeout));
    }

    internal (string? Audio, string? Cover) Ensure(string projectPath, ChartProject snapshot, double? audioLengthMs,
        AppConfig config, string? workingAudio = null, string? cover = null, string? outputDirectory = null)
    {
        var root = ProjectFolderLayout.ProjectDirectory(projectPath);
        var export = outputDirectory ?? ProjectFolderLayout.ExportDirectory(projectPath);
        Directory.CreateDirectory(export);
        workingAudio ??= SaveCompanionService.ResolveAudio(root, snapshot.AudioFileName);
        cover ??= ProjectRelativePath.TryResolve(root, snapshot.Chart.CoverArtPath);
        string? audio = null, art = null;
        try
        {
            if (workingAudio != null)
            {
                var source = ProjectExportAudio.ForSnapshot(snapshot, workingAudio);
                audio = Path.Combine(export, "song.ogg");
                var sourceIsOgg = source.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase);
                var canonical = sourceIsOgg ? source : Encode(source, "song.ogg", [], config, root);
                if (canonical != null) ExportAssetLink.Publish(canonical, audio, _link, _copy, allowHardLink: !sourceIsOgg);
                var totalMs = audioLengthMs ?? ReadLength(source);
                var start = PreviewClip.ClampStartMs(snapshot.Chart.PreviewStartMs ?? PreviewClip.DefaultStartMs(totalMs), totalMs);
                var preview = Encode(source, "song_preview.ogg",
                    ["-ss", (start / 1000).ToString("R", CultureInfo.InvariantCulture), "-t", "28"], config, root);
                if (preview != null) ExportAssetLink.Publish(preview, Path.Combine(export, "song_preview.ogg"), _link, _copy);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Project, "Companion audio could not be prepared.", ex);
        }
        try
        {
            if (!string.IsNullOrWhiteSpace(cover) && File.Exists(cover))
            {
                art = Path.Combine(export, "cover.png");
                var sourceIsPng = cover.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
                var canonical = sourceIsPng ? cover : Encode(cover, "cover.png", ["-frames:v", "1"], config, root);
                if (canonical != null) ExportAssetLink.Publish(canonical, art, _link, _copy, allowHardLink: !sourceIsPng);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Project, "Companion cover could not be prepared.", ex);
        }
        return (audio, art);
    }

    internal static ChartProject Snapshot(ChartProject snapshot)
    {
        var copy = ChartProjectCloner.Clone(snapshot, retainIdentities: true);
        copy.AudioFileName = string.IsNullOrWhiteSpace(snapshot.AudioFileName) ? "" : "song.ogg";
        copy.Chart.CoverArtPath = string.IsNullOrWhiteSpace(snapshot.Chart.CoverArtPath) ? "" : "cover.png";
        copy.Stems.Clear();
        copy.ArrangementFiles.Clear();
        return copy;
    }

    private string? Encode(string source, string name, IReadOnlyList<string> clip, AppConfig config, string root)
    {
        var cache = Directory.CreateDirectory(Path.Combine(ProjectFolderLayout.MetadataDirectory(root), "cache")).FullName;
        var target = Path.Combine(cache, name);
        var signature = ProjectVolumes.Signature(source) + string.Join('|', clip);
        var receipt = target + ".source";
        if (File.Exists(receipt) && AtomicFile.ReadAllText(receipt) == signature && CompanionOwnership.IsUnmodified(target))
            return target;
        var ffmpeg = MediaToolResolver.Resolve("ffmpeg", AppContext.BaseDirectory, config.FfmpegPath,
            MediaToolResolver.CurrentPathDirs(), File.Exists, OperatingSystem.IsWindows());
        if (ffmpeg == null)
        {
            _logger.Info(AppLogCategory.Project, $"Companion media: ffmpeg not found; '{name}' could not be prepared.");
            return null;
        }
        var written = CompanionOwnership.WriteIfUnmodified(target, destination =>
        {
            var staging = Path.Combine(cache, $"{Guid.NewGuid():N}.writing{Path.GetExtension(name)}");
            try
            {
                var codec = name.EndsWith(".png", StringComparison.Ordinal)
                    ? (string[])[] : [.. FfmpegAudioOnly.Args, "-c:a", "libvorbis", "-q:a", "5"];
                var result = _run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-i", source,
                    .. clip, .. codec, staging], TranscodeTimeout);
                if (!result.Succeeded)
                {
                    throw new IOException(result.ExitCode == null ? "Companion media encode timed out and was killed."
                        : $"Companion media encode failed ({result.ExitCode}): {result.StandardError}");
                }
                AtomicFile.Publish(staging, destination);
            }
            finally
            {
                if (File.Exists(staging)) RecycleBin.Remove(staging);
            }
        }, out _);
        if (!written) return null;
        AtomicFile.WriteAllText(receipt, signature);
        return target;
    }

    private static double ReadLength(string source)
    {
        try
        {
            var info = JuceSharp.AudioIo.JuceAudioDecoder.Probe(source);
            return info.SampleRate > 0 ? info.Frames * 1000.0 / info.SampleRate : 0;
        }
        catch (Exception) { return 0; }
    }
}
