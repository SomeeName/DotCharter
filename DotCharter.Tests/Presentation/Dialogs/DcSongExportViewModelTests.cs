using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Editing;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Package.DcSong;
using DotCharter.Tone.Infrastructure.Shell;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     File ▸ Export ▸ DotCharter song (#707): what the dialog offers, what greys Export and
///     says why, and the request its choices become. The exporter is scripted
///     (<see cref="FakeDcSongExporter" />), so nothing here needs ffmpeg or writes a package.
/// </summary>
public sealed class DcSongExportViewModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter-dcsongvm-").FullName;
    private readonly FakeLocalization _loc = FakeLocalization.English();

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void Opens_on_Opus_with_every_arrangement_ticked_stems_offered_but_off_and_the_file_in_the_Library()
    {
        var h = new Harness(_dir);
        h.Exporter.Probe = h.Exporter.Probe with { Stems = [Stem("drums"), Stem("vocals")] };

        var vm = h.Open();

        Assert.Equal(0, vm.AudioQualityIndex);
        Assert.False(vm.IsLossless);
        Assert.Equal(["Lead", "Rhythm", "Bass"], vm.Arrangements.Select(a => a.Name));
        Assert.All(vm.Arrangements, a => Assert.True(a.IsSelected));
        Assert.True(vm.CanIncludeStems);
        Assert.False(vm.IncludeStems);
        Assert.Contains("drums, vocals", vm.StemsNote, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(h.Library, "Polkadot Stingray - Sakasama.dcsong"), vm.OutputPath);
        Assert.Equal(_loc["Dialog_DcSong_Output_InLibraryDesc"], vm.OutputNote);
        Assert.Equal("Polkadot Stingray — Sakasama", vm.SongLine);
        Assert.False(vm.IsExportBlocked);
        Assert.True(vm.ExportCommand.CanExecute(null));
        // The probe was asked about the project's own stems and its audio, resolved against the project folder.
        var probe = Assert.Single(h.Exporter.ProbeRequests);
        Assert.Equal(Path.Combine(h.ProjectDir, "song.wav"), probe.AudioPath);
        Assert.Equal(h.ProjectDir, probe.ProjectDirectory);
    }

    [Fact]
    public void Without_ffmpeg_Export_is_grey_and_says_why_and_with_it_the_same_dialog_is_live()
    {
        var missing = new Harness(_dir);
        missing.Exporter.Probe = missing.Exporter.Probe with { EncoderAvailable = false };
        var vm = missing.Open();

        Assert.False(vm.ExportCommand.CanExecute(null));
        Assert.True(vm.IsExportBlocked);
        Assert.Equal(_loc["Dialog_DcSong_FfmpegMissing"], vm.ExportBlockedReason);

        // The control: the same chart with ffmpeg present is not blocked at all.
        var present = new Harness(_dir).Open();
        Assert.True(present.ExportCommand.CanExecute(null));
        Assert.Equal("", present.ExportBlockedReason);
    }

    [Fact]
    public async Task Export_waits_for_the_check_and_says_so_while_it_runs()
    {
        var h = new Harness(_dir);
        h.Exporter.ProbeGate = new TaskCompletionSource<DcSongExportProbe>();
        var vm = h.Open();

        Assert.False(vm.ExportCommand.CanExecute(null));
        Assert.Equal(_loc["Dialog_DcSong_Checking"], vm.ExportBlockedReason);
        Assert.False(vm.CanIncludeStems);

        h.Exporter.ProbeGate.SetResult(h.Exporter.Probe);
        await vm.ProbeTask;

        Assert.True(vm.ExportCommand.CanExecute(null));
    }

    [Fact]
    public void No_separated_stems_means_the_toggle_cannot_turn_on_and_the_note_says_how_to_make_them()
    {
        var vm = new Harness(_dir).Open();

        Assert.False(vm.CanIncludeStems);
        vm.IncludeStems = true;
        Assert.False(vm.IncludeStems);
        Assert.Equal(_loc["Dialog_DcSong_Stems_NoneDesc"], vm.StemsNote);
    }

    [Fact]
    public void Every_failing_clause_is_printed_once_in_order()
    {
        var h = new Harness(_dir);
        h.Exporter.Probe = h.Exporter.Probe with { EncoderAvailable = false, AudioExists = false };
        var vm = h.Open();
        foreach (var row in vm.Arrangements) row.IsSelected = false;
        vm.OutputPath = "  ";

        Assert.Equal(
            string.Join("\n", _loc["Dialog_DcSong_FfmpegMissing"], _loc["Dialog_DcSong_AudioMissing"],
                _loc["Dialog_DcSong_NothingSelected"], _loc["Dialog_DcSong_NoOutput"]),
            vm.ExportBlockedReason);
    }

    [Fact]
    public void The_estimate_follows_the_audio_choice_and_the_stems()
    {
        var h = new Harness(_dir);
        var stems = new[] { Stem("drums"), Stem("vocals") };
        h.Exporter.Probe = h.Exporter.Probe with { Stems = stems };
        var vm = h.Open();
        var audio = Path.Combine(h.ProjectDir, "song.wav");

        Assert.Equal(Mb(DcSongSizeEstimate.Bytes(180_000, false, audio, h.Exporter.Probe.AudioBytes, [])), vm.EstimatedSize);
        vm.AudioQualityIndex = 1;
        Assert.Equal(Mb(DcSongSizeEstimate.Bytes(180_000, true, audio, h.Exporter.Probe.AudioBytes, [])), vm.EstimatedSize);
        vm.IncludeStems = true;
        Assert.Equal(Mb(DcSongSizeEstimate.Bytes(180_000, true, audio, h.Exporter.Probe.AudioBytes, stems)), vm.EstimatedSize);
        Assert.Equal(_loc["Dialog_DcSong_Quality_FlacDesc"], vm.AudioQualityNote);
    }

    [Fact]
    public async Task Export_writes_what_was_chosen_and_says_where_it_went()
    {
        var h = new Harness(_dir);
        h.Exporter.Probe = h.Exporter.Probe with { Stems = [Stem("drums"), Stem("vocals")] };
        h.Exporter.Write = (request, _, _) => Task.FromResult(
            FakeDcSongExporter.Written(request, 27_600_000, DcSongWriteWarning.LossySourceInLosslessPackage));
        var vm = h.Open();
        vm.Arrangements[1].IsSelected = false;
        vm.AudioQualityIndex = 1;
        vm.IncludeStems = true;
        vm.BannerPath = @"D:\Art\banner.jpg";
        vm.OutputPath = Path.Combine(h.Library, "mine");

        await vm.ExportCommand.ExecuteAsync(null);

        var request = Assert.Single(h.Exporter.WriteRequests);
        Assert.Same(h.State.ActiveProject, request.Project);
        Assert.Equal([0, 2], request.ArrangementIndexes);
        Assert.True(request.Lossless);
        Assert.Equal(["drums", "vocals"], request.Stems.Select(s => s.Name));
        Assert.Null(request.CoverPath);
        Assert.Equal(@"D:\Art\banner.jpg", request.BannerPath);
        Assert.Equal(Path.Combine(h.Library, "mine.dcsong"), request.OutputPath);
        Assert.Equal(h.ProjectDir, request.ProjectDirectory);

        Assert.True(vm.HasResult);
        Assert.False(vm.IsWriting);
        Assert.Contains(request.OutputPath, vm.ResultMessage, StringComparison.Ordinal);
        Assert.Equal([_loc["DcSong_WriteWarning_LossySourceInLosslessPackage"]], vm.ResultWarnings);
        Assert.Equal(_loc.Format("Status_DcSong_Saved", request.OutputPath), h.State.MainDisplayMessage);

        Assert.True(vm.TryCancel());
        Assert.True(await vm.Result);
    }

    [Fact]
    public async Task A_refused_write_says_why_inside_the_dialog_and_leaves_it_open()
    {
        var h = new Harness(_dir);
        h.Exporter.Write = (_, _, _) => throw new DcSongWriteException(DcSongWriteFailure.ImageUnsupported, "not an image");
        var vm = h.Open();
        vm.CoverPath = @"D:\Art\cover.webp";

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Equal(_loc["DcSong_WriteFailure_ImageUnsupported"], vm.ErrorMessage);
        Assert.False(vm.HasResult);
        Assert.False(vm.Result.IsCompleted);
        Assert.Equal(@"D:\Art\cover.webp", h.Exporter.WriteRequests[0].CoverPath);
    }

    [Fact]
    public async Task Close_while_writing_cancels_the_write_and_keeps_the_dialog_until_it_unwinds()
    {
        var h = new Harness(_dir);
        var started = new TaskCompletionSource();
        CancellationToken seen = default;
        h.Exporter.Write = async (_, progress, ct) =>
        {
            seen = ct;
            progress?.Report(new DcSongWriteProgress(2, 5, DcSongWriteStage.EncodingAudio));
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        };
        var vm = h.Open();

        var run = vm.ExportCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.True(vm.IsWriting);
        Assert.False(vm.IsExportBlocked);
        Assert.False(vm.ExportCommand.CanExecute(null));
        Assert.True(vm.CancelWriteCommand.CanExecute(null));
        Assert.False(vm.TryCancel());
        Assert.True(seen.IsCancellationRequested);

        await run.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.False(vm.IsWriting);
        Assert.False(vm.HasResult);
        Assert.False(vm.HasError);
        Assert.False(vm.Result.IsCompleted);
        Assert.Equal(_loc["Dialog_DcSong_Canceled"], h.State.MainDisplayMessage);
        Assert.Equal("", vm.ProgressMessage);
    }

    [Fact]
    public async Task Progress_names_the_stage_in_the_users_language()
    {
        var h = new Harness(_dir);
        var gate = new TaskCompletionSource<DcSongWriteResult>();
        h.Exporter.Write = (request, progress, _) =>
        {
            progress?.Report(new DcSongWriteProgress(2, 5, DcSongWriteStage.EncodingAudio));
            return gate.Task;
        };
        var vm = h.Open();
        var shown = new TaskCompletionSource<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.ProgressMessage) && vm.ProgressMessage.Length > 0) shown.TrySetResult(vm.ProgressMessage);
        };

        var run = vm.ExportCommand.ExecuteAsync(null);
        var message = await shown.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal(_loc.Format("Dialog_Export_ProgressStep", 2, 5, _loc["DcSong_WriteStage_EncodingAudio"]), message);
        gate.SetResult(FakeDcSongExporter.Written(h.Exporter.WriteRequests[0], 1));
        await run.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.True(vm.HasResult);
    }

    [Fact]
    public void Without_a_Library_folder_the_package_goes_to_the_export_folder_and_the_note_says_so()
    {
        var h = new Harness(_dir);
        h.Config.AppConfig.PsarcLibraryPath = "";
        h.Config.AppConfig.LastExportDir = h.ProjectDir;

        var vm = h.Open();

        Assert.Equal(Path.Combine(h.ProjectDir, "Polkadot Stingray - Sakasama.dcsong"), vm.OutputPath);
        Assert.Equal(_loc["Dialog_DcSong_Output_NoLibraryDesc"], vm.OutputNote);
    }

    [Fact]
    public void A_file_moved_out_of_the_Library_says_so()
    {
        var h = new Harness(_dir);
        var vm = h.Open();

        vm.OutputPath = Path.Combine(h.ProjectDir, "elsewhere.dcsong");

        Assert.Equal(_loc["Dialog_DcSong_Output_OutsideLibraryDesc"], vm.OutputNote);
    }

    [Theory]
    [InlineData("AC/DC", "Back: in Black?", "AC_DC - Back_ in Black_.dcsong")]
    [InlineData("", "", "song.dcsong")]
    [InlineData("  ", "Title", "Title.dcsong")]
    public void The_default_file_name_is_artist_dash_title_and_a_legal_name(string artist, string title, string expected) =>
        Assert.Equal(expected, DcSongExportViewModel.DefaultFileName(artist, title));

    [Fact]
    public void Conforms_to_its_contract() => Assert.IsAssignableFrom<IDcSongExportScreen>(new Harness(_dir).Open());

    private DcSongStemCandidate Stem(string name) => new(name, Path.Combine(_dir, name + ".wav"), 30_000_000);

    private string Mb(long bytes) =>
        _loc.Format("Dialog_DcSong_SizeMeta",
            _loc.Format("Dialog_DcSong_SizeValue", (bytes / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture)));

    private sealed class Harness
    {
        public Harness(string root)
        {
            ProjectDir = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
            Library = Directory.CreateDirectory(Path.Combine(root, "library")).FullName;
            Config.AppConfig.PsarcLibraryPath = Library;
            var project = new ChartProject();
            project.Chart.Artist = "Polkadot Stingray";
            project.Chart.SongName = "Sakasama";
            foreach (var (name, type) in new[] { ("Lead", ArrangementType.Lead), ("Rhythm", ArrangementType.Rhythm), ("Bass", ArrangementType.Bass) })
                project.Arrangements.Add(new Arrangement { Name = name, ArrangementType = type });
            State.ActiveProject = project;
            State.CurrentProjectPath = Path.Combine(ProjectDir, "song.rscp");
            State.CurrentAudioFileName = "song.wav";
            State.TotalAudioLengthMs = 180_000;
        }

        public string ProjectDir { get; }
        public string Library { get; }
        public ProjectState State { get; } = new();
        public FakeConfigurationService Config { get; } = new();
        public FakeDcSongExporter Exporter { get; } = new();

        public DcSongExportViewModel Open()
        {
            var logger = new FakeAppLogger();
            var edit = new ChartEditDialogServices(RecordingProxy.Create<IChartEditorSurface>().Proxy,
                new HistoryService(logger), State, Config, FakeLocalization.English(), logger);
            var io = new DcSongExportServices(RecordingProxy.Create<IEditorTransport>().Proxy,
                RecordingProxy.Create<IProjectDocumentService>().Proxy, new FakeFileStorageService(), new ExportTargetWriter(),
                RecordingProxy.Create<IShellLauncher>().Proxy, Exporter);
            return new DcSongExportViewModel(edit, io);
        }
    }
}
