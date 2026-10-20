using System.Collections.Concurrent;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Services.Library.Cache;

namespace DotCharter.Services.Package.DcSong;

/// <summary>A package's playable song and decoded stems in the decoded-audio cache.</summary>
/// <param name="PlayablePath">The song the engine opens: <c>song.flac</c> or <c>song.wav</c>.</param>
/// <param name="Stems">Each decoded stem's WAV, by stem name.</param>
/// <param name="Wrote">True when this call wrote anything (a miss); false for a pure hit.</param>
internal sealed record DcSongCachedAudio(string PlayablePath, IReadOnlyDictionary<string, string> Stems, bool Wrote);

/// <summary>
/// Puts a library <c>.dcsong</c>'s audio where the engine can open it: the
///     package's folder in <see cref="LibraryAudioCacheStore" />, the BOUNDED decoded-audio
///     cache Rocksmith songs use, named by the package's path and last-write time (a
///     changed package is a new folder; the old one is an orphan the sweep takes first).
///     <list type="bullet">
///         <item>FLAC: the stored member's verified bytes, copied once — the engine decodes
///         FLAC itself, so nothing is decoded.</item>
///         <item>Opus: decoded once by ffmpeg to a 48 kHz WAV — the engine does not decode
///         Opus (<c>JUCE_USE_OPUS=0</c>).</item>
///         <item>Stems (Opus, playback-only): decoded once to <c>package-stems/&lt;name&gt;.wav</c>,
///         a folder of their own so the stem cache's own <c>stems/</c> never meets them.</item>
///     </list>
///     Every file is written to a staging name and renamed, so a file under its final name
///     is whole, and one gate per package keeps an Open and a convert-on-select prefetch
///     from writing the same folder at once. Every byte comes through
///     <see cref="DcSongPackage" />, which verifies each member's SHA-256 before handing it out.
/// </summary>
internal sealed class DcSongLibraryAudio(LibraryAudioCacheStore cache, IAppLogger logger)
{
    internal const string FlacName = "song.flac";
    internal const string WavName = "song.wav";
    internal const string StemFolderName = "package-stems";

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    public async Task<DcSongCachedAudio> MaterializeAsync(
        DcSongPackage package, IStemEncoder decoder, bool withStems, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(Path.GetFullPath(package.FilePath), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var folder = cache.PrepareForUse(package.FilePath);
            var wrote = false;
            string playable;
            if (package.IsLossless)
            {
                playable = Path.Combine(folder, FlacName);
                if (!IsWhole(playable, package.Manifest.Find(DcSongFileRole.Audio)!.Size))
                {
                    StageAndReplace(playable, staging => Copy(package.OpenAudio, staging));
                    wrote = true;
                }
            }
            else
            {
                playable = Path.Combine(folder, WavName);
                if (!IsWhole(playable, null))
                {
                    await DecodeAsync(package.OpenAudio, playable, decoder, "the song audio", ct).ConfigureAwait(false);
                    wrote = true;
                }
            }

            var stems = new Dictionary<string, string>(StringComparer.Ordinal);
            if (withStems && decoder.IsExternalEncoderAvailable)
            {
                foreach (var entry in package.Manifest.Stems)
                {
                    var name = entry.StemName!;
                    var wav = Path.Combine(folder, StemFolderName, name + ".wav");
                    if (!IsWhole(wav, null))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(wav)!);
                        await DecodeAsync(() => package.OpenStem(name), wav, decoder, $"stem '{name}'", ct).ConfigureAwait(false);
                        wrote = true;
                    }

                    stems[name] = wav;
                }
            }

            if (wrote) cache.ScheduleSweep(Path.GetFileName(folder));
            return new DcSongCachedAudio(playable, stems, wrote);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task DecodeAsync(Func<Stream> member, string wav, IStemEncoder decoder, string what, CancellationToken ct)
    {
        var encoded = StagingName(wav, ".opus");
        var decoded = StagingName(wav, ".wav");
        try
        {
            Copy(member, encoded);
            await decoder.DecodeToWavAsync(encoded, StemCodec.Opus, decoded, ct).ConfigureAwait(false);
            if (!IsWhole(decoded, null))
                throw new DcSongReadException(DcSongReadFailure.DecodeFailed, $"ffmpeg wrote no file for {what}.");
            File.Move(decoded, wav, overwrite: true);
            logger.Info(AppLogCategory.Import, $".dcsong {what} decoded into the audio cache: '{wav}'.");
        }
        catch (StemDecodeUnavailableException ex)
        {
            throw new DcSongReadException(DcSongReadFailure.DecodeFailed, $"ffmpeg could not decode {what}: {ex.Message}", ex);
        }
        finally
        {
            BestEffortDelete.File(encoded);
            BestEffortDelete.File(decoded);
        }
    }

    private static void StageAndReplace(string target, Action<string> write)
    {
        var staging = StagingName(target, Path.GetExtension(target));
        try
        {
            write(staging);
            File.Move(staging, target, overwrite: true);
        }
        finally
        {
            BestEffortDelete.File(staging);
        }
    }

    private static void Copy(Func<Stream> member, string path)
    {
        using var source = member();
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        source.CopyTo(file);
    }

    /// <summary>A file under its final name is whole (it got there by a rename); the size
    /// check is for the FLAC copy, whose length the manifest states.</summary>
    private static bool IsWhole(string path, long? size)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length > 0 && (size == null || info.Length == size);
    }

    private static string StagingName(string target, string extension) =>
        Path.Combine(Path.GetDirectoryName(target)!, $"{Path.GetFileNameWithoutExtension(target)}.part-{Guid.NewGuid():N}{extension}");
}
