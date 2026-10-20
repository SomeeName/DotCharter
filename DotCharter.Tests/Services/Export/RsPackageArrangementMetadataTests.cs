using System.Xml.Linq;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Export;

/// <summary>
/// Artist / album / year live on <see cref="Arrangement"/> as well as on
/// <c>ChartData</c> — <c>RsXmlSongReader</c> fills the arrangement fields and
/// <c>RsXmlSongWriter</c> PREFERS them. The psarc manifest read only the
/// chart-level values, so a chart whose metadata arrived on the arrangement
/// shipped as "Unknown Artist" with no album in the strings the game's song list
/// shows, while the XML beside it in the same bundle carried the real ones.
/// </summary>
public sealed class RsPackageArrangementMetadataTests
{
    private static ChartProject BuildProject()
    {
        var project = new ChartProject { Bpm = 120, Chart = { SongName = "Arrangement Metadata" } };
        for (var i = 0; i < 16; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0 });
        return project;
    }

    private static Arrangement AddArrangement(ChartProject project, ArrangementType type = ArrangementType.Lead)
    {
        var arr = new Arrangement { ArrangementType = type, StringCount = 6 };
        for (var s = 0; s < 6; s++) arr.TuningPitches.Add(new TuningOffset(0));
        arr.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        project.Arrangements.Add(arr);
        return arr;
    }

    private static RocksmithBuildOptions Options() => new()
    {
        OutputPath = "unused_p.psarc",
        AudioPath = "unused.wem",
        DlcKey = "DotcTestKey"
    };

    [Fact]
    public void ArrangementLevelArtistAlbumAndYear_ReachTheManifest()
    {
        var project = BuildProject();
        var arr = AddArrangement(project);
        arr.ArtistName = "Joe Satriani";
        arr.ArtistNameSort = "Satriani, Joe";
        arr.AlbumName = "Surfing with the Alien";
        arr.AlbumYear = 1987;
        // Nothing at chart level — exactly the shape a psarc/RS-XML import lands in.

        var metadata = RsPackageMetadataFactory.Create(project, Options());

        Assert.Equal("Joe Satriani", metadata.Artist);
        Assert.Equal("Satriani, Joe", metadata.ArtistSort);
        Assert.Equal("Surfing with the Alien", metadata.Album);
        Assert.Equal(1987, metadata.Year);
    }

    [Fact]
    public void TheManifestStrings_AgreeWithTheRsXmlBuiltFromTheSameChart()
    {
        var project = BuildProject();
        var arr = AddArrangement(project);
        arr.ArtistName = "Joe Satriani";
        arr.AlbumName = "Surfing with the Alien";
        arr.AlbumYear = 1987;

        var metadata = RsPackageMetadataFactory.Create(project, Options());
        var song = new RsXmlSongWriter().Write(project).Root!;

        Assert.Equal(Text(song, "artistName"), metadata.Artist);
        Assert.Equal(Text(song, "albumName"), metadata.Album);
        Assert.Equal(Text(song, "albumYear"), metadata.Year!.Value.ToString());
    }

    [Fact]
    public void TheArrangementWins_OverAChartLevelValue_LikeTheXmlWriter()
    {
        var project = BuildProject();
        project.Chart.Artist = "Chart Artist";
        project.Chart.AlbumName = "Chart Album";
        project.Chart.AlbumYear = "2001";

        var arr = AddArrangement(project);
        arr.ArtistName = "Arrangement Artist";
        arr.AlbumName = "Arrangement Album";
        arr.AlbumYear = 1987;

        var metadata = RsPackageMetadataFactory.Create(project, Options());

        Assert.Equal("Arrangement Artist", metadata.Artist);
        Assert.Equal("Arrangement Album", metadata.Album);
        Assert.Equal(1987, metadata.Year);
    }

    [Fact]
    public void ABlankArrangementValue_FallsThroughToTheChart()
    {
        var project = BuildProject();
        project.Chart.Artist = "Chart Artist";
        project.Chart.AlbumName = "Chart Album";
        project.Chart.AlbumYear = "2001";
        AddArrangement(project); // every field left at its "" / null default

        var metadata = RsPackageMetadataFactory.Create(project, Options());

        Assert.Equal("Chart Artist", metadata.Artist);
        Assert.Equal("Chart Album", metadata.Album);
        Assert.Equal(2001, metadata.Year);
    }

    [Fact]
    public void AVocalTrackIsNotAnArrangement_AndTheFirstInstrumentalStillSpeaks()
    {
        var project = BuildProject();
        // A vocals ARRANGEMENT carries no album/artist of its own; it must not be
        // able to shadow the instrumental that does.
        var vocalArrangement = AddArrangement(project, ArrangementType.Vocal);
        vocalArrangement.ArtistName = "";
        var lead = AddArrangement(project);
        lead.ArtistName = "Joe Satriani";
        lead.AlbumName = "Surfing with the Alien";
        project.VocalTracks.Add(new VocalTrack { Name = "Vocals" });

        var metadata = RsPackageMetadataFactory.Create(project, Options());

        Assert.Equal("Joe Satriani", metadata.Artist);
        Assert.Equal("Surfing with the Alien", metadata.Album);
    }

    [Fact]
    public void WithNoMetadataAnywhere_TheOldDefaultsStand()
    {
        var project = BuildProject();
        AddArrangement(project);

        var metadata = RsPackageMetadataFactory.Create(project, Options());

        Assert.Equal("Unknown Artist", metadata.Artist);
        Assert.Equal("", metadata.Album);
        Assert.Null(metadata.Year);
        // No authored sort name: the article-stripping derivation still applies.
        Assert.Equal("Unknown Artist", metadata.ArtistSort);
    }

    private static string Text(XElement song, string name) => song.Element(name)!.Value;
}
