using System.Text.Json;
using System.Xml.Linq;
using DotCharter.Services.Export;
using DotCharter.Services.Export.DlcBuilder;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Parsing;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Parsing.Xml;
using DotCharter.Tone.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     #1036: the DLC Builder project export applies the low bass tuning fix the <c>.psarc</c> export does, to the
///     project's bass entry and to its XML, and a chart with RS Extended Range paths gets a standard project DLC
///     Builder builds and an <c>_rsxr</c> one, mirroring the <c>_p</c> / <c>_rsxr_p</c> packages. The standard one
///     is checked against DLC Builder's own rules: I41 (<c>TuningPitch &gt; 230</c> and a string below -4 on a bass)
///     and I36 (a bass note on a string above 3).
/// </summary>
public sealed class Rs2DlcLowBassAndExtendedRangeTests : IDisposable
{
    private const int BassName = 3;
    private const int AlternativePriority = 1;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", "rs2dlc-1036-" + Guid.NewGuid().ToString("N"));
    private readonly FakeAppLogger _logger = new();

    public Rs2DlcLowBassAndExtendedRangeTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Theory]
    [InlineData(true, -5, new short[] { 7, 7, 7, 7, 0, 0 }, 220.0)]
    [InlineData(false, -5, new short[] { -5, -5, -5, -5, 0, 0 }, 440.0)]
    [InlineData(true, 0, new short[] { 0, 0, 0, 0, 0, 0 }, 440.0)]
    public void The_bass_entry_takes_the_fix_only_when_it_is_on_and_needed(bool fix, int offset, short[] tuning, double pitch)
    {
        var project = Project(Bass4(offset));

        var entry = (Rs2DlcInstrumentalDto)Rs2DlcProjectBuilder.Build(project,
            new("song.ogg", "", null, "Charter", FixLowBassTuning: fix)).Arrangements[0].Fields[0];

        Assert.Equal(tuning, entry.Tuning);
        Assert.Equal(pitch, entry.TuningPitch);
        Assert.Equal(offset, project.Arrangements[0].TuningPitches[0].Step);
        Assert.Equal(0, project.Arrangements[0].CentOffset);
    }

    [Fact]
    public void A_chart_with_extended_paths_writes_a_standard_project_dlc_builder_builds_and_an_rsxr_one()
    {
        var project = Project(Lead(7), Bass4(-5), Bass5());

        var paths = Export(project, fix: true);

        Assert.NotNull(paths.ExtendedRangeProject);
        Assert.EndsWith("_rsxr.rs2dlc", paths.ExtendedRangeProject);
        Assert.Equal(Path.GetFileNameWithoutExtension(paths.Project) + "_rsxr.rs2dlc", Path.GetFileName(paths.ExtendedRangeProject));

        var standard = Entries(paths.Project);
        var extended = Entries(paths.ExtendedRangeProject!);
        Assert.Equal(["1_Lead_Main_6String_RS2.xml", "2_Bass_Main_RS2.xml"], standard.Select(e => e.File));
        Assert.Equal(["1_Lead_Main_RS2.xml", "2_Bass_Main_RS2.xml", "3_Bass_Main_RS2.xml"], extended.Select(e => e.File));
        Assert.All(standard.Concat(extended), e => Assert.True(File.Exists(e.XmlPath), e.XmlPath));
        // Every bundle file is one some project reads: no duplicate of the shared bass XML.
        Assert.Equal(4, Directory.GetFiles(Path.Combine(_dir, DlcBuilderBundle.DirectoryName), "*_RS2.xml").Length);

        // DLC Builder's own rules over the standard project.
        Assert.DoesNotContain(standard, e => e.IsBass && e.TuningPitch > 230 && e.Tuning.Any(t => t < -4));
        Assert.All(standard.Where(e => e.IsBass), e => Assert.InRange(HighestString(e.XmlPath), 0, 3));
        Assert.All(standard.Where(e => !e.IsBass), e => Assert.InRange(HighestString(e.XmlPath), 0, 5));
        Assert.Contains(extended, e => e.IsBass && HighestString(e.XmlPath) > 3);

        var bass = standard.Single(e => e.IsBass);
        Assert.Equal(new short[] { 7, 7, 7, 7, 0, 0 }, bass.Tuning);
        Assert.Equal(220.0, bass.TuningPitch);
        var xml = XDocument.Load(bass.XmlPath).Root!;
        Assert.Equal(Enumerable.Repeat(7, 4), Enumerable.Range(0, 4).Select(i => (int)xml.Element("tuning")!.Attribute($"string{i}")!));
        Assert.Equal(-1200, (int)xml.Element("centOffset")!);

        // The chart itself is never touched.
        Assert.All(project.Arrangements[1].TuningPitches, t => Assert.Equal(-5, t.Step));
        Assert.Equal(0, project.Arrangements[1].CentOffset);
    }

    [Fact]
    public void With_the_fix_off_the_bass_entry_and_xml_stay_as_charted()
    {
        var paths = Export(Project(Lead(7), Bass4(-5), Bass5()), fix: false);

        var bass = Entries(paths.Project).Single(e => e.IsBass);
        Assert.Equal(new short[] { -5, -5, -5, -5, 0, 0 }, bass.Tuning);
        Assert.Equal(440.0, bass.TuningPitch);
        Assert.True(bass.TuningPitch > 230 && bass.Tuning.Any(t => t < -4), "DLC Builder's I41 holds without the fix");
        var xml = XDocument.Load(bass.XmlPath).Root!;
        Assert.Equal(-5, (int)xml.Element("tuning")!.Attribute("string0")!);
        Assert.Equal(0, (int)xml.Element("centOffset")!);
    }

    [Fact]
    public void A_chart_without_extended_paths_keeps_its_single_project()
    {
        var paths = Export(Project(Lead(6), Bass4(-5)), fix: true);

        Assert.Null(paths.ExtendedRangeProject);
        Assert.Single(Directory.GetFiles(_dir, "*.rs2dlc"));
        Assert.Equal(["1_Lead_Main_RS2.xml", "2_Bass_Main_RS2.xml"], Entries(paths.Project).Select(e => e.File));
        Assert.Equal(220.0, Entries(paths.Project).Single(e => e.IsBass).TuningPitch);
    }

    [Fact]
    public void A_seven_string_guitar_with_a_stock_twin_stays_out_of_the_standard_project_and_the_twin_is_alternate_in_the_rsxr_one()
    {
        var paths = Export(Project(Lead(7), Lead(6)), fix: true);

        var standard = Entries(paths.Project);
        var extended = Entries(paths.ExtendedRangeProject!);
        Assert.Equal(["2_Lead_Main_RS2.xml"], standard.Select(e => e.File));
        Assert.Equal(0, standard[0].Priority);
        Assert.Equal(["1_Lead_Main_RS2.xml", "2_Lead_Main_RS2.xml"], extended.Select(e => e.File));
        Assert.Equal(AlternativePriority, extended[1].Priority);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, DlcBuilderBundle.DirectoryName), "*_6String_*"));
    }

    [Fact]
    public void A_seven_string_guitar_beside_an_unrelated_stock_guitar_is_projected_into_the_standard_project()
    {
        var paths = Export(Project(Lead(7, "Doloris"), Lead(6, "Mortis")), fix: true);

        var standard = Entries(paths.Project);
        Assert.Equal(2, standard.Count);
        Assert.Contains(standard, e => e.File == "2_Lead_Main_RS2.xml");
        var projected = Assert.Single(standard, e => e.File.Contains("_6String", StringComparison.Ordinal));
        Assert.True(HighestString(projected.XmlPath) <= 5);
    }

    [Fact]
    public void A_seven_string_guitar_with_a_named_stock_twin_carries_the_twin_and_no_projection()
    {
        var paths = Export(Project(Lead(7, "Doloris"), Lead(6, " doloris (6 String) ")), fix: true);

        Assert.Equal(["2_Lead_Main_RS2.xml"], Entries(paths.Project).Select(e => e.File));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, DlcBuilderBundle.DirectoryName), "*_6String_*"));
    }

    [Fact]
    public void An_alternate_stock_twin_is_main_in_the_standard_project_only()
    {
        var twin = Lead(6, "Lead (6 String)");
        twin.Subtype = ArrangementSubtype.Alternate;
        var project = Project(Lead(7, "Lead"), twin);

        var paths = Export(project, fix: true);

        var standard = Assert.Single(Entries(paths.Project));
        Assert.Equal("2_Lead_Alternate_RS2.xml", standard.File);
        Assert.Equal(0, standard.Priority);
        Assert.Equal([0, AlternativePriority], Entries(paths.ExtendedRangeProject!).Select(e => e.Priority));
        Assert.Equal(ArrangementSubtype.Alternate, project.Arrangements[1].Subtype);
    }

    [Fact]
    public void A_stock_guitar_is_the_twin_of_one_extended_guitar_only()
    {
        var project = Project(Lead(7, "Lead"), Lead(7, "Lead 2"), Lead(6, "Lead 2 (6 String)"));

        Assert.False(Rs2DlcEditions.HasStockTwin(project, project.Arrangements[0]));
        Assert.True(Rs2DlcEditions.HasStockTwin(project, project.Arrangements[1]));
    }

    [Fact]
    public void A_save_without_extended_paths_removes_our_stale_rsxr_project_only()
    {
        var config = Config();
        config.SaveCompanionRs2Dlc = true;
        var projectPath = Path.Combine(_dir, "song.dcproj");
        var service = new SaveCompanionService(new FakeConfigurationService { AppConfig = config }, _logger, recycle: File.Delete);
        var extendedProject = Project(Lead(7), Bass4(0));
        Documents(config).SaveDlcBuilderXmlBundle(projectPath, extendedProject);
        var standard = service.WriteDlcBuilderProject(projectPath, extendedProject, null)!;
        var ours = Rs2DlcEditions.PathFor(standard, Rs2DlcEdition.ExtendedRange);
        Assert.True(File.Exists(ours));

        var stockProject = Project(Lead(6), Bass4(0));
        Documents(config).SaveDlcBuilderXmlBundle(projectPath, stockProject);
        Assert.Equal(standard, service.WriteDlcBuilderProject(projectPath, stockProject, null));
        Assert.False(File.Exists(ours));

        File.WriteAllText(ours, "{\"theirs\":true}");
        service.WriteDlcBuilderProject(projectPath, stockProject, null);
        Assert.Equal("{\"theirs\":true}", File.ReadAllText(ours));
    }

    [Fact]
    public void The_save_companion_writes_both_projects_with_the_configured_fix()
    {
        var config = Config();
        config.SaveCompanionRs2Dlc = true;
        config.ExportFixLowBassTuning = true;
        var projectPath = Path.Combine(_dir, "song.dcproj");
        var project = Project(Lead(7), Bass4(-5), Bass5());
        Documents(config).SaveDlcBuilderXmlBundle(projectPath, project);

        var written = new SaveCompanionService(new FakeConfigurationService { AppConfig = config }, _logger)
            .WriteDlcBuilderProject(projectPath, project, null);

        Assert.NotNull(written);
        var extended = Rs2DlcEditions.PathFor(written!, Rs2DlcEdition.ExtendedRange);
        Assert.True(File.Exists(extended));
        Assert.Equal(220.0, Entries(written!).Single(e => e.IsBass).TuningPitch);
        Assert.Equal(3, Entries(extended).Count);
    }

    private DlcBuilderProjectPaths Export(ChartProject project, bool fix)
    {
        var config = Config();
        return new DlcBuilderProjectWriter(Documents(config), new FakeConfigurationService { AppConfig = config }, _logger)
            .Write(_dir, project, new DlcBuilderProjectSources("", "", null, fix));
    }

    private static AppConfig Config() =>
        new() { ExportApplyDlcBuilderFixes = false, ExportGenerateDynamicDifficulty = false, RsXmlSixStringProjection = false };

    private ProjectDocumentService Documents(AppConfig config)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppLogger>(_logger);
        services.AddKeyedTransient<IChartSerializer, RscpChartSerializer>("rscp");
        services.AddKeyedTransient<IChartSerializer, XmlChartSerializer>("xml");
        services.AddSingleton<IConfigurationService>(new FakeConfigurationService { AppConfig = config });
        return new ProjectDocumentService(services.BuildServiceProvider(), _logger);
    }

    private sealed record Entry(string File, string XmlPath, bool IsBass, short[] Tuning, double TuningPitch, int Priority);

    private static List<Entry> Entries(string rs2dlc)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(rs2dlc));
        var folder = Path.GetDirectoryName(rs2dlc)!;
        return
        [
            .. json.RootElement.GetProperty("Arrangements").EnumerateArray()
                .Where(a => a.GetProperty("Case").GetString() == "Instrumental")
                .Select(a => a.GetProperty("Fields")[0])
                .Select(f => new Entry(
                    Path.GetFileName(f.GetProperty("XML").GetString()!),
                    Path.Combine(folder, f.GetProperty("XML").GetString()!),
                    f.GetProperty("Name").GetInt32() == BassName,
                    [.. f.GetProperty("Tuning").EnumerateArray().Select(t => t.GetInt16())],
                    f.GetProperty("TuningPitch").GetDouble(),
                    f.GetProperty("Priority").GetInt32()))
        ];
    }

    private static int HighestString(string xmlPath)
    {
        var song = XDocument.Load(xmlPath).Root!;
        return song.Descendants("note").Concat(song.Descendants("chordNote"))
            .Select(n => (int)n.Attribute("string")!).DefaultIfEmpty(0).Max();
    }

    private static ChartProject Project(params Arrangement[] arrangements)
    {
        var project = RsXmlSixStringProjectionTests.Chart(6);
        project.Chart.Artist = "Tester";
        project.Chart.SongName = "Split";
        project.Arrangements.Clear();
        foreach (var arrangement in arrangements) project.Arrangements.Add(arrangement);
        return project;
    }

    private static Arrangement Lead(int strings) => RsXmlSixStringProjectionTests.Chart(strings).Arrangements[0];

    private static Arrangement Lead(int strings, string name)
    {
        var lead = Lead(strings);
        lead.Name = name;
        return lead;
    }

    private static Arrangement Bass4(int offset) => Bass(4, offset, highestString: 3);

    private static Arrangement Bass5() => Bass(5, 0, highestString: 4);

    private static Arrangement Bass(int strings, int offset, int highestString)
    {
        var beats = RsXmlSixStringProjectionTests.Chart(6).Beats;
        var bass = new Arrangement { ArrangementType = ArrangementType.Bass, Name = $"Bass{strings}", StringCount = strings };
        for (var i = 0; i < strings; i++) bass.TuningPitches.Add(new TuningOffset(offset));
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Anchors.Add(new FretAnchor(TimelineMath.PositionFromMs(4000, beats), 1, 4));
        level.Notes.Add(new GuitarNote { StringIndex = 0, Fret = 2, Position = TimelineMath.PositionFromMs(4000, beats) });
        level.Notes.Add(new GuitarNote { StringIndex = highestString, Fret = 3, Position = TimelineMath.PositionFromMs(5000, beats) });
        bass.Levels.Add(level);
        return bass;
    }
}
