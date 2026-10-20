using DotCharter.Tone.Audio;
using DotCharter.Services.Audio.SilentTransport;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Audio;

/// <summary>
///     Headless proof that the audio-less-chart silent transport actually drives
///     the REAL native engine: a generated silent WAV loads, reports the project
///     length, and the playhead advances at wall-clock rate on Play — the exact
///     behaviour an audio-less chart (RS1 chart-only import) needs so scrubbing,
///     count-in and Play Mode work. Output-only, muted (silence anyway), no
///     capture device, no window. Env-gated (<c>DOTCHARTER_SILENT_PROBE=1</c>):
///     it opens an audio device, which has no place in the default gate.
/// </summary>
public class SilentTransportProbeTests
{
    [Fact]
    public async Task SilentTransport_LoadsAndAdvancesThePlayhead()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("DOTCHARTER_SILENT_PROBE") != "1",
            "Opens a real audio output device — set DOTCHARTER_SILENT_PROBE=1 to run.");

        var cacheDir = Path.Combine(Path.GetTempPath(), "dc-silent-probe-" + Guid.NewGuid().ToString("N"));
        var track = new SilentAudioTrack(logger: null, cacheDirectoryOverride: cacheDir);
        // A 3-minute audio-less chart.
        const double projectMs = 180_000;
        var planned = SilentAudioTrack.PlanLengthMs(projectMs);
        var silentWav = track.GetOrCreateSilentWav(projectMs);

        var config = new AppConfig { AudioDriver = "WASAPI", AudioBufferFrames = 480 };

        using var svc = new JuceAudioPlaybackService(FakeLocalization.English(), null, null, null);
        try
        {
            await svc.InitializeAsync();
            svc.ApplyAudioConfiguration(config);
            await svc.LoadAudioAsync(silentWav);
            svc.Volume = 0;
            svc.SongVolume = 0;

            // The transport now has the silent audio of the project's length.
            var length = svc.GetTotalAudioLengthMs();
            Assert.InRange(length, planned - 200, planned + 200);

            svc.SeekTo(0);
            svc.Play();
            Thread.Sleep(1200);
            var t1 = svc.CurrentTimeMs;
            Thread.Sleep(600);
            var t2 = svc.CurrentTimeMs;

            // Playhead advanced at roughly wall-clock rate (scrubbing/Play Mode work).
            Assert.True(t2 > t1, $"Playhead did not advance ({t1:F0} → {t2:F0} ms).");
            Assert.InRange(t2 - t1, 300, 900);
        }
        finally
        {
            svc.Stop();
            BestEffortDelete.Directory(cacheDir);
        }
    }
}
