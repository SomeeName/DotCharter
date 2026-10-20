using DotCharter.Services.Editing.Catalog;
using DotCharter.Services.Export;
using DotCharter.Services.Export.DlcBuilder;
using DotCharter.Services.Export.GuitarPro;
using DotCharter.Services.Export.Sheet;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Package.DcSong;
using DotCharter.Services.Parsing;
using DotCharter.Services.Parsing.Eof;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Parsing.Xml;
using DotCharter.Tests.Services.Parsing.Eof;
using DotCharter.Tone.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     #822: a chart imported from play-only content is refused. One
///     refusal per exporter, at the exporter's OWN entry point (not the menu), and
///     the same exporters still writing an unrestricted chart. Every project here is
///     synthetic — no game file is read.
/// </summary>
public sealed class PlayOnlyExportGuardTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_playonly_").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private string P(string name) => Path.Combine(_dir, name);

    private static ChartProject PlayOnlyProject()
    {
        var project = EofExportTestCharts.BuildRepresentativeProject();
        project.Provenance = ChartProvenance.PlayOnly;
        foreach (var arrangement in project.Arrangements) arrangement.Provenance = ChartProvenance.PlayOnly;
        return project;
    }

    private static (Arrangement Arrangement, ChartLevel Level) FirstPart(ChartProject project)
    {
        var arrangement = project.Arrangements[0];
        return (arrangement, arrangement.Levels[0]);
    }

    // ---- the rule itself -------------------------------------------------------

    [Fact]
    public void A_project_is_play_only_when_it_or_any_arrangement_is_play_only()
    {
        var project = EofExportTestCharts.BuildRepresentativeProject();
        Assert.False(PlayOnlyExportGuard.IsPlayOnly(project));

        project.Arrangements[^1].Provenance = ChartProvenance.PlayOnly;
        Assert.True(PlayOnlyExportGuard.IsPlayOnly(project));

        project.Arrangements[^1].Provenance = ChartProvenance.Unrestricted;
        project.Provenance = ChartProvenance.PlayOnly;
        Assert.True(PlayOnlyExportGuard.IsPlayOnly(project));
    }

    // ---- one refusal per exporter ----------------------------------------------

    [Fact]
    public async Task Rocksmith_package_builder_refuses_before_any_work()
    {
        var output = P("song_p.psarc");
        var ex = await Assert.ThrowsAsync<PlayOnlyContentException>(async () =>
            await new RocksmithPackageBuilder(new FakeAppLogger()).BuildPsarcAsync(PlayOnlyProject(),
                new RocksmithBuildOptions { OutputPath = output, AudioPath = P("missing.wav") },
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("play-only", ex.Message);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Rocksmith_xml_serializer_refuses()
    {
        var output = P("song_lead.xml");
        Assert.Throws<PlayOnlyContentException>(() => new XmlChartSerializer().Save(output, PlayOnlyProject()));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Rocksmith_xml_bundle_is_skipped_on_save_without_failing_it()
    {
        using var provider = DocumentServices();
        var service = new ProjectDocumentService(provider, new FakeAppLogger());

        service.SaveDlcBuilderXmlBundle(P("project.rscp"), PlayOnlyProject());

        Assert.False(Directory.Exists(P(DlcBuilderBundle.DirectoryName)));
    }

    [Fact]
    public void Editor_on_fire_exporter_refuses()
    {
        var output = P("song.eof");
        Assert.Throws<PlayOnlyContentException>(() => EofExporter.ExportToFile(PlayOnlyProject(), output));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Editor_on_fire_refuses_an_play_only_arrangement_in_an_unrestricted_project()
    {
        // The arrangement flag alone: a part moved out of a play-only song.
        var project = EofExportTestCharts.BuildRepresentativeProject();
        project.Arrangements[0].Provenance = ChartProvenance.PlayOnly;

        Assert.Throws<PlayOnlyContentException>(() => EofExporter.Export(project));
    }

    [Fact]
    public void Guitar_pro_exporter_refuses()
    {
        var project = PlayOnlyProject();
        var (arrangement, level) = FirstPart(project);
        var output = P("song.gp");

        Assert.Throws<PlayOnlyContentException>(() =>
            GpExporter.Export(arrangement, level, project.Beats, output));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Sheet_pdf_exporter_refuses()
    {
        var project = PlayOnlyProject();
        var (arrangement, level) = FirstPart(project);
        var output = P("song.pdf");

        await Assert.ThrowsAsync<PlayOnlyContentException>(() =>
            new SheetPdfExporter(FakeLocalization.English()).ExportToFileAsync(project, arrangement, level,
                project.Beats, staves: 2, SheetPageFormat.A4, output, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Sheet_pdf_exporter_refuses_an_play_only_arrangement_without_its_project()
    {
        var project = PlayOnlyProject();
        var (arrangement, level) = FirstPart(project);

        using var sink = new MemoryStream();
        await Assert.ThrowsAsync<PlayOnlyContentException>(() =>
            new SheetPdfExporter(FakeLocalization.English()).ExportAsync(null, arrangement, level,
                project.Beats, staves: 2, SheetPageFormat.A4, sink, TestContext.Current.CancellationToken));
        Assert.Equal(0, sink.Length);
    }

    [Fact]
    public async Task Dcsong_writer_refuses()
    {
        var output = P("song.dcsong");
        await Assert.ThrowsAsync<PlayOnlyContentException>(() => new DcSongWriter().WriteAsync(new DcSongWriteRequest
        {
            Project = PlayOnlyProject(),
            AudioPath = P("missing.wav"),
            OutputPath = output
        }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Every_save_companion_is_skipped_for_play_only_content()
    {
        var service = new SaveCompanionService(new FakeConfigurationService
        {
            AppConfig = new AppConfig
            {
                SaveCompanionEof = true,
                SaveCompanionRs2Dlc = true,
                SaveCompanionPsarc = true
            }
        }, new FakeAppLogger());
        var projectPath = P("MySong.rscp");
        var ct = TestContext.Current.CancellationToken;

        Assert.Null(service.WriteEof(projectPath, PlayOnlyProject()));
        Assert.Null(service.WriteDlcBuilderProject(projectPath, PlayOnlyProject(), audioLengthMs: null));
        Assert.Null(await service.BuildPsarcAsync(projectPath, PlayOnlyProject(), audioLengthMs: null, ct));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Phrase_catalog_refuses_to_store_play_only_content()
    {
        var project = PlayOnlyProject();
        var (arrangement, level) = FirstPart(project);
        var catalog = new PhraseCatalogService(P("catalog"), new RscpChartSerializer(), new FakeAppLogger());

        var entry = catalog.Add("riff", arrangement, level.Notes.Take(4), [], [], []);

        Assert.Null(entry);
        Assert.False(Directory.Exists(P("catalog")) && Directory.EnumerateFiles(P("catalog")).Any());
    }

    [Fact]
    public void Clipboard_paste_of_play_only_content_into_an_unrestricted_part_is_blocked()
    {
        var playOnly = PlayOnlyProject().Arrangements[0];
        var own = EofExportTestCharts.BuildRepresentativeProject().Arrangements[0];
        var payload = new ClipboardPayload();
        payload.Notes.Add(new GuitarNote { Position = new FractionalPosition(0), StringIndex = 1, Fret = 3 });

        var intoOwn = PhrasePasteCompatibility.Evaluate(payload, PhraseSourceContext.From(playOnly), own);
        Assert.True(intoOwn.IsBlocked);
        Assert.Contains(intoOwn.Issues, i => i.LocalizationKey == PhrasePasteCompatibility.KeyPlayOnly);

        // Within play-only content it stays an ordinary paste.
        var intoPlayOnly = PhrasePasteCompatibility.Evaluate(payload, PhraseSourceContext.From(playOnly),
            PlayOnlyProject().Arrangements[0]);
        Assert.DoesNotContain(intoPlayOnly.Issues, i => i.LocalizationKey == PhrasePasteCompatibility.KeyPlayOnly);
    }

    // ---- an unrestricted chart still exports -------------------------------------

    [Fact]
    public void An_unrestricted_chart_still_exports_through_every_file_exporter()
    {
        var project = EofExportTestCharts.BuildRepresentativeProject();
        var (arrangement, level) = FirstPart(project);

        EofExporter.ExportToFile(project, P("song.eof"));
        GpExporter.Export(arrangement, level, project.Beats, P("song.gp"));
        new XmlChartSerializer().Save(P("song_lead.xml"), project);
        using (var provider = DocumentServices())
            new ProjectDocumentService(provider, new FakeAppLogger()).SaveDlcBuilderXmlBundle(P("project.rscp"), project);

        Assert.True(new FileInfo(P("song.eof")).Length > 0);
        Assert.True(new FileInfo(P("song.gp")).Length > 0);
        Assert.True(new FileInfo(P("song_lead.xml")).Length > 0);
        Assert.NotEmpty(Directory.GetFiles(P(DlcBuilderBundle.DirectoryName), "*.xml"));
    }

    [Fact]
    public void An_unrestricted_chart_still_gets_its_eof_companion()
    {
        var service = new SaveCompanionService(new FakeConfigurationService
        {
            AppConfig = new AppConfig { SaveCompanionEof = true, SaveCompanionEofAudio = false }
        }, new FakeAppLogger());

        Assert.NotNull(service.WriteEof(P("MySong.rscp"), EofExportTestCharts.BuildRepresentativeProject()));
    }

    [Fact]
    public void The_refusal_messages_resolve()
    {
        // The three resx files are held in sync by the resx parity lint; this pins
        // that the keys the guard and the paste check name exist at all.
        var loc = FakeLocalization.English();
        Assert.NotEqual(PlayOnlyExportGuard.MessageKey, loc[PlayOnlyExportGuard.MessageKey]);
        Assert.NotEqual(PhrasePasteCompatibility.KeyPlayOnly, loc[PhrasePasteCompatibility.KeyPlayOnly]);
    }

    private static ServiceProvider DocumentServices()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IChartSerializer, RscpChartSerializer>("rscp");
        services.AddKeyedSingleton<IChartSerializer, XmlChartSerializer>("xml");
        services.AddSingleton<IConfigurationService>(new FakeConfigurationService { AppConfig = new AppConfig() });
        return services.BuildServiceProvider();
    }
}
