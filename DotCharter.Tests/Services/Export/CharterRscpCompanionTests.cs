using System.Xml.Linq;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Charter;
using DotCharter.Services.Parsing;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tests.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     The Charter-compatible <c>.rscp</c> every save writes beside the <c>.dcproj</c>: it holds
///     nothing outside Charter's model (<see cref="CharterProjectSchema" />, read from Charter
///     <see cref="CharterProjectSchema.CharterVersion" />), it reads back in DotCharter with the
///     chart intact, and it never overwrites a <c>.rscp</c> this app did not write.
/// </summary>
public sealed class CharterRscpCompanionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_charter_rscp_").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private string ProjectPath => Path.Combine(_dir, ProjectFiles.DefaultFileName);

    private static SaveCompanionService Create(FakeAppLogger? logger = null) =>
        new(new FakeConfigurationService(), logger ?? new FakeAppLogger());

    [Fact]
    public void TheCompanion_HoldsOnlyWhatCharterKnows()
    {
        var written = Create().WriteCharterProject(ProjectPath, RichProjectFactory.Build());

        Assert.Equal(CharterRscp.CompanionPath(ProjectPath), written);
        var doc = SafeXml.LoadFromFile(written!);
        Assert.Empty(CharterProjectSchema.Violations(doc));
        Assert.DoesNotContain(doc.Descendants(), e => e.Name.LocalName.StartsWith("dc", StringComparison.Ordinal));
        Assert.DoesNotContain(doc.Descendants().Attributes(), a => a.Name.LocalName.StartsWith("dc", StringComparison.Ordinal));
        Assert.Equal("3", doc.Root!.Element("chartFormatVersion")?.Value);
    }

    [Fact]
    public void TheFullProject_WouldViolateTheAllowList_SoTheCheckHasSomethingToCatch()
    {
        // The same rich project WITHOUT the projection: the dc* nodes and extension
        // attributes are there, and the allow-list names them.
        var doc = new XDocument();
        new RscpWriter().WriteDocument(doc, RichProjectFactory.Build());

        var violations = CharterProjectSchema.Violations(doc);
        Assert.Contains("project/dcWriterVersion", violations);
        Assert.Contains(violations, v => v.StartsWith("project/dcToneDefinitions", StringComparison.Ordinal));
    }

    [Fact]
    public void ADcNodeAnywhereInTheTree_IsAViolation()
    {
        var doc = new XDocument();
        new RscpWriter().WriteDocument(doc, RichProjectFactory.Build());
        CharterRscpProjection.Project(doc);
        Assert.Empty(CharterProjectSchema.Violations(doc));

        doc.Root!.Element("arrangements")!.Element("arrangement")!.Add(new XElement("dcExtra"));
        doc.Root!.Element("beats")!.Element("beat2")!.SetAttributeValue("dcFlag", "T");

        var violations = CharterProjectSchema.Violations(doc);
        Assert.Contains("project/arrangements/arrangement/dcExtra", violations);
        Assert.Contains("project/beats/beat2/@dcFlag", violations);
    }

    [Fact]
    public void TheCompanion_ReimportsWithTheChartIntact()
    {
        var project = RichProjectFactory.Build();
        new RscpChartSerializer().Save(ProjectPath, project);
        var written = Create().WriteCharterProject(ProjectPath, project);

        var full = new RscpChartSerializer().Load(ProjectPath);
        var companion = new RscpChartSerializer().Load(written!);

        Assert.Equal(Chart(full), Chart(companion));
    }

    [Fact]
    public void TheCompanion_LogsWhatItDropped_OncePerUnchangedLoss()
    {
        var logger = new FakeAppLogger();
        var service = Create(logger);
        var project = RichProjectFactory.Build();

        service.WriteCharterProject(ProjectPath, project);
        service.WriteCharterProject(ProjectPath, project);

        var lines = logger.Entries.Where(e => e.Message.Contains("Charter .rscp companion written", StringComparison.Ordinal)).ToList();
        Assert.Single(lines);
        Assert.Contains("dcToneDefinitions", lines[0].Message, StringComparison.Ordinal);
        Assert.Contains("dcAnnotations", lines[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACharterProjectAtTheCompanionPath_IsLeftAlone()
    {
        var companionPath = CharterRscp.CompanionPath(ProjectPath);
        const string charterXml = "<?xml version='1.0' encoding='UTF-8'?>\n<project><chartFormatVersion>3</chartFormatVersion></project>";
        File.WriteAllText(companionPath, charterXml);

        var written = Create().WriteCharterProject(ProjectPath, RichProjectFactory.Build());

        Assert.Null(written);
        Assert.Equal(charterXml, File.ReadAllText(companionPath));
    }

    [Fact]
    public void ACompanionWeWrote_IsRewrittenOnTheNextSave()
    {
        var service = Create();
        var project = RichProjectFactory.Build();
        service.WriteCharterProject(ProjectPath, project);

        project.Chart.SongName = "Renamed";
        var written = service.WriteCharterProject(ProjectPath, project);

        Assert.NotNull(written);
        Assert.Equal("Renamed", SafeXml.LoadFromFile(written!).Root!.Element("title")?.Value);
    }

    [Fact]
    public void AProjectPathThatIsItselfARscp_WritesNoCompanion()
    {
        var rscp = Path.Combine(_dir, "song.rscp");
        Assert.Null(Create().WriteCharterProject(rscp, RichProjectFactory.Build()));
        Assert.False(File.Exists(rscp));
    }

    /// <summary>The chart content a Charter round trip must keep: metadata, beat grid, and every arrangement's sounds.</summary>
    private static string Chart(ChartProject p)
    {
        var lines = new List<string>
        {
            $"meta {p.Chart.SongName}|{p.Chart.Artist}|{p.Chart.AlbumName}|{p.Chart.AlbumYear}|{p.AudioFileName}",
            "beats " + string.Join(";", p.Beats.Select(b => $"{b.Time:R}/{b.BeatsInMeasure}/{b.NoteDenominator}/{b.IsMeasure}")),
            "vocals " + string.Join(";", p.VocalTracks.SelectMany(t => t.Phrases).SelectMany(ph => ph.Lyrics).Select(l => l.Text))
        };

        foreach (var arrangement in p.Arrangements)
        {
            lines.Add($"arr strings={arrangement.TuningPitches.Count} templates={arrangement.ChordTemplates.Count} events={arrangement.EventPoints.Count}");
            foreach (var level in arrangement.Levels)
            {
                lines.Add("notes " + string.Join(";", level.Notes.Select(n => $"{n.Position}:{n.StringIndex}/{n.Fret}")));
                lines.Add("chords " + string.Join(";", level.Chords.Select(c => $"{c.Position}:{c.TemplateId}:{c.ChordNotes.Count}")));
                lines.Add("shapes " + string.Join(";", level.HandShapes.Select(h => $"{h.Position}-{h.EndPosition}:{h.TemplateId}")));
                lines.Add("anchors " + string.Join(";", level.Anchors.Select(a => $"{a.Position}:{a.Fret}")));
            }
        }

        return string.Join("\n", lines);
    }
}
