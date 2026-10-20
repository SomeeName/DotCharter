using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace DotCharter.Services.Audio.StemSeparation.Cache;

/// <summary>
/// A cheap, deterministic content fingerprint of a source audio file that
/// invalidates cached stems when the underlying audio changes. Combines the
/// exact byte length with a sampled SHA-256 so that a re-packed CDLC that
/// reuses a SongKey (or an own-chart audio file that was replaced in place)
/// never serves stale stems.
/// </summary>
/// <remarks>
/// The hash is deliberately a <b>fingerprint, not a cryptographic guarantee</b>:
/// for files larger than <see cref="FullHashThresholdBytes"/> only three
/// windows (head / middle / tail) are folded into the digest, so computing a
/// signature stays fast enough to run on every song-open (cache lookup) without
/// re-reading tens of megabytes. The exact byte length is always part of the
/// digest, and the fingerprint is combined with the logical song identity in
/// <see cref="StemCacheKey"/>, so the odds of two genuinely different songs
/// colliding are negligible in practice.
/// </remarks>
public readonly record struct StemAudioSignature(long Bytes, string Sha256Hex)
{
    /// <summary>Sentinel for "no audio / could not read".</summary>
    public static readonly StemAudioSignature None = new(0, string.Empty);

    public bool IsEmpty => Bytes == 0 && string.IsNullOrEmpty(Sha256Hex);

    private const int WindowBytes = 256 * 1024;

    /// <summary>Files at or below this size are hashed in full; larger files
    /// are sampled (head/middle/tail windows).</summary>
    public const long FullHashThresholdBytes = 3L * WindowBytes; // 768 KB

    /// <summary>
    /// Computes the signature for a file. Returns <see cref="None"/> if the
    /// file cannot be read.
    /// </summary>
    public static StemAudioSignature ComputeFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1 << 16, FileOptions.SequentialScan);
            return Compute(fs);
        }
        catch
        {
            return None;
        }
    }

    /// <summary>
    /// Computes the signature over a stream. When the stream is seekable and
    /// larger than <see cref="FullHashThresholdBytes"/>, only sample windows
    /// are read; otherwise the whole stream is hashed. Deterministic for a
    /// given byte content regardless of which path is taken (the strategy is
    /// selected purely by length, which is itself folded into the digest).
    /// </summary>
    public static StemAudioSignature Compute(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var length = stream.CanSeek ? stream.Length : -1;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // Fold the exact length in first so two files that share sampled bytes
        // but differ in size can never collide.
        Span<byte> lengthBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(lengthBytes, length);
        hash.AppendData(lengthBytes);

        var buffer = new byte[1 << 16];

        if (!stream.CanSeek || length < 0 || length <= FullHashThresholdBytes)
        {
            // Small or non-seekable: hash everything. For a non-seekable stream
            // we also count the bytes so the length prefix above was written as
            // -1, keeping the result stable across repeated non-seekable reads.
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                hash.AppendData(buffer, 0, read);
            return new StemAudioSignature(length < 0 ? StreamCountFallback(stream) : length, ToHex(hash.GetHashAndReset()));
        }

        // Large + seekable: three fixed sample windows.
        var mid = Math.Max(0, (length / 2) - (WindowBytes / 2));
        var tail = Math.Max(0, length - WindowBytes);
        foreach (var start in new[] { 0L, mid, tail })
        {
            stream.Seek(start, SeekOrigin.Begin);
            long remaining = WindowBytes;
            while (remaining > 0)
            {
                var want = (int)Math.Min(buffer.Length, remaining);
                var read = stream.Read(buffer, 0, want);
                if (read <= 0) break;
                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }
        }

        return new StemAudioSignature(length, ToHex(hash.GetHashAndReset()));
    }

    private static readonly ConcurrentDictionary<(string Path, long Length, long WriteTicks), string> FullHashMemo =
        new(StringTupleComparer.Instance);

    /// <summary>
    /// SHA-256 over the WHOLE file (lowercase hex), or <c>null</c> when it cannot be
    /// read. The cache KEY keeps the sampled <see cref="ComputeFile"/> (changing it
    /// would strand every archive on disk); this is what an archive RECORDS so a hit
    /// can be verified: a same-length re-render with an edit between the
    /// three sample windows would keep serving the old stems.
    /// <para>Memoized per (path, length, last-write time), so each version of a file
    /// is read once per session. The decoder has just read it, so it is page-cache
    /// hot: tens of milliseconds for a 40 MB WAV.</para>
    /// </summary>
    public static string? ComputeFullFileHash(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            var memoKey = (Path.GetFullPath(path), info.Length, info.LastWriteTimeUtc.Ticks);
            if (FullHashMemo.TryGetValue(memoKey, out var known)) return known;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16,
                FileOptions.SequentialScan);
            var hex = ToHex(SHA256.HashData(fs));
            if (FullHashMemo.Count > 512) FullHashMemo.Clear();
            FullHashMemo[memoKey] = hex;
            return hex;
        }
        catch
        {
            return null;
        }
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string Path, long Length, long WriteTicks)>
    {
        public static readonly StringTupleComparer Instance = new();

        public bool Equals((string Path, long Length, long WriteTicks) x, (string Path, long Length, long WriteTicks) y) =>
            x.Length == y.Length && x.WriteTicks == y.WriteTicks
            && string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Path, long Length, long WriteTicks) k) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(k.Path), k.Length, k.WriteTicks);
    }

    // A non-seekable stream's length is unknown up front; we already hashed it
    // fully above, but we can't cheaply report a byte count without buffering,
    // so callers on that path get 0. Seekable file streams (the real world
    // case) never hit this.
    private static long StreamCountFallback(Stream _) => 0;

    private static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
