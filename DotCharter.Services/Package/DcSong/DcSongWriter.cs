using System.IO.Compression;
using System.Security.Cryptography;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Audio;
using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Services.Package.DcSong;

/// <summary>
///     Writes a <c>.dcsong</c> package (format: <c>docs/agents/services/dcsong-format.md</c>).
///     <para>
///         Nothing here is new machinery. The song is decoded by the editor's own
///         decoder (<see cref="PcmAudioLoader" />, so the package plays against the
///         chart exactly as the editor did), the preview is the Rocksmith export's
///         clip (<see cref="PreviewClip" />), Opus and FLAC come from the stem
///         cache's ffmpeg encoder (<see cref="FfmpegStemEncoder" />, at 192 kb/s
///         here), and the chart is the project writer's <c>.dcproj</c>
///         (<see cref="RscpChartSerializer" />).
///     </para>
///     <para>
///         The write is ATOMIC: every member is staged in a temporary folder, the zip
///         is written to <c>&lt;output&gt;.writing</c> beside the destination, flushed
///         to disk and renamed over it (<see cref="AtomicFile.ReplaceWithRetry" />,
///         the same swap <c>PsarcWriter.WriteFile</c> uses). A failed or cancelled
///         write leaves the previous package, if any, untouched.
///     </para>
/// </summary>
public sealed class DcSongWriter
{
    /// <summary>Containers whose samples are copied into FLAC as they are, so a
    /// lossless package is bit-exact to the source. Anything else is decoded first.</summary>
    private static readonly HashSet<string> LosslessContainers =
        new(StringComparer.OrdinalIgnoreCase) { ".wav", ".flac", ".aif", ".aiff" };

    private readonly IAppLogger? _logger;
    private readonly Func<string?>? _configuredFfmpegPath;
    private readonly FfmpegStemEncoder _encoder;

    /// <param name="logger">Optional log sink.</param>
    /// <param name="configuredFfmpegPath">Reads <c>AppConfig.FfmpegPath</c>.</param>
    public DcSongWriter(IAppLogger? logger = null, Func<string?>? configuredFfmpegPath = null)
        : this(new FfmpegStemEncoder(logger, configuredFfmpegPath: configuredFfmpegPath,
            opusBitrateKbps: DcSongFormat.OpusBitrateKbps), logger, configuredFfmpegPath)
    {
    }

    /// <summary>Test seam: an encoder with a fake resolver or runner.</summary>
    internal DcSongWriter(FfmpegStemEncoder encoder, IAppLogger? logger = null, Func<string?>? configuredFfmpegPath = null)
    {
        _encoder = encoder;
        _logger = logger;
        _configuredFfmpegPath = configuredFfmpegPath;
    }

    /// <summary>
    ///     Writes the package. The project is CLONED before the first await, on the
    ///     calling thread, so a caller on the UI thread hands over a consistent
    ///     snapshot and may go on editing. Throws <see cref="DcSongWriteException" />
    ///     for a request it cannot honour.
    /// </summary>
    public async Task<DcSongWriteResult> WriteAsync(
        DcSongWriteRequest request, IProgress<DcSongWriteProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        PlayOnlyExportGuard.ThrowIfPlayOnly(request.Project, "DotCharter song export");

        if (!File.Exists(request.AudioPath))
            throw new DcSongWriteException(DcSongWriteFailure.AudioMissing, $"Song audio not found: '{request.AudioPath}'.");
        if (!_encoder.IsExternalEncoderAvailable)
            throw new DcSongWriteException(DcSongWriteFailure.FfmpegMissing, "ffmpeg was not found; it encodes the package audio.");

        var warnings = new List<DcSongWriteWarning>();
        var stems = ValidateStems(request.Stems);
        var cover = ResolveCover(request, warnings);
        var banner = request.BannerPath == null ? null : LoadImage(request.BannerPath);
        var codec = request.Lossless ? DcSongAudioCodec.Flac : DcSongAudioCodec.Opus;
        var audioMember = DcSongManifestJson.AudioPath(codec);
        var coverMember = cover == null ? null : DcSongManifestJson.ImagePath(DcSongFileRole.Cover, cover.MediaType);
        var chart = DcSongChart.ForPackage(request.Project, audioMember, coverMember,
            stems.Select(s => (s.Name, DcSongManifestJson.StemPath(s.Name))).ToList(), request.ArrangementIndexes);

        var work = Directory.CreateTempSubdirectory("dotcharter_dcsong_").FullName;
        try
        {
            return await Task.Run(
                () => WriteStagedAsync(request, chart, codec, stems, cover, banner, warnings, work, progress, ct), ct)
                .ConfigureAwait(false);
        }
        finally
        {
            BestEffortDelete.Directory(work);
        }
    }

    private async Task<DcSongWriteResult> WriteStagedAsync(
        DcSongWriteRequest request, ChartProject chart, DcSongAudioCodec codec, IReadOnlyList<DcSongStemSource> stems,
        DcSongImage? cover, DcSongImage? banner, List<DcSongWriteWarning> warnings, string work,
        IProgress<DcSongWriteProgress>? progress, CancellationToken ct)
    {
        var total = stems.Count > 0 ? 6 : 5;
        var step = 0;
        void Report(DcSongWriteStage stage)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new DcSongWriteProgress(++step, total, stage));
        }

        Report(DcSongWriteStage.DecodingAudio);
        var decodeNotes = new List<string>();
        var pcm = PcmAudioLoader.Load(request.AudioPath, decodeNotes, _configuredFfmpegPath?.Invoke());
        foreach (var note in decodeNotes) _logger?.Info(AppLogCategory.Project, $".dcsong audio decode: {note}");
        var lengthMs = pcm.Duration.TotalMilliseconds;

        Report(DcSongWriteStage.EncodingAudio);
        var audioFile = await EncodeSongAsync(request, pcm, codec, warnings, work, ct).ConfigureAwait(false);

        Report(DcSongWriteStage.EncodingPreview);
        var previewStart = Math.Clamp(request.PreviewStartMs ?? PreviewClip.DefaultStartMs(lengthMs), 0, lengthMs);
        var clip = PreviewClip.Create(pcm, previewStart);
        var previewWav = Path.Combine(work, "preview.wav");
        PcmAudioLoader.WriteWav(previewWav, clip);
        var previewFile = await EncodeAsync(previewWav, Path.Combine(work, "preview"), StemCodec.Opus, ct).ConfigureAwait(false);
        var previewLengthMs = clip.Duration.TotalMilliseconds;
        File.Delete(previewWav);

        var stemFiles = new List<(DcSongStemSource Stem, string File)>();
        if (stems.Count > 0)
        {
            Report(DcSongWriteStage.EncodingStems);
            foreach (var stem in stems)
            {
                var staged = Path.Combine(work, "stem-" + stem.Name);
                var file = Path.GetExtension(stem.SourcePath).Equals(".opus", StringComparison.OrdinalIgnoreCase)
                    ? CopyInto(stem.SourcePath, staged + ".opus")
                    : await EncodeAsync(stem.SourcePath, staged, StemCodec.Opus, ct).ConfigureAwait(false);
                stemFiles.Add((stem, file));
            }
        }

        Report(DcSongWriteStage.WritingChart);
        var chartFile = Path.Combine(work, DcSongFormat.ChartPath);
        var serializer = _logger == null ? new RscpChartSerializer() : new RscpChartSerializer(_logger);
        serializer.Save(chartFile, chart);

        Report(DcSongWriteStage.WritingPackage);
        var members = new List<Staged>
        {
            Staged.FromFile(DcSongFormat.ChartPath, DcSongFileRole.Chart, chartFile, compress: true),
            Staged.FromFile(DcSongManifestJson.AudioPath(codec), DcSongFileRole.Audio, audioFile, compress: false),
            Staged.FromFile(DcSongFormat.PreviewPath, DcSongFileRole.Preview, previewFile, compress: false)
        };
        if (cover != null)
            members.Add(Staged.FromImage(DcSongFileRole.Cover, cover));
        if (banner != null)
            members.Add(Staged.FromImage(DcSongFileRole.Banner, banner));
        foreach (var (stem, file) in stemFiles)
            members.Add(Staged.FromFile(DcSongManifestJson.StemPath(stem.Name), DcSongFileRole.Stem, file, compress: false, stem.Name));

        var manifest = new DcSongManifest(
            DcSongFormat.CurrentMajor,
            DcSongFormat.CurrentMinor,
            "DotCharter " + RscpFormatContract.CurrentWriterVersion,
            SongInfo(request, chart, lengthMs),
            new DcSongAudioInfo(codec, codec == DcSongAudioCodec.Flac,
                codec == DcSongAudioCodec.Opus ? _encoder.OpusBitrateKbps : null, previewStart, previewLengthMs),
            members.Select(m => m.Entry).ToList());

        WriteZip(request.OutputPath, DcSongManifestJson.Write(manifest), members);
        var bytes = new FileInfo(request.OutputPath).Length;
        _logger?.Info(AppLogCategory.Project,
            $".dcsong written: '{request.OutputPath}', {bytes} bytes, {DcSongManifestJson.CodecName(codec)}, " +
            $"{stemFiles.Count} stem(s), warnings=[{string.Join(", ", warnings)}].");
        return new DcSongWriteResult(request.OutputPath, manifest, bytes, warnings);
    }

    /// <summary>
    ///     Opus encodes the editor's decode. FLAC copies a lossless SOURCE's samples
    ///     as they are — rate, depth and all — so the package is bit-exact to it; a
    ///     lossy source, or a lossless one ffmpeg will not take as it is, stores the
    ///     editor's 48 kHz 16-bit decode instead, with a warning saying which.
    /// </summary>
    private async Task<string> EncodeSongAsync(
        DcSongWriteRequest request, PcmAudio pcm, DcSongAudioCodec codec, List<DcSongWriteWarning> warnings,
        string work, CancellationToken ct)
    {
        if (codec == DcSongAudioCodec.Flac)
        {
            if (LosslessContainers.Contains(Path.GetExtension(request.AudioPath)))
            {
                var direct = await TryEncodeAsync(request.AudioPath, Path.Combine(work, "song-source"), StemCodec.Flac, ct)
                    .ConfigureAwait(false);
                if (direct != null) return direct;
                warnings.Add(DcSongWriteWarning.LosslessFromDecode);
            }
            else
            {
                warnings.Add(DcSongWriteWarning.LossySourceInLosslessPackage);
            }
        }

        var wav = Path.Combine(work, "song-decoded.wav");
        PcmAudioLoader.WriteWav(wav, pcm);
        var encoded = await EncodeAsync(wav, Path.Combine(work, "song"),
            codec == DcSongAudioCodec.Flac ? StemCodec.Flac : StemCodec.Opus, ct).ConfigureAwait(false);
        File.Delete(wav);
        return encoded;
    }

    private async Task<string> EncodeAsync(string source, string destWithoutExtension, StemCodec codec, CancellationToken ct) =>
        await TryEncodeAsync(source, destWithoutExtension, codec, ct).ConfigureAwait(false)
        ?? throw new DcSongWriteException(DcSongWriteFailure.EncodeFailed,
            $"ffmpeg could not encode '{source}' to {codec}.");

    /// <summary>The encoded file, or null. The stem encoder never loses a stem, so on
    /// failure it hands back a WAV copy; a package has no WAV member, so that is a
    /// failure here.</summary>
    private async Task<string?> TryEncodeAsync(string source, string destWithoutExtension, StemCodec codec, CancellationToken ct)
    {
        var result = await _encoder.EncodeAsync(source, destWithoutExtension, codec, ct).ConfigureAwait(false);
        if (result.EndsWith("." + codec.FileExtension(), StringComparison.OrdinalIgnoreCase)) return result;

        BestEffortDelete.File(result);
        _logger?.Warning(AppLogCategory.Project, $".dcsong: ffmpeg could not encode '{source}' to {codec}.");
        return null;
    }

    private static DcSongInfo SongInfo(DcSongWriteRequest request, ChartProject chart, double lengthMs)
    {
        var data = chart.Chart;
        var key = string.IsNullOrWhiteSpace(request.SongKey)
            ? RocksmithDlcKey.Create(data.Artist, data.SongName)
            : request.SongKey.Trim();
        var arrangements = chart.Arrangements
            .Select(a => new DcSongArrangementInfo(a.Name, a.ArrangementType.ToString(), a.TuningType))
            .ToList();
        return new DcSongInfo(key, chart.ProjectId, data.SongName, data.Artist, data.AlbumName, data.AlbumYear,
            data.Charter, lengthMs, chart.VocalTracks.Any(t => t.Phrases.Count > 0), arrangements);
    }

    private static IReadOnlyList<DcSongStemSource> ValidateStems(IReadOnlyList<DcSongStemSource>? stems)
    {
        stems ??= [];
        if (stems.Count > DcSongFormat.MaxStems)
            throw new DcSongWriteException(DcSongWriteFailure.InvalidStem, $"{stems.Count} stems; a package holds at most {DcSongFormat.MaxStems}.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stem in stems)
        {
            if (!DcSongFormat.IsValidStemName(stem.Name) || !names.Add(stem.Name))
                throw new DcSongWriteException(DcSongWriteFailure.InvalidStem, $"Stem name '{stem.Name}' is invalid or repeated.");
            if (!File.Exists(stem.SourcePath))
                throw new DcSongWriteException(DcSongWriteFailure.InvalidStem, $"Stem '{stem.Name}' has no file at '{stem.SourcePath}'.");
        }

        return stems;
    }

    /// <summary>An explicit cover must be usable; the project's own is skipped with a
    /// warning when it is not, because it is not something the user just chose.</summary>
    private static DcSongImage? ResolveCover(DcSongWriteRequest request, List<DcSongWriteWarning> warnings)
    {
        if (request.CoverPath != null) return LoadImage(request.CoverPath);

        var own = request.Project.Chart.CoverArtPath.Trim();
        if (string.IsNullOrEmpty(own)) return null;

        try
        {
            // The project's own cover is data from its file: only a file inside the
            // project folder is packed, never a rooted or escaping name.
            var path = ProjectRelativePath.TryResolve(request.ProjectDirectory, own);
            if (path == null)
            {
                warnings.Add(DcSongWriteWarning.CoverSkipped);
                return null;
            }

            return LoadImage(path);
        }
        catch (Exception ex) when (ex is DcSongWriteException or ArgumentException)
        {
            warnings.Add(DcSongWriteWarning.CoverSkipped);
            return null;
        }
    }

    private static DcSongImage LoadImage(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > DcSongFormat.MaxImageBytes)
                throw new DcSongWriteException(DcSongWriteFailure.ImageUnsupported, $"Image '{path}' is missing or too large.");

            var bytes = File.ReadAllBytes(path);
            var mediaType = DcSongImages.Sniff(bytes)
                            ?? throw new DcSongWriteException(DcSongWriteFailure.ImageUnsupported, $"Image '{path}' is not a PNG or JPEG.");
            return new DcSongImage(mediaType, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new DcSongWriteException(DcSongWriteFailure.ImageUnsupported, $"Image '{path}' could not be read: {ex.Message}", ex);
        }
    }

    private static string CopyInto(string source, string dest)
    {
        File.Copy(source, dest, overwrite: true);
        return dest;
    }

    private static void WriteZip(string outputPath, byte[] manifest, IReadOnlyList<Staged> members)
    {
        var full = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // Sibling, not %TEMP%: a move across volumes is a copy, not an atomic rename.
        var staging = full + ".writing";
        try
        {
            using (var file = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            {
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                {
                    // The manifest first, so a reader that streams the zip meets it first.
                    using (var entry = zip.CreateEntry(DcSongFormat.ManifestPath, CompressionLevel.Optimal).Open())
                        entry.Write(manifest);

                    foreach (var member in members)
                    {
                        var level = member.Compress ? CompressionLevel.Optimal : CompressionLevel.NoCompression;
                        using var entry = zip.CreateEntry(member.Entry.Path, level).Open();
                        if (member.Bytes != null)
                        {
                            entry.Write(member.Bytes);
                        }
                        else
                        {
                            using var source = new FileStream(member.File!, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
                            source.CopyTo(entry);
                        }
                    }
                }

                file.Flush(flushToDisk: true);
            }

            AtomicFile.ReplaceWithRetry(staging, full);
        }
        finally
        {
            // Only still there when the write threw; the swap consumes it.
            BestEffortDelete.File(staging);
        }
    }

    /// <summary>One member, staged on disk or in memory, with its manifest entry.</summary>
    private sealed record Staged(DcSongFileEntry Entry, string? File, byte[]? Bytes, bool Compress)
    {
        public static Staged FromFile(string path, DcSongFileRole role, string file, bool compress, string? stemName = null)
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
            var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            return new Staged(new DcSongFileEntry(path, role, stream.Length, hash, stemName), file, null, compress);
        }

        /// <summary>PNG and JPEG are compressed already, so images are stored as they are.</summary>
        public static Staged FromImage(DcSongFileRole role, DcSongImage image) =>
            new(new DcSongFileEntry(DcSongManifestJson.ImagePath(role, image.MediaType), role, image.Bytes.Length,
                Convert.ToHexStringLower(SHA256.HashData(image.Bytes)), MediaType: image.MediaType), null, image.Bytes, false);
    }
}
