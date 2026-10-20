using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Services.Audio.Calibration;

/// <summary>
///     What the latency wizard needs from the engine and nothing more: a reference
///     click on the sample-accurate path, the input capture ring with its host-clock
///     sync, and a temporary override of which open channel seat 0's ring reads.
/// </summary>
public interface ICalibrationAudio
{
    /// <summary>0 while no input is open.</summary>
    int InputSampleRate { get; }

    double EstimatedOutputLatencyMs { get; }

    /// <summary>Schedules a clap and returns the host tick at which it is AUDIBLE; false while the render clock is not up.</summary>
    bool TryScheduleClap(out long audibleTicks);

    /// <summary>Triggers a clap now: the fallback when <see cref="TryScheduleClap" /> cannot schedule.</summary>
    void PlayClapNow();

    int ReadInputCapture(float[] buffer, out ulong streamSamplePosition);

    /// <summary>Empties the capture ring, so what sat there cannot pass for the first click's echo.</summary>
    void DrainInput(float[] buffer);

    /// <summary>Samples captured so far and the host tick they were captured at: the clock a detection is placed on.</summary>
    bool TryGetInputCaptureSync(out ulong capturedSamples, out long syncTicks);

    /// <summary>Points seat 0's capture ring at <paramref name="openChannel" /> over the config's own layout.</summary>
    void ListenOn(int openChannel, AppConfig config);

    /// <summary>Hands seat 0's ring back to the config's layout; a no-op when nothing was overridden.</summary>
    void RestoreRouting();

    /// <summary>The key a learned judging offset is stored under for the device running now.</summary>
    string DeviceKey(AppConfig config);
}

/// <summary><see cref="ICalibrationAudio" /> over the playback engine.</summary>
public sealed class CalibrationAudio(IAudioPlaybackService audio) : ICalibrationAudio
{
    private bool _overridden;

    public int InputSampleRate => audio.InputSampleRate;

    public double EstimatedOutputLatencyMs => audio.EstimatedOutputLatencyMs;

    public bool TryScheduleClap(out long audibleTicks) => audio.TryScheduleCalibrationClap(out audibleTicks);

    public void PlayClapNow() => audio.PlayClap();

    public int ReadInputCapture(float[] buffer, out ulong streamSamplePosition) =>
        audio.ReadInputCapture(buffer, out streamSamplePosition);

    public void DrainInput(float[] buffer)
    {
        for (var i = 0; i < 8 && audio.ReadInputCapture(buffer) > 0; i++) { }
    }

    public bool TryGetInputCaptureSync(out ulong capturedSamples, out long syncTicks) =>
        audio.TryGetInputCaptureSync(out capturedSamples, out syncTicks);

    /// <summary>
    ///     Through the routing OVERRIDE layer, so the seat-layout writer and the
    ///     config stay untouched; only seat 0's channel is replaced.
    /// </summary>
    public void ListenOn(int openChannel, AppConfig config)
    {
        if (audio is not IPlayerInputRouting routing) return;
        var baseline = LiveInputSeatLayout.FromInputConfig(config);
        var channels = baseline.Channels.ToArray();
        channels[0] = openChannel;
        routing.SetLiveInputOverride(new LiveInputSeatLayout(baseline.PlayerCount, channels, baseline.MonitorGains));
        _overridden = true;
    }

    public void RestoreRouting()
    {
        if (!_overridden) return;
        _overridden = false;
        if (audio is IPlayerInputRouting routing) routing.RestoreLiveInputBaseline();
    }

    public string DeviceKey(AppConfig config) => AudioDeviceKey.From(
        config.AudioDriver,
        config.AudioDriver == "ASIO" ? config.AsioInputDeviceName : config.WasapiInputDeviceName,
        config.AudioDriver == "ASIO" ? config.AsioOutputDeviceName : config.WasapiOutputDeviceName,
        audio.ActiveSampleRate,
        audio.ActualBufferFrames,
        (int)Math.Round(audio.InputPitchShiftSemitones),
        audio.InputPitchShiftMode);
}
