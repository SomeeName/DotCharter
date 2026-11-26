using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Maintenance;

/// <summary>
///     Sets the optional per-arrangement artist, artist-sort and album overrides as one value pair, so a
///     whole-project clean-up (the export dialog's stray-space trim) is a single undo node.
/// </summary>
public sealed class SetArrangementMetadataTextAction(Arrangement arrangement, (string Artist, string ArtistSort, string Album) after)
    : IChartAction, ILinkPolicyAction
{
    private readonly (string Artist, string ArtistSort, string Album) _before = (arrangement.ArtistName, arrangement.ArtistNameSort, arrangement.AlbumName);

    public LinkPolicy LinkPolicy => LinkPolicy.Retain;

    public bool HasEffect => _before != after;

    public void Execute() => Apply(after);

    public void Undo() => Apply(_before);

    private void Apply((string Artist, string ArtistSort, string Album) values)
    {
        arrangement.ArtistName = values.Artist;
        arrangement.ArtistNameSort = values.ArtistSort;
        arrangement.AlbumName = values.Album;
    }
}
