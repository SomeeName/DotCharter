using System.Text;
using System.Text.Json;
using DotCharter.Services.Export.Package;
using DotCharter.Services.Export.Sng.Model;
using DotCharter.Tests.Services.Export.Rsxr;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     A 5-/6-string bass ships on the Bass path, Main and representative, in every package that
///     carries it. It is always an RS Extended Range arrangement, and that format ties the chart's
///     instrument to the manifest's path: the reader refuses a bass chart on the Rhythm path. The
///     classic Bonus Rhythm convention is an arrangement the charter AUTHORS (type Rhythm, subtype
///     Bonus, a guitar chart in bass tuning), never an export-time relabel of a Bass arrangement.
/// </summary>
public sealed class ExtendedBassRouteTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("dotcharter-xbass-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_directory);

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void A_bass_of_any_string_count_stays_on_the_bass_path(int strings)
    {
        var bass = new Arrangement { ArrangementType = ArrangementType.Bass, StringCount = strings };
        var package = new RsPackageArrangement
        {
            ArrangementName = "Bass", PartitionName = "bass", PersistentId = Guid.Empty, Sng = new RocksmithSng(),
            Domain = bass
        };

        Assert.Equal(ArrangementPropertiesDeriver.RouteBass, package.RouteMaskValue);
        Assert.Equal(1, package.Properties!.PathBass);
        Assert.Equal(0, package.Properties.PathRhythm);
        Assert.Equal(0, package.Properties.BonusArr);
        Assert.True(package.IsRepresentative);
    }

    /// <summary>Exports each extended target and reads every arrangement back the way the add-on's
    /// reader does: the manifest's path must agree with the SNXR header's instrument byte.</summary>
    [Theory]
    [InlineData(true, 5)]
    [InlineData(true, 6)]
    [InlineData(false, 7)]
    [InlineData(false, 8)]
    public async Task Every_manifest_path_agrees_with_its_chart_instrument(bool bass, int strings)
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var built = RsxrTestSupport.Chart(bass, strings);
        var audio = Path.Combine(_directory, "audio.wem");
        File.WriteAllBytes(audio, new byte[4000]);
        var result = await RsxrTestSupport.Build(built.Project, built.SongLengthMs, audio, Path.Combine(_directory, "x_p.psarc"));
        var entries = RsxrTestSupport.Entries(result.PsarcPath);

        var checkedCharts = 0;
        foreach (var (name, bytes) in entries.Where(e => e.Key.EndsWith(".json", StringComparison.Ordinal)))
        {
            var partition = Path.GetFileNameWithoutExtension(name).Split('_')[^1];
            var sng = Assert.Single(entries, e => e.Key.EndsWith($"_{partition}.sng", StringComparison.Ordinal));
            var body = RsxrTestSupport.Inflate(sng.Value);
            Assert.Equal("SNXR", Encoding.ASCII.GetString(body, 0, 4));
            var chartIsBass = body[5] == RsExtensionBlock.InstrumentBass;
            Assert.Equal(bass, chartIsBass);

            using var document = JsonDocument.Parse(bytes);
            foreach (var entry in document.RootElement.GetProperty("Entries").EnumerateObject())
            {
                var attr = entry.Value.GetProperty("Attributes");
                var path = attr.GetProperty("ArrangementProperties");
                Assert.Equal(chartIsBass, path.GetProperty("pathBass").GetInt32() == 1);
                Assert.Equal(chartIsBass, attr.GetProperty("ArrangementName").GetString() == "Bass");
                if (chartIsBass) Assert.Equal(0, path.GetProperty("pathRhythm").GetInt32());
                checkedCharts++;
            }
        }

        Assert.True(checkedCharts > 0);
    }
}
