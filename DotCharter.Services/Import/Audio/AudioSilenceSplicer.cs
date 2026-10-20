using System.Globalization;
using System.Text.RegularExpressions;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Services.Export.Audio;
using DotCharter.Services.Import.Youtube;
using DotCharter.Services.Library;

namespace DotCharter.Services.Import.Audio;

/// <summary>One audio file the splice pads: a stem's name and its absolute path.</summary>
public sealed record SilenceSpliceFile(string Name, string Path);

/// <summary>What to pad, and where the padded copies go.</summary>
/// <param name="AudioPath">The song's working audio, absolute.</param>
/// <param name="Stems">The project's stems that exist on disk, absolute.</param>
/// <param name="OutputDirectory">The project folder: the padded song goes here and the padded
/// stems under its <c>stems/</c>.</param>
/// <param name="LeadingSeconds">Silence before the song.</param>
/// <param name="TrailingSeconds">Silence after it.</param>
/// <param name="SongIdentity">The session's song identity; a library one (<c>lib:</c>) keys the stem cache
/// like a library song, anything else keys it by the padded audio.</param>
public sealed record SilenceSpliceRequest(
    string AudioPath,
    IReadOnlyList<SilenceSpliceFile> Stems,
    string OutputDirectory,
    double LeadingSeconds,
    double TrailingSeconds,
    string? SongIdentity = null);

/// <summary>Why a splice wrote nothing.</summary>
public enum SilenceSpliceFailure
{
    None,

    /// <summary>The song needs a re-encode and ffmpeg was not found.</summary>
    NoFfmpeg,

    /// <summary>The song could not be padded.</summary>
    AudioFailed,

    /// <summary>A stem could not be padded, so neither was anything else.</summary>
    StemFailed
}

/// <summary>The padded copies, or why there are none.</summary>
public sealed record SilenceSpliceResult(
    string? AudioPath, IReadOnlyList<SilenceSpliceFile> Stems, SilenceSpliceFailure Failure)
{
    public bool Succeeded => Failure == SilenceSpliceFailure.None;

    public static SilenceSpliceResult Failed(SilenceSpliceFailure failure) => new(null, [], failure);
}

/// <summary>
/// Add silence as a real audio splice: writes NEW padded copies of the song and its stems
///     and leaves the originals where they are, so the undo step can point the project back at them.
/// </summary>
public interface IAudioSilenceSplicer
{
    Task<SilenceSpliceResult> SpliceAsync(SilenceSpliceRequest request, CancellationToken ct = default);
}

/// <summary>
///     The splice. A WAVE is padded in place of a re-encode (<see cref="WavSilencePadder" />), so a
///     stem stays lossless; anything else goes through ffmpeg with the new-project bake's filter
///     (<see cref="ImportAudioPreparer.BuildSilenceFilter" />): the song to Vorbis, a stem to float
///     WAVE. All or nothing: a stem that fails takes every file this run wrote with it. The padded
///     stems are then saved to the stem cache under the padded song's key, so the never-regenerate
///     rule holds for the new audio too.
/// </summary>
public sealed partial class AudioSilenceSplicer(
    IConfigurationService? config,
    StemCacheStore? stemCache,
    IAppLogger? logger,
    Func<string?>? resolveFfmpeg = null,
    Func<string, IReadOnlyList<string>, bool>? runEncode = null) : IAudioSilenceSplicer
{
    private static readonly TimeSpan EncodeTimeout = TimeSpan.FromMinutes(5);

    public async Task<SilenceSpliceResult> SpliceAsync(SilenceSpliceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await Task.Run(() => Splice(request), ct).ConfigureAwait(false);
        if (result.Succeeded && result.Stems.Count > 0)
            await SaveStemsToCacheAsync(request.SongIdentity, result, ct).ConfigureAwait(false);
        return result;
    }

    private SilenceSpliceResult Splice(SilenceSpliceRequest request)
    {
        var filter = ImportAudioPreparer.BuildSilenceFilter(request.LeadingSeconds, request.TrailingSeconds);
        if (filter is null) return SilenceSpliceResult.Failed(SilenceSpliceFailure.AudioFailed);

        var slot = NextSlot(request.OutputDirectory, request.AudioPath, request.Stems.Count > 0, File.Exists, Directory.Exists);
        var written = new List<string>();
        try
        {
            var audio = PadOne(request, request.AudioPath, slot.AudioStem, asStem: false, filter, written);
            if (audio.Failure != SilenceSpliceFailure.None) return Cleanup(written, audio.Failure);

            var stems = new List<SilenceSpliceFile>(request.Stems.Count);
            if (request.Stems.Count > 0) Directory.CreateDirectory(slot.StemDirectory);
            foreach (var stem in request.Stems)
            {
                var target = Path.Combine(slot.StemDirectory, Path.GetFileNameWithoutExtension(stem.Path));
                var padded = PadOne(request, stem.Path, target, asStem: true, filter, written);
                if (padded.Failure != SilenceSpliceFailure.None)
                {
                    logger?.Warning(AppLogCategory.Audio, $"Add silence: the stem '{stem.Path}' could not be padded.");
                    return Cleanup(written, padded.Failure == SilenceSpliceFailure.NoFfmpeg
                        ? SilenceSpliceFailure.NoFfmpeg
                        : SilenceSpliceFailure.StemFailed);
                }

                stems.Add(new SilenceSpliceFile(stem.Name, padded.Path!));
            }

            logger?.Info(AppLogCategory.Audio,
                $"Add silence: wrote '{audio.Path}' and {stems.Count} stem(s), lead {request.LeadingSeconds:0.###} s, " +
                $"trail {request.TrailingSeconds:0.###} s. The originals are kept for undo.");
            return new SilenceSpliceResult(audio.Path, stems, SilenceSpliceFailure.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.Warning(AppLogCategory.Audio, $"Add silence failed: {ex.Message}");
            return Cleanup(written, SilenceSpliceFailure.AudioFailed);
        }

        SilenceSpliceResult Cleanup(List<string> files, SilenceSpliceFailure failure)
        {
            foreach (var file in files) BestEffortDelete.File(file);
            if (Directory.Exists(slot.StemDirectory) && !Directory.EnumerateFileSystemEntries(slot.StemDirectory).Any())
                BestEffortDelete.Directory(slot.StemDirectory);
            return SilenceSpliceResult.Failed(failure);
        }
    }

    /// <summary>Pads one file to <paramref name="targetWithoutExtension" /> plus the extension it ends up with.</summary>
    private (string? Path, SilenceSpliceFailure Failure) PadOne(SilenceSpliceRequest request, string source,
        string targetWithoutExtension, bool asStem, string filter, List<string> written)
    {
        if (string.Equals(Path.GetExtension(source), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            var wav = targetWithoutExtension + ".wav";
            if (WavSilencePadder.TryPad(source, wav, request.LeadingSeconds, request.TrailingSeconds))
            {
                written.Add(wav);
                return (wav, SilenceSpliceFailure.None);
            }
        }

        var ffmpeg = (resolveFfmpeg ?? DefaultResolveFfmpeg)();
        if (string.IsNullOrEmpty(ffmpeg)) return (null, SilenceSpliceFailure.NoFfmpeg);

        var target = targetWithoutExtension + (asStem ? ".wav" : ".ogg");
        var args = asStem
            ? BuildStemEncodeArgs(source, target, filter)
            : ImportAudioPreparer.BuildEncodeArgs(source, target, filter);
        written.Add(target);
        var ok = (runEncode ?? DefaultRunEncode)(ffmpeg, args);
        return ok && File.Exists(target) && new FileInfo(target).Length > 0
            ? (target, SilenceSpliceFailure.None)
            : (null, SilenceSpliceFailure.AudioFailed);
    }

    /// <summary>ffmpeg arguments that pad a stem into float WAVE: lossless for every source depth,
    /// and the format the stem tools read.</summary>
    public static IReadOnlyList<string> BuildStemEncodeArgs(string sourcePath, string targetWavPath, string filter) =>
    [
        "-hide_banner", "-loglevel", "error", "-y",
        "-i", sourcePath,
        .. FfmpegAudioOnly.Args,
        "-af", filter,
        "-c:a", "pcm_f32le",
        targetWavPath
    ];

    /// <summary>Where one splice writes.</summary>
    /// <param name="AudioStem">The padded song's path without its extension.</param>
    /// <param name="StemDirectory">The folder its padded stems go in.</param>
    public sealed record SpliceSlot(string AudioStem, string StemDirectory);

    /// <summary>
    ///     The first free <c>&lt;song&gt;-silenceN</c> beside the project with a free
    ///     <c>stems/silence-N</c>: a second splice never overwrites the first, whose files the undo
    ///     history still points at. A <c>-silenceN</c> already on the name is replaced, not stacked.
    /// </summary>
    public static SpliceSlot NextSlot(string outputDirectory, string audioPath, bool hasStems,
        Func<string, bool> fileExists, Func<string, bool> directoryExists)
    {
        var root = SilenceSuffix().Replace(Path.GetFileNameWithoutExtension(audioPath), string.Empty);
        if (root.Length == 0) root = "song";
        for (var n = 1; ; n++)
        {
            var stem = Path.Combine(outputDirectory, root + "-silence" + n.ToString(CultureInfo.InvariantCulture));
            var stems = Path.Combine(outputDirectory, "stems", "silence-" + n.ToString(CultureInfo.InvariantCulture));
            if (fileExists(stem + ".wav") || fileExists(stem + ".ogg")) continue;
            if (hasStems && directoryExists(stems)) continue;
            return new SpliceSlot(stem, stems);
        }
    }

    [GeneratedRegex(@"-silence\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex SilenceSuffix();

    private async Task SaveStemsToCacheAsync(string? songIdentity, SilenceSpliceResult result, CancellationToken ct)
    {
        if (stemCache is null || result.AudioPath is null) return;
        try
        {
            var identity = LibrarySongIdentity.IsLibrary(songIdentity)
                ? StemCacheIdentity.ForLibrary(songIdentity, result.AudioPath)
                : StemCacheIdentity.ForAudioFile(result.AudioPath);
            // Keyed like every other save (StemCacheSource): the padded audio has no
            // .wem beside it, so this is its own bytes.
            var source = StemCacheSource.TryResolve(identity, result.AudioPath);
            if (source is null) return;
            var tracks = result.Stems.Select(s => new StemTrack { Name = s.Name, Path = s.Path }).ToList();
            await stemCache.SaveAsync(source.Key, identity, source.Signature, tracks, modelId: string.Empty,
                appVersion: string.Empty, ct, StemAudioSignature.ComputeFullFileHash(source.SignedPath)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The padded stems are in the project either way; only a later re-open from the cache misses.
            logger?.Warning(AppLogCategory.Audio, $"Add silence: the padded stems were not cached ({ex.Message}).");
        }
    }

    private string? DefaultResolveFfmpeg()
    {
        var configured = config?.LoadAppConfig().FfmpegPath;
        return MediaToolResolver.Resolve("ffmpeg", AppContext.BaseDirectory,
            string.IsNullOrWhiteSpace(configured) ? null : configured,
            MediaToolResolver.CurrentPathDirs(), File.Exists, OperatingSystem.IsWindows());
    }

    private static bool DefaultRunEncode(string ffmpeg, IReadOnlyList<string> args) =>
        MediaProcess.Run(ffmpeg, args, EncodeTimeout).Succeeded;
}
