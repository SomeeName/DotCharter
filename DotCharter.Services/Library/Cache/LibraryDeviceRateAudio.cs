using System.Collections.Concurrent;
using System.Diagnostics;
using DotCharter.Services.Parsing.Audio;
using JuceSharp;

namespace DotCharter.Services.Library.Cache;

/// <summary>
/// Keeps each cached song at the audio DEVICE's rate, so a repeat open skips the
/// load-time upsample.
/// <para>
/// <b>Why.</b> The engine converts a song below the device rate on every open, on
/// the load thread (JuceSharp <c>LoadResampler.h</c>; it only ever UPsamples).
/// Rocksmith audio is 44.1 kHz and the owner's interface runs at 48 kHz, so every
/// repeat open of a library song paid that conversion again: 0.3-0.4 s of a
/// 0.5-0.7 s open for a 5-7 minute song.
/// </para>
/// <para>
/// <b>What.</b> After a song opens, <see cref="ScheduleConversion"/> converts the
/// cache's <c>&lt;id&gt;.wav</c> with <c>dca_convert_audio_for_device</c> and the
/// result REPLACES it. That file is exactly what the engine holds in memory: the
/// preload's int16 samples at the device rate plus the preload step in a
/// <c>dcps</c> chunk (<see cref="DeviceRateWavHeader"/>), so an open of it is
/// BIT-IDENTICAL to the load-time conversion -- same samples, step, frame count and
/// length, sync cannot move -- at 16 bits a sample: 1.09x the 44.1 kHz original,
/// one file per song, not a second copy beside it.
/// </para>
/// <para>
/// <b>Never a wrong-rate play.</b> <see cref="PrepareForOpen"/> runs before every
/// library open: a device-rate file made for ANOTHER rate (the device changed), or
/// one that is damaged, is re-derived from the song's <c>.wem</c> -- the archive's
/// own audio, extracted beside it by the package source's audio extract -- and the open converts
/// at load as it always did. <see cref="RecoverAfterFailedOpen"/> does the same when
/// the engine refuses a file. Nothing here deletes from the cache:
/// <see cref="LibraryAudioCacheStore"/> stays its only owner, and a song is still one
/// folder its sweep sizes and evicts whole. The stem cache keys on the <c>.wem</c>,
/// never on these bytes.
/// </para>
/// </summary>
public sealed class LibraryDeviceRateAudio
{
    /// <summary>Writes <paramref name="sourcePath"/> at a device rate (the native one
    /// is <see cref="JuceAudioEngine.ConvertAudioForDevice"/>).</summary>
    internal delegate JuceDeviceRateConversion DeviceRateConverter(
        string sourcePath, string destinationWavPath, int deviceSampleRate);

    /// <summary>Decodes the song's <c>.wem</c> back into its original WAV at
    /// <c>wavPath</c>, through a staging file. True when it did.</summary>
    internal delegate bool OriginalRederiver(string wemPath, string wavPath);

    private readonly LibraryAudioCacheStore _store;
    private readonly IAppLogger? _logger;
    private readonly DeviceRateConverter _convert;
    private readonly OriginalRederiver _rederive;
    private readonly Func<bool> _originalsReplaceable;
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="store">The cache whose songs this converts.</param>
    /// <param name="logger">Optional.</param>
    /// <param name="originalsReplaceable">False while something still needs the
    /// original WAV's exact bytes: the stem cache's one-off WEM-key adoption
    /// (<c>WemKeyAdoption</c>) proves an archive by the WAV it was keyed on,
    /// and stems are never regenerated. Null means always.</param>
    public LibraryDeviceRateAudio(LibraryAudioCacheStore store, IAppLogger? logger = null,
        Func<bool>? originalsReplaceable = null)
        : this(store, logger, JuceAudioEngine.ConvertAudioForDevice, RederiveFromWem, originalsReplaceable)
    {
    }

    internal LibraryDeviceRateAudio(LibraryAudioCacheStore store, IAppLogger? logger, DeviceRateConverter convert,
        OriginalRederiver? rederive = null, Func<bool>? originalsReplaceable = null)
    {
        _store = store;
        _logger = logger;
        _convert = convert;
        _rederive = rederive ?? RederiveFromWem;
        _originalsReplaceable = originalsReplaceable ?? (static () => true);
    }

    /// <summary>
    /// Before an open reads <paramref name="playablePath"/>'s bytes on a device at
    /// <paramref name="deviceSampleRate"/>. A device-rate file made for another rate,
    /// or a damaged one, is replaced by the original re-derived from the song's
    /// <c>.wem</c>, so the open converts at load. True when it
    /// re-derived. Anything else -- the original, a device-rate file for this rate, a
    /// file outside the cache, no engine yet, a header it cannot read -- is left
    /// alone. Never throws.
    /// </summary>
    public bool PrepareForOpen(string playablePath, int deviceSampleRate)
    {
        if (deviceSampleRate <= 0 || !InCache(playablePath)) return false;
        var header = DeviceRateWavHeader.Read(playablePath);
        var why = header.Kind switch
        {
            DeviceRateWavKind.Damaged => $"the device-rate file is damaged ({header.Problem})",
            DeviceRateWavKind.DeviceRate when header.SampleRate != deviceSampleRate =>
                $"it was converted for {header.SampleRate} Hz and the device runs at {deviceSampleRate} Hz",
            _ => null,
        };
        return why != null && Rederive(playablePath, why);
    }

    /// <summary>
    /// After the engine failed to open <paramref name="playablePath"/>: a device-rate
    /// file is replaced by the re-derived original and the caller opens again (true).
    /// The original itself failing is not this class's business (false).
    /// </summary>
    public bool RecoverAfterFailedOpen(string playablePath)
    {
        if (!InCache(playablePath)) return false;
        var header = DeviceRateWavHeader.Read(playablePath);
        return header.Kind != DeviceRateWavKind.Plain &&
               Rederive(playablePath, "the engine could not open the device-rate file");
    }

    /// <summary>
    /// Converts <paramref name="playablePath"/> to <paramref name="deviceSampleRate"/>
    /// in the background and replaces it, when it is an original below that rate and
    /// not already being converted. The result goes to a staging file in the song's
    /// folder and is renamed over the original only when its header checks out and
    /// the original did not change meanwhile, so an open sees one whole file or the
    /// other. A song without its <c>.wem</c> beside it is never converted, because
    /// nothing could re-derive it for another rate. Returns the background task
    /// (completed when there is nothing to do);
    /// callers need not await it.
    /// </summary>
    public Task ScheduleConversion(string playablePath, int deviceSampleRate)
    {
        if (deviceSampleRate <= 0 || !InCache(playablePath)) return Task.CompletedTask;
        // Only what can be undone: a device change re-derives the original from the
        // song's .wem. A file with none (a .dcsong's decode) is never replaced.
        if (!File.Exists(Path.ChangeExtension(playablePath, ".wem"))) return Task.CompletedTask;
        var header = DeviceRateWavHeader.Read(playablePath);
        if (header.Kind != DeviceRateWavKind.Plain || header.SampleRate <= 0 || header.SampleRate >= deviceSampleRate)
            return Task.CompletedTask;
        if (!_originalsReplaceable())
        {
            _logger?.Trace(AppLogCategory.Audio,
                $"Device-rate conversion of '{playablePath}' deferred: the stem cache still needs the original WAVs.");
            return Task.CompletedTask;
        }

        if (!_inFlight.TryAdd(playablePath, 0)) return Task.CompletedTask;
        return Task.Run(() =>
        {
            try { Replace(playablePath, header.SampleRate, deviceSampleRate); }
            finally { _inFlight.TryRemove(playablePath, out _); }
        });
    }

    private void Replace(string playablePath, int sourceRate, int deviceSampleRate)
    {
        var staging = StagingPathFor(playablePath);
        try
        {
            var before = new FileInfo(playablePath);
            var (length, written) = (before.Length, before.LastWriteTimeUtc);
            var sw = Stopwatch.StartNew();
            if (_convert(playablePath, staging, deviceSampleRate) == JuceDeviceRateConversion.NotNeeded) return;

            var result = DeviceRateWavHeader.Read(staging);
            if (result.Kind != DeviceRateWavKind.DeviceRate || result.SampleRate != deviceSampleRate)
            {
                _logger?.Warning(AppLogCategory.Audio,
                    $"Device-rate conversion of '{playablePath}' produced an unusable file " +
                    $"({result.Kind}, {result.SampleRate} Hz{(result.Problem is { } p ? ", " + p : "")}); the original stays.");
                return;
            }

            // A song re-extracted while it was being read can leave a conversion of
            // half the old bytes; never put that in its place.
            var after = new FileInfo(playablePath);
            if (!after.Exists || after.Length != length || after.LastWriteTimeUtc != written)
            {
                _logger?.Trace(AppLogCategory.Audio,
                    $"Device-rate conversion discarded: '{playablePath}' changed while it was converted.");
                return;
            }

            var convertedBytes = new FileInfo(staging).Length;
            File.Move(staging, playablePath, overwrite: true);
            _logger?.Info(AppLogCategory.Audio,
                $"Cached song converted to the device rate: '{playablePath}' {sourceRate} -> {deviceSampleRate} Hz, " +
                $"step {result.Step * 32768f:0.0000}/32768, {length:N0} -> {convertedBytes:N0} bytes, in " +
                $"{sw.ElapsedMilliseconds} ms; its next open skips the load-time conversion.");
        }
        catch (Exception ex)
        {
            // Best effort: without it the next open converts, exactly as before.
            _logger?.Warning(AppLogCategory.Audio,
                $"Device-rate conversion of '{playablePath}' at {deviceSampleRate} Hz failed: {ex.Message}");
        }
        finally
        {
            TryDelete(staging);
        }
    }

    private bool Rederive(string playablePath, string why)
    {
        var wem = Path.ChangeExtension(playablePath, ".wem");
        if (!File.Exists(wem))
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"'{playablePath}': {why}, and its .wem is gone, so it cannot be re-derived here; " +
                "the next pick of the song extracts it from the archive again.");
            return false;
        }

        try
        {
            var sw = Stopwatch.StartNew();
            if (!_rederive(wem, playablePath))
            {
                _logger?.Warning(AppLogCategory.Audio, $"'{playablePath}': {why}; re-deriving it from '{wem}' failed.");
                return false;
            }

            _logger?.Info(AppLogCategory.Audio,
                $"'{playablePath}': {why}; re-derived the original from its .wem in {sw.ElapsedMilliseconds} ms, " +
                "so this open converts at load.");
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"'{playablePath}': {why}; re-deriving it from '{wem}' failed: {ex.Message}");
            return false;
        }
    }

    private static bool RederiveFromWem(string wemPath, string wavPath)
        => AudioFormatConverter.TryWriteStaged(wavPath,
            staging => AudioFormatConverter.WriteNativeWave(wemPath, staging, CancellationToken.None));

    private bool InCache(string playablePath)
    {
        try
        {
            return _store.Contains(playablePath) && File.Exists(playablePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The staging name <see cref="AudioFormatConverter.TryWriteStaged"/>
    /// uses too: <c>&lt;id&gt;.part-&lt;guid&gt;.wav</c> in the song's folder.</summary>
    private static string StagingPathFor(string playablePath)
        => Path.Combine(Path.GetDirectoryName(playablePath) ?? "",
            $"{Path.GetFileNameWithoutExtension(playablePath)}.part-{Guid.NewGuid():N}.wav");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* swept with the folder */ }
    }
}
