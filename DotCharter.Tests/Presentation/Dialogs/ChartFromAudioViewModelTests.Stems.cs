using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Import.Audio;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     The stem strip (#641): with no separated stem of the arrangement's instrument,
///     the dialog says the full mix is analysed and offers Separate stems, which runs
///     the app's own separation and then looks again. Nothing separates on its own.
///     The locator and the separator are fakes; the analyzer is the real one, over
///     the same locator, so "the next analysis uses the stem" is the product's path.
/// </summary>
public sealed partial class ChartFromAudioViewModelTests
{
    /// <summary>Answers <see cref="Stem" />; records every look, from any thread.</summary>
    private sealed class FakeStemLocator : IChartFromAudioStemLocator
    {
        public volatile ChartFromAudioStem? Stem;

        public ConcurrentQueue<string> Looks { get; } = new();

        public ChartFromAudioStem? TryLocate(string mixWavPath, ArrangementType? arrangementType)
        {
            Looks.Enqueue(mixWavPath);
            return Stem;
        }
    }

    /// <summary>
    ///     A separation the test finishes by hand: <see cref="Finish" />, or a cancel through
    ///     the caller's token or the published run. Like the app's, it publishes
    ///     <see cref="Running" /> for the song's audio while it runs and clears it before the
    ///     run completes.
    /// </summary>
    private sealed class FakeSongStemSeparation : ISongStemSeparation
    {
        private TaskCompletionSource<SongStemSeparationOutcome> _run = NewRun();
        private volatile SongStemSeparationRun? _running;

        public SongStemSeparationAvailability Availability { get; set; } = SongStemSeparationAvailability.Ready;

        public ConcurrentQueue<string> AvailabilityAsked { get; } = new();

        /// <summary>The audio a run separates: the song's own, which <see cref="Harness.LoadProject" /> sets.</summary>
        public string SongAudio { get; set; } = "";

        public int Runs { get; private set; }

        public IProgress<double>? Progress { get; private set; }

        public CancellationToken Token { get; private set; }

        public Action? OnFinish { get; set; }

        public SongStemSeparationRun? Running => _running;

        public Task<SongStemSeparationAvailability> GetAvailabilityAsync(string audioPath, CancellationToken ct = default)
        {
            AvailabilityAsked.Enqueue(audioPath);
            return Task.FromResult(Availability);
        }

        public Task<SongStemSeparationOutcome> SeparateAsync(IProgress<double>? progress, CancellationToken ct)
        {
            Runs++;
            Progress = progress;
            Token = ct;
            _run = NewRun();
            var task = _run;
            var run = new SongStemSeparationRun(SongAudio, ct);
            _running = run;
            run.Token.Register(() =>
            {
                _running = null;
                run.Complete(new SongStemSeparationOutcome(SongStemSeparationStatus.Cancelled));
                task.TrySetCanceled(run.Token);
            });
            return task.Task;
        }

        /// <summary>The backend's progress, as the app's run reports it: to the starter and to the published run.</summary>
        public void Report(double value)
        {
            Progress?.Report(value);
            _running?.Report(value);
        }

        public void Finish(SongStemSeparationOutcome outcome)
        {
            OnFinish?.Invoke();
            var run = _running;
            _running = null;
            _run.SetResult(outcome);
            run?.Complete(outcome);
        }

        public void Throw(Exception ex)
        {
            var run = _running;
            _running = null;
            _run.SetException(ex);
            run?.Complete(new SongStemSeparationOutcome(SongStemSeparationStatus.Failed, ex.Message));
        }

        private static TaskCompletionSource<SongStemSeparationOutcome> NewRun() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private (Harness H, ChartFromAudioViewModel Vm) OpenOnTheSongsMix()
    {
        var h = new Harness(FakeLocalization.KeysOnly());
        h.LoadProject(WritePluckWav("mix.wav"));
        return (h, h.Open());
    }

    private static Task Separate(ChartFromAudioViewModel vm) => ((IAsyncRelayCommand)vm.SeparateStemsCommand).ExecuteAsync(null);

    [Fact]
    public async Task With_no_stem_the_strip_says_the_full_mix_is_analysed_and_offers_Separate_stems()
    {
        var (h, vm) = OpenOnTheSongsMix();
        await vm.StemCheck;

        Assert.True(vm.HasStemHint);
        Assert.Equal("ChartFromAudio_StemHint", vm.StemHint);
        Assert.True(vm.CanSeparateStems);
        Assert.Equal("", vm.SeparateStemsBlockedReason);
        Assert.True(vm.SeparateStemsCommand.CanExecute(null));
        Assert.Equal(vm.AudioPath, Assert.Single(h.Separation.AvailabilityAsked));
        Assert.Equal(0, h.Separation.Runs);
    }

    [Fact]
    public async Task A_stem_that_already_exists_shows_no_strip_and_asks_nothing()
    {
        var h = new Harness(FakeLocalization.KeysOnly());
        h.Stems.Stem = new ChartFromAudioStem("guitar", WritePluckWav("guitar.wav"));
        h.LoadProject(WritePluckWav("mix.wav"));
        var vm = h.Open();
        await vm.StemCheck;

        Assert.False(vm.HasStemHint);
        Assert.Equal("", vm.StemHint);
        Assert.False(vm.CanSeparateStems);
        Assert.Empty(h.Separation.AvailabilityAsked);
    }

    /// <summary>A vocal arrangement has no stem to listen to, so there is nothing to offer.</summary>
    [Fact]
    public async Task A_vocal_arrangement_is_offered_no_separation()
    {
        var h = new Harness(FakeLocalization.KeysOnly());
        h.LoadProject(WritePluckWav("mix.wav"));
        h.State.ActiveArrangement!.ArrangementType = ArrangementType.Vocal;
        var vm = h.Open();
        await vm.StemCheck;

        Assert.False(vm.HasStemHint);
        Assert.Empty(h.Separation.AvailabilityAsked);
    }

    /// <summary>The issue's whole loop: mix draft, separate, the hint goes, the next draft is from the stem.</summary>
    [Fact]
    public async Task After_the_separator_completes_the_hint_goes_and_the_next_analysis_uses_the_stem()
    {
        var (h, vm) = OpenOnTheSongsMix();
        await vm.StemCheck;
        await Analyze(vm);
        Assert.StartsWith("ChartFromAudio_Summary(", vm.DraftSummary, StringComparison.Ordinal);

        var stem = new ChartFromAudioStem("guitar", WritePluckWav("guitar.wav"));
        h.Separation.OnFinish = () => h.Stems.Stem = stem;
        var run = Separate(vm);
        Assert.True(vm.IsSeparating);
        Assert.True(vm.HasStemHint);
        Assert.False(vm.CanSeparateStems);
        Assert.Equal("ChartFromAudio_Separating", vm.StemHint);

        h.Separation.Finish(new SongStemSeparationOutcome(SongStemSeparationStatus.Separated));
        await run;

        Assert.False(vm.IsSeparating);
        Assert.False(vm.HasStemHint);
        Assert.Equal("", vm.StemHint);

        await Analyze(vm);
        Assert.StartsWith("ChartFromAudio_SummaryFromStem(", vm.DraftSummary, StringComparison.Ordinal);
        Assert.Equal(1, h.Separation.Runs);
    }

    [Fact]
    public async Task The_running_separation_reports_its_progress()
    {
        var (h, vm) = OpenOnTheSongsMix();
        await vm.StemCheck;
        var run = Separate(vm);

        h.Separation.Progress!.Report(0.4);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (Math.Abs(vm.SeparationProgress - 0.4) > 1e-9 && DateTime.UtcNow < deadline) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(Math.Abs(vm.SeparationProgress - 0.4) < 1e-9,
            $"the progress never arrived within the 60 s deadlock cap (box starved?): {vm.SeparationProgress}");

        h.Separation.Finish(new SongStemSeparationOutcome(SongStemSeparationStatus.Cancelled));
        await run;
    }

    [Fact]
    public async Task Cancel_separation_cancels_the_run_and_the_strip_says_so()
    {
        var (h, vm) = OpenOnTheSongsMix();
        await vm.StemCheck;
        var run = Separate(vm);
        Assert.True(vm.CancelSeparationCommand.CanExecute(null));

        vm.CancelSeparationCommand.Execute(null);
        await run;

        Assert.True(h.Separation.Token.IsCancellationRequested);
        Assert.False(vm.IsSeparating);
        Assert.True(vm.HasStemHint);
        Assert.Equal("ChartFromAudio_SeparationCancelled", vm.StemHint);
        Assert.True(vm.CanSeparateStems, "a cancelled run can be started again");
    }

    /// <summary>The dialog's own Cancel closes it; the user's separation keeps going on the status bar.</summary>
    [Fact]
    public async Task Closing_the_dialog_leaves_a_started_separation_running()
    {
        var (h, vm) = OpenOnTheSongsMix();
        await vm.StemCheck;
        var run = Separate(vm);

        vm.CancelCommand.Execute(null);

        Assert.True(vm.Result.IsCompleted);
        Assert.False(h.Separation.Token.IsCancellationRequested);
        h.Separation.Finish(new SongStemSeparationOutcome(SongStemSeparationStatus.Separated));
        await run;
    }

    [Theory]
    [InlineData(SongStemSeparationStatus.Failed, "GPU unavailable", "ChartFromAudio_SeparationFailed(GPU unavailable)")]
    [InlineData(SongStemSeparationStatus.Unavailable, "no model", "ChartFromAudio_SeparationFailed(no model)")]
    [InlineData(SongStemSeparationStatus.Failed, "", "ChartFromAudio_SeparationFailedNoReason")]
    [InlineData(SongStemSeparationStatus.Separated, "", "ChartFromAudio_StemStillMissing")]
    [InlineData(SongStemSeparationStatus.LoadedFromCache, "", "ChartFromAudio_StemStillMissing")]
    [InlineData(SongStemSeparationStatus.Declined, "", "ChartFromAudio_StemHint")]
    public async Task A_run_that_leaves_no_stem_keeps_the_strip_with_its_reason(SongStemSeparationStatus status,
        string reason, string expected)
    {
        var (h, vm) = OpenOnTheSongsMix();
        await vm.StemCheck;
        var run = Separate(vm);

        h.Separation.Finish(new SongStemSeparationOutcome(status, reason));
        await run;

        Assert.False(vm.IsSeparating);
        Assert.True(vm.HasStemHint);
        Assert.Equal(expected, vm.StemHint);
    }

    [Fact]
    public async Task A_separator_that_throws_is_a_failure_with_its_message()
    {
        var (h, vm) = OpenOnTheSongsMix();
        await vm.StemCheck;
        var run = Separate(vm);

        h.Separation.Throw(new InvalidOperationException("device lost"));
        await run;

        Assert.False(vm.IsSeparating);
        Assert.Equal("ChartFromAudio_SeparationFailed(device lost)", vm.StemHint);
        Assert.True(vm.CanSeparateStems);
    }

    /// <summary>A capability the app cannot honour is greyed with its reason printed, and pressing it does nothing.</summary>
    [Fact]
    public async Task A_blocked_separation_is_greyed_with_its_reason_and_never_runs()
    {
        var h = new Harness(FakeLocalization.KeysOnly());
        h.Separation.Availability = SongStemSeparationAvailability.Blocked("Dialog_StemsNoModel_Message");
        h.LoadProject(WritePluckWav("mix.wav"));
        var vm = h.Open();
        await vm.StemCheck;

        Assert.True(vm.HasStemHint);
        Assert.False(vm.CanSeparateStems);
        Assert.Equal("Dialog_StemsNoModel_Message", vm.SeparateStemsBlockedReason);

        await Separate(vm);
        Assert.Equal(0, h.Separation.Runs);
        Assert.False(vm.IsSeparating);
    }

    [Fact]
    public async Task Choosing_a_file_that_does_not_exist_hides_the_strip()
    {
        var (_, vm) = OpenOnTheSongsMix();
        await vm.StemCheck;
        Assert.True(vm.HasStemHint);

        vm.AudioPath = Path.Combine(_dir, "missing.wav");
        await vm.StemCheck;

        Assert.False(vm.HasStemHint);
    }
}
