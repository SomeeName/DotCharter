using System.Diagnostics.CodeAnalysis;

namespace DotCharter.Services.Library;

/// <summary>
///     The ONE place that knows how a library song's identity string is spelled:
///     <c>lib:&lt;SongKey&gt;</c> for a song from the player's library (a <c>.psarc</c>
///     or a <c>.dcsong</c>), as opposed to an own chart, which is keyed on its project
///     path. Every store keyed on song identity (practice profiles and stats, setlists,
///     tone edits, per-song tone preferences, the drop-tuner list, checkpoints, the stem
///     cache) goes through here.
///     <para>
///         <b>Older builds wrote <c>psarc:&lt;SongKey&gt;</c>.</b> Every reader
///         accepts both; every writer writes <see cref="Prefix" />. A store migrates its
///         old entries when it loads them, and an old entry is only dropped once its new
///         one is written — an existing <c>lib:</c> entry always wins over a
///         <c>psarc:</c> duplicate.
///     </para>
///     <para>
///         The stem cache hashes the identity into its archive names, so its migration
/// is a RE-KEY rather than a map rewrite: an archive found under the
///         <c>psarc:</c>-derived name is copied with its recorded identity rewritten,
///         verified and only then filed under the <c>lib:</c> name
///         (<c>StemCacheStore.TryRekeyLegacyLibraryArchive</c>). Stems are never
///         re-separated.
///     </para>
/// </summary>
public static class LibrarySongIdentity
{
    /// <summary>The prefix every build writes.</summary>
    public const string Prefix = "lib:";

    /// <summary>The prefix earlier builds wrote; read, never written.</summary>
    public const string LegacyPrefix = "psarc:";

    /// <summary>The identity of the library song <paramref name="songKey" />.</summary>
    public static string For(string songKey) => Prefix + songKey;

    /// <summary>A library song's identity, in either spelling.</summary>
    public static bool IsLibrary([NotNullWhen(true)] string? identity)
    {
        var trimmed = identity?.TrimStart();
        return trimmed != null &&
               (trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith(LegacyPrefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The old <c>psarc:</c> spelling.</summary>
    public static bool IsLegacy([NotNullWhen(true)] string? identity) =>
        identity != null && identity.StartsWith(LegacyPrefix, StringComparison.Ordinal);

    /// <summary>The song key inside a library identity of either spelling.</summary>
    public static bool TryGetSongKey(string? identity, out string songKey)
    {
        if (identity != null && identity.StartsWith(Prefix, StringComparison.Ordinal))
        {
            songKey = identity[Prefix.Length..];
            return true;
        }

        if (IsLegacy(identity))
        {
            songKey = identity[LegacyPrefix.Length..];
            return true;
        }

        songKey = string.Empty;
        return false;
    }

    /// <summary>
    ///     <paramref name="identity" /> in the current spelling: a <c>psarc:</c> identity
    ///     becomes <c>lib:</c>; anything else (an own chart's path, a <c>lib:</c> identity,
    ///     null) comes back unchanged. Works on a store key that STARTS with the identity
    ///     (<c>psarc:X|Lead:Lead</c>) too.
    /// </summary>
    public static string Normalize(string identity) =>
        IsLegacy(identity) ? Prefix + identity[LegacyPrefix.Length..] : identity;

    /// <inheritdoc cref="Normalize(string)" />
    public static string? NormalizeOrNull(string? identity) => identity == null ? null : Normalize(identity);

    /// <summary>The spelling earlier builds wrote for this identity, or null when it has none
    /// (not a <c>lib:</c> identity).</summary>
    public static string? LegacyFormOf(string? identity) =>
        identity != null && identity.StartsWith(Prefix, StringComparison.Ordinal)
            ? LegacyPrefix + identity[Prefix.Length..]
            : null;

    /// <summary>
    ///     Rewrites every <c>psarc:</c> key of <paramref name="entries" /> to <c>lib:</c>.
    ///     A key whose new spelling is already present keeps the existing entry (lib wins)
    ///     and the old one is dropped from the in-memory map only — the caller's next
    ///     atomic write is what removes it from disk, together with writing the new one.
    ///     Idempotent. Returns how many keys changed.
    /// </summary>
    public static int MigrateKeys<T>(IDictionary<string, T> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var legacy = entries.Keys.Where(IsLegacy).ToList();
        foreach (var key in legacy)
        {
            var current = Normalize(key);
            if (!entries.ContainsKey(current)) entries[current] = entries[key];
            entries.Remove(key);
        }

        return legacy.Count;
    }
}
