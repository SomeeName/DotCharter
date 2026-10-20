using System.IO.Compression;
using System.Text;
using DotCharter.Services.Parsing;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     The arrangement TYPE every tab importer stamps used to be
///     <c>isBass ? Bass : Lead</c>, so a GP file's "Rhythm (…)" tracks all
///     imported as Lead and a psarc built from them shipped the wrong Rocksmith
///     route. All three paths (gpif, GP3-5 binary, Songsterr) now derive the
///     type from the track NAME through the one shared
///     <see cref="ArrangementTypeHeuristics"/>, with each format's content
///     signal (string count / instrument) demoted to the keyword-less fallback.
/// </summary>
public sealed class GpArrangementTypeTests
{
    // ---- the shared heuristic --------------------------------------------

    [Theory]
    [InlineData("Lead (6  String Drop D)", ArrangementType.Lead)]
    [InlineData("Rhythm (7 String)", ArrangementType.Rhythm)]
    [InlineData("rhythm guitar", ArrangementType.Rhythm)]
    [InlineData("Electric Bass", ArrangementType.Bass)]
    [InlineData("Combo", ArrangementType.Combo)]
    [InlineData("Vocals", ArrangementType.Vocal)]
    [InlineData("Lead Vocals", ArrangementType.Vocal)]
    [InlineData("PART REAL_BASS", ArrangementType.Bass)]
    public void FromTrackName_ReadsTheKeyword(string name, ArrangementType expected) => Assert.Equal(expected, ArrangementTypeHeuristics.FromTrackName(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Acoustic Guitar")]
    public void FromTrackName_KeywordlessNamesTakeTheFallback(string? name)
    {
        Assert.Equal(ArrangementType.Lead, ArrangementTypeHeuristics.FromTrackName(name));
        Assert.Equal(ArrangementType.Bass,
            ArrangementTypeHeuristics.FromTrackName(name, ArrangementType.Bass));
    }

    // ---- gpif (GP6+ container) -------------------------------------------

    /// <summary>Three tracks: a name-typed Rhythm, a name-typed Bass, and a
    /// keyword-less 4-string track that must fall back to Bass via its string
    /// count (the same fallback the GP3-5 binary path applies).</summary>
    private const string ThreeTrackGpif = """
        <GPIF>
          <MasterTrack><Tracks>0 1 2</Tracks></MasterTrack>
          <Tracks>
            <Track id="0">
              <Name>Rhythm (6 String)</Name>
              <Staves><Staff><Properties>
                <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
              </Properties></Staff></Staves>
            </Track>
            <Track id="1">
              <Name>Electric Bass</Name>
              <Staves><Staff><Properties>
                <Property name="Tuning"><Pitches>28 33 38 43</Pitches></Property>
              </Properties></Staff></Staves>
            </Track>
            <Track id="2">
              <Name>Shamisen</Name>
              <Staves><Staff><Properties>
                <Property name="Tuning"><Pitches>28 33 38 43</Pitches></Property>
              </Properties></Staff></Staves>
            </Track>
          </Tracks>
          <MasterBars>
            <MasterBar><Time>4/4</Time><Bars>0 1 2</Bars></MasterBar>
          </MasterBars>
          <Bars>
            <Bar id="0"><Voices>-1 -1 -1 -1</Voices></Bar>
            <Bar id="1"><Voices>-1 -1 -1 -1</Voices></Bar>
            <Bar id="2"><Voices>-1 -1 -1 -1</Voices></Bar>
          </Bars>
        </GPIF>
        """;

    private static string WriteGpFile(string gpif)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gptype-{Guid.NewGuid():N}.gp");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("Content/score.gpif");
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(gpif);
        return path;
    }

    [Fact]
    public void Gpif_DerivesTypeFromTrackName_AndStringCountFallback()
    {
        var path = WriteGpFile(ThreeTrackGpif);
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            var result = importer.ImportTracksWithMetadata(path, tracks.Select(t => t.Id).ToList());

            Assert.Equal(3, result.Arrangements.Count);
            Assert.Equal(ArrangementType.Rhythm, result.Arrangements[0].ArrangementType);
            Assert.Equal(ArrangementType.Bass, result.Arrangements[1].ArrangementType);
            // No keyword in "Shamisen" — the 4-string tuning decides.
            Assert.Equal(ArrangementType.Bass, result.Arrangements[2].ArrangementType);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ---- GP3-5 binary ----------------------------------------------------

    [Fact]
    public void LegacyBinary_DerivesTypeFromTrackName()
    {
        var measures = new[] { new FxMeasure().AddVoice(FxBeat.Note(0, 0, 5)) };
        var path = LegacyGpFixtureBuilder.WriteMultiTrackFile("5.10", new (string, IReadOnlyList<FxMeasure>)[]
        {
            ("Lead (6 String)", measures),
            ("Rhythm (6 String)", new[] { new FxMeasure().AddVoice(FxBeat.Note(0, 0, 5)) })
        });
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            var result = importer.ImportTracksWithMetadata(path, tracks.Select(t => t.Id).ToList());

            Assert.Equal(2, result.Arrangements.Count);
            Assert.Equal(ArrangementType.Lead, result.Arrangements[0].ArrangementType);
            Assert.Equal(ArrangementType.Rhythm, result.Arrangements[1].ArrangementType);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>A 6-string track NAMED "Bass" is a bass — the old
    /// <c>isBass &amp;&amp; StringCount &lt;= 5</c> demoted it to Lead.</summary>
    [Fact]
    public void LegacyBinary_ASixStringTrackNamedBass_IsStillABass()
    {
        var measures = new[] { new FxMeasure().AddVoice(FxBeat.Note(0, 0, 5)) };
        var path = LegacyGpFixtureBuilder.WriteFile("5.10", measures, trackName: "Bass (6 String)");
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            var result = importer.ImportTracksWithMetadata(path, tracks.Select(t => t.Id).ToList());

            var arr = Assert.Single(result.Arrangements);
            Assert.Equal(ArrangementType.Bass, arr.ArrangementType);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
