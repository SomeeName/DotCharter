namespace DotCharter.Services.Session;

/// <summary>What one re-key moved, by store.</summary>
/// <param name="PracticeEntries">Arrangements moved in the practice stores (stats, bests; the leaderboards read them).</param>
/// <param name="SetlistEntries">Setlist songs retargeted.</param>
/// <param name="ConfigEntries">Per-song settings moved in appconfig (tone preference, drop-tuner opt-in).</param>
public sealed record ProjectRekeyResult(int PracticeEntries, int SetlistEntries, int ConfigEntries)
{
    public int Total => PracticeEntries + SetlistEntries + ConfigEntries;
}

/// <summary>
///     Moves everything the app keeps under an own chart's PROJECT PATH to the path the project
///     now lives at, when the project file is renamed by a migration (<c>X.rscp</c> to
///     <c>X.dcproj</c>): Play Mode stats and best scores (and so the leaderboards), setlist
///     entries and the per-song settings in appconfig.
/// </summary>
public interface IProjectPathRekey
{
    /// <summary>
    ///     Moves each store's entries from <paramref name="fromPath" /> to <paramref name="toPath" />.
    ///     Each store is written atomically; an entry already under the new path is merged with
    ///     the old one, never replaced; a store that fails is logged and left as it was, and the
    ///     others still move. Running it again moves nothing. Never throws.
    /// </summary>
    ProjectRekeyResult Rekey(string fromPath, string toPath);
}
