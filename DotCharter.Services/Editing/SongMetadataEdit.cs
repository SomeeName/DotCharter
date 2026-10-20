using DotCharter.Services.State;

namespace DotCharter.Services.Editing;

/// <summary>
///     The song-metadata fields as one value — the before/after pair of a
///     metadata Save. A record on purpose: value equality is what lets
///     <see cref="SetSongMetadataAction.HasEffect" /> report a no-op Save, so
///     OK-without-changes does not grow the undo tree.
/// </summary>
public sealed record SongMetadataFields(
    string Artist,
    string ArtistSort,
    string SongName,
    string JapaneseArtist,
    string JapaneseTitle,
    string AlbumName,
    string AlbumYear,
    string ReleaseDate,
    string Charter,
    string Genre,
    int KeyAccidentals,
    bool KeyIsMinor,
    string Version = "1")
{
    public static SongMetadataFields From(ChartData chart)
    {
        return new SongMetadataFields(chart.Artist, chart.ArtistSort, chart.SongName,
            chart.JapaneseArtist, chart.JapaneseTitle, chart.AlbumName,
            chart.AlbumYear, chart.ReleaseDate, chart.Charter, chart.Genre,
            chart.KeyAccidentals, chart.KeyIsMinor, chart.Version);
    }

    public void ApplyTo(ChartData chart)
    {
        chart.Artist = Artist;
        chart.ArtistSort = ArtistSort;
        chart.SongName = SongName;
        chart.JapaneseArtist = JapaneseArtist;
        chart.JapaneseTitle = JapaneseTitle;
        chart.AlbumName = AlbumName;
        chart.AlbumYear = AlbumYear;
        chart.ReleaseDate = ReleaseDate;
        chart.Charter = Charter;
        chart.Genre = Genre;
        chart.KeyAccidentals = KeyAccidentals;
        chart.KeyIsMinor = KeyIsMinor;
        chart.Version = Version;
    }
}

/// <summary>
///     One Song Metadata Save — ONE undo node, and not mergeable, so a Save can
///     never fold into an in-flight editing gesture. Undo restores every field
///     the dialog owns.
/// </summary>
public sealed class SetSongMetadataAction(ChartData chart, SongMetadataFields before, SongMetadataFields after)
    : IChartAction
{
    public bool HasEffect => before != after;

    public void Execute() => after.ApplyTo(chart);

    public void Undo() => before.ApplyTo(chart);
}
