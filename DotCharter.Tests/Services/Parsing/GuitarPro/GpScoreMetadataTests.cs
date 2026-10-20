using System.IO.Compression;
using System.Text;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     GP files carry score-level song metadata (title/artist/album, plus the
///     "tabbed by" credit) and both importers used to read those strings off
///     the wire and DISCARD them — so every GP import left "New Song" / an
///     empty artist in the project's Song Metadata. They now travel on
///     <see cref="GpImportMetadata"/>; the import dialog copies non-empty
///     values onto the project (pinned in <c>GpImportViewModelTests</c>).
/// </summary>
public sealed class GpScoreMetadataTests
{
    private static string WriteGpFile(string gpif)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gpmeta-{Guid.NewGuid():N}.gp");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("Content/score.gpif");
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(gpif);
        return path;
    }

    [Fact]
    public void Gpif_ScoreMetadata_TravelsOnTheImportResult()
    {
        // CDATA + stray whitespace, exactly how Guitar Pro itself writes them.
        var path = WriteGpFile("""
            <GPIF>
              <Score>
                <Title><![CDATA[ Crucifix X ]]></Title>
                <Artist><![CDATA[Ave Mujica]]></Artist>
                <Album><![CDATA[Completeness]]></Album>
                <Tabber><![CDATA[A Charter]]></Tabber>
              </Score>
              <MasterTrack><Tracks>0</Tracks></MasterTrack>
              <Tracks>
                <Track id="0">
                  <Name>Lead</Name>
                  <Staves><Staff><Properties>
                    <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
                  </Properties></Staff></Staves>
                </Track>
              </Tracks>
              <MasterBars><MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar></MasterBars>
              <Bars><Bar id="0"><Voices>-1 -1 -1 -1</Voices></Bar></Bars>
            </GPIF>
            """);
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            var result = importer.ImportTracksWithMetadata(path, tracks.Select(t => t.Id).ToList());

            Assert.Equal("Crucifix X", result.Metadata.Title);
            Assert.Equal("Ave Mujica", result.Metadata.Artist);
            Assert.Equal("Completeness", result.Metadata.Album);
            Assert.Equal("A Charter", result.Metadata.Tabber);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Gpif_AFileWithoutAScoreElement_LeavesTheMetadataEmpty()
    {
        var path = WriteGpFile("""
            <GPIF>
              <MasterTrack><Tracks>0</Tracks></MasterTrack>
              <Tracks>
                <Track id="0">
                  <Name>Lead</Name>
                  <Staves><Staff><Properties>
                    <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
                  </Properties></Staff></Staves>
                </Track>
              </Tracks>
              <MasterBars><MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar></MasterBars>
              <Bars><Bar id="0"><Voices>-1 -1 -1 -1</Voices></Bar></Bars>
            </GPIF>
            """);
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            var result = importer.ImportTracksWithMetadata(path, tracks.Select(t => t.Id).ToList());

            Assert.Equal("", result.Metadata.Title);
            Assert.Equal("", result.Metadata.Artist);
            Assert.Equal("", result.Metadata.Album);
            Assert.Equal("", result.Metadata.Tabber);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData("3.00")]
    [InlineData("4.06")]
    [InlineData("5.10")]
    public void LegacyBinary_HeaderMetadata_TravelsOnTheImportResult(string version)
    {
        var measures = new[] { new FxMeasure().AddVoice(FxBeat.Note(0, 0, 5)) };
        var path = LegacyGpFixtureBuilder.WriteFile(version, measures,
            title: "Amy", artist: "Yorushika", album: "Elma", tab: "A Charter");
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            var result = importer.ImportTracksWithMetadata(path, tracks.Select(t => t.Id).ToList());

            Assert.Equal("Amy", result.Metadata.Title);
            Assert.Equal("Yorushika", result.Metadata.Artist);
            Assert.Equal("Elma", result.Metadata.Album);
            Assert.Equal("A Charter", result.Metadata.Tabber);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
