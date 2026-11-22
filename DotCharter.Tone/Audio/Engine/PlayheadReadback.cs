using JuceSharp;

namespace DotCharter.Tone.Audio.Engine;

/// <summary>Reads the audible clock, applies speed-scaled wall latency, and bounds it to the current media.</summary>
internal static class PlayheadReadback
{
    // Device latency and user delay are wall time; queued audio spans latency times speed in song time.
    internal static double CompensateLatency(double rawMs, double wallLatencyMs, double playbackSpeed)
    {
        var speed = playbackSpeed <= 0.0 ? 1.0 : playbackSpeed;
        var adj = rawMs - wallLatencyMs * speed;
        return adj < 0.0 ? 0.0 : adj;
    }

    internal static double Read(IJuceAudioSource? src, PlayheadInterpolator playhead, PlayheadFloor floor,
        double playbackSpeed, double lengthMs, Func<double, double> compensate, long nowTicks)
    {
        if (src == null) return floor.Ms;
        try
        {
            // Prefer the smooth interpolated clock so that consumers
            // polling at render rate (e.g. the chart playhead at 60+
            // FPS) get sub-tick precision instead of being snapped to
            // the 10 ms timer cadence. At high playback speeds (2x+)
            // a 10 ms snap would otherwise look visibly jittery.
            var speed = playbackSpeed <= 0 ? 1.0 : playbackSpeed;
            if (playhead.TryExtrapolate(nowTicks, speed, out var extrap,
                    out var epoch))
            {
                var lat = compensate(extrap);
                var len = lengthMs;
                if (len > 0 && lat > len) lat = len;
                if (lat < 0) lat = 0;
                // Monotonic during playback: the timer's low-pass
                // already prevents back-correction, but defend
                // against the rare case where latency compensation
                // shifts (e.g. mirror device opens mid-playback).
                // Only within one epoch: a sample from before a seek
                // reports the seek's position instead.
                return floor.Clamp(lat, epoch);
            }
            // `epoch` was read before the native position below, so a
            // seek landing in between makes this reading the stale one.
            var raw = src.AudiblePosition.TotalMilliseconds;
            var adj = compensate(raw);
            var len2 = lengthMs;
            if (len2 > 0 && adj > len2) adj = len2;
            return floor.Publish(adj, epoch);
        }
        catch (ObjectDisposedException)
        {
            // Source was disposed by LoadAudioAsync/FreeAudio while a
            // UI/render-thread caller was reading CurrentTimeMs. Return
            // the last known good value; the next tick will pick up the
            // fresh source.
            return floor.Ms;
        }
        catch
        {
            return floor.Ms;
        }
    }

    // Output latency reported by the active device (samples) converted to ms
    // and combined with any user-tunable AudioDelayMs. Subtracted from the
    // raw audible position so the on-screen playhead matches what's hitting
    // the speakers right now (audio in the device buffer hasn't been heard
    // yet, so the playhead would otherwise lead the audio).
    //
    // Read on every 250 Hz position tick and once per frame by both renderers
    // (through CurrentTimeMs), so the two P/Invokes behind it are cached per
    // engine instance for OutputLatencyCache.RefreshInterval — a rebuilt engine
    // misses on its reference, and an in-place Reconfigure invalidates.
    internal static double EstimateOutputLatency(JuceAudioEngine? eng, OutputLatencyCache cache, long now)
    {
        if (eng == null) return 0.0;
        if (cache.TryGet(eng, now, out var cached)) return cached;
        try
        {
            var sr = eng.SampleRate;
            var ms = sr <= 0 ? 0.0 : eng.OutputLatencySamples * 1000.0 / sr;
            cache.Store(eng, now, ms);
            return ms;
        }
        catch { return 0.0; }
    }
}
