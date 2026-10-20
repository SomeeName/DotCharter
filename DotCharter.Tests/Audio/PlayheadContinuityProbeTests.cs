using DotCharter.Tone.Audio;

namespace DotCharter.Tests.Audio;

/// <summary>
///     Headless playhead-continuity probe — the regression oracle for the
///     2026-07-17 "Live Play judging inert" incident: the playhead advanced in
///     bursts larger than <c>LivePlaySessionService.SeekJumpThresholdMs</c>
///     (750 ms) between UI-rate ticks, so the matcher treated every tick as a
///     seek and rewound instead of judging. Runs the REAL native engine and
///     REAL audio output (no window, no input device, no focus) and asserts
///     the sampled playhead advances smoothly at wall-clock rate.
///     Env-gated (`DOTCHARTER_POSITION_PROBE=1`): it opens an audio device and
///     runs for ~20 s, which has no place in the default gate.
/// </summary>
public class PlayheadContinuityProbeTests
{
    [Fact]
    public async Task Playhead_advances_smoothly_at_wall_clock_rate()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("DOTCHARTER_POSITION_PROBE") != "1",
            "Opens a real audio output device and runs ~20 s — set DOTCHARTER_POSITION_PROBE=1 to run.");

        var audioPath = Path.Combine(
            AppContext.BaseDirectory, "TestAssets", "Audio", "probe-tone.wav");
        // Fall back to any real test song when the fixture is absent locally.
        if (!File.Exists(audioPath))
            audioPath = LocalFixtures.TestData("EOF", "Yorushika - Matasaburo", "Yorushika - Matasaburo", "song.ogg");
        Assert.SkipWhen(audioPath == null || !File.Exists(audioPath), "No audio fixture available.");

        var config = new AppConfig
        {
            AudioDriver = "WASAPI",
            // Output only — the probe must never open a capture device on the
            // owner's interface (2026-07-17 workflow-interruption complaint).
            AudioBufferFrames = 480
        };

        using var svc = new JuceAudioPlaybackService(FakeLocalization.English(), null, null, null);
        await svc.InitializeAsync();
        svc.ApplyAudioConfiguration(config);
        await svc.LoadAudioAsync(audioPath);

        // Muted: the probe measures the CLOCK, and the default output device
        // is the owner's real speakers — this must be inaudible.
        svc.Volume = 0;
        svc.SongVolume = 0;

        svc.Play();
        try
        {
            // Let the transport spin up, then sample at the UI cadence.
            Thread.Sleep(1500);

            const int ticks = 500;          // ~16.5 s at 33 ms
            const int tickMs = 33;
            var last = svc.CurrentTimeMs;
            double maxDelta = 0, sumDelta = 0;
            var negatives = 0;

            for (var i = 0; i < ticks; i++)
            {
                Thread.Sleep(tickMs);
                var now = svc.CurrentTimeMs;
                var delta = now - last;
                last = now;
                if (delta < 0) negatives++;
                maxDelta = Math.Max(maxDelta, delta);
                sumDelta += Math.Max(0, delta);
            }

            var avgDelta = sumDelta / ticks;

            // The incident signature: inter-tick jumps beyond the matcher's
            // seek threshold. Healthy playback shows deltas near the sleep
            // cadence; 750 ms is the exact boundary that flips the matcher
            // into rewind mode, so we fail well before it.
            Assert.True(maxDelta < 400,
                $"Playhead burst detected: max inter-tick delta {maxDelta:F0} ms (avg {avgDelta:F1} ms) — " +
                "the Live Play matcher rewinds instead of judging above 750 ms.");
            Assert.True(negatives == 0, $"Playhead went backwards {negatives} time(s).");
            // And it must actually ADVANCE at roughly wall-clock rate.
            Assert.InRange(avgDelta, tickMs * 0.7, tickMs * 1.6);
        }
        finally
        {
            svc.Stop();
        }
    }
}
