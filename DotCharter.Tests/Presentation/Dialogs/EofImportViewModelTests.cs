using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Import.Eof;
using DotCharter.Services.Parsing.Eof;
using DotCharter.Tests.Services.Import;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     The Editor on Fire import: the launcher reads the track list off the UI
///     thread under a status-bar pill, the dialog parses the ticked tracks off it
///     too and keeps every failure IN the dialog — these used to reach the status
///     bar only, behind the still-open modal — and its answer drives the project
///     creation.
/// </summary>
public sealed class EofImportViewModelTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dotcharter-eofimportvm-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    private static EofImportViewModel Open(string eofPath)
    {
        var vm = new EofImportViewModel(new ExportTargetWriter(), FakeLocalization.English(), new FakeAppLogger());
        vm.Configure(new EofImportRequest(eofPath, new EofImporter().GetAvailableTracks(eofPath)));
        return vm;
    }

    private static Task Import(EofImportViewModel vm) => ((IAsyncRelayCommand)vm.ImportCommand).ExecuteAsync(null);

    [Fact]
    public void It_offers_every_track_with_notes_all_ticked()
    {
        var vm = Open(EofProjectCreatorTests.WriteFixtureEofFile(Path.Combine(_tempDir, "song")));

        Assert.NotEmpty(vm.AvailableTracks);
        Assert.False(vm.HasNoTracks);
        Assert.All(vm.AvailableTracks, t => Assert.True(t.IsSelected));
    }

    /// <summary>#232: the lyrics are a track of their own, counted in lyrics, ticked like the rest.</summary>
    [Fact]
    public void It_offers_the_vocal_track_counted_in_lyrics()
    {
        var vm = Open(EofProjectCreatorTests.WriteFixtureEofFile(Path.Combine(_tempDir, "vocals")));

        var vocals = Assert.Single(vm.AvailableTracks, t => t.Name.EndsWith("lyrics)", StringComparison.Ordinal));
        Assert.Equal("PART VOCALS (2 lyrics)", vocals.Name);
        Assert.True(vocals.IsSelected);
    }

    /// <summary>#595: an instrument track's note count picks the singular or plural resource.</summary>
    [Theory]
    [InlineData("en-US", 1, "PART GUITAR (1 note)")]
    [InlineData("en-US", 3, "PART GUITAR (3 notes)")]
    [InlineData("de-DE", 1, "PART GUITAR (1 Note)")]
    [InlineData("de-DE", 3, "PART GUITAR (3 Noten)")]
    public void It_counts_instrument_notes_in_the_singular_and_plural(string culture, int notes, string expected)
    {
        var vm = new EofImportViewModel(new ExportTargetWriter(), FakeLocalization.For(culture), new FakeAppLogger());
        var track = new EofTrackInfo { Id = 1, Name = "PART GUITAR", Format = EofConstants.TrackFormatProGuitar, NoteCount = notes };

        vm.Configure(new EofImportRequest("song.eof", [track]));

        Assert.Equal(expected, Assert.Single(vm.AvailableTracks).Name);
    }

    /// <summary>A parse failure appears INSIDE the dialog and leaves it open.</summary>
    [Fact]
    public async Task When_the_parse_throws_the_error_shows_in_the_dialog_and_it_stays_open()
    {
        var path = EofProjectCreatorTests.WriteFixtureEofFile(Path.Combine(_tempDir, "throwing"));
        var vm = Open(path);
        File.Delete(path);

        await Import(vm);

        Assert.False(vm.Result.IsCompleted, "a failed import must not close the dialog");
        Assert.False(string.IsNullOrEmpty(vm.ErrorMessage));
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Import_raises_and_clears_IsBusy_and_disables_itself_while_busy()
    {
        var vm = Open(EofProjectCreatorTests.WriteFixtureEofFile(Path.Combine(_tempDir, "busy")));
        var busyStates = new List<bool>();
        var canExecuteWhileBusy = true;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(IEofImportScreen.IsBusy)) return;
            busyStates.Add(vm.IsBusy);
            if (vm.IsBusy) canExecuteWhileBusy = vm.ImportCommand.CanExecute(null);
        };

        // No companion audio beside the fixture, so the run stops right after the parse.
        await Import(vm);

        Assert.Equal([true, false], busyStates);
        Assert.False(canExecuteWhileBusy, "Import must be disabled while the parse is running");
    }

    [Fact]
    public async Task With_no_companion_audio_it_refuses_inside_the_dialog()
    {
        var vm = Open(EofProjectCreatorTests.WriteFixtureEofFile(Path.Combine(_tempDir, "noaudio")));

        await Import(vm);

        Assert.False(vm.Result.IsCompleted);
        Assert.False(string.IsNullOrEmpty(vm.ErrorMessage));
    }

    [Fact]
    public async Task With_audio_beside_it_it_answers_the_parsed_tracks_and_the_audio()
    {
        var songDir = Path.Combine(_tempDir, "withaudio");
        var vm = Open(EofProjectCreatorTests.WriteFixtureEofFile(songDir));
        EofProjectCreatorTests.WriteCompanionAudio(songDir);

        await Import(vm);

        var selection = Assert.IsType<EofImportSelection>(ChartEditHarness.Answer(vm.Result));
        Assert.EndsWith("song.ogg", selection.AudioPath);
        Assert.NotEmpty(selection.Import.Arrangements);
    }

    [Fact]
    public async Task With_nothing_ticked_it_closes_with_no_answer()
    {
        var vm = Open(EofProjectCreatorTests.WriteFixtureEofFile(Path.Combine(_tempDir, "none")));
        foreach (var track in vm.AvailableTracks) track.IsSelected = false;

        await Import(vm);

        Assert.Null(ChartEditHarness.Answer(vm.Result));
    }

    /// <summary>Esc/Cancel while the parse is in flight requests cancellation rather than closing over a live parse.</summary>
    [Fact]
    public void Cancel_while_busy_does_not_close_the_dialog()
    {
        var vm = Open(EofProjectCreatorTests.WriteFixtureEofFile(Path.Combine(_tempDir, "cancel")));

        vm.IsBusy = true;
        Assert.False(vm.TryCancel(), "Esc during a run must request cancellation, not close");
        vm.CancelCommand.Execute(null);
        Assert.False(vm.Result.IsCompleted);

        vm.IsBusy = false;
        vm.CancelCommand.Execute(null);
        Assert.Null(ChartEditHarness.Answer(vm.Result));
    }

    /// <summary>The pre-dialog read is off the UI thread and the dialog is not on screen yet, so the pill is its busy state — cleared on failure too.</summary>
    [Fact]
    public async Task The_launcher_shows_a_status_bar_pill_for_the_read_and_clears_it_either_way()
    {
        var h = new CreatorHarness();
        var statusBar = new RecordingStatusBarService();
        var dialogs = new ScriptedDialogs(_ => new EofImportViewModel(new ExportTargetWriter(),
            FakeLocalization.English(), new FakeAppLogger()), vm => ((EofImportViewModel)vm).TryCancel());
        var launcher = new EofImportLauncher(dialogs, new EofImportFiles(h.Storage, h.Config), statusBar, h.State,
            new DialogBasics(FakeLocalization.English(), new FakeAppLogger()), h.Creator, QuietOffer(h));
        var path = EofProjectCreatorTests.WriteFixtureEofFile(Path.Combine(_tempDir, "pill"));

        await launcher.OpenAsync(path);
        Assert.Equal(["begin:import.eof.read:Reading " + Path.GetFileName(path) + "…", "complete:import.eof.read"],
            statusBar.Log);
        Assert.NotEmpty(Assert.IsType<EofImportViewModel>(Assert.Single(dialogs.Opened)).AvailableTracks);

        statusBar.Log.Clear();
        await launcher.OpenAsync(Path.Combine(_tempDir, "gone.eof"));
        Assert.Equal(["begin:import.eof.read:Reading gone.eof…", "complete:import.eof.read"], statusBar.Log);
        Assert.Single(dialogs.Opened);
        Assert.False(string.IsNullOrEmpty(h.State.MainDisplayMessage), "a failed read has no dialog to live in");
    }

    [Fact]
    public async Task The_launcher_offers_the_tuning_correction_for_the_audio_that_came_with_the_project()
    {
        var h = new CreatorHarness();
        var songDir = Path.Combine(_tempDir, "tuning");
        var path = EofProjectCreatorTests.WriteFixtureEofFile(songDir);
        EofProjectCreatorTests.WriteCompanionAudio(songDir);
        h.Storage.NextFolder = Directory.CreateDirectory(Path.Combine(_tempDir, "out-tuning")).FullName;
        var dialogs = new ScriptedDialogs(_ => new EofImportViewModel(new ExportTargetWriter(),
            FakeLocalization.English(), new FakeAppLogger()), vm => ((EofImportViewModel)vm).ImportCommand.Execute(null));
        var services = new ChartEditDialogServices(h.Editor, new HistoryService(new FakeAppLogger()), h.State, h.Config,
            FakeLocalization.English(), new FakeAppLogger());
        var offer = new ReferencePitchOffer(new FixedPitch(-31), services);
        var launcher = new EofImportLauncher(dialogs, new EofImportFiles(h.Storage, h.Config), new RecordingStatusBarService(), h.State,
            new DialogBasics(FakeLocalization.English(), new FakeAppLogger()), h.Creator, offer);

        await launcher.OpenAsync(path);

        Assert.IsType<StatusMessageAction>(h.State.MainDisplayAction);
    }

    private static ReferencePitchOffer QuietOffer(CreatorHarness h) => new(new DotCharter.Tests.Infrastructure.FakeReferencePitchService(),
        new ChartEditDialogServices(h.Editor, new HistoryService(new FakeAppLogger()), h.State, h.Config,
            FakeLocalization.English(), new FakeAppLogger()));

    private sealed class FixedPitch(double cents) : DotCharter.Services.Audio.Tuning.IReferencePitchService
    {
        public Task<DotCharter.Services.Audio.Tuning.ReferencePitchCheck> MeasureAsync(bool bass, double authoredCentOffset,
            CancellationToken cancellationToken) =>
            Task.FromResult(DotCharter.Services.Audio.Tuning.ReferencePitchCheck.From(
                new DotCharter.Analysis.Tuning.ReferencePitchEstimate(cents, 0.8, 500, 60), authoredCentOffset,
                DotCharter.Services.Editing.Tempo.RecordingAlignment.RecordingAlignmentSource.Mix));
    }

    /// <summary>Picker → read → dialog → Import → project: the whole door, with the dialog's answer creating the project.</summary>
    [Fact]
    public async Task The_launcher_creates_the_project_from_the_dialogs_answer()
    {
        var h = new CreatorHarness();
        var songDir = Path.Combine(_tempDir, "e2e");
        var path = EofProjectCreatorTests.WriteFixtureEofFile(songDir);
        EofProjectCreatorTests.WriteCompanionAudio(songDir);
        h.Storage.NextOpenFile = path;
        h.Storage.NextFolder = Directory.CreateDirectory(Path.Combine(_tempDir, "out-e2e")).FullName;
        var dialogs = new ScriptedDialogs(_ => new EofImportViewModel(new ExportTargetWriter(),
            FakeLocalization.English(), new FakeAppLogger()), vm => ((EofImportViewModel)vm).ImportCommand.Execute(null));
        var launcher = new EofImportLauncher(dialogs, new EofImportFiles(h.Storage, h.Config), new RecordingStatusBarService(), h.State,
            new DialogBasics(FakeLocalization.English(), new FakeAppLogger()), h.Creator, QuietOffer(h));

        await launcher.PickAsync();

        Assert.Single(h.Documents.SavedPaths);
        Assert.Equal(songDir, h.Config.LoadAppConfig().LastEofDir);
        Assert.StartsWith("Created project", h.State.MainDisplayMessage);
    }

    [Fact]
    public void Conforms_to_its_contract()
    {
        var vm = Open(EofProjectCreatorTests.WriteFixtureEofFile(Path.Combine(_tempDir, "contract")));
        // Nothing ticked, so the probe's Import answers at once instead of racing
        // it with a parse that lands on another thread mid-measurement.
        foreach (var track in vm.AvailableTracks) track.IsSelected = false;
        Assert.Empty(ContractConformanceTests.Violations(typeof(IEofImportScreen), vm));
    }
}
