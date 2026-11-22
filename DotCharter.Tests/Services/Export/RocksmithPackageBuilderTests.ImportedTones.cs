using DotCharter.Tests.Services.Parsing.GuitarPro;
using DotCharter.Services.Editing;
using DotCharter.Services.Export;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     An arrangement that no importer (and no New project) gave a tone used to make the
///     package builder invent <c>lead_base</c> for the manifest and then warn that its own
///     invention had no definition. Nothing was declared, so nothing is undefined; the
///     validator's missing-base-tone finding already covers "no tone at all".
/// </summary>
public sealed partial class RocksmithPackageBuilderTests
{
    public static TheoryData<string> ToneFreeSources => ["gpif", "gp5", "midi-draft", "new-project"];

    [Theory]
    [MemberData(nameof(ToneFreeSources))]
    public async Task An_arrangement_no_importer_gave_a_tone_exports_without_an_undefined_tone_warning(string source)
    {
        var project = CreateProject();
        var arrangement = ToneFreeArrangement(source);
        Assert.True(string.IsNullOrWhiteSpace(arrangement.ToneBase));
        project.Arrangements.Clear();
        project.Arrangements.Add(arrangement);

        var result = await new RocksmithPackageBuilder(new FakeAppLogger()).BuildPsarcAsync(project, new RocksmithBuildOptions
        {
            OutputPath = Path.Combine(_tempDir, $"tone_free_{source}_p.psarc"),
            AudioPath = CreateFakeWem(),
            SongLengthMs = 32_000,
            IncludeVocals = false
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(result.Warnings, w => w.Contains("declares tone", StringComparison.Ordinal));
    }

    private static Arrangement ToneFreeArrangement(string source)
    {
        switch (source)
        {
            case "gpif":
            case "gp5":
                var path = source == "gp5"
                    ? LegacyFile()
                    : GpifFile();
                try
                {
                    var importer = new GpImporter();
                    var ids = importer.GetAvailableTracks(path).Select(t => t.Id).Take(1).ToList();
                    return importer.ImportTracksWithMetadata(path, ids).Arrangements[0];
                }
                finally
                {
                    File.Delete(path);
                }
            case "midi-draft":
                return DraftArrangementFactory.FromTemplate(null, "Lead");
            default:
                return new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
        }
    }

    private static string LegacyFile() => LegacyGpFixtureBuilder.WriteFile(
        "5.10", [new FxMeasure().AddVoice(FxBeat.Note(0, 0, 5))], trackName: "Lead (6 String)");

    private static string GpifFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-tonefree-{Guid.NewGuid():N}.gp");
        using var fs = File.Create(path);
        using var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create);
        var entry = zip.CreateEntry("Content/score.gpif");
        using var writer = new StreamWriter(entry.Open(), System.Text.Encoding.UTF8);
        writer.Write("""
            <GPIF>
              <MasterTrack><Tracks>0</Tracks></MasterTrack>
              <Tracks><Track id="0"><Name>Lead (6 String)</Name>
                <Staves><Staff><Properties><Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property></Properties></Staff></Staves>
              </Track></Tracks>
              <MasterBars><MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar></MasterBars>
              <Bars><Bar id="0"><Voices>-1 -1 -1 -1</Voices></Bar></Bars>
            </GPIF>
            """);
        return path;
    }
}
