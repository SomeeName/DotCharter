using System.Diagnostics;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Audio.Calibration;
using DotCharter.Tests.Presentation.Settings;

namespace DotCharter.Tests.UI;

/// <summary>
///     The latency wizard's loopback step over a fake engine that captures TWO
///     channels and serves whichever one seat 0 is routed to — the owner's
///     "no click heard although the meter lights on the guitar's channel"
///     (2026-08-30). The wizard must listen on the guitar's channel, route the
///     ring there through the override layer (never a second seat-layout
///     writer), fall back to the other channel when nothing comes back, SAY
///     which channel it heard on, and hand the baseline back when it stops.
/// </summary>
public sealed class LatencyCalibrationWizardChannelTests
{
    private const int SampleRate = 48_000;
    private const int BlockSamples = SampleRate / 10;   // one 100 ms window per click
    private const int ClickAtSample = SampleRate / 20;  // 50 ms into the window

    private static long Ms(double ms) => (long)(ms / 1000.0 * Stopwatch.Frequency);

    /// <summary>
    ///     A two-channel capture with the click on ONE channel. The ring hands
    ///     out the channel the last routing override put seat 0 on — exactly the
    ///     native engine's contract (<c>inputCaptureChannel_[0]</c>).
    /// </summary>
    private sealed class TwoChannelEngine : FakeAudioPlaybackService, IPlayerInputRouting
    {
        private readonly float[][] _channels = [new float[BlockSamples], new float[BlockSamples]];
        private ulong _streamPos;
        private bool _pendingWindow;

        public TwoChannelEngine(int clickOnChannel, float amplitude = 0.8f)
        {
            InputSampleRate = SampleRate;
            for (var i = 0; i < 96; i++)
                _channels[clickOnChannel][ClickAtSample + i] = (i & 1) == 0 ? amplitude : -amplitude;
        }

        public int RoutedChannel { get; private set; }
        public List<LiveInputSeatLayout> Overrides { get; } = [];
        public int Restores { get; private set; }
        /// <summary>Host ticks at which the most recently served window ENDED.</summary>
        public long WindowEndTicks { get; set; }

        /// <summary>Arms one capture window for the next read — the echo of the
        /// click the wizard just scheduled.</summary>
        public void ArmWindow() => _pendingWindow = true;

        public override int ReadInputCapture(float[] destination) => 0;

        public override int ReadInputCapture(float[] destination, out ulong streamPosition)
        {
            streamPosition = _streamPos;
            if (!_pendingWindow) return 0;
            _pendingWindow = false;
            var n = Math.Min(destination.Length, BlockSamples);
            Array.Copy(_channels[RoutedChannel], destination, n);
            _streamPos += (ulong)n;
            return n;
        }

        public override bool TryGetInputCaptureSync(out ulong capturedSamples, out long hostTicks)
        {
            capturedSamples = _streamPos;
            hostTicks = WindowEndTicks;
            return true;
        }

        public void SetLiveInputOverride(LiveInputSeatLayout layout)
        {
            Overrides.Add(layout);
            RoutedChannel = layout.Channels[0];
        }

        public void RestoreLiveInputBaseline() { Restores++; RoutedChannel = 0; }
        public void SetPlayerInputPitchShift(int player, double semitones) { }
        public void SetPlayerInputPitchShiftMode(int player, int mode) { }
        public bool SupportsPerPlayerDropTune => false;
        public int ReadPlayerInputCaptureStream(int player, Span<float> destination, out ulong streamPosition) { streamPosition = 0; return 0; }
        public int ReadPlayerInputCaptureWindow(int player, float[] destination) => 0;
        public int LiveInputPlayerCount => 1;
        public bool TryGetPlayerInputCaptureSync(int player, out ulong capturedSamples, out long hostTicks) { capturedSamples = 0; hostTicks = 0; return false; }
        public float GetAndResetPlayerInputPeak(int player) => 0f;
    }

    /// <summary>First sample over a threshold per pushed block — the onset
    /// finder a calibration needs, without the native analyser.</summary>
    private sealed class ThresholdDetector(int sampleRate) : ILiveInputDetector
    {
        public int SampleRate { get; } = sampleRate;

        public IReadOnlyList<LiveDetectionEvent> Push(ReadOnlySpan<float> samples, ulong blockStreamPosition)
        {
            for (var i = 0; i < samples.Length; i++)
            {
                if (Math.Abs(samples[i]) > 0.5f)
                    return [new LiveDetectionEvent(false, blockStreamPosition + (ulong)i, 0, [], 0, new float[12], 0, 0, 0, 0, 0, default)];
            }

            return [];
        }
    }

    private static (LatencyWizardViewModel Vm, TwoChannelEngine Engine, FakeConfigurationService Config) Build(
        int clickOnChannel, float amplitude = 0.8f, bool productDetector = false)
    {
        var engine = new TwoChannelEngine(clickOnChannel, amplitude);
        var config = new FakeConfigurationService();
        // The owner's interface: ASIO, both jacks open as one stereo pair.
        config.AppConfig.EnableInput = true;
        config.AppConfig.AudioDriver = "ASIO";
        config.AppConfig.AsioInputMono = false;
        config.AppConfig.AsioInputChannelLeft = 0;
        config.AppConfig.AsioInputChannelRight = 1;
        var vm = new LatencyWizardViewModel(new CalibrationAudio(engine), config, new GraphicsStubs.FakeDevice(),
            FakeLocalization.English(), new FakeAppLogger(), new TestUiDispatcher())
        {
            DetectorFactory = productDetector ? null : sr => new ThresholdDetector(sr)
        };
        return (vm, engine, config);
    }

    /// <summary>Pumps the wizard on a synthetic clock until the judging step
    /// finishes. Each click is "audible" at the pump instant and its window is
    /// read one pump (100 ms) later, so a heard echo measures 50 ms.</summary>
    private static void RunToCompletion(LatencyWizardViewModel vm, TwoChannelEngine engine)
    {
        vm.MeasureJudgingCommand.Execute(null);
        // The session was started on the REAL clock; the synthetic one starts
        // there and runs ahead of it, 100 ms per pump.
        var now = Stopwatch.GetTimestamp();
        Assert.True(vm.IsMeasuringJudging, $"the run never started: {vm.JudgingStatus}");
        for (var pump = 0; pump < 400 && vm.IsMeasuringJudging; pump++)
        {
            now += Ms(100);
            var clicksBefore = engine.CalibrationClapCount;
            engine.CalibrationClapAudibleTicks = now;
            // A window read THIS pump ended now, i.e. 100 ms after the click
            // that went out last pump: its onset 50 ms in measures 50 ms.
            engine.WindowEndTicks = now;
            vm.PumpLoopbackForTests(now);
            if (engine.CalibrationClapCount > clicksBefore)
            {
                // The click went out this pump; its echo window is read next pump.
                engine.ArmWindow();
            }
        }
    }

    [Fact]
    public void Open_OffersEveryOpenChannel_AndDefaultsToTheGuitarsSeat()
    {
        var (vm, _, config) = Build(clickOnChannel: 1);
        Assert.Equal(2, vm.ListeningChannels.Count);
        Assert.Equal(GuitarInputChannels.SeatZeroOpenChannel(config.AppConfig), vm.SelectedOpenChannel);
        Assert.Contains(GuitarInputChannels.JackLabel(FakeLocalization.English(), 0), vm.ListeningChannels[0]);
    }

    [Fact]
    public void ClickOnTheChosenChannel_IsHeardThere_ThroughTheRoutingOverride()
    {
        var (vm, engine, config) = Build(clickOnChannel: 1);
        vm.SelectedListeningChannelIndex = 1;

        RunToCompletion(vm, engine);

        Assert.True(vm.JudgingMeasured, $"not measured: {vm.JudgingStatus}");
        Assert.Equal(1, vm.HeardOnChannel);
        Assert.Contains(vm.ListeningChannels[1], vm.JudgingResult);
        Assert.Equal(string.Empty, vm.JudgingStatus);
        Assert.Equal(50.0, config.AppConfig.LivePlayLatencyOffsetMs, 0.5);
        // The ring was moved through the OVERRIDE layer, over the config's own
        // baseline with only seat 0's channel replaced — and handed back.
        Assert.All(engine.Overrides, o => Assert.Equal(1, o.PlayerCount));
        Assert.Equal(1, engine.Overrides[0].Channels[0]);
        Assert.Equal(1, engine.Restores);
        Assert.False(vm.IsMeasuringJudging);
    }

    [Fact]
    public void ClickOnTheOtherChannel_FallsBack_AndTheResultSaysWhichChannelHeardIt()
    {
        var (vm, engine, _) = Build(clickOnChannel: 0);
        // The guitar is "configured" on open channel 1; the loopback cable sits
        // on channel 0.
        vm.SelectedListeningChannelIndex = 1;

        RunToCompletion(vm, engine);

        Assert.True(vm.JudgingMeasured, $"not measured: {vm.JudgingStatus}");
        Assert.Equal(0, vm.HeardOnChannel);
        Assert.Contains(vm.ListeningChannels[0], vm.JudgingResult);
        // The status names BOTH channels: the one that heard nothing and the
        // one that heard the click.
        Assert.Contains(vm.ListeningChannels[1], vm.JudgingStatus);
        Assert.Contains(vm.ListeningChannels[0], vm.JudgingStatus);
        Assert.Equal([1, 0], engine.Overrides.Select(o => o.Channels[0]).ToArray());
        Assert.Equal(1, engine.Restores);
    }

    /// <summary>
    ///     The PRODUCT detector, on a click that comes back at -48 dBFS — the
    ///     owner's friend's loopback (2026-08-30). The note analyser the wizard
    ///     used until 0.49.3 gated this out as hiss and the run failed with
    ///     "no click heard"; the level detector hears it on the configured jack.
    /// </summary>
    [Fact]
    public void QuietLoopback_IsStillHeard_WithTheProductDetector()
    {
        var (vm, engine, config) = Build(clickOnChannel: 0, amplitude: 0.004f, productDetector: true);
        RunToCompletion(vm, engine);

        Assert.True(vm.JudgingMeasured, $"not measured: {vm.JudgingStatus}");
        Assert.Equal(0, vm.HeardOnChannel);
        Assert.Equal(50.0, config.AppConfig.LivePlayLatencyOffsetMs, 0.5);
    }

    /// <summary>The host disposes the dialog's scope however it closed; a run in flight must still hand the ring back.</summary>
    [Fact]
    public void Disposing_MidRun_HandsTheBaselineBack()
    {
        var (vm, engine, _) = Build(clickOnChannel: 1);
        vm.MeasureJudgingCommand.Execute(null);
        vm.Dispose();
        Assert.Equal(1, engine.Restores);
        Assert.False(vm.IsMeasuringJudging);
    }

    [Fact]
    public void Cancel_MidRun_HandsTheBaselineBack()
    {
        var (vm, engine, _) = Build(clickOnChannel: 1);
        vm.MeasureJudgingCommand.Execute(null);
        Assert.Single(engine.Overrides);
        vm.CancelCommand.Execute(null);
        Assert.Equal(1, engine.Restores);
    }
}
