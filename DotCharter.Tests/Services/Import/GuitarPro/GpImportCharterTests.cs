using System.IO.Compression;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Import.GuitarPro;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Import.GuitarPro;

/// <summary>
///     The Guitar Pro "tabbed by" credit is the tab author, not the chart author: an import never
///     writes it into <c>Chart.Charter</c>, whether the project's charter was typed or came from the
///     default charter name a new project is created with.
/// </summary>
public sealed class GpImportCharterTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dotcharter-gpcharter-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    private string WriteGpFileWithTabber()
    {
        const string gpif = """
            <GPIF>
              <Score><Title>Fixture</Title><Artist>Fixture Artist</Artist><Tabber>Tabs, I Guess</Tabber></Score>
              <MasterTrack><Tracks>0</Tracks></MasterTrack>
              <Tracks>
                <Track id="0"><Name>Lead</Name><Staves><Staff><Properties>
                  <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
                </Properties></Staff></Staves></Track>
              </Tracks>
              <MasterBars><MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar></MasterBars>
              <Bars><Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar></Bars>
              <Voices><Voice id="0"><Beats>0</Beats></Voice></Voices>
              <Beats><Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat></Beats>
              <Notes><Note id="0"><Properties><Property name="String"><String>2</String></Property><Property name="Fret"><Fret>5</Fret></Property></Properties></Note></Notes>
              <Rhythms><Rhythm id="0"><NoteValue>Whole</NoteValue></Rhythm></Rhythms>
            </GPIF>
            """;
        var path = Path.Combine(_tempDir, $"fixture-{Guid.NewGuid():N}.gp");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("Content/score.gpif").Open(), Encoding.UTF8);
        writer.Write(gpif);
        return path;
    }

    private async Task<string> ImportAsync(string charter) => (await ImportProjectAsync(charter)).Chart.Charter;

    private async Task<ChartProject> ImportProjectAsync(string charter)
    {
        var edit = new ChartEditHarness();
        var project = new ChartProject { Bpm = 120, Chart = { SongName = "Fixture", Artist = "DotCharter", Charter = charter } };
        edit.State.ActiveProject = project;
        var (maintenance, _) = UndoStepProxy.Create<IChartMaintenanceService>();
        var applier = new GpImportApplier(edit.State, edit.Editor, edit.Transport, new FakeConfigurationService(), maintenance,
            new FakeAppLogger(), FakeLocalization.English());
        var path = WriteGpFileWithTabber();
        var vm = new GpImportViewModel(applier, new FakeFileStorageService(), new FakeConfigurationService(), edit.State,
            new FakeAppLogger(), FakeLocalization.English());
        vm.Configure(new GpImportRequest(path, new GpImporter().GetAvailableTracks(path)));
        await ((IAsyncRelayCommand)vm.ImportCommand).ExecuteAsync(null);
        Assert.True(ChartEditHarness.Answer(vm.Result));
        Assert.Equal("Fixture Artist", project.Chart.Artist);
        return project;
    }

    /// <summary>The score's tempo map replaces the beat map unsynced: D110 marks it at the first note.</summary>
    [Fact]
    public async Task The_tab_tempo_map_is_marked_for_an_audio_check()
    {
        var arrangement = Assert.Single((await ImportProjectAsync("Default Charter")).Arrangements);
        var spot = Assert.Single(arrangement.AudioCheckSpots);
        Assert.Equal(new AudioCheckSpot(arrangement.Levels[0].Notes[0].Position, AudioCheckReason.TempoFromTab), spot);
    }

    [Fact]
    public async Task Import_into_a_project_with_a_set_charter_leaves_it_unchanged() =>
        Assert.Equal("Project Owner", await ImportAsync("Project Owner"));

    [Fact]
    public async Task New_project_keeps_the_default_charter_name_it_was_created_with() =>
        Assert.Equal("Default Charter", await ImportAsync("Default Charter"));
}
