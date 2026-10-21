using System.Diagnostics;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Services.Library;
using DotCharter.Services.Parsing.Images;
using DotCharter.Services.Infrastructure.LibraryDoctor;
using DotCharter.Services.Library.Cache;
using DotCharter.Services.Library.Prefetch;

namespace DotCharter.Services.Package.DcSong;

/// <summary>
///     The Library's and Play Mode's door to <c>.dcsong</c> packages
///     (<c>docs/agents/services/dcsong-format.md</c> § Library and Play Mode).
///     One <see cref="ILibraryPackageSource" /> of the neutral <see cref="LibraryService" />:
/// the service hands it every <c>.dcsong</c> its scan finds and every package
///     song it is asked to open, so a package rides the same scan, SQLite index, song
///     lists and load path as any other library file:
///     <list type="bullet">
///         <item><see cref="ReadForIndex" />: open, verify EVERY member's checksum, read the
///         chart, build the row. A damaged or tampered package throws
///         <see cref="DcSongReadException" />, which the scan logs as skipped.</item>
///         <item><see cref="LoadAsync" />: the chart straight from the package, the audio
///         and stems through <see cref="DcSongLibraryAudio" /> into the bounded cache.</item>
///         <item><see cref="BlockFor" />: an Opus package without ffmpeg shows disabled with
///         its reason, from the answer the last scan or load probed off the UI thread.</item>
///     </list>
///     Packaged stems reach the stem mixer only (the project's <see cref="ChartProject.Stems" />);
///     nothing under <c>Audio/LivePlay/</c> reads them (<c>LiveDetectionAudioSourceTests</c>).
/// </summary>
public sealed class DcSongLibrarySource : ILibraryPackageSource
{
    // _decoderState: 0 until the first probe (a row shows live: a load then says why).
    private const int DecoderPresent = 1;
    private const int DecoderAbsent = 2;

    private readonly IAppLogger _logger;
    private readonly Func<IStemEncoder> _decoder;
    private readonly DcSongLibraryAudio _audio;
    private int _decoderState;

    /// <param name="logger">The log sink.</param>
    /// <param name="audioCache">The decoded-audio cache the app's Rocksmith songs use.</param>
    /// <param name="configuredFfmpegPath">Reads <c>AppConfig.FfmpegPath</c>, again on every
    /// probe, so an ffmpeg installed while the app runs is found by the next scan.</param>
    public DcSongLibrarySource(IAppLogger logger, LibraryAudioCacheStore audioCache, Func<string?> configuredFfmpegPath)
        : this(logger, audioCache, () => new FfmpegStemEncoder(logger, configuredFfmpegPath: configuredFfmpegPath))
    {
    }

    /// <summary>Test seam: a decoder that answers without ffmpeg.</summary>
    internal DcSongLibrarySource(IAppLogger logger, LibraryAudioCacheStore audioCache, Func<IStemEncoder> decoder)
    {
        _logger = logger;
        _decoder = decoder;
        _audio = new DcSongLibraryAudio(audioCache, logger);
    }

    /// <summary><c>.dcsong</c>.</summary>
    public IReadOnlyList<string> FileExtensions { get; } = [DcSongFormat.Extension];

    /// <summary>Looks for ffmpeg again. Off the UI thread: the scan calls it first.</summary>
    public void ProbeDecoder() => Record(_decoder().IsExternalEncoderAvailable);

    /// <summary>Every scan probes for ffmpeg again (<see cref="ProbeDecoder" />).</summary>
    public void BeginScan() => ProbeDecoder();

    public LibrarySongBlock BlockFor(LibrarySongEntry song) =>
        song.NeedsAudioDecoder && Volatile.Read(ref _decoderState) == DecoderAbsent
            ? LibrarySongBlock.AudioDecoderMissing
            : LibrarySongBlock.None;

    /// <summary>The library row of the package at <paramref name="path" />. Throws
    /// <see cref="DcSongReadException" /> for a package that is damaged, tampered or newer.</summary>
    public IReadOnlyList<LibrarySongEntry> ReadForIndex(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var package = DcSongPackage.Open(path);
        package.VerifyAll();
        var project = package.LoadChart(_logger);
        var cover = LibraryCoverCache.GetCachedAlbumArtPngPath(path, package.Manifest.Song.Key);
        return [DcSongLibraryEntry.Build(path, package.Manifest, project, DdsImageConverter.IsUsablePng(cover) ? cover : "")];
    }

    /// <summary>
    ///     Opens a library package to play: the chart as the package carries it (sections,
    ///     phrases, tones, lyrics — the song map comes from those), the song audio and the
    ///     stems from the cache. Throws <see cref="DcSongReadException" />:
    ///     <see cref="DcSongReadFailure.DecoderMissing" /> for an Opus package without ffmpeg.
    /// </summary>
    public Task<LibraryLoadedSong> LoadAsync(LibrarySongEntry song, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var clock = Stopwatch.StartNew();
            using var package = DcSongPackage.Open(song.PackagePath);
            var decoder = _decoder();
            var canDecode = Record(decoder.IsExternalEncoderAvailable);
            if (!package.IsLossless && !canDecode)
            {
                throw new DcSongReadException(DcSongReadFailure.DecoderMissing,
                    "the package's audio is Opus, and no ffmpeg was found to decode it.");
            }

            var project = package.LoadChart(_logger);
            var audio = await _audio.MaterializeAsync(package, decoder, withStems: true, cancellationToken).ConfigureAwait(false);
            project.AudioFileName = Path.GetFileName(audio.PlayablePath);
            // A member name means nothing outside the package; the cover is the Library's
            // (LoadCover), as a Rocksmith song's is.
            project.Chart.CoverArtPath = "";
            var skipped = AttachStems(project, package.Manifest, audio.Stems);
            _logger.Info(AppLogCategory.Import,
                $".dcsong library song opened. Package='{song.PackagePath}', codec={package.Manifest.Audio.Codec}, " +
                $"audio='{audio.PlayablePath}', cached={!audio.Wrote}, stems=[{string.Join(", ", audio.Stems.Keys)}], " +
                $"skippedStems=[{string.Join(", ", skipped)}], arrangements={project.Arrangements.Count}, ms={clock.ElapsedMilliseconds}.");
            return new LibraryLoadedSong
            {
                Project = project,
                AudioEntryPath = song.SongBank,
                ExtractedAudioPath = audio.PlayablePath,
                PlayableAudioPath = audio.PlayablePath,
                AudioConverted = !package.IsLossless
            };
        }, cancellationToken);

    Task<LibraryLoadedSong> ILibraryPackageSource.LoadSongAsync(LibrarySongEntry song) => LoadAsync(song);

    /// <summary>Convert-on-select: the song audio only, under the same per-package gate an
    /// Open waits on.</summary>
    public LibraryAudioPrepareOutcome PrepareAudio(LibrarySongEntry song, CancellationToken cancellationToken)
    {
        using var package = DcSongPackage.Open(song.PackagePath);
        var decoder = _decoder();
        if (!Record(decoder.IsExternalEncoderAvailable) && !package.IsLossless) return LibraryAudioPrepareOutcome.NoAudio;
        _audio.MaterializeAsync(package, decoder, withStems: false, cancellationToken).GetAwaiter().GetResult();
        return LibraryAudioPrepareOutcome.Prepared;
    }

    /// <summary>The cover, saved once into the album-art cache under the name a Rocksmith
    /// song's would take (keyed by the package's path and last-write time); "" when the
    /// package has none or cannot be read.</summary>
    public string LoadCover(LibrarySongEntry song)
    {
        var cached = LibraryCoverCache.GetCachedAlbumArtPngPath(song.PackagePath, song.SongKey);
        if (DdsImageConverter.IsUsablePng(cached)) return cached;
        try
        {
            using var package = DcSongPackage.Open(song.PackagePath);
            if (package.ReadCover() is not { } cover) return "";
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            var staging = cached + $".part-{Guid.NewGuid():N}";
            try
            {
                // A JPEG keeps its bytes: every reader of this cache sniffs the format.
                File.WriteAllBytes(staging, cover.Bytes);
                File.Move(staging, cached, overwrite: true);
            }
            finally
            {
                BestEffortDelete.File(staging);
            }

            return DdsImageConverter.IsUsablePng(cached) ? cached : "";
        }
        catch (Exception ex) when (ex is DcSongReadException or IOException or UnauthorizedAccessException)
        {
            _logger.Warning(AppLogCategory.Import, $".dcsong cover not read from '{song.PackagePath}': {ex.Message}");
            return "";
        }
    }

    /// <summary>Library Doctor: a package is readable when every member matches its checksum,
    /// and always carries its song audio (the format requires it).</summary>
    public PackageProbeResult Probe(string path)
    {
        try
        {
            using var package = DcSongPackage.Open(path);
            package.VerifyAll();
            return new PackageProbeResult(true, AudioAvailability.Present);
        }
        catch (Exception ex)
        {
            _logger.Trace(AppLogCategory.Import,
                $"Library doctor could not open package. Path='{path}', type={ex.GetType().Name}, message='{ex.Message}'.");
            return new PackageProbeResult(false, AudioAvailability.Unknown, ex.Message);
        }
    }

    /// <summary>Points each packaged stem the chart declares at its decoded WAV, keeping the
    /// mixer state the package carried; a stem that was not decoded (no ffmpeg) is left out.
    /// Returns the names left out.</summary>
    private static List<string> AttachStems(ChartProject project, DcSongManifest manifest, IReadOnlyDictionary<string, string> decoded)
    {
        var skipped = new List<string>();
        foreach (var entry in manifest.Stems)
        {
            var name = entry.StemName!;
            // The chart names each packaged stem by its member path (DcSongChart.ForPackage).
            var declared = project.Stems.FirstOrDefault(s => string.Equals(s.Path, entry.Path, StringComparison.Ordinal));
            if (!decoded.TryGetValue(name, out var wav))
            {
                if (declared != null) project.Stems.Remove(declared);
                skipped.Add(name);
                continue;
            }

            if (declared == null)
            {
                declared = new Stem { Name = name };
                project.Stems.Add(declared);
            }

            declared.Path = wav;
            declared.Local = true;
        }

        return skipped;
    }

    private bool Record(bool available)
    {
        Volatile.Write(ref _decoderState, available ? DecoderPresent : DecoderAbsent);
        return available;
    }
}
