namespace DotCharter.Services.Export.DlcBuilder;

/// <summary>
///     The names inside the DLC Builder bundle a save writes beside the
///     <c>.rscp</c>: the folder, the RS-XML file per arrangement / vocal track, and
///     the showlights file.
/// </summary>
/// <remarks>
///     This exists because TWO writers now have to agree on them —
///     <c>ProjectDocumentService.SaveDlcBuilderXmlBundle</c>, which puts the files
///     there, and <see cref="Rs2DlcProjectBuilder" />, which writes the
///     <c>.rs2dlc</c> project that POINTS AT them by relative path. A second
///     hand-written copy of the naming would produce a project file referencing XML
///     that does not exist, and DLC Builder reports that as an empty project rather
///     than as a missing file.
/// </remarks>
public static class DlcBuilderBundle
{
    /// <summary>The bundle folder, a sibling of the project file.</summary>
    public const string DirectoryName = "DTRS XML";

    /// <summary>The single showlights file, written only when the project has any.</summary>
    public const string ShowlightsFileName = "showlights.xml";

    /// <summary>Absolute path of the bundle folder for a project file.</summary>
    public static string DirectoryFor(string projectPath)
    {
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
        if (string.IsNullOrWhiteSpace(projectDirectory))
            projectDirectory = Directory.GetCurrentDirectory();
        return Path.Combine(projectDirectory, DirectoryName);
    }

    /// <summary><c>1_Lead_Main_RS2.xml</c>. <paramref name="id" /> is 1-based.</summary>
    public static string ArrangementFileName(int id, Arrangement arrangement)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        return $"{id}_{arrangement.ArrangementType}_{arrangement.Subtype}_RS2.xml";
    }

    /// <summary><c>1_Vocals_RS2.xml</c>, or <c>1_Vocals_&lt;name&gt;_RS2.xml</c> when
    /// the track is named. <paramref name="id" /> is 1-based.</summary>
    public static string VocalTrackFileName(int id, VocalTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);

        var sanitizedName = string.IsNullOrWhiteSpace(track.Name)
            ? string.Empty
            : new string(track.Name
                .Where(c => char.IsAsciiLetterOrDigit(c) || c == ' ' || c == '-')
                .ToArray()).Trim();

        return string.IsNullOrWhiteSpace(sanitizedName)
            ? $"{id}_Vocals_RS2.xml"
            : $"{id}_Vocals_{sanitizedName}_RS2.xml";
    }

    /// <summary>A vocal track is only written when it actually carries lyrics —
    /// so the <c>.rs2dlc</c> must apply the same filter or it names a file the
    /// bundle never produced.</summary>
    public static bool HasLyrics(VocalTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return track.Phrases.Any(phrase => phrase.Lyrics.Count > 0);
    }
}
