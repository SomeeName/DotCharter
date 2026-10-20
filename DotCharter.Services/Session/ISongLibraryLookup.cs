using DotCharter.Services.Library.Index;

namespace DotCharter.Services.Session;

/// <summary>
///     What the Home screen asks the song library: how big it is, and which
///     cover image belongs to a play-stats song key.
/// </summary>
public interface ISongLibraryLookup
{
    /// <inheritdoc cref="ILibraryIndex.ReadFacts" />
    LibraryIndexFacts ReadFacts();

    /// <summary>
    ///     The cover image on disk for a play-stats key, or "" when there is none.
    ///     A library key (<c>lib:</c>, or the older <c>psarc:</c>) asks the library index; any other key is an own
    ///     chart's project path (<c>LivePlayViewModel.ProfileSongKey</c>).
    ///     Best-effort and cheap enough to call on every Home refresh.
    /// </summary>
    string FindCover(string statsKey);
}
