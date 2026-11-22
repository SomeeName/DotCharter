using DotCharter.Core.Domain.Project;
using DotCharter.Localization;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     What the export dialog derives from the song's title, artist and version: the DLC key and the PC package
///     name. It remembers the name last derived so an output file named by it can follow a changed title.
/// </summary>
internal sealed class RocksmithExportSongNames(ILocalizationService loc)
{
    public string Key { get; private set; } = "";

    /// <summary>The PC package name the song's title, artist and version derive now.</summary>
    public string PackageName { get; private set; } = "";

    public string KeyWatermark => loc["Dialog_Export_DlcKeyAuto"] + " — " + Key;

    public void RefreshKey(ChartProject project)
    {
        var title = string.IsNullOrWhiteSpace(project.Chart.SongName) ? "Untitled" : project.Chart.SongName.Trim();
        var artist = string.IsNullOrWhiteSpace(project.Chart.Artist) ? "Unknown Artist" : project.Chart.Artist.Trim();
        Key = RocksmithDlcKey.Create(artist, title);
    }

    /// <summary>Derives the package name for <paramref name="version" /> and returns the previous one.</summary>
    public string Advance(ChartProject project, string version)
    {
        var before = PackageName;
        PackageName = RocksmithPackageName.For(project, version);
        return before;
    }

    /// <summary>The output path after the names changed: a name derived from the previous names follows; a typed one stays.</summary>
    public string? Follow(ChartProject project, string version, string outputPath)
    {
        RefreshKey(project);
        var previous = Advance(project, version);
        return RocksmithReleaseName.Retarget(outputPath, previous, PackageName);
    }
}
