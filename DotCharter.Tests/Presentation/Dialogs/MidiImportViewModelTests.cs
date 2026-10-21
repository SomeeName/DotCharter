using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Parsing.Midi;
using DotCharter.Tests.Services.Parsing.Midi;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     The MIDI dialog's conversion contract. The converter used to run on the
///     UI thread from three call sites with no busy flag, and its catch returned
///     null — rendered as "no note in this selection maps onto the arrangement's
///     tuning", a claim about the music printed because the code had crashed.
/// </summary>
public sealed class MidiImportViewModelTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dotcharter-midiimportvm-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    private sealed class Harness
    {
        public Harness(Func<MidiFileData, MidiDraftOptions, MidiChartDraft>? converter = null)
        {
            var project = new ChartProject { Bpm = 120 };
            var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = 6 };
            arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
            project.Arrangements.Add(arrangement);
            State.ActiveProject = project;
            State.ActiveArrangement = arrangement;
            (Maintenance, Recorder) = RecordingProxy.Create<IChartMaintenanceService>();
            Vm = new MidiImportViewModel(Maintenance, State, FakeLocalization.English(), new FakeAppLogger(), converter);
        }

        public ProjectState State { get; } = new();
        public IChartMaintenanceService Maintenance { get; }
        public RecordingProxy Recorder { get; }
        public MidiImportViewModel Vm { get; }

        public List<(MidiChartDraft Draft, bool ReplaceBeatGrid)> Applied =>
        [
            .. Recorder.Invocations.Where(i => i.Method == nameof(IChartMaintenanceService.ApplyMidiDraft))
                .Select(i => ((MidiChartDraft)i.Args[0]!, (bool)i.Args[1]!))
        ];

        public async Task OpenAsync(string path)
        {
            Vm.Configure(new MidiImportRequest(Path.GetFileName(path), MidiFileParser.Parse(File.ReadAllBytes(path))));
            await Vm.DraftRefresh;
        }
    }

    /// <summary>Two named tracks, each one note, on a 480-PPQ 4/4 grid.</summary>
    private string WriteFixtureMidiFile()
    {
        var trackA = MidiTestBytes.Track(
            MidiTestBytes.TrackName(0, "Guitar"),
            MidiTestBytes.SetTempo(0, 500_000),
            MidiTestBytes.NoteOn(0, 0, 40, 100),
            MidiTestBytes.NoteOff(480, 0, 40),
            MidiTestBytes.EndOfTrack());
        var trackB = MidiTestBytes.Track(
            MidiTestBytes.TrackName(0, "Bass"),
            MidiTestBytes.NoteOn(0, 1, 45, 100),
            MidiTestBytes.NoteOff(480, 1, 45),
            MidiTestBytes.EndOfTrack());

        var path = Path.Combine(_tempDir, $"fixture-{Guid.NewGuid():N}.mid");
        File.WriteAllBytes(path, MidiTestBytes.SmfFile(1, 480, trackA, trackB));
        return path;
    }

    private static Task Import(MidiImportViewModel vm) => ((IAsyncRelayCommand)vm.ImportCommand).ExecuteAsync(null);

    [Fact]
    public async Task Opening_raises_and_clears_IsBusy_and_lists_all_tracks_first()
    {
        var h = new Harness();
        var busyStates = new List<bool>();
        h.Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IMidiImportScreen.IsBusy)) busyStates.Add(h.Vm.IsBusy);
        };

        await h.OpenAsync(WriteFixtureMidiFile());

        Assert.Contains(true, busyStates);
        Assert.False(h.Vm.IsBusy);
        Assert.Equal(3, h.Vm.Tracks.Count);
        Assert.Equal(0, h.Vm.SelectedTrackIndex);
        Assert.True(h.Vm.ReplaceBeatGrid, "an uncharted grid pre-ticks the replacement");
    }

    /// <summary>A converter that throws is an ERROR, and never the honest "nothing chartable here" summary.</summary>
    [Fact]
    public async Task When_the_converter_throws_the_dialog_says_so_and_does_not_claim_the_track_is_empty()
    {
        var h = new Harness((_, _) => throw new InvalidOperationException("converter blew up"));

        await h.OpenAsync(WriteFixtureMidiFile());

        Assert.Contains("converter blew up", h.Vm.ErrorMessage);
        Assert.Equal("", h.Vm.Summary);
        Assert.False(h.Vm.HasDraft);
        Assert.False(h.Vm.ImportCommand.CanExecute(null), "a crashed conversion must not leave Import pressable");
    }

    /// <summary>
    ///     The draft maps pitches onto the arrangement's open strings, read the way the app
    ///     sounds them (<see cref="SoundingPitch.OpenStringMidi" />): a 5-string bass is
    ///     E A D G B, so a standard B E A D G bass is <c>-5 -5 -5 -5 -4</c>. The dialog used to
    ///     measure 5 and 6 bass strings against B E A D G (C) and put every pitch a fourth low.
    /// </summary>
    [Theory]
    [InlineData(4, new[] { 0, 0, 0, 0 }, new[] { 28, 33, 38, 43 })]
    [InlineData(5, new[] { -5, -5, -5, -5, -4 }, new[] { 23, 28, 33, 38, 43 })]
    [InlineData(6, new[] { -5, -5, -5, -5, -4, -4 }, new[] { 23, 28, 33, 38, 43, 48 })]
    public async Task A_bass_draft_maps_onto_the_open_strings_the_app_sounds(int strings, int[] offsets, int[] expected)
    {
        IReadOnlyList<int>? open = null;
        var h = new Harness((_, options) =>
        {
            open = options.OpenStringMidi;
            return new MidiChartDraft([], []);
        });
        var bass = h.State.ActiveArrangement!;
        bass.ArrangementType = ArrangementType.Bass;
        bass.StringCount = strings;
        foreach (var offset in offsets) bass.TuningPitches.Add(new TuningOffset(offset));

        await h.OpenAsync(WriteFixtureMidiFile());

        Assert.Equal(expected, open);
        Assert.Equal(Enumerable.Range(0, strings).Select(i => SoundingPitch.OpenStringMidi(bass, i)!.Value), open);
    }

    [Fact]
    public async Task An_honestly_empty_conversion_explains_itself_and_shows_no_error()
    {
        var h = new Harness((_, _) => new MidiChartDraft([], []));

        await h.OpenAsync(WriteFixtureMidiFile());

        Assert.Equal("", h.Vm.ErrorMessage);
        Assert.False(string.IsNullOrEmpty(h.Vm.Summary));
        Assert.False(h.Vm.HasDraft);
    }

    /// <summary>Opening converts once; re-asserting the same inputs does not; Import reuses the preview's draft.</summary>
    [Fact]
    public async Task The_converter_runs_once_per_distinct_selection_and_Import_reuses_the_preview()
    {
        var calls = 0;
        var h = new Harness((file, options) =>
        {
            calls++;
            return MidiToDraftConverter.Convert(file, options);
        });

        await h.OpenAsync(WriteFixtureMidiFile());
        Assert.Equal(1, calls);

        h.Vm.SelectedTrackIndex = h.Vm.SelectedTrackIndex;
        await h.Vm.DraftRefresh;
        Assert.Equal(1, calls);

        h.Vm.IncludeDrumChannel = true;
        await h.Vm.DraftRefresh;
        Assert.Equal(2, calls);

        await Import(h.Vm);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Import_hands_the_draft_and_the_beat_grid_choice_to_chart_maintenance()
    {
        var h = new Harness();
        await h.OpenAsync(WriteFixtureMidiFile());
        Assert.True(h.Vm.HasDraft);
        h.Vm.ReplaceBeatGrid = true;

        await Import(h.Vm);

        var applied = Assert.Single(h.Applied);
        Assert.True(applied.ReplaceBeatGrid);
        Assert.NotEmpty(applied.Draft.Notes);
        Assert.True(ChartEditHarness.Answer(h.Vm.Result));
    }

    [Fact]
    public async Task Import_after_a_converter_crash_applies_nothing()
    {
        var h = new Harness((_, _) => throw new InvalidOperationException("converter blew up"));
        await h.OpenAsync(WriteFixtureMidiFile());

        await Import(h.Vm);

        Assert.Empty(h.Applied);
        Assert.False(h.Vm.Result.IsCompleted);
    }

    [Fact]
    public async Task Cancel_always_closes_even_mid_conversion()
    {
        var gate = new ManualResetEventSlim();
        var h = new Harness((file, options) =>
        {
            gate.Wait(TimeSpan.FromSeconds(60));
            return MidiToDraftConverter.Convert(file, options);
        });
        h.Vm.Configure(new MidiImportRequest("x.mid", MidiFileParser.Parse(File.ReadAllBytes(WriteFixtureMidiFile()))));
        Assert.True(h.Vm.IsBusy);

        Assert.True(h.Vm.TryCancel());
        Assert.False(h.Vm.IsBusy);
        Assert.False(ChartEditHarness.Answer(h.Vm.Result));

        gate.Set();
        await h.Vm.DraftRefresh;
        Assert.Equal("", h.Vm.Summary);
    }

    /// <summary>The pre-dialog parse shows as a status-bar pill, cleared on failure too, and a failure opens nothing.</summary>
    [Fact]
    public async Task The_launcher_shows_a_pill_for_the_parse_and_clears_it_either_way()
    {
        var h = new Harness();
        var statusBar = new RecordingStatusBarService();
        var dialogs = new ScriptedDialogs(_ => new MidiImportViewModel(h.Maintenance, h.State,
            FakeLocalization.English(), new FakeAppLogger()), vm => ((MidiImportViewModel)vm).TryCancel());
        var launcher = new MidiImportLauncher(dialogs, new FakeFileStorageService(), new FakeConfigurationService(),
            statusBar, h.State, new ExportTargetWriter(), new DialogBasics(FakeLocalization.English(), new FakeAppLogger()));
        var path = WriteFixtureMidiFile();

        await launcher.OpenAsync(path);
        Assert.Equal(["begin:import.midi.read:Reading " + Path.GetFileName(path) + "…", "complete:import.midi.read"],
            statusBar.Log);
        Assert.Equal(3, Assert.IsType<MidiImportViewModel>(Assert.Single(dialogs.Opened)).Tracks.Count);

        statusBar.Log.Clear();
        await launcher.OpenAsync(Path.Combine(_tempDir, "gone.mid"));
        Assert.Equal(["begin:import.midi.read:Reading gone.mid…", "complete:import.midi.read"], statusBar.Log);
        Assert.Single(dialogs.Opened);
    }

    /// <summary>A MIDI draft is added to the open project; with none, the launcher says so and opens nothing.</summary>
    [Fact]
    public async Task The_launcher_refuses_without_a_project()
    {
        var state = new ProjectState();
        var dialogs = new ScriptedDialogs(_ => throw new InvalidOperationException("no dialog should open"));
        var launcher = new MidiImportLauncher(dialogs, new FakeFileStorageService(), new FakeConfigurationService(),
            new RecordingStatusBarService(), state, new ExportTargetWriter(), new DialogBasics(FakeLocalization.English(), new FakeAppLogger()));

        await launcher.OpenAsync(WriteFixtureMidiFile());

        Assert.Empty(dialogs.Opened);
        Assert.False(string.IsNullOrEmpty(state.MainDisplayMessage));
    }

    [Fact]
    public async Task Conforms_to_its_contract()
    {
        var h = new Harness();
        await h.OpenAsync(WriteFixtureMidiFile());
        Assert.Empty(ContractConformanceTests.Violations(typeof(IMidiImportScreen), h.Vm));
    }
}
