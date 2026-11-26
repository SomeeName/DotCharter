namespace DotCharter.Services.Editing;

/// <summary>The stray-space rule for the song's title, artist and album: leading and trailing white space is dropped and a run of white space becomes ONE space.</summary>
public static class SongMetadataSpaces
{
    public static string Tidy(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
