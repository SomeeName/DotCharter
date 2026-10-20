using DotCharter.Services.Audio.StemSeparation.Cache;

namespace DotCharter.Services.Audio.Waveform;

/// <summary>
///     Generate-once disk cache for the timeline waveform (0.14 P4). Keyed
///     exactly like the stem cache — the logical song identity folded with the
///     sampled audio-content signature (<see cref="StemCacheKey.Derive"/>) —
///     so the same song + audio hits forever and changed audio bytes can never
///     serve stale peaks.
///     <para>
///     Only the BASE mipmap level is persisted (~1.2 MB for a 10-minute song
///     at <see cref="WaveformSamples.GenerationBlockMs"/> blocks); BOTH
///     pyramids are rebuilt via <see cref="WaveformSamples.FromBasePeaks"/> so
///     persisted and generated ones are byte-identical by construction.
///     Schema-versioned; EVERY read failure (corrupt file, schema mismatch,
///     wrong resolution, IO/ACL error) degrades to a miss and a regenerate —
///     the cache is never the source of truth, so unlike the stem cache
///     nothing here is precious.
///     </para>
///     <para>
///     <b>The stored block width is validated, not trusted.</b> An entry whose
///     <c>blockMs</c> is not the current
///     <see cref="WaveformSamples.GenerationBlockMs"/> is a MISS (and is
///     deleted). Without that guard, raising the pyramid's resolution would
///     leave every user with a warm cache pinned to the old coarse peaks
///     forever — the file carries its own blockMs, so it would keep loading
///     perfectly and keep looking wrong. This is the mechanism that makes a
///     resolution change safe; do not replace it with "remember to bump the
///     schema version".
///     </para>
///     <para>Thread-safe for the app's usage (one load at a time per project
///     open); a torn concurrent write is prevented by the temp-then-move
///     publish and would in the worst case read as corruption = miss.</para>
/// </summary>
public sealed class WaveformDiskCache
{
    private const uint Magic = 0x56574344; // "DCWV" little-endian

    /// <summary>
    ///     Bumped whenever the BINNING changes, not just the file layout —
    ///     a cached entry stores the peaks a particular generator produced, so
    ///     a generator that now places its block boundaries somewhere else is
    ///     as much a schema change as a new field would be.
    ///     <para>2: version 1 binned on a ROUNDED
    ///     integer samples-per-block, so at 44.1 kHz a nominal 2 ms block was
    ///     really 1.99546 ms and the whole pyramid ran 0.227 % fast against the
    ///     <c>index × BaseBlockMs</c> mapping every renderer uses (+408 ms three
    ///     minutes into a song — the waveform drawn to the RIGHT of its audio).
    ///     Version 2 boundaries are fractional. The stored <c>blockMs</c> is 2.0 either
    ///     way, so nothing but this number can tell the two apart.</para>
    /// </summary>
    private const int SchemaVersion = 2;

    /// <summary>Backstop against a corrupt count field allocating gigabytes:
    /// 16M blocks at <see cref="WaveformSamples.GenerationBlockMs"/> is
    /// ~9 hours of audio.</summary>
    private const int MaxBaseBlocks = 16_000_000;

    /// <summary>Float slop on the persisted block width — the value round-trips
    /// through a binary double, so this is exact-match with room for nothing
    /// but representation noise, never a "close enough" resolution.</summary>
    private const double BlockMsTolerance = 1e-9;

    private readonly string _root;
    private readonly IAppLogger? _logger;

    public WaveformDiskCache(IAppLogger? logger = null, string? root = null)
    {
        _logger = logger;
        _root = string.IsNullOrWhiteSpace(root) ? DefaultRoot : root!;
    }

    public static string DefaultRoot => Path.Combine(
        UserDataFolders.LocalAppData,
        "DotCharter", "WaveformCache");

    public string CachePath(StemCacheKey key) => Path.Combine(_root, key.DirectoryToken + ".dcwave");

    /// <summary>Cache lookup. Never throws for cache problems — any failure is
    /// a miss (and a corrupt/mismatched file is deleted so it cannot miss
    /// forever). Returns a FRESH <see cref="WaveformSamples"/> instance —
    /// consumers compare by reference.</summary>
    public async Task<WaveformSamples?> TryLoadAsync(StemCacheIdentity identity, string audioPath)
        => await Task.Run(() => TryLoad(identity, audioPath)).ConfigureAwait(false);

    public WaveformSamples? TryLoad(StemCacheIdentity identity, string audioPath)
    {
        string? path = null;
        try
        {
            if (!File.Exists(audioPath)) return null;
            var key = DeriveKey(identity, audioPath);
            if (key == null) return null;

            path = CachePath(key);
            if (!File.Exists(path)) return null;

            float[]? peaks = null;
            double blockMs = 0;
            // Inner scope: the stream MUST be closed before the corrupt-entry
            // delete below — File.Delete on a file we still hold open fails
            // silently and the entry would miss forever.
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                       1 << 16, FileOptions.SequentialScan))
            using (var br = new BinaryReader(fs))
            {
                if (br.ReadUInt32() == Magic && br.ReadInt32() == SchemaVersion)
                {
                    blockMs = br.ReadDouble();
                    var count = br.ReadInt32();
                    // A right-shaped entry at the WRONG resolution is as useless
                    // as a corrupt one — see the type doc.
                    if (Math.Abs(blockMs - WaveformSamples.GenerationBlockMs) <= BlockMsTolerance
                        && count > 0 && count <= MaxBaseBlocks)
                    {
                        var bytes = br.ReadBytes(count * sizeof(float));
                        if (bytes.Length == count * sizeof(float))
                        {
                            peaks = new float[count];
                            Buffer.BlockCopy(bytes, 0, peaks, 0, bytes.Length);
                        }
                    }
                }
            }

            if (peaks == null)
            {
                // Corrupt / schema-mismatched entry: delete so it cannot miss
                // forever (unlike the stem cache, nothing here is precious —
                // the next open regenerates it in one pass).
                BestEffortDelete.File(path);
                return null;
            }

            return WaveformSamples.FromBasePeaks(peaks, blockMs);
        }
        catch (EndOfStreamException)
        {
            // Truncated entry (torn write, disk-full leftovers) — corrupt.
            if (path != null) BestEffortDelete.File(path);
            return null;
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"Waveform cache read failed ({ex.Message}); regenerating.");
            return null;
        }
    }

    /// <summary>Best-effort save; failures are logged and ignored (the cache
    /// regenerates). Publishes via temp-file + move so readers never see a
    /// half-written entry.</summary>
    public void Save(StemCacheIdentity identity, string audioPath, WaveformSamples samples)
    {
        if (samples is not { HasData: true }) return;

        try
        {
            var key = DeriveKey(identity, audioPath);
            if (key == null) return;

            Directory.CreateDirectory(_root);
            var path = CachePath(key);
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

            var basePeaks = samples.Mipmaps[0];
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(Magic);
                bw.Write(SchemaVersion);
                bw.Write(samples.BaseBlockMs);
                bw.Write(basePeaks.Length);
                var bytes = new byte[basePeaks.Length * sizeof(float)];
                Buffer.BlockCopy(basePeaks, 0, bytes, 0, bytes.Length);
                bw.Write(bytes);
            }

            // Atomic overwrite (no delete-then-move window a concurrent open
            // could see as a miss). On a losing concurrent-writer race the Move
            // throws — clean up our temp file so `.dcwave.*.tmp` orphans never
            // accumulate.
            try
            {
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                BestEffortDelete.File(tmp);
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"Waveform cache write failed ({ex.Message}); the next open regenerates.");
        }
    }

    /// <summary>Null when the audio can't be read (signature =
    /// <see cref="StemAudioSignature.None"/>) — a keyless entry could serve
    /// stale peaks after the audio changes, so no key means no caching.
    /// <para>The guard now lives in <see cref="StemCacheKey.TryDerive"/> so BOTH
    /// caches share one predicate; it was open-coded here and missing entirely on
    /// the stem side, which is the side whose artifacts are never regenerated.</para>
    /// </summary>
    private static StemCacheKey? DeriveKey(StemCacheIdentity identity, string audioPath)
        => StemCacheKey.TryDerive(identity, StemAudioSignature.ComputeFile(audioPath));
}
