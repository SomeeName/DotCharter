using System.Collections.Concurrent;
using DotCharter.Analysis.Key;
using DotCharter.Services.Audio.Lyrics;

namespace DotCharter.Services.Audio.Key;

/// <summary>Reads a song's key from its audio, for a song with no notes yet.</summary>
public interface ISongKeyAudioEstimator
{
    /// <summary>The key of the audio at <paramref name="audioPath" />, or null when it is missing, unreadable or too quiet.</summary>
    Task<SongKeyEstimate?> EstimateAsync(string audioPath, CancellationToken ct);
}

/// <summary>
///     Decodes the mix to 16 kHz mono off the UI thread, sums its chroma
///     (<see cref="ChromaKeyHistogram" />) and ranks the 24 keys through
///     <see cref="SongKeyEstimator.FromHistogram" />, the same correlation the notes use.
///     The answer is remembered for the app session per file (full path, length, last-write
///     time), so reopening the dialog never decodes an unchanged file again. The file is
///     stat'ed on the worker thread, so this method never touches the disk on the calling
///     thread; locating the audio file is the caller's job and belongs off the UI thread too.
/// </summary>
public sealed class SongKeyAudioEstimator : ISongKeyAudioEstimator
{
    private const int SampleRate = 16000;

    private readonly ConcurrentDictionary<(string Path, long Length, long WriteTicks), Lazy<SongKeyEstimate?>> _cache = new();
    private readonly Func<string, CancellationToken, SongKeyEstimate?> _compute;

    public SongKeyAudioEstimator() : this(Compute)
    {
    }

    /// <summary>Test seam: <paramref name="compute" /> replaces the decode + chroma step.</summary>
    public SongKeyAudioEstimator(Func<string, CancellationToken, SongKeyEstimate?> compute) => _compute = compute;

    public Task<SongKeyEstimate?> EstimateAsync(string audioPath, CancellationToken ct) => Task.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(audioPath)) return null;
        var info = new FileInfo(audioPath);
        if (!info.Exists) return null;
        var key = (info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
        var entry = _cache.GetOrAdd(key, k => new Lazy<SongKeyEstimate?>(() => _compute(k.Item1, ct)));
        try
        {
            return entry.Value;
        }
        catch
        {
            _cache.TryRemove(key, out _);
            throw;
        }
    }, ct);

    private static SongKeyEstimate? Compute(string path, CancellationToken ct) =>
        SongKeyEstimator.FromHistogram(ChromaKeyHistogram.FromMono(LyricAudioSource.LoadMono16k(path), SampleRate, ct));
}
