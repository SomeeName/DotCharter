using System.IO.Compression;
using System.Text;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     Track names are normalized at every read: real GP files carry doubled
///     spaces IN their bytes (the Ave Mujica reference GP5's second track is
///     literally "Lead (6  String Drop D)" on the wire), and that name flowed
///     verbatim onto <c>Arrangement.Name</c>, the import dialog's track list
///     and the export dialog. <see cref="GpTrackNames.Normalize"/> collapses
///     interior whitespace runs and trims, on the gpif, GP3-5 binary and
///     Songsterr paths alike.
/// </summary>
public sealed class GpTrackNameTests
{
    [Theory]
    [InlineData("Lead (6  String Drop D)", "Lead (6 String Drop D)")]
    [InlineData("  Rhythm   Guitar  ", "Rhythm Guitar")]
    [InlineData("Lead\t(7 String)", "Lead (7 String)")]
    [InlineData("Plain Name", "Plain Name")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void Normalize_CollapsesInteriorWhitespaceAndTrims(string? raw, string expected) => Assert.Equal(expected, GpTrackNames.Normalize(raw));

    [Fact]
    public void Gpif_TrackNamesAreNormalized_InTrackListAndArrangement()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gpname-{Guid.NewGuid():N}.gp");
        using (var fs = File.Create(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("Content/score.gpif");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write("""
                <GPIF>
                  <MasterTrack><Tracks>0</Tracks></MasterTrack>
                  <Tracks>
                    <Track id="0">
                      <Name>Lead (6  String Drop D)</Name>
                      <Staves><Staff><Properties>
                        <Property name="Tuning"><Pitches>38 45 50 55 59 64</Pitches></Property>
                      </Properties></Staff></Staves>
                    </Track>
                  </Tracks>
                  <MasterBars><MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar></MasterBars>
                  <Bars><Bar id="0"><Voices>-1 -1 -1 -1</Voices></Bar></Bars>
                </GPIF>
                """);
        }

        try
        {
            var importer = new GpImporter();
            var track = Assert.Single(importer.GetAvailableTracks(path));
            Assert.Equal("Lead (6 String Drop D)", track.Name);

            var result = importer.ImportTracksWithMetadata(path, [track.Id]);
            var arr = Assert.Single(result.Arrangements);
            Assert.Equal("Lead (6 String Drop D)", arr.Name);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LegacyBinary_TrackNamesAreNormalized_InTrackListAndArrangement()
    {
        var measures = new[] { new FxMeasure().AddVoice(FxBeat.Note(0, 0, 5)) };
        var path = LegacyGpFixtureBuilder.WriteFile("5.10", measures,
            trackName: "Lead (6  String Drop D)");
        try
        {
            var importer = new GpImporter();
            var track = Assert.Single(importer.GetAvailableTracks(path));
            Assert.Equal("Lead (6 String Drop D)", track.Name);

            var result = importer.ImportTracksWithMetadata(path, [track.Id]);
            var arr = Assert.Single(result.Arrangements);
            Assert.Equal("Lead (6 String Drop D)", arr.Name);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
