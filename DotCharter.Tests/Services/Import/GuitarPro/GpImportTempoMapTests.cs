using System.IO.Compression;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Linking;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Import.GuitarPro;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Import.GuitarPro;

/// <summary>
///     #1025: a Guitar Pro "add as new arrangements" import keeps the project's synced tempo map
///     unless the dialog's "replace the tempo map" box is ticked, which starts ticked only for a
///     project with no charted arrangement. A kept map gets the score bar by bar, even where the
///     score's beats per bar differ from the map's.
/// </summary>
public sealed class GpImportTempoMapTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dotcharter-gptempomap-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    /// <summary>Three 4/4 bars at 200 BPM, a whole note on each downbeat (score beats 0, 4 and 8).</summary>
    private string WriteThreeBarScore()
    {
        const string gpif = """
            <GPIF>
              <Score><Title>Tempo Map Fixture</Title><Artist>Fixture Artist</Artist></Score>
              <MasterTrack>
                <Tracks>0</Tracks>
                <Automations>
                  <Automation><Type>Tempo</Type><Bar>0</Bar><Position>0</Position><Value>200 2</Value></Automation>
                </Automations>
              </MasterTrack>
              <Tracks>
                <Track id="0"><Name>Lead</Name><Staves><Staff><Properties>
                  <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
                </Properties></Staff></Staves></Track>
              </Tracks>
              <MasterBars>
                <MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar>
                <MasterBar><Time>4/4</Time><Bars>1</Bars></MasterBar>
                <MasterBar><Time>4/4</Time><Bars>2</Bars></MasterBar>
              </MasterBars>
              <Bars>
                <Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar>
                <Bar id="1"><Voices>1 -1 -1 -1</Voices></Bar>
                <Bar id="2"><Voices>2 -1 -1 -1</Voices></Bar>
              </Bars>
              <Voices>
                <Voice id="0"><Beats>0</Beats></Voice>
                <Voice id="1"><Beats>1</Beats></Voice>
                <Voice id="2"><Beats>2</Beats></Voice>
              </Voices>
              <Beats>
                <Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>
                <Beat id="1"><Rhythm ref="0" /><Notes>1</Notes></Beat>
                <Beat id="2"><Rhythm ref="0" /><Notes>2</Notes></Beat>
              </Beats>
              <Notes>
                <Note id="0"><Properties><Property name="String"><String>2</String></Property><Property name="Fret"><Fret>5</Fret></Property></Properties></Note>
                <Note id="1"><Properties><Property name="String"><String>2</String></Property><Property name="Fret"><Fret>7</Fret></Property></Properties></Note>
                <Note id="2"><Properties><Property name="String"><String>2</String></Property><Property name="Fret"><Fret>9</Fret></Property></Properties></Note>
              </Notes>
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

    /// <summary>
    ///     A hand-synced map that is NOT the score's: a 3/4 bar, then 4/4 bars, uneven beat
    ///     times and an anchor — and a charted arrangement on it.
    /// </summary>
    private static ChartProject ChartedProjectWithSyncedMap()
    {
        var project = new ChartProject { Bpm = 120, Chart = { SongName = "Fixture", Artist = "DotCharter" } };
        var beat = 0;
        foreach (var beatsInBar in new[] { 3, 4, 4, 4 })
        {
            for (var b = 0; b < beatsInBar; b++, beat++)
            {
                project.Beats.Add(new BeatMarker
                {
                    Time = 250 + beat * 497.0 + beat % 3 * 11.5, Bpm = 120.7, BeatsInMeasure = beatsInBar, NoteDenominator = 4,
                    IsMeasure = b == 0, IsAnchor = beat == 3
                });
            }
        }

        var arrangement = new Arrangement { Name = "Charted" };
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), StringIndex = 1, Fret = 3 });
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        project.GpSourceBars = [new GpSourceBar { StartBeat = 0, BeatCount = 3, BeatsInMeasure = 3, NoteDenominator = 4, WrittenBar = 7 }];
        return project;
    }

    private static ChartProject EmptyProject()
    {
        var project = new ChartProject { Bpm = 120, Chart = { SongName = "Fixture", Artist = "DotCharter" } };
        for (var i = 0; i < 16; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500.0, IsMeasure = i % 4 == 0, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4 });
        project.Arrangements.Add(new Arrangement { Name = "Guitar 1", Levels = [new ChartLevel()] });
        return project;
    }

    private static string Snapshot(IEnumerable<BeatMarker> beats) => string.Join("|", beats.Select(b =>
        $"{BitConverter.DoubleToInt64Bits(b.Time)},{BitConverter.DoubleToInt64Bits(b.Bpm)},{b.BeatsInMeasure},{b.NoteDenominator},{b.IsMeasure},{b.IsAnchor}"));

    private (GpImportViewModel Vm, ChartProject Project) Open(ChartProject project)
    {
        var edit = new ChartEditHarness();
        edit.State.ActiveProject = project;
        edit.State.ActiveArrangement = project.Arrangements.FirstOrDefault();
        var (maintenance, _) = UndoStepProxy.Create<IChartMaintenanceService>();
        var config = new FakeConfigurationService();
        var applier = new GpImportApplier(edit.State, edit.Editor, edit.Transport, config, maintenance, new FakeAppLogger(),
            FakeLocalization.English());
        var path = WriteThreeBarScore();
        var vm = new GpImportViewModel(applier, new FakeFileStorageService(), config, edit.State, new FakeAppLogger(),
            FakeLocalization.English());
        vm.Configure(new GpImportRequest(path, new GpImporter().GetAvailableTracks(path)));
        return (vm, project);
    }

    private static async Task ImportAsync(GpImportViewModel vm)
    {
        await ((IAsyncRelayCommand)vm.ImportCommand).ExecuteAsync(null);
        Assert.True(ChartEditHarness.Answer(vm.Result), vm.ErrorMessage);
    }

    [Fact]
    public async Task Add_new_into_a_charted_project_keeps_its_synced_map_and_puts_the_notes_on_its_bars()
    {
        var (vm, project) = Open(ChartedProjectWithSyncedMap());
        var before = Snapshot(project.Beats);
        var sourceBars = project.GpSourceBars;
        Assert.False(vm.ReplaceTempoMap);

        await ImportAsync(vm);

        Assert.Equal(before, Snapshot(project.Beats));
        Assert.Equal(120, project.Bpm);
        Assert.Same(sourceBars, project.GpSourceBars);
        var imported = Assert.Single(project.Arrangements, a => a.Name != "Charted");
        Assert.Empty(imported.AudioCheckSpots);

        // Score bars 0, 1, 2 are the map's measures 0 (3/4), 1 and 2: their downbeats.
        var downbeats = MeasureGrid.Build(project.Beats).Take(3).Select(m => m.Start).ToList();
        Assert.Equal(downbeats, imported.Levels[0].Notes.Select(n => n.Position).OrderBy(p => p).ToList());
        Assert.Equal([new FractionalPosition(0), new FractionalPosition(3), new FractionalPosition(7)], downbeats);
    }

    [Fact]
    public async Task Ticking_replace_puts_the_score_tempo_map_in_place()
    {
        var (vm, project) = Open(ChartedProjectWithSyncedMap());
        var before = Snapshot(project.Beats);
        vm.ReplaceTempoMap = true;

        await ImportAsync(vm);

        Assert.NotEqual(before, Snapshot(project.Beats));
        Assert.Equal(12, project.Beats.Count);
        Assert.All(project.Beats, b => Assert.Equal(4, b.BeatsInMeasure));
        Assert.Equal(200, project.Bpm);
        var imported = Assert.Single(project.Arrangements, a => a.Name != "Charted");
        Assert.Equal([new FractionalPosition(0), new FractionalPosition(4), new FractionalPosition(8)],
            imported.Levels[0].Notes.Select(n => n.Position).OrderBy(p => p).ToList());
    }

    [Fact]
    public async Task An_empty_project_replaces_the_map_by_default()
    {
        var (vm, project) = Open(EmptyProject());
        Assert.True(vm.ReplaceTempoMap);

        await ImportAsync(vm);

        Assert.Equal(12, project.Beats.Count);
        Assert.Equal(200, project.Bpm);
    }

    [Fact]
    public async Task A_locked_map_is_kept_even_when_replace_is_ticked_and_the_dialog_says_so()
    {
        var project = ChartedProjectWithSyncedMap();
        project.IsBeatMapLocked = true;
        var (vm, _) = Open(project);
        var before = Snapshot(project.Beats);
        Assert.True(vm.IsTempoMapLocked);
        vm.ReplaceTempoMap = true;

        await ImportAsync(vm);

        Assert.Equal(before, Snapshot(project.Beats));
        var imported = Assert.Single(project.Arrangements, a => a.Name != "Charted");
        Assert.Equal(new FractionalPosition(3), imported.Levels[0].Notes.Select(n => n.Position).Order().ElementAt(1));
    }

    [Fact]
    public async Task The_choice_shows_only_for_add_new()
    {
        var (vm, _) = Open(ChartedProjectWithSyncedMap());
        Assert.True(vm.ShowsTempoMapChoice);
        vm.IsTechniqueOnlyMode = true;
        Assert.False(vm.ShowsTempoMapChoice);
        vm.IsDiffCompareMode = true;
        Assert.False(vm.ShowsTempoMapChoice);
    }

    /// <summary>A 4/4 score bar on a 3/4 map bar keeps its fraction of the bar: beat 2 (half way) lands on
    ///     1.5, the next bar's downbeat on the map's; past the map's end it continues beat for beat.</summary>
    [Fact]
    public void Score_bars_scale_into_map_bars_with_other_beats_per_bar()
    {
        var bars = Enumerable.Range(0, 4).Select(_ => new GpBarInfo { BeatsInMeasure = 4, NoteDenominator = 4 }).ToList();
        var measures = new List<MeasureSpan> { new(0, 0, 3, 3, 4), new(1, 3, 9, 6, 8) };
        var map = GpMeterGrid.BarToMeasure(bars, measures, importTimeSignatures: true);

        Assert.Equal(new FractionalPosition(0), map(new FractionalPosition(0)));
        Assert.Equal(new FractionalPosition(1, new Fraction(1, 2)), map(new FractionalPosition(2)));
        Assert.Equal(new FractionalPosition(3), map(new FractionalPosition(4)));
        Assert.Equal(new FractionalPosition(6), map(new FractionalPosition(6)));
        Assert.Equal(new FractionalPosition(9), map(new FractionalPosition(8)));
        Assert.Equal(new FractionalPosition(14), map(new FractionalPosition(13)));
    }

    /// <summary>A map whose last measure the grid cuts short still counts it at its signature's length.</summary>
    [Fact]
    public void A_cut_short_last_measure_counts_at_its_signature()
    {
        var bars = Enumerable.Range(0, 2).Select(_ => new GpBarInfo { BeatsInMeasure = 4, NoteDenominator = 4 }).ToList();
        var measures = new List<MeasureSpan> { new(0, 0, 2, 4, 4) };
        var map = GpMeterGrid.BarToMeasure(bars, measures, importTimeSignatures: true);

        Assert.Equal(new FractionalPosition(3), map(new FractionalPosition(3)));
        Assert.Equal(new FractionalPosition(5), map(new FractionalPosition(5)));
    }
}
