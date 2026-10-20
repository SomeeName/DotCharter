using DotCharter.Presentation.Shared;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Services.Import.Audio;
using DotCharter.Services.Session;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     #710: a separation the dialog started is the APP's run. Closing the dialog does
///     not cancel it — it runs on in the status bar and its stems land in the
///     never-regenerated cache, where the next Chart from audio on the song finds them —
///     and the dialog says so while it runs. Reopened mid-run, the dialog follows that
///     run instead of starting a second one.
/// </summary>
public sealed partial class ChartFromAudioViewModelTests
{
    private const string KeepsRunningKey = "ChartFromAudio_SeparationKeepsRunning";

    [Fact]
    public async Task The_keeps_running_note_shows_only_while_a_separation_runs()
    {
        var (h, vm) = OpenOnTheSongsMix();
        await vm.StemCheck;
        Assert.Equal("", vm.SeparationKeepsRunningNote);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        var run = Separate(vm);
        Assert.Equal(KeepsRunningKey, vm.SeparationKeepsRunningNote);
        Assert.Contains(nameof(vm.SeparationKeepsRunningNote), raised);

        h.Separation.Finish(new SongStemSeparationOutcome(SongStemSeparationStatus.Failed, "GPU unavailable"));
        await run;

        Assert.Equal("", vm.SeparationKeepsRunningNote);
        Assert.True(vm.HasStemHint, "the strip stays, with how the run ended");
    }

    /// <summary>A greyed button, a found stem or a missing file: nothing runs, so there is no note.</summary>
    [Fact]
    public async Task No_note_without_a_running_separation()
    {
        var blocked = new Harness(FakeLocalization.KeysOnly());
        blocked.Separation.Availability = SongStemSeparationAvailability.Blocked("Dialog_StemsNoModel_Message");
        blocked.LoadProject(WritePluckWav("blocked.wav"));
        var vm = blocked.Open();
        await vm.StemCheck;

        Assert.True(vm.HasStemHint);
        Assert.Equal("", vm.SeparationKeepsRunningNote);
    }

    [Fact]
    public async Task Closing_does_not_cancel_the_run_and_it_completes_into_the_cache_after_close()
    {
        var (h, vm) = OpenOnTheSongsMix();
        await vm.StemCheck;
        var looksBefore = h.Stems.Looks.Count;
        var asksBefore = h.Separation.AvailabilityAsked.Count;
        var started = Separate(vm);
        var appRun = Assert.IsType<SongStemSeparationRun>(h.Separation.Running);

        vm.CancelCommand.Execute(null);

        Assert.True(vm.Result.IsCompleted);
        Assert.False(await vm.Result);
        Assert.False(h.Separation.Token.IsCancellationRequested, "closing cancelled the dialog's token");
        Assert.False(appRun.Token.IsCancellationRequested, "closing cancelled the app's run");
        Assert.Same(appRun, h.Separation.Running);

        // The dead dialog hears nothing more: its progress target is closed, so a
        // late report is dropped before anything is posted to the UI thread.
        var target = Assert.IsType<ClosableProgress<double>>(h.Separation.Progress);
        Assert.True(target.IsClosed);
        h.Separation.Report(0.9);
        Assert.Equal(0, vm.SeparationProgress);

        var stem = new ChartFromAudioStem("guitar", WritePluckWav("guitar.wav"));
        h.Separation.OnFinish = () => h.Stems.Stem = stem;
        h.Separation.Finish(new SongStemSeparationOutcome(SongStemSeparationStatus.Separated));
        await started;

        Assert.Equal(SongStemSeparationStatus.Separated, (await appRun.Completion).Status);
        Assert.Equal(looksBefore, h.Stems.Looks.Count);
        Assert.Equal(asksBefore, h.Separation.AvailabilityAsked.Count);
        Assert.Equal("ChartFromAudio_Separating", vm.StemHint);

        // The next Chart from audio on this song finds the stem and separates nothing.
        var next = h.Open();
        await next.StemCheck;
        Assert.False(next.HasStemHint);
        Assert.False(next.IsSeparating);
        Assert.Equal(1, h.Separation.Runs);
    }

    [Fact]
    public async Task Reopening_mid_run_follows_the_running_separation_instead_of_starting_another()
    {
        var (h, first) = OpenOnTheSongsMix();
        await first.StemCheck;
        var started = Separate(first);
        h.Separation.Report(0.25);
        first.CancelCommand.Execute(null);

        var reopened = h.Open();

        Assert.True(reopened.IsSeparating);
        Assert.True(reopened.HasStemHint);
        Assert.Equal("ChartFromAudio_Separating", reopened.StemHint);
        Assert.Equal(KeepsRunningKey, reopened.SeparationKeepsRunningNote);
        Assert.Equal(0.25, reopened.SeparationProgress, 6);
        Assert.False(reopened.CanSeparateStems);
        await Separate(reopened);
        Assert.Equal(1, h.Separation.Runs);

        h.Separation.Report(0.6);
        await SettleAsync(() => Math.Abs(reopened.SeparationProgress - 0.6) < 1e-9, "the followed run's progress");

        var stem = new ChartFromAudioStem("guitar", WritePluckWav("guitar.wav"));
        h.Separation.OnFinish = () => h.Stems.Stem = stem;
        h.Separation.Finish(new SongStemSeparationOutcome(SongStemSeparationStatus.Separated));
        await started;
        await reopened.StemCheck;

        Assert.False(reopened.IsSeparating);
        Assert.False(reopened.HasStemHint);
        Assert.Equal("", reopened.SeparationKeepsRunningNote);
        Assert.Equal(1, h.Separation.Runs);
    }

    /// <summary>The reopened dialog's Cancel separation reaches the run it follows.</summary>
    [Fact]
    public async Task Cancel_separation_on_a_followed_run_cancels_it()
    {
        var (h, first) = OpenOnTheSongsMix();
        await first.StemCheck;
        var started = Separate(first);
        first.CancelCommand.Execute(null);
        var reopened = h.Open();
        Assert.True(reopened.IsSeparating);

        reopened.CancelSeparationCommand.Execute(null);
        await started;
        await reopened.StemCheck;

        Assert.False(reopened.IsSeparating);
        Assert.Equal("ChartFromAudio_SeparationCancelled", reopened.StemHint);
        Assert.True(reopened.CanSeparateStems, "a cancelled run can be started again");
    }

    /// <summary>A run of ANOTHER file is not this dialog's to show: the button is greyed with the separator's reason.</summary>
    [Fact]
    public async Task A_run_of_another_file_is_not_followed()
    {
        var (h, first) = OpenOnTheSongsMix();
        await first.StemCheck;
        h.Separation.SongAudio = Path.Combine(_dir, "another-song.wav");
        var started = Separate(first);
        first.CancelCommand.Execute(null);
        h.Separation.Availability = SongStemSeparationAvailability.Blocked("StemSeparation_AlreadyRunning");

        var reopened = h.Open();
        await reopened.StemCheck;

        Assert.False(reopened.IsSeparating);
        Assert.Equal("StemSeparation_AlreadyRunning", reopened.SeparateStemsBlockedReason);
        h.Separation.Finish(new SongStemSeparationOutcome(SongStemSeparationStatus.Separated));
        await started;
    }

    /// <summary>
    ///     The same story over the app's REAL separation verb, <see cref="StemSeparationViewModel" />,
    ///     with a fake backend and a real <see cref="StemCacheStore" />: no model runs. The run
    ///     outlives the dialog that started it, the reopened dialog follows it, and its stems
    ///     land in the durable cache and on the project.
    /// </summary>
    [Fact]
    public async Task The_apps_own_run_outlives_the_dialog_and_lands_in_the_cache()
    {
        var h = new Harness(FakeLocalization.KeysOnly());
        var mix = WritePluckWav("mix.wav");
        h.LoadProject(mix);
        var cacheRoot = Path.Combine(_dir, "stem-cache");
        var backend = new GatedBackend();
        var ui = new GatedInlineDispatcher();
        var logger = new FakeAppLogger();
        using var mixer = new StemMixerViewModel(h.State, new FakeAudioPlaybackService(), FakeLocalization.KeysOnly(), logger, ui);
        var app = new StemSeparationViewModel(h.State, RecordingProxy.Create<IProjectSessionService>().Proxy, mixer,
            RecordingProxy.Create<IAppDialogService>().Proxy, ui, logger, FakeLocalization.KeysOnly(), backend,
            stemCache: new StemCacheStore(new CopyStemEncoder(), logger, cacheRoot));

        var first = h.Open(app);
        await first.StemCheck;
        Assert.True(first.CanSeparateStems);
        var started = Separate(first);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        first.CancelCommand.Execute(null);

        Assert.False(backend.Token.IsCancellationRequested, "closing the dialog cancelled the app's separation");
        var appRun = Assert.IsType<SongStemSeparationRun>(app.Running);
        Assert.True(appRun.Separates(mix));

        var reopened = h.Open(app);
        Assert.True(reopened.IsSeparating, "the reopened dialog did not follow the running separation");
        Assert.Equal(KeepsRunningKey, reopened.SeparationKeepsRunningNote);
        await Separate(reopened);
        Assert.Equal(1, backend.Calls);

        backend.Progress!.Report(0.5);
        await SettleAsync(() => Math.Abs(reopened.SeparationProgress - 0.5) < 1e-9, "the app run's progress");
        Assert.Equal(0, first.SeparationProgress);

        h.Stems.Stem = new ChartFromAudioStem("guitar", WritePluckWav("guitar.wav"));
        backend.Release();
        await started;
        await reopened.StemCheck;

        Assert.Null(app.Running);
        Assert.Equal(SongStemSeparationStatus.Separated, (await appRun.Completion).Status);
        Assert.Single(Directory.EnumerateFiles(cacheRoot, "*" + StemArchive.ArchiveExtension));
        string[] separated = ["bass", "guitar"];
        Assert.Equal(separated, ui.Read(() => h.State.ActiveProject!.Stems.Select(s => s.Name).Order().ToArray()));
        Assert.False(reopened.IsSeparating);
        Assert.False(reopened.HasStemHint);
        Assert.Equal(1, backend.Calls);
    }

    private static async Task SettleAsync(Func<bool> done, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!done() && DateTime.UtcNow < deadline) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(done(), $"{what} never arrived within the 60 s deadlock cap (box starved?)");
    }

    /// <summary>A separation backend that waits for <see cref="Release" />, then writes two stems.</summary>
    private sealed class GatedBackend : IStemSeparationService
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls => Volatile.Read(ref _calls);

        public CancellationToken Token { get; private set; }

        public IProgress<double>? Progress { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<string> DescribeBackendAsync(CancellationToken ct = default) => Task.FromResult("fake backend");

        public async Task<StemSeparationResult> SeparateAsync(string inputAudioPath, string outputDirectory,
            IProgress<double>? progress = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            Token = ct;
            Progress = progress;
            Started.TrySetResult();
            await _release.Task.WaitAsync(ct);
            Directory.CreateDirectory(outputDirectory);
            var stems = new[] { "guitar", "bass" }.Select(name =>
            {
                var path = Path.Combine(outputDirectory, name + ".wav");
                File.WriteAllBytes(path, Enumerable.Repeat((byte)7, 256).ToArray());
                return new StemTrack { Name = name, Path = path };
            }).ToList();
            return new StemSeparationResult { Success = true, OutputDirectory = outputDirectory, Stems = stems };
        }

        public void Release() => _release.TrySetResult();
    }

    /// <summary>"Encodes" by copying, so the cache writes an archive without ffmpeg.</summary>
    private sealed class CopyStemEncoder : IStemEncoder
    {
        public StemCodec PreferredCodec => StemCodec.Wav;

        public bool IsExternalEncoderAvailable => false;

        public Task<string> EncodeAsync(string wavPath, string destNoExt, StemCodec target, CancellationToken ct = default)
        {
            var dest = destNoExt + "." + target.FileExtension();
            File.Copy(wavPath, dest, overwrite: true);
            return Task.FromResult(dest);
        }

        public Task<string> DecodeToWavAsync(string encodedPath, StemCodec codec, string destWavPath,
            CancellationToken ct = default)
        {
            File.Copy(encodedPath, destWavPath, overwrite: true);
            return Task.FromResult(destWavPath);
        }
    }
}
