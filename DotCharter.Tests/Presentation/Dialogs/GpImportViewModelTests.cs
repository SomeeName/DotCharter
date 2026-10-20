using System.IO.Compression;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Import.GuitarPro;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     File ▸ Import ▸ Guitar Pro, driven end to end: the launcher reads the
///     track list off the UI thread under a status-bar pill and opens the
///     dialog on it; the dialog parses the score off the UI thread behind
///     <c>IsBusy</c> and applies it as ONE undo step, showing a failure inside
///     itself. The ordering rule the apply half keeps — the Guitar Pro beat map
///     lands BEFORE spacing is enforced — is pinned here too.
/// </summary>
public sealed class GpImportViewModelTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dotcharter-gpimportvm-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    private sealed class Harness
    {
        public Harness(ChartProject project)
        {
            Edit.State.ActiveProject = project;
            Edit.State.ActiveArrangement = project.Arrangements.FirstOrDefault();
            (Maintenance, Recorder) = UndoStepProxy.Create<IChartMaintenanceService>();
            Applier = new GpImportApplier(Edit.State, Edit.Editor, Edit.Transport, Config, Maintenance, new FakeAppLogger(), FakeLocalization.English());
            Dialogs = new ScriptedDialogs(
                _ => new GpImportViewModel(Applier, Storage, Config, Edit.State, new FakeAppLogger(), FakeLocalization.English(), Parse),
                screen => Act?.Invoke((GpImportViewModel)screen));
            Launcher = new GpImportLauncher(Dialogs, Storage, Config, StatusBar, Edit.State, new FakeAppLogger(), FakeLocalization.English());
        }

        public ChartEditHarness Edit { get; } = new();

        public FakeConfigurationService Config { get; } = new();

        public FakeFileStorageService Storage { get; } = new();

        public RecordingStatusBarService StatusBar { get; } = new();

        public IChartMaintenanceService Maintenance { get; }

        public UndoStepProxy Recorder { get; }

        public GpImportApplier Applier { get; }

        public ScriptedDialogs Dialogs { get; }

        public GpImportLauncher Launcher { get; }

        /// <summary>The "user": runs when the dialog opens. Unset, the dialog is left open and the launch never finishes.</summary>
        public Action<GpImportViewModel>? Act { get; set; }

        /// <summary>Replaces the score parse when set; null parses the file for real.</summary>
        public Func<string, List<int>, GpImportSettings, GpImportResult>? Parse { get; set; }

        /// <summary>Opens the dialog on <paramref name="path" /> through the launcher and hands it back unanswered.</summary>
        public async Task<GpImportViewModel> OpenOnAsync(string path)
        {
            var opened = new TaskCompletionSource<GpImportViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
            Act = vm => opened.TrySetResult(vm);
            _ = Launcher.OpenAsync(path);
            return await opened.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        }
    }

    /// <summary>The pre-import grid a brand-new project ships with: flat 4/4 at 120 BPM — deliberately NOT the fixture's tempo.</summary>
    private static ChartProject NewProjectWithFlat120Grid()
    {
        var project = new ChartProject { Bpm = 120, Chart = { SongName = "Fixture", Artist = "DotCharter" } };
        for (var i = 0; i < 32; i++)
        {
            project.Beats.Add(new BeatMarker
            { Time = i * 500.0, IsMeasure = i % 4 == 0, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4 });
        }

        return project;
    }

    /// <summary>Two 4/4 bars of half notes on one string, pinned to 240 BPM — half the pre-import grid's beat.</summary>
    private string WriteFixtureGpFile()
    {
        const string gpif = """
            <GPIF>
              <Score><Title>Beat Map Ordering Fixture</Title><Artist>Fixture Artist</Artist></Score>
              <MasterTrack>
                <Tracks>0</Tracks>
                <Automations>
                  <Automation><Type>Tempo</Type><Bar>0</Bar><Position>0</Position><Value>240 2</Value></Automation>
                </Automations>
              </MasterTrack>
              <Tracks>
                <Track id="0">
                  <Name>Lead</Name>
                  <Staves><Staff><Properties>
                    <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
                  </Properties></Staff></Staves>
                </Track>
              </Tracks>
              <MasterBars>
                <MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar>
                <MasterBar><Time>4/4</Time><Bars>1</Bars></MasterBar>
              </MasterBars>
              <Bars>
                <Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar>
                <Bar id="1"><Voices>1 -1 -1 -1</Voices></Bar>
              </Bars>
              <Voices>
                <Voice id="0"><Beats>0 1</Beats></Voice>
                <Voice id="1"><Beats>2 3</Beats></Voice>
              </Voices>
              <Beats>
                <Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>
                <Beat id="1"><Rhythm ref="0" /><Notes>1</Notes></Beat>
                <Beat id="2"><Rhythm ref="0" /><Notes>2</Notes></Beat>
                <Beat id="3"><Rhythm ref="0" /><Notes>3</Notes></Beat>
              </Beats>
              <Notes>
                <Note id="0"><Properties><Property name="String"><String>2</String></Property><Property name="Fret"><Fret>5</Fret></Property></Properties></Note>
                <Note id="1"><Properties><Property name="String"><String>2</String></Property><Property name="Fret"><Fret>7</Fret></Property></Properties></Note>
                <Note id="2"><Properties><Property name="String"><String>2</String></Property><Property name="Fret"><Fret>5</Fret></Property></Properties></Note>
                <Note id="3"><Properties><Property name="String"><String>2</String></Property><Property name="Fret"><Fret>7</Fret></Property></Properties></Note>
              </Notes>
              <Rhythms><Rhythm id="0"><NoteValue>Half</NoteValue></Rhythm></Rhythms>
            </GPIF>
            """;

        var path = Path.Combine(_tempDir, $"fixture-{Guid.NewGuid():N}.gp");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("Content/score.gpif").Open(), Encoding.UTF8);
        writer.Write(gpif);
        return path;
    }

    private static Task ImportAsync(GpImportViewModel vm) => ((IAsyncRelayCommand)vm.ImportCommand).ExecuteAsync(null);

    // ---- the launcher ------------------------------------------------------------

    /// <summary>The picker's file is read off the UI thread under a pill, then the dialog opens on its tracks.</summary>
    [Fact]
    public async Task The_launcher_reads_the_tracks_under_a_status_pill_and_opens_the_dialog_on_them()
    {
        var h = new Harness(NewProjectWithFlat120Grid());
        var path = WriteFixtureGpFile();
        h.Storage.NextOpenFile = path;
        h.Act = vm => vm.CancelCommand.Execute(null);

        await h.Launcher.PickAsync();

        var vm = Assert.IsType<GpImportViewModel>(Assert.Single(h.Dialogs.Opened));
        Assert.Equal("Lead", Assert.Single(vm.Tracks).Name);
        Assert.True(vm.Tracks[0].IsSelected);
        Assert.Equal(["begin:import.gp.read:Reading " + Path.GetFileName(path) + "…", "complete:import.gp.read"],
            h.StatusBar.Log);
    }

    /// <summary>A dropped file goes straight to its tracks: no picker.</summary>
    [Fact]
    public async Task A_dropped_file_opens_without_a_picker()
    {
        var h = new Harness(NewProjectWithFlat120Grid());

        var vm = await h.OpenOnAsync(WriteFixtureGpFile());

        Assert.Empty(h.Storage.RequestedTitles);
        Assert.True(vm.HasTracks);
        Assert.True(vm.ImportCommand.CanExecute(null));
    }

    /// <summary>A file the parser rejects opens no dialog, says why on the status line, and clears the pill.</summary>
    [Fact]
    public async Task An_unreadable_file_opens_nothing_and_reports_on_the_status_line()
    {
        var h = new Harness(NewProjectWithFlat120Grid());
        var bogus = Path.Combine(_tempDir, "not-a-tab.gp");
        File.WriteAllBytes(bogus, [1, 2, 3, 4]);

        await h.Launcher.OpenAsync(bogus);

        Assert.Empty(h.Dialogs.Opened);
        Assert.Contains("Guitar Pro import failed", h.Edit.State.MainDisplayMessage);
        Assert.Equal("complete:import.gp.read", h.StatusBar.Log[^1]);
    }

    // ---- the apply half ------------------------------------------------------------

    /// <summary>
    ///     Spacing is enforced against the GUITAR PRO beat map, not the pre-import
    ///     grid. With a 125 ms minimum gap: 240 BPM (the fixture) ⇒ 1/2 beat ⇒ a
    ///     1 1/2 beat sustain; the discarded 120 BPM grid would give 1 3/4.
    /// </summary>
    [Fact]
    public async Task Add_new_enforces_spacing_against_the_guitar_pro_beat_map_in_one_undo_step()
    {
        var h = new Harness(NewProjectWithFlat120Grid());
        h.Config.AppConfig.MinNoteDistanceMode = MinNoteDistanceMode.Milliseconds;
        h.Config.AppConfig.MinNoteDistanceMs = 125.0;
        var vm = await h.OpenOnAsync(WriteFixtureGpFile());

        await ImportAsync(vm);

        Assert.True(ChartEditHarness.Answer(vm.Result));
        Assert.Single(h.Recorder.Calls, c => c.StartsWith("RunAsSingleUndoStep", StringComparison.Ordinal));
        var beats = h.Edit.State.ActiveProject!.Beats;
        Assert.Equal(8, beats.Count);
        Assert.Equal(250.0, beats[1].Time, 3);
        Assert.Equal(240.0, h.Edit.State.ActiveProject.Bpm, 3);
        Assert.Equal(240.0, h.Edit.Transport.Bpm, 3);

        // The harness's stub "Lead" arrangement is empty, so the import replaces it.
        var arrangement = Assert.Single(h.Edit.State.ActiveProject.Arrangements);
        var notes = arrangement.Levels[0].Notes.OrderBy(n => n.Position).ToList();
        Assert.Equal(4, notes.Count);
        var expected = new FractionalPosition(1, new Fraction(1, 2));
        Assert.True(notes[0].Sustain.CompareTo(expected) == 0,
            $"spacing must use the GP beat map (1+1/2 beats); got {notes[0].Sustain.BeatId}+" +
            $"{notes[0].Sustain.Fraction.Numerator}/{notes[0].Sustain.Fraction.Denominator}");
        Assert.Contains(arrangement.Name, h.Edit.State.MainDisplayMessage);
    }

    /// <summary>A NON-EMPTY score value wins; an empty one never erases what the user authored.</summary>
    [Fact]
    public async Task Add_new_applies_non_empty_score_metadata_and_keeps_authored_fields_on_empty()
    {
        var h = new Harness(NewProjectWithFlat120Grid());
        var chart = h.Edit.State.ActiveProject!.Chart;
        chart.AlbumName = "Authored Album";
        var vm = await h.OpenOnAsync(WriteFixtureGpFile());

        await ImportAsync(vm);

        Assert.Equal("Beat Map Ordering Fixture", chart.SongName);
        Assert.Equal("Fixture Artist", chart.Artist);
        Assert.Equal("Authored Album", chart.AlbumName);
    }

    /// <summary>Technique-only folds onto arrangements the project owns, so the authored grid survives verbatim.</summary>
    [Fact]
    public async Task Technique_only_leaves_the_beat_map_untouched_and_merges_into_the_chosen_target()
    {
        var project = NewProjectWithFlat120Grid();
        project.Arrangements.Add(new Arrangement { Name = "Rhythm", Levels = { new ChartLevel() } });
        var lead = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = 6 };
        lead.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        project.Arrangements.Add(lead);
        var h = new Harness(project);
        var vm = await h.OpenOnAsync(WriteFixtureGpFile());
        vm.IsTechniqueOnlyMode = true;

        Assert.True(vm.RequiresTargetSelection);
        Assert.Equal(["Rhythm", "Lead"], vm.Tracks[0].Targets);
        Assert.Equal(1, vm.Tracks[0].TargetIndex); // the same-named arrangement, not the first
        await ImportAsync(vm);

        var beats = h.Edit.State.ActiveProject!.Beats;
        Assert.Equal(32, beats.Count);
        Assert.Equal(500.0, beats[1].Time, 3);
        Assert.Equal(120.0, h.Edit.State.ActiveProject.Bpm, 3);
        Assert.Equal(2, project.Arrangements.Count);
        Assert.True(ChartEditHarness.Answer(vm.Result));
    }

    [Fact]
    public async Task The_three_modes_are_one_radio_group()
    {
        var h = new Harness(NewProjectWithFlat120Grid());
        var vm = await h.OpenOnAsync(WriteFixtureGpFile());
        Assert.True(vm.IsAddNewMode);
        Assert.False(vm.RequiresOldFile);

        vm.IsDiffCompareMode = true;

        Assert.Equal((false, false, true), (vm.IsAddNewMode, vm.IsTechniqueOnlyMode, vm.IsDiffCompareMode));
        Assert.True(vm.RequiresOldFile);
        Assert.True(vm.RequiresTargetSelection);
        vm.IsDiffCompareMode = false; // unticking a radio does not leave the group empty
        Assert.True(vm.IsDiffCompareMode);
    }

    // ---- busy, errors, cancel ------------------------------------------------------

    /// <summary>The parse runs behind IsBusy, which must RISE and FALL, and Import is off while it is up.</summary>
    [Fact]
    public async Task Import_raises_and_clears_IsBusy_and_is_disabled_while_busy()
    {
        var h = new Harness(NewProjectWithFlat120Grid());
        var vm = await h.OpenOnAsync(WriteFixtureGpFile());
        var busyStates = new List<bool>();
        var canExecuteWhileBusy = true;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(IGpImportScreen.IsBusy)) return;
            busyStates.Add(vm.IsBusy);
            if (vm.IsBusy) canExecuteWhileBusy = vm.ImportCommand.CanExecute(null);
        };

        await ImportAsync(vm);

        Assert.Equal([true, false], busyStates);
        Assert.False(canExecuteWhileBusy);
    }

    /// <summary>A failed parse is shown INSIDE the dialog and leaves it open — not only on a status bar behind it.</summary>
    [Fact]
    public async Task A_parse_that_throws_shows_the_error_in_the_dialog_and_keeps_it_open()
    {
        var h = new Harness(NewProjectWithFlat120Grid());
        var path = WriteFixtureGpFile();
        var vm = await h.OpenOnAsync(path);
        File.Delete(path); // the track list is in; the score parse now throws

        await ImportAsync(vm);

        Assert.False(vm.Result.IsCompleted);
        Assert.False(string.IsNullOrEmpty(vm.ErrorMessage));
        Assert.False(vm.IsBusy);
        Assert.Empty(h.Recorder.Calls);
    }

    [Fact]
    public async Task Diff_compare_without_an_older_file_refuses_inside_the_dialog()
    {
        var h = new Harness(NewProjectWithFlat120Grid());
        var vm = await h.OpenOnAsync(WriteFixtureGpFile());
        vm.IsDiffCompareMode = true;

        await ImportAsync(vm);

        Assert.False(vm.Result.IsCompleted);
        Assert.Equal(FakeLocalization.English()["Status_GpImport_DiffNeedsOldFile"],
            vm.ErrorMessage);
    }

    [Fact]
    public async Task With_no_track_chosen_Import_is_off()
    {
        var h = new Harness(NewProjectWithFlat120Grid());
        var vm = await h.OpenOnAsync(WriteFixtureGpFile());

        vm.Tracks[0].IsSelected = false;

        Assert.False(vm.ImportCommand.CanExecute(null));
    }

    /// <summary>
    ///     Esc or Cancel during the parse REQUESTS cancellation instead of closing
    ///     over a live parse; when the parse returns, nothing of it is applied and
    ///     the dialog closes.
    /// </summary>
    [Fact]
    public async Task Cancel_while_parsing_cancels_the_import_and_applies_nothing()
    {
        var h = new Harness(NewProjectWithFlat120Grid());
        var started = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var real = new GpImporter();
        h.Parse = (path, ids, settings) =>
        {
            started.Set();
            release.Wait(TimeSpan.FromSeconds(60));
            return real.ImportTracksWithMetadata(path, ids, settings);
        };
        var vm = await h.OpenOnAsync(WriteFixtureGpFile());

        var import = ImportAsync(vm);
        Assert.True(started.Wait(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken), "the parse never started");
        vm.CancelCommand.Execute(null);
        Assert.False(vm.TryCancel(), "Esc during a parse must cancel it, not close over it");
        Assert.False(vm.Result.IsCompleted);
        release.Set();
        await import;

        Assert.False(ChartEditHarness.Answer(vm.Result));
        Assert.Empty(h.Recorder.Calls);
        Assert.Equal(32, h.Edit.State.ActiveProject!.Beats.Count);
    }

    [Fact]
    public async Task Cancel_at_rest_answers_false_and_imports_nothing()
    {
        var h = new Harness(NewProjectWithFlat120Grid());
        var vm = await h.OpenOnAsync(WriteFixtureGpFile());

        vm.CancelCommand.Execute(null);

        Assert.False(ChartEditHarness.Answer(vm.Result));
        Assert.Empty(h.Recorder.Calls);
    }

    [Fact]
    public async Task Conforms_to_its_contract()
    {
        var h = new Harness(NewProjectWithFlat120Grid());
        Assert.Empty(ContractConformanceTests.Violations(typeof(IGpImportScreen), await h.OpenOnAsync(WriteFixtureGpFile())));
    }
}
