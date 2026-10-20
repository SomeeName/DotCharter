using System.Xml.Linq;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Parsing.Xml.Song;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.Rscp;

/// <summary>
///     Loading a pre-v3 project must recover it, not quietly empty it.
/// </summary>
/// <remarks>
///     In v2 the arrangements were NOT embedded in the .rscp — it named sibling Rocksmith
///     XML files. Parsed with the v3 readers alone, such a file yields a chart with no
///     arrangements at all; the user opens their old project, sees nothing, and the moment
///     they hit save the empty project overwrites the original. These tests pin the
///     recovery, and pin that detection is by STRUCTURE — the declared version number
///     cannot be trusted (RsCharter's own default is 3, so a file older than the version
///     element still claims to be 3).
/// </remarks>
public sealed class RscpMigrationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_rscp_migrate_").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void A_v2_project_recovers_its_arrangements_from_the_sibling_xml()
    {
        var rscp = WriteLegacyProject(declaredVersion: 2, arrangementFile: "1_Lead_x_RS2.xml");

        var project = new RscpChartSerializer().Load(rscp);

        // Exactly one — the recovered arrangement, NOT the recovered one plus the
        // parser's fabricated placeholder.
        Assert.Single(project.Arrangements);
        Assert.Equal(6, project.Arrangements[0].TuningPitches.Count);

        // And it is stamped current, so the next save writes the modern schema.
        Assert.Equal(RscpMigrations.CurrentVersion, project.Chart.ChartFormatVersion);
    }

    [Fact]
    public void The_fabricated_placeholder_does_not_survive_a_recovery()
    {
        // This is what made the bug deceptive: with no embedded arrangements, the parser
        // invents an empty "Lead Guitar", so a v2 project opened looking like a plausible
        // FRESH chart rather than a broken one — and the user saved over the original.
        var rscp = WriteLegacyProject(declaredVersion: 2, arrangementFile: "1_Lead_x_RS2.xml");

        var project = new RscpChartSerializer().Load(rscp);

        Assert.DoesNotContain(project.Arrangements,
            a => a.Name == "Lead Guitar" && a.Levels.Count == 1 && a.Levels[0].Notes.Count == 0);
    }

    [Fact]
    public void A_file_that_LIES_about_its_version_is_still_recovered()
    {
        // RsCharter's field default is 3, so a project older than the version element
        // deserializes as "3". Trusting the number would mean silently emptying it.
        var rscp = WriteLegacyProject(declaredVersion: 3, arrangementFile: "1_Lead_x_RS2.xml");

        var project = new RscpChartSerializer().Load(rscp);

        Assert.NotEmpty(project.Arrangements);
    }

    [Fact]
    public void A_file_with_no_version_element_at_all_is_still_recovered()
    {
        var rscp = WriteLegacyProject(declaredVersion: null, arrangementFile: "1_Lead_x_RS2.xml");

        var project = new RscpChartSerializer().Load(rscp);

        Assert.NotEmpty(project.Arrangements);
    }

    [Fact]
    public void A_missing_sibling_costs_only_that_arrangement_not_the_project()
    {
        // Half a recovered project the user can see beats an exception or an empty one.
        // With nothing recovered we deliberately LEAVE the parser's placeholder alone —
        // an editor with no arrangement at all cannot be edited.
        var rscp = WriteLegacyProject(declaredVersion: 2, arrangementFile: "gone.xml", writeTheXml: false);

        var project = new RscpChartSerializer().Load(rscp);

        Assert.NotEmpty(project.Beats); // the beat map still loaded
        Assert.Equal(RscpMigrations.CurrentVersion, project.Chart.ChartFormatVersion);
    }

    [Fact]
    public void A_modern_v3_project_is_left_completely_alone()
    {
        // The migration must not touch the 51-out-of-51 real projects that are already v3.
        var project = BuildProject();
        var path = Path.Combine(_dir, "modern.rscp");
        new RscpChartSerializer().Save(path, project);

        var doc = XDocument.Load(path);
        Assert.False(RscpMigrations.NeedsArrangementRecovery(doc));

        var reloaded = new RscpChartSerializer().Load(path);
        Assert.Single(reloaded.Arrangements);
        Assert.Equal(RscpMigrations.CurrentVersion, reloaded.Chart.ChartFormatVersion);
    }

    [Fact]
    public void A_project_naming_no_files_is_not_mistaken_for_a_legacy_one()
    {
        var doc = XDocument.Parse("""
            <project>
              <arrangementFiles/>
              <arrangements/>
            </project>
            """);

        Assert.False(RscpMigrations.NeedsArrangementRecovery(doc));
    }

    // ---- helpers ------------------------------------------------------------

    /// <summary>
    ///     Writes a v2-SHAPED project: header + beats embedded, arrangements only NAMED,
    ///     with the real arrangement in a sibling Rocksmith XML — exactly what RsCharter
    ///     wrote before v3.
    /// </summary>
    private string WriteLegacyProject(int? declaredVersion, string arrangementFile, bool writeTheXml = true)
    {
        var project = BuildProject();
        var modern = Path.Combine(_dir, "project.rscp");
        new RscpChartSerializer().Save(modern, project);

        if (writeTheXml)
            new RsXmlSongWriter().WriteTo(project, Path.Combine(_dir, arrangementFile));

        // Strip the arrangements back OUT and name the file instead — the v2 layout.
        var doc = XDocument.Load(modern);
        doc.Descendants("arrangements").Remove();

        var version = doc.Descendants("chartFormatVersion").FirstOrDefault();
        if (declaredVersion is { } v) version?.SetValue(v);
        else version?.Remove();

        var files = doc.Descendants("arrangementFiles").FirstOrDefault();
        if (files is null)
        {
            files = new XElement("arrangementFiles");
            doc.Root!.Add(files);
        }

        files.RemoveNodes();
        files.Add(new XElement("string", arrangementFile));

        doc.Save(modern);
        return modern;
    }

    private static ChartProject BuildProject()
    {
        var project = new ChartProject
        {
            Bpm = 120,
            Chart = { SongName = "Old Song", Artist = "Legacy" }
        };

        for (var i = 0; i < 16; i++)
        {
            project.Beats.Add(new BeatMarker
            {
                Time = i * 500.0,
                IsMeasure = i % 4 == 0,
                Bpm = 120,
                BeatsInMeasure = 4,
                NoteDenominator = 4
            });
        }

        var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        project.Arrangements.Add(arrangement);

        return project;
    }
}
