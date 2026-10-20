using DotCharter.Services.Library;

namespace DotCharter.Services.Audio.StemSeparation.Cache;

/// <summary>
/// The logical identity of the song whose stems are being cached. Two flavours:
/// <list type="bullet">
///   <item><b>PSARC / library songs</b> — carry a stable
///     <see cref="LogicalId"/> derived from SongKey (+ Charter + Version when
///     known). A re-packed CDLC
///     can reuse a SongKey, so the cache key ALSO folds in the audio content
///     signature (see <see cref="StemCacheKey"/>).</item>
///   <item><b>Own-chart projects</b> — have no library id (<see cref="LogicalId"/>
///     is null); they key off the audio CONTENT SIGNATURE alone. They do not
///     fold the normalized source-audio PATH in, which would make a rename, a
///     moved project folder or a switch to a synced path re-separate
///     byte-identical audio — see <see cref="StemCacheKey"/>.</item>
/// </list>
/// This is a pure value object; it carries no filesystem side effects.
/// </summary>
public sealed record StemCacheIdentity
{
    /// <summary>
    /// Stable per-song identity (e.g. <c>lib:SONGKEY|Charter|Version</c> or
    /// <c>lib:SONGKEY</c> — see
    /// <see cref="ForLibrary" />). Null for own-chart projects, which key off the
    /// audio path instead.
    /// </summary>
    public string? LogicalId { get; init; }

    /// <summary>
    /// Human/diagnostic label for the source (typically the audio file path).
    /// It is NOT part of an own-chart key any more; what it still decides is
    /// whether the identity names anything at all (<see cref="StemCacheKey.TryDerive"/>
    /// refuses one that names neither a song nor a source), and it is the basis of
    /// the SUPERSEDED key an existing archive may still be filed under
    /// (<see cref="StemCacheKey.TryDeriveSuperseded"/>).
    /// </summary>
    public string SourceLabel { get; init; } = string.Empty;

    /// <summary>Library identity from a song identity (<c>lib:SongKey</c>, or the
    /// <c>psarc:SongKey</c> earlier builds wrote; optionally <c>|Charter|Version</c>).
    /// <para>Always in the CURRENT spelling (<c>lib:</c>): the key hashes it, and
    /// an archive an earlier build filed under the <c>psarc:</c>-derived name is
    /// re-keyed on lookup (<see cref="StemCacheKey.LegacyLibraryForm" />,
    /// <c>StemCacheStore.TryRekeyLegacyLibraryArchive</c>), never re-separated.</para></summary>
    public static StemCacheIdentity ForLibrary(string leaderboardOrSongKey, string sourceLabel = "")
    {
        var id = string.IsNullOrWhiteSpace(leaderboardOrSongKey)
            ? null
            : LibrarySongIdentity.Normalize(leaderboardOrSongKey.Trim());
        return new StemCacheIdentity { LogicalId = id, SourceLabel = sourceLabel };
    }

    /// <summary>Own-chart identity: keyed on the audio CONTENT, not its path.</summary>
    public static StemCacheIdentity ForAudioFile(string audioPath) =>
        new() { LogicalId = null, SourceLabel = audioPath ?? string.Empty };
}
