// The wizard's measuring half: the judging loopback over the capture ring and the two-tap metronome, both driven by one periodic tick.
using System.Diagnostics;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Services.Audio.Calibration;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Tone.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Dialogs;

public sealed partial class LatencyWizardViewModel
{
    private const double BeatPeriodMs = 600.0;
    private static readonly TimeSpan TickPeriod = TimeSpan.FromMilliseconds(15);
    private const double PulseDecayMs = 200.0;
    private const int TargetTaps = 12;
    private const int WarmupBeats = 2;
    private const int MaxTaps = 40;

    private readonly float[] _captureBuffer = new float[32768];
    private readonly List<long> _beatRefs = [];
    private readonly List<double> _visualTaps = [];
    private readonly List<double> _audioTaps = [];
    private IDisposable? _tick;
    private ILiveInputDetector? _analyzer;
    private LoopbackChannelSearch? _loopback;
    private bool _metronomeActive;
    private bool _isVisualMetronome;
    private long _periodTicks;
    private long _nextBeatTicks;
    private long _lastPresentedRefTicks;
    private int _beatsEmitted;
    private bool _isMeasuringJudging;
    private double _visualPulse;
    private string _tapReadout = "";
    private bool _canContinueTaps;

    /// <summary>Builds the onset detector for a sample rate; a test hands in a threshold detector.</summary>
    internal Func<int, ILiveInputDetector>? DetectorFactory { get; set; }

    public bool IsMeasuringJudging
    {
        get => _isMeasuringJudging;
        private set
        {
            if (!SetProperty(ref _isMeasuringJudging, value)) return;
            OnPropertyChanged(nameof(JudgingButtonLabel));
            _measure.NotifyCanExecuteChanged();
        }
    }

    public double VisualPulse { get => _visualPulse; private set => SetProperty(ref _visualPulse, value); }

    public string TapReadout { get => _tapReadout; private set => SetProperty(ref _tapReadout, value); }

    public bool CanContinueTaps { get => _canContinueTaps; private set => SetProperty(ref _canContinueTaps, value); }

    internal int TapCount => (_isVisualMetronome ? _visualTaps : _audioTaps).Count;

    /// <summary>The open channel the last completed run heard the click on.</summary>
    internal int? HeardOnChannel { get; private set; }

    internal void PumpLoopbackForTests(long nowTicks) => PumpLoopback(nowTicks);

    private void MeasureJudging()
    {
        if (IsMeasuringJudging)
        {
            StopLoopback();
            JudgingStatus = _loc.Format("LatencyWizard_Judging_Current", CurrentJudgingOffsetMs());
            return;
        }

        if (!EnsureAnalyzer())
        {
            JudgingStatus = _loc["LivePlay_NoInput"];
            return;
        }

        var config = _config.LoadAppConfig();
        if (_channels.Count == 0) LoadListeningChannels(config);
        var preferred = SelectedOpenChannel ?? GuitarInputChannels.SeatZeroOpenChannel(config);
        var order = GuitarInputChannels.CalibrationChannelOrder(preferred,
            Math.Max(_channels.Count, GuitarInputChannels.OpenChannelCount(config)));
        // Two missed clicks per channel: a wrong jack costs ~3 s before the search moves on.
        _loopback = new LoopbackChannelSearch(order,
            static () => new LatencyCalibrationSession { ClicksTarget = 7, MaxMissedEchoes = 2 });
        ListenOn(_loopback.CurrentChannel, config);
        _loopback.Start(Stopwatch.GetTimestamp());
        IsMeasuringJudging = true;
        JudgingResult = "";
        JudgingStatus = _loc["LatencyWizard_Judging_Listening"] + "  " +
                        _loc.Format("LatencyWizard_Judging_ListeningOnFmt", ChannelLabel(_loopback.CurrentChannel));
        StartTick();
    }

    private void ListenOn(int openChannel, AppConfig config)
    {
        _audio.ListenOn(openChannel, config);
        _audio.DrainInput(_captureBuffer);
    }

    private void PumpLoopback(long now)
    {
        if (_loopback is not { } loopback || _analyzer is not { } analyzer) return;

        if (loopback.ShouldEmitClick(now))
        {
            // Time the round trip from when the click is AUDIBLE; the trigger-now fallback is one output latency early.
            if (_audio.TryScheduleClap(out var emitTicks))
            {
                loopback.NotifyClickEmitted(emitTicks);
            }
            else
            {
                _audio.PlayClapNow();
                loopback.NotifyClickEmitted(Stopwatch.GetTimestamp());
            }
        }

        for (var drain = 0; drain < 8; drain++)
        {
            var read = _audio.ReadInputCapture(_captureBuffer, out var streamPos);
            if (read <= 0) break;
            foreach (var ev in analyzer.Push(_captureBuffer.AsSpan(0, read), streamPos))
            {
                if (TryEventHostTicks(ev, out var hostTicks))
                    loopback.OfferOnset(hostTicks);
            }

            if (read < _captureBuffer.Length) break;
        }

        if (loopback.Advance(now))
        {
            var givenUp = loopback.ChannelsGivenUpOn[^1];
            ListenOn(loopback.CurrentChannel, _config.LoadAppConfig());
            JudgingStatus = _loc.Format("LatencyWizard_Judging_TryingNextFmt",
                ChannelLabel(givenUp), ChannelLabel(loopback.CurrentChannel));
        }

        switch (loopback.State)
        {
            case LatencyCalibrationState.Complete when loopback.ResultMs is { } resultMs:
                CompleteJudging(resultMs, loopback.Session.SamplesMs, loopback);
                break;
            case LatencyCalibrationState.Failed:
                JudgingStatus = _loc.Format("LatencyWizard_Judging_FailedOnFmt",
                    string.Join(", ", loopback.ChannelsGivenUpOn.Select(ChannelLabel)));
                StopLoopback();
                break;
        }
    }

    private void CompleteJudging(double resultMs, IReadOnlyList<double> samplesMs, LoopbackChannelSearch search)
    {
        var offset = Math.Round(resultMs, 1);
        var stats = TapAsyncStats.From(samplesMs);
        // A fresh measurement supersedes what this device LEARNED, or the learned value would override it at once.
        SaveConfig(c =>
        {
            c.LivePlayLatencyOffsetMs = offset;
            c.LivePlayLatencyOffsetsByDevice.Remove(_audio.DeviceKey(c));
        });

        JudgingMeasured = true;
        HeardOnChannel = search.HeardOnChannel;
        var heardLabel = ChannelLabel(search.CurrentChannel);
        JudgingResult = _loc.Format("LatencyWizard_Judging_ResultOnFmt",
            offset, Math.Round(stats.StdDev, 1), stats.UsedCount, heardLabel);
        JudgingStatus = search.FellBack
            ? _loc.Format("LatencyWizard_Judging_FellBackFmt", ChannelLabel(search.PreferredChannel), heardLabel)
            : "";
        _logger.Info(AppLogCategory.Audio,
            $"Latency wizard: judging offset {offset:0.0} ms round trip (+/-{stats.StdDev:0.0} ms, {stats.UsedCount} clicks) " +
            $"heard on open input channel {search.CurrentChannel}" +
            (search.FellBack ? $" after nothing on {search.PreferredChannel}." : "."));
        StopLoopback();
    }

    private void StartMetronome(bool visual)
    {
        _isVisualMetronome = visual;
        _beatRefs.Clear();
        _beatsEmitted = 0;
        _lastPresentedRefTicks = 0;
        _periodTicks = MsToTicks(BeatPeriodMs);
        _nextBeatTicks = Stopwatch.GetTimestamp() + _periodTicks;
        VisualPulse = 0;
        UpdateTapReadout();
        _metronomeActive = true;
        StartTick();
    }

    private void PumpMetronome(long now)
    {
        if (now >= _nextBeatTicks)
        {
            _lastPresentedRefTicks = EmitBeat(now);
            _beatRefs.Add(_lastPresentedRefTicks);
            if (_beatRefs.Count > 8) _beatRefs.RemoveAt(0);
            _beatsEmitted++;
            _nextBeatTicks += _periodTicks;
            // Resync rather than flam when the UI thread stalled past a whole beat.
            if (_nextBeatTicks <= now) _nextBeatTicks = now + _periodTicks;
        }

        if (!_isVisualMetronome) return;
        var since = _lastPresentedRefTicks == 0 ? double.MaxValue : TicksToMs(now - _lastPresentedRefTicks);
        var pulse = since >= PulseDecayMs ? 0.0 : 1.0 - since / PulseDecayMs;
        if (Math.Abs(pulse - VisualPulse) > 0.01) VisualPulse = pulse;
    }

    /// <summary>The host tick a beat is PRESENTED at — the flash now, or the click's audible instant — which every tap is measured against.</summary>
    private long EmitBeat(long now)
    {
        if (_isVisualMetronome)
        {
            VisualPulse = 1.0;
            return now;
        }

        if (_audio.TryScheduleClap(out var audible)) return audible;
        _audio.PlayClapNow();
        return now + MsToTicks(_audio.EstimatedOutputLatencyMs);
    }

    private void Tap()
    {
        if (!IsTapStep || _beatRefs.Count == 0 || _beatsEmitted <= WarmupBeats) return;
        var now = Stopwatch.GetTimestamp();
        var nearest = _beatRefs.MinBy(r => Math.Abs(now - r));
        var asyncMs = TicksToMs(now - nearest);
        // Between beats is a double tap or a lost pulse, not an attempt at this beat.
        if (Math.Abs(asyncMs) > BeatPeriodMs * 0.5) return;
        var list = _isVisualMetronome ? _visualTaps : _audioTaps;
        if (list.Count >= MaxTaps) return;
        list.Add(asyncMs);
        UpdateTapReadout();
    }

    private void UpdateTapReadout()
    {
        var list = _isVisualMetronome ? _visualTaps : _audioTaps;
        var stats = TapAsyncStats.From(list);
        TapReadout = list.Count == 0
            ? _loc.Format("LatencyWizard_TapProgress", 0, TargetTaps)
            : _loc.Format("LatencyWizard_TapProgress", list.Count, TargetTaps) + "   " +
              _loc.Format("LatencyWizard_TapScatter", Math.Round(stats.Mean, 1), Math.Round(stats.StdDev, 1));
        CanContinueTaps = stats.UsedCount >= LatencyCalibrationMath.DefaultMinTaps;
    }

    private void Tick()
    {
        try
        {
            var now = Stopwatch.GetTimestamp();
            if (IsMeasuringJudging) PumpLoopback(now);
            if (_metronomeActive) PumpMetronome(now);
            if (!IsMeasuringJudging && !_metronomeActive) StopTick();
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Audio, $"Latency wizard tick failed: {ex.Message}");
        }
    }

    private void StartTick() => _tick ??= _ui.SchedulePeriodic(TickPeriod, Tick);

    private void StopTick()
    {
        _tick?.Dispose();
        _tick = null;
    }

    /// <summary>A level detector for one known signal: the note analyser's attack gate lost a moderate loopback's clap.</summary>
    private bool EnsureAnalyzer()
    {
        if (_analyzer != null) return true;
        var sampleRate = _audio.InputSampleRate;
        if (sampleRate <= 0) return false;
        _analyzer = DetectorFactory?.Invoke(sampleRate) ?? new LoopbackClapDetector(sampleRate);
        _audio.DrainInput(_captureBuffer);
        return true;
    }

    /// <summary>A detection's attack in host ticks, on the same clock the scheduled clap is on.</summary>
    private bool TryEventHostTicks(in LiveDetectionEvent ev, out long eventTicks)
    {
        eventTicks = 0;
        if (_analyzer is not { } analyzer || !_audio.TryGetInputCaptureSync(out var captured, out var syncTicks) ||
            captured < ev.StreamSamplePosition)
        {
            return false;
        }

        var deltaTicks = (captured - ev.StreamSamplePosition) / (double)analyzer.SampleRate * Stopwatch.Frequency;
        eventTicks = syncTicks - (long)deltaTicks;
        return true;
    }

    private void StopLoopback()
    {
        _loopback = null;
        IsMeasuringJudging = false;
        _audio.RestoreRouting();
        if (!_metronomeActive) StopTick();
    }

    private void StopMetronome()
    {
        _metronomeActive = false;
        VisualPulse = 0;
        if (!IsMeasuringJudging) StopTick();
    }

    private void StopEverything()
    {
        StopTick();
        _metronomeActive = false;
        _loopback = null;
        _analyzer = null;
        IsMeasuringJudging = false;
        _audio.RestoreRouting();
        VisualPulse = 0;
    }

    private static long MsToTicks(double ms) => (long)(ms / 1000.0 * Stopwatch.Frequency);

    private static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
