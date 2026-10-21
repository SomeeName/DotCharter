using System.Security.Cryptography;
using System.Text;

namespace DotCharter.Core.Linking;

/// <summary>
///     The canonical content of a run of measures (<see cref="MeasureContentIndex.KeyFor" />).
///     Two runs hold the same content exactly when their keys are equal. The
///     <see cref="Hash" /> is stable across processes and machines (SHA-256 of the
///     canonical text), so it can be stored with a link group and re-checked on load.
/// </summary>
public sealed class MeasureContentKey : IEquatable<MeasureContentKey>
{
    private string? _hash;

    internal MeasureContentKey(string canonical, int soundCount)
    {
        Canonical = canonical;
        SoundCount = soundCount;
    }

    /// <summary>The canonical text the key compares. Its format is internal and versioned
    /// by its first token; compare keys, never parse this.</summary>
    public string Canonical { get; }

    /// <summary>Notes and chords that start inside the run.</summary>
    public int SoundCount { get; }

    /// <summary>No sound starts inside the run (rests only). Empty runs are never linked.</summary>
    public bool IsEmpty => SoundCount == 0;

    /// <summary>32 lowercase hex digits: the first 16 bytes of SHA-256 over the UTF-8 canonical text.</summary>
    public string Hash => _hash ??= Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical)).AsSpan(0, 16));

    public bool Equals(MeasureContentKey? other) =>
        other is not null && string.Equals(Canonical, other.Canonical, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is MeasureContentKey other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Canonical);

    public override string ToString() => Hash;
}
