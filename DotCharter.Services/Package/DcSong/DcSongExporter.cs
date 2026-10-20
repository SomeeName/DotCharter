using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Cache;

namespace DotCharter.Services.Package.DcSong;

/// <summary>What the export dialog asks about a song before it offers anything.</summary>
/// <param name="AudioPath">The song audio, absolute.</param>
/// <param name="ProjectDirectory">The project's folder, the base of a relative stem path; "" for none.</param>
/// <param name="Stems">The project's declared stems (a snapshot; never modified).</param>
public sealed record DcSongProbeRequest(string AudioPath, string ProjectDirectory, IReadOnlyList<Stem> Stems);

/// <summary>
///     What a package can be written from: whether ffmpeg is there to encode it at all,
///     the song audio's size, and the stems already separated for the song (the files on
///     disk, under their package names). Read off the UI thread.
/// </summary>
public sealed record DcSongExportProbe(
    bool EncoderAvailable,
    bool AudioExists,
    long AudioBytes,
    IReadOnlyList<DcSongStemCandidate> Stems);

/// <summary>One stem a package can carry: its package name, its file and the file's size.</summary>
public sealed record DcSongStemCandidate(string Name, string SourcePath, long Bytes);

/// <summary>
/// The app's door onto <see cref="DcSongWriter" /> (File ▸ Export ▸ DotCharter song).
///     Probing and writing both run off the calling thread; nothing here talks to the user.
/// </summary>
public interface IDcSongExporter
{
    Task<DcSongExportProbe> ProbeAsync(DcSongProbeRequest request, CancellationToken cancellationToken = default);

    /// <summary>Writes the package. Throws <see cref="DcSongWriteException" /> for a request it
    /// cannot honour and <see cref="OperationCanceledException" /> when cancelled.</summary>
    Task<DcSongWriteResult> WriteAsync(
        DcSongWriteRequest request, IProgress<DcSongWriteProgress>? progress, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class DcSongExporter : IDcSongExporter
{
    private readonly IAppLogger _logger;
    private readonly Func<string?> _configuredFfmpegPath;
    private readonly Func<bool> _encoderAvailable;

    /// <param name="logger">The log sink.</param>
    /// <param name="configuredFfmpegPath">Reads <c>AppConfig.FfmpegPath</c>; read again on every probe
    /// and write, so an ffmpeg installed while the app runs is found.</param>
    public DcSongExporter(IAppLogger logger, Func<string?> configuredFfmpegPath)
        : this(logger, configuredFfmpegPath,
            () => new FfmpegStemEncoder(configuredFfmpegPath: configuredFfmpegPath).IsExternalEncoderAvailable)
    {
    }

    /// <summary>Test seam: an encoder probe that answers without looking for ffmpeg.</summary>
    internal DcSongExporter(IAppLogger logger, Func<string?> configuredFfmpegPath, Func<bool> encoderAvailable)
    {
        _logger = logger;
        _configuredFfmpegPath = configuredFfmpegPath;
        _encoderAvailable = encoderAvailable;
    }

    public Task<DcSongExportProbe> ProbeAsync(DcSongProbeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stems = request.Stems.ToList();
        return Task.Run(() => Probe(request, stems), cancellationToken);
    }

    public Task<DcSongWriteResult> WriteAsync(
        DcSongWriteRequest request, IProgress<DcSongWriteProgress>? progress, CancellationToken cancellationToken) =>
        new DcSongWriter(_logger, _configuredFfmpegPath).WriteAsync(request, progress, cancellationToken);

    private DcSongExportProbe Probe(DcSongProbeRequest request, IReadOnlyList<Stem> declared)
    {
        var encoder = _encoderAvailable();
        var audio = new FileInfo(request.AudioPath);
        var audioExists = !string.IsNullOrWhiteSpace(request.AudioPath) && audio.Exists;
        var stems = FindStems(request, declared);
        _logger.Info(AppLogCategory.Project,
            $".dcsong export probe: ffmpeg={(encoder ? "found" : "missing")}, audio={(audioExists ? audio.Length + " bytes" : "missing")}, " +
            $"stems={stems.Count} [{string.Join(", ", stems.Select(s => s.Name))}].");
        return new DcSongExportProbe(encoder, audioExists, audioExists ? audio.Length : 0, stems);
    }

    /// <summary>The declared stems whose files exist, under unique package names, at most
    /// <see cref="DcSongFormat.MaxStems" />; a file declared twice is offered once.</summary>
    private static List<DcSongStemCandidate> FindStems(DcSongProbeRequest request, IReadOnlyList<Stem> declared)
    {
        var legacyDir = Path.GetDirectoryName(request.AudioPath) ?? "";
        var found = new List<(string Name, string Path, long Bytes)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stem in declared)
        {
            var path = StemFolderProbe.ResolveStemPath(request.ProjectDirectory, legacyDir, stem.Path);
            if (string.IsNullOrEmpty(path)) continue;
            var file = new FileInfo(path);
            if (!file.Exists || !seen.Add(file.FullName)) continue;
            found.Add((stem.Name, file.FullName, file.Length));
            if (found.Count == DcSongFormat.MaxStems) break;
        }

        var names = DcSongStemNames.ForPackage(found.Select(f => f.Name).ToList());
        return found.Select((f, i) => new DcSongStemCandidate(names[i], f.Path, f.Bytes)).ToList();
    }
}
